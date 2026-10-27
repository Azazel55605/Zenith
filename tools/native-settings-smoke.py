#!/usr/bin/env python3
"""Build/sign/install the Settings Appearance page on disposable disks; observe real UI input."""
from pathlib import Path
import argparse
import functools
import hashlib
import http.server
import importlib.util
import re
import subprocess
import sys
import threading
import time
ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('keep',type=Path)
    parser.add_argument('--prepared',type=Path,help='replay a copied setup fixture with its signed packages/scripts')
    args=parser.parse_args();keep=args.keep.resolve();keep.mkdir(parents=True,exist_ok=False)
    h=load('ui_headless',ROOT/'tools/headless-smoke.py')
    login=load('ui_login',ROOT/'tools/login-session-smoke.py')
    load('ui_accel',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    profile=load('ui_input',ROOT/'deploy/distributor/validation-input.py');profile.install(h.Machine)
    import recovery_console,boot_image
    profile.install_monitor(recovery_console);monitor_command=recovery_console.monitor_command
    subprocess.run(['dotnet','build',str(ROOT/'tools/Zenith.PackageTool'),'-c','Release'],check=True,stdout=subprocess.DEVNULL)
    tool=['dotnet',str(ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll')]
    def package(*args):
        result=subprocess.run([*tool,*map(str,args)],capture_output=True,text=True)
        if result.returncode:raise RuntimeError('package tool: '+result.stderr.strip())
    served=keep/'served';served.mkdir();package('keygen',keep/'keys')
    key=(keep/'keys/public.pem').read_bytes();(served/'key.pem').write_bytes(key)
    for version in ('0.1.0','0.1.1'):
        payload=keep/('payload-'+version)
        subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(payload),'--program','settings','--version',version],check=True)
        bundle=keep/('bundle-'+version);package('build-native','zenith-settings',version,payload,bundle)
        manifest=(bundle/'manifest.tsv').read_text()
        for capability in ('zenith-native-graphics-abi','zenith-native-settings-abi','zenith-native-window-state-abi'):
            assert 'requires\t'+capability+'\t1.0\n' in manifest
        package('seal',bundle,keep/'keys/private.pem',served/(version+'.zpk'))
    desktop_payload=keep/'desktop-payload'
    subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(desktop_payload),'--program','desktop-tools'],check=True)
    package('build-native','zenith-desktop-tools','0.1.0',desktop_payload,keep/'desktop-bundle')
    package('seal',keep/'desktop-bundle',keep/'keys/private.pem',served/'desktop.zpk')
    theme=keep/'theme/usr/share/zenith/appearance/default';theme.mkdir(parents=True)
    (theme/'native.conf').write_text('VERSION=1\nlight.Surface=f6f7f9\n')
    package('build','zenith-theme-default','0.1.1',keep/'theme',keep/'theme-bundle')
    package('seal',keep/'theme-bundle',keep/'keys/private.pem',served/'theme.zpk')
    scripts={
        'u':'native run settings --page style.colors | logger; echo S_UI=$? | logger',
        'v':'native run settings --show | logger; echo S_SHOW=$? | logger',
        'b':'native run settings --bounds | logger; echo S_BOUNDS=$? | logger',
        'p':'poweroff',
        'j':'native run settings --system-info | logger; echo S_INFO=$? | logger',
        'm':'native run settings | logger; echo S_MERIDIAN=$? | logger',
        'w':'open Welcome; echo S_WELCOME_LAUNCH=$? | logger',
        'z':'open System; echo S_SYSTEM_LAUNCH=$? | logger',
        'd':'su user; export USER=root; native run settings --set light rose; echo S_DENIED=$? | logger',
        'g':'pkg install /root/0.1.1.zpk; echo S_UPGRADE=$? | logger',
        'n':'native run settings --version | logger; echo S_VERSION=$? | logger',
    }
    for name,text in scripts.items():(served/name).write_text(text+'\n')
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self,*args):pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)))
    threading.Thread(target=server.serve_forever,daemon=True).start()
    net=['-netdev','user,id=n','-device','e1000e,netdev=n'];url='http://10.0.2.2:'+str(server.server_port)
    monitor=keep/'monitor.sock';m=None
    def serial(text,marker,timeout=300):
        off=len(m.log());m.send(text+'\r')
        assert m.wait(marker,off,timeout),marker+'; '+str(m.serial)
        print('PASS '+marker,flush=True)
        return off
    def status(text,label,code=0):
        off=serial(text+'; echo '+label+'=$? | logger','user: '+label+'=')
        deadline=time.monotonic()+2
        while True:
            raw=h.efi.smoke.COSMOS_NOISE.sub('',m.log()[off:])
            found=re.findall(r'user: '+re.escape(label)+r'=(\d+)',raw)
            if found:
                assert int(found[-1])==code,label+' returned '+found[-1]+'; '+str(m.serial)
                return
            if time.monotonic()>=deadline:raise RuntimeError('missing status value '+label)
            time.sleep(.05)
    def shutdown():
        serial('poweroff','all filesystems unmounted');assert m.process.wait(timeout=30)==0
        disk=m.disk;m.stop();return disk
    try:
        if args.prepared:
            boot_image.verify(args.prepared,ROOT/'output-x64/Zenith.elf');disk=args.prepared
        else:
            m=h.Machine(keep/'live',network=net)
            assert m.wait('headless ready',timeout=180)
            assert m.wait('network: online address=10.0.2.15',timeout=90)
            status('session set-default headless','S_SESSION');status('install sata0 --yes','S_INSTALL')
            disk=shutdown();m=None
            m=h.Machine(keep/'setup',disk,network=net);assert m.wait('headless ready',timeout=180)
            status('fetch '+url+'/key.pem /root/key.pem','S_KEY')
            status('pkg trust /root/key.pem '+hashlib.sha256(key).hexdigest(),'S_TRUST')
            for name in ('0.1.0.zpk','0.1.1.zpk','theme.zpk','desktop.zpk',*scripts):
                status('fetch '+url+'/'+name+' /root/'+name,'S_FETCH_'+name.replace('.',''))
            status('pkg install /root/0.1.0.zpk','S_PACKAGE')
            status('pkg install /root/theme.zpk','S_THEME')
            status('pkg install /root/desktop.zpk','S_DESKTOP_PACKAGE')
            status('native run settings --bounds','S_BOUNDS')
            status('settings set appearance light teal','S_CLI')
            serial('native run settings --show','SETTINGS saved=light/teal active=dark/blue persistent=1 writable=1 pending=1')
            status('native run settings --set dark violet','S_NATIVE_SET')
            serial('settings show','appearance=dark/violet')
            status('sh /root/d','S_DENY_SCRIPT')
            assert 'S_DENIED=1' in m.log(),'unprivileged native write was accepted'
            serial('native run settings --show','SETTINGS saved=dark/violet')
            status('native run settings --set light teal','S_REBOOT_SETUP')
            disk=shutdown();m=None
        m=h.Machine(keep/'desktop',disk,'session=de',network=net+['-device','virtio-gpu-pci','-device','virtio-keyboard-pci'],monitor=monitor)
        assert m.wait('desktop ready (installed)',timeout=180)
        time.sleep(1);off=len(m.log());login.monitor_type(monitor,'sh /root/u\n')
        assert m.wait('user: SETTINGS_READY',off,60),'Settings launch'
        observer=profile.RenderedMouse(monitor,monitor_command)
        def frame(name,predicate):
            deadline=time.monotonic()+20
            while True:
                data=observer.capture(keep/(name+'.ppm'));w,hgt,rgb=profile.frame_pixels(data)
                if predicate(w,hgt,rgb):return w,hgt,rgb
                if time.monotonic()>=deadline:raise RuntimeError('visible frame '+name)
                time.sleep(.05)
        geometry=[]
        def locate(w,height,rgb):
            # Chrome shares the surface color and clips rounded bottom corners.
            # Locate the selected Light button inside a fully rendered client body.
            color=bytes.fromhex('f6f7f9');selected=bytes.fromhex('197571')
            if sum(rgb[i:i+3]==color for i in range(0,len(rgb),3))<200000:return False
            needle=selected*110
            for row_y in range(height):
                row=rgb[row_y*w*3:(row_y+1)*w*3];i=row.find(needle)
                if i<3 or i%3 or row[i-3:i]==selected or row[i+330:i+333]==selected:continue
                left,top=i//3-149,row_y-108
                if top<0:continue
                sample=((top+2)*w+left+2)*3
                if rgb[sample:sample+3]==color:geometry[:]=[left,top];return True
            return False
        frame('dark',locate);x,y=geometry
        def color_at(rgb,w,px,py):return rgb[(py*w+px)*3:(py*w+px)*3+3]
        def background(color):return lambda w,h,rgb:color_at(rgb,w,x+2,y+2)==bytes.fromhex(color)
        def click(px,py):
            (cx,cy),_=observer.position();monitor_command(monitor,f'mouse_move {px-cx} {py-cy}')
            monitor_command(monitor,'mouse_button 1');monitor_command(monitor,'mouse_button 0')
        # Preview is not persistence. Keyboard input picks Dark without saving.
        monitor_command(monitor,'sendkey tab 30');time.sleep(.1)
        changed=len(m.log());monitor_command(monitor,'sendkey ret 30')
        assert m.wait('user: SETTINGS_UNSAVED',changed,10),'keyboard draft'
        frame('preview',background('202125'));print('PASS keyboard preview',flush=True)
        # Real chrome close must leave the surface alive and offer a discard choice.
        changed=len(m.log());click(x+580,y-19)
        assert m.wait('user: SETTINGS_DISCARD_PROMPT',changed,10),'chrome close lost edits'
        frame('discard-prompt',lambda w,h,rgb:background('202125')(w,h,rgb) and color_at(rgb,w,x+100,y+424)==bytes.fromhex('2a2c31'))
        click(x+90,y+438) # Keep editing.
        frame('kept-editing',lambda w,h,rgb:color_at(rgb,w,x+100,y+424)==bytes.fromhex('7ecac7'))
        print('PASS chrome close intent and Keep editing',flush=True)
        changed=len(m.log());click(x+90,y+438) # Save now.
        assert m.wait('user: SETTINGS_REVISION_CHANGED',changed,15),'GUI save did not complete'
        frame('saved',background('202125'));print('PASS GUI save',flush=True)
        done=len(m.log());monitor_command(monitor,'sendkey esc 30')
        assert m.wait('user: S_UI=0',done,30),'clean close and shell return'
        def desktop_script(name,marker,timeout=60):
            time.sleep(.5);off=len(m.log());login.monitor_type(monitor,'sh /root/'+name+'\n')
            assert m.wait(marker,off,timeout),marker+'; '+str(m.serial)
            print('PASS '+marker,flush=True)
            return off
        desktop_script('v','SETTINGS saved=dark/teal active=dark/teal persistent=1 writable=1 pending=0')
        desktop_script('g','user: S_UPGRADE=0')
        desktop_script('n','user: settings 0.1.1')
        desktop_script('b','user: S_BOUNDS=0')
        observed=desktop_script('j','user: S_INFO=0')
        info=m.log()[observed:]
        for actual in ('Runtime: Cosmos Gen 3 / NativeAOT','Display: 1280x800','Boot: installed','Time zone:'):
            assert actual in info, 'missing actual system information: '+actual
        desktop_script('m','MERIDIAN_FOCUSED')
        monitor_command(monitor,'sendkey ctrl-f 30');time.sleep(.2)
        login.monitor_type(monitor,'bluetooth')
        monitor_command(monitor,'sendkey ret 30');time.sleep(.2)
        off=len(m.log());monitor_command(monitor,'sendkey ret 30')
        assert m.wait('MERIDIAN_PAGE devices.bluetooth',off,10),'settings search did not open WIP destination'
        def wip_visible(w,h,rgb):
            color=bytes.fromhex('25262b');needle=color*552
            for y in range(h-56):
                row=rgb[y*w*3:(y+1)*w*3];i=row.find(needle)
                if i<0 or i%3:continue
                x=i//3
                # Both full-width edges and 56-pixel side edges distinguish the
                # page banner from the same-color 36-pixel trusted title bar.
                if rgb[((y+55)*w+x)*3:((y+55)*w+x+552)*3]!=needle:continue
                if all(color_at(rgb,w,x,y+dy)==color and color_at(rgb,w,x+551,y+dy)==color for dy in range(56)):
                    return True
            return False
        frame('meridian-wip',wip_visible)
        off=len(m.log());monitor_command(monitor,'sendkey ctrl-q 30')
        assert m.wait('user: S_MERIDIAN=0',off,30),'Meridian close'
        desktop_script('v','SETTINGS saved=dark/teal active=dark/teal persistent=1 writable=1 pending=0')
        for script in ('w','z'):
            marker='native surface opened 600x440'
            desktop_script(script,marker)
            # GUI launch output is deliberately private/discarded, not journalled.
            # A rendered enabled Close button proves the native frame is visible
            # after host window admission/focus, unlike a client readiness print.
            needle=bytes.fromhex('2a2c31')*146
            frame('desktop-'+script,lambda w,h,rgb:any(needle in rgb[y*w*3:(y+1)*w*3] for y in range(h)))
            off=len(m.log());monitor_command(monitor,'sendkey esc 30')
            assert m.wait('native surface retired',off,30),'native launcher window retirement'
            time.sleep(.5)
        # Test the actual start menu too: shell `open` must not be the only
        # route to signed applications. The fixed test desktop has seven apps.
        _,(screen_width,screen_height)=observer.position()
        for row in (0,5):
            click(32,screen_height-32)
            frame('launcher',lambda w,h,rgb:color_at(rgb,w,250,h-480)==bytes.fromhex('2a2c31'))
            launched=len(m.log());target=(130,screen_height-480+row*46)
            (cx,cy),_=observer.position()
            monitor_command(monitor,f'mouse_move {target[0]-cx} {target[1]-cy}')
            # Menu actions fire on press. Window admission acknowledges that
            # actual press while startup work is scheduled; do not require a
            # synthetic one-pixel restore beneath a newly opened window.
            profile.monitor_text(monitor,'mouse_button 1')
            try:
                assert m.wait('native surface opened 600x440',launched,30),'start menu bypassed signed native launch'
            finally:
                profile.monitor_text(monitor,'mouse_button 0')
            needle=bytes.fromhex('2a2c31')*146
            frame('launcher-native-'+str(row),lambda w,h,rgb:any(needle in rgb[y*w*3:(y+1)*w*3] for y in range(h)))
            off=len(m.log());monitor_command(monitor,'sendkey esc 30')
            assert m.wait('native surface retired',off,30),'launcher client retirement'
        print('PASS Meridian search/WIP no-write navigation and signed launcher Welcome/System',flush=True)
        desktop_script('p','all filesystems unmounted');assert m.process.wait(timeout=30)==0
        disk=m.disk;m.stop();m=None
        # Independent installed boot re-reads the committed system preference.
        m=h.Machine(keep/'reboot',disk,network=net);assert m.wait('headless ready',timeout=180)
        serial('native run settings --show','SETTINGS saved=dark/teal active=dark/teal persistent=1 writable=1 pending=0')
        disk=shutdown()
        state=boot_image.verify(m.disk)
        assert state['current']==hashlib.sha256((ROOT/'output-x64/Zenith.elf').read_bytes()).hexdigest()
        partition=next(p for p in boot_image.partitions(m.disk) if str(p[1])==state['root'])
        root=keep/'root.ext2'
        with m.disk.open('rb') as src,root.open('wb') as dst:
            src.seek(partition[2]*512);remaining=(partition[3]-partition[2]+1)*512
            while remaining:
                chunk=src.read(min(remaining,1048576));assert chunk;dst.write(chunk);remaining-=len(chunk)
        audit=subprocess.run(['e2fsck','-fn',str(root)],capture_output=True)
        (keep/'e2fsck.log').write_bytes(audit.stdout+audit.stderr);assert audit.returncode==0,'ext2 audit'
        print('PASS Settings save/apply/reboot, independent upgrade, permissions and ext2 audit',flush=True)
    finally:
        if m is not None:m.stop()
        server.shutdown();server.server_close()
if __name__=='__main__':main()
