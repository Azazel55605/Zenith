#!/usr/bin/env python3
"""Build/sign/install the client UI gallery on disposable disks; observe real UI input."""
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
    parser.add_argument('--disk',type=Path,help='prepared disposable baseline, copied before use')
    parser.add_argument('--material-light',action='store_true',help='qualify the light palette')
    parser.add_argument('--materials',action='store_true',help='qualify the opt-in coverage/material extension')
    parser.add_argument('--version',default='0.1.0')
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
    payload=keep/'payload'
    subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(payload),'--program','ui-gallery','--version',args.version],check=True)
    assert not (payload/'build').exists(),'compiler intermediates in package'
    package('build-native','zenith-ui-gallery',args.version,payload,keep/'bundle')
    assert 'requires\tzenith-native-graphics-abi\t1.0\n' in (keep/'bundle/manifest.tsv').read_text()
    package('seal',keep/'bundle',keep/'keys/private.pem',served/'ui.zpk')
    (served/'u').write_text(('for n in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do echo MATERIAL BACKDROP TEXTURE $n; done\n' if args.materials else '')+'native run ui-gallery'+(' --materials' if args.materials else '')+' | logger; echo UI_DONE=$? | logger\n')
    (served/'p').write_text('poweroff\n')
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self,*args):pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)))
    threading.Thread(target=server.serve_forever,daemon=True).start()
    net=['-netdev','user,id=n','-device','e1000e,netdev=n'];url='http://10.0.2.2:'+str(server.server_port)
    monitor=keep/'monitor.sock';m=None;stem='ui'+args.version.replace('.','')
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
        if args.disk:
            boot_image.verify(args.disk,ROOT/'output-x64/Zenith.elf');disk=args.disk
        else:
            m=h.Machine(keep/'live',network=net)
            assert m.wait('headless ready',timeout=180)
            assert m.wait('network: online address=10.0.2.15',timeout=90)
            status('session set-default headless','UI_SESSION');status('install sata0 --yes','UI_INSTALL')
            disk=shutdown();m=None
        m=h.Machine(keep/'setup',disk,network=net);assert m.wait('headless ready',timeout=180)
        status('fetch '+url+'/key.pem /root/'+stem+'-key.pem','UI_KEY')
        status('pkg trust /root/'+stem+'-key.pem '+hashlib.sha256(key).hexdigest(),'UI_TRUST')
        for name in ('ui.zpk','u','p'):status('fetch '+url+'/'+name+' /root/'+stem+'-'+name,'UI_FETCH_'+name.replace('.',''))
        status('pkg install /root/'+stem+'-ui.zpk','UI_PACKAGE')
        status('native run ui-gallery --probe','UI_HEADLESS',1)
        if args.materials:status('settings set materials blur','MATERIAL_ENABLE')
        if args.materials:status('settings set appearance '+('light' if args.material_light else 'dark')+' blue','MATERIAL_THEME')
        disk=shutdown();m=None
        m=h.Machine(keep/'desktop',disk,'session=de',network=net+['-device','virtio-gpu-pci','-device','virtio-keyboard-pci'],monitor=monitor)
        assert m.wait('desktop ready (installed)',timeout=180)
        time.sleep(1);off=len(m.log());login.monitor_type(monitor,'sh /root/'+stem+'-u\n')
        assert m.wait('user: MATERIALS_READY' if args.materials else 'user: UI_READY',off,60),'gallery launch'
        if args.materials:assert 'user: MATERIALS_BOUNDS_OK' in m.log()[off:],'material bounds'
        observer=profile.RenderedMouse(monitor,monitor_command)
        def frame(name,predicate):
            deadline=time.monotonic()+20
            while True:
                data=observer.capture(keep/(name+'.ppm'));w,hgt,rgb=profile.frame_pixels(data)
                if predicate(w,hgt,rgb):return w,hgt,rgb
                if time.monotonic()>=deadline:raise RuntimeError('visible frame '+name)
                time.sleep(.05)
        if args.materials:
            nav,body,ink,accent=('eff0f3','fafafb','202126','3157b7') if args.material_light else ('25262b','202125','f1f2f4','8baaff')
            geometry=[]
            def locate(w,height,rgb):
                needle=bytes.fromhex(accent)*200
                for row_y in range(height):
                    row=rgb[row_y*w*3:(row_y+1)*w*3];i=row.find(needle)
                    if i<0 or i%3 or row[i+600:i+603]==bytes.fromhex(accent):continue
                    x=i//3-224
                    if x<0 or x+600>w:continue
                    for y in range(max(0,row_y-255),row_y-223):
                        if y+440>height:continue
                        def at(px,py):
                            offset=((y+py)*w+x+px)*3;return rgb[offset:offset+3]
                        if at(324,223)==bytes.fromhex(body) and at(324,224)==bytes.fromhex(accent) and at(100,400)==bytes.fromhex(nav):
                            geometry[:]=[x,y];return True
                return False
            width,height,solid=frame('material-solid',locate);x,y=geometry
            def pixel(rgb,px,py):
                at=((y+py)*width+x+px)*3;return rgb[at:at+3]
            frames=[]
            for key,name in [('2','TINT'),('3','BLUR')]:
                off=len(m.log());monitor_command(monitor,'sendkey '+key+' 30')
                assert m.wait('user: MATERIALS_'+name,off,10),name
                def visible(w,h,rgb):
                    previous=solid if not frames else frames[-1]
                    return any(pixel(rgb,px,py)!=pixel(previous,px,py) for px in range(40,180,20) for py in range(230,435,20))
                _,_,rgb=frame('material-'+name.lower(),visible);frames.append(rgb)
            tint,blur=frames
            assert any(pixel(tint,px,py)!=pixel(blur,px,py) for px in range(40,180,20) for py in range(230,435,20))
            text=[(px,py) for px in range(24,180) for py in range(24,185) if pixel(solid,px,py)==bytes.fromhex(ink)]
            assert len(text)>100,'foreground text sample'
            assert all(pixel(tint,px,py)==pixel(blur,px,py)==bytes.fromhex(ink) for px,py in text),'foreground faded'
            assert pixel(solid,240,238)==pixel(tint,240,238)==pixel(blur,240,238),'opaque control changed'
            assert pixel(solid,500,380)==pixel(tint,500,380)==pixel(blur,500,380)==bytes.fromhex(body),'content canvas changed'
            (cx,cy),_=observer.position();monitor_command(monitor,f'mouse_move {x+100-cx} {y-22-cy}')
            monitor_command(monitor,'mouse_button 1');monitor_command(monitor,'mouse_move 80 40');monitor_command(monitor,'mouse_button 0')
            x+=80;y+=40
            frame('material-moved',lambda w,h,rgb:pixel(rgb,230,238)==bytes.fromhex(accent))
            print('PASS material bounds, solid/tint/blur, opaque text/controls/content and movement',flush=True)
        else:
            geometry=[]
            def locate(w,height,rgb):
                # The host draws a one-pixel border over the outer surface edge.
                needle=bytes.fromhex('202125')*598
                for y in range(height-440):
                    row=rgb[y*w*3:(y+1)*w*3];i=row.find(needle)
                    if i>=0 and i%3==0:geometry[:]=[i//3-1,y-1];return True
                return False
            frame('dark',locate);x,y=geometry
            def color_at(rgb,w,px,py):return rgb[(py*w+px)*3:(py*w+px)*3+3]
            def background(color):return lambda w,h,rgb:color_at(rgb,w,x+2,y+2)==bytes.fromhex(color)
            # Keyboard-only appearance switch: focus first enabled control, then Light.
            monitor_command(monitor,'sendkey tab 30');time.sleep(.1)
            monitor_command(monitor,'sendkey tab 30');time.sleep(.1)
            change=len(m.log());monitor_command(monitor,'sendkey ret 30')
            assert m.wait('user: UI_CHANGED',change,10),'keyboard activation'
            frame('light',background('fafafb'));print('PASS keyboard light mode',flush=True)
            # Click Teal through actual compositor input; observe both log and accent pixels.
            (cx,cy),_=observer.position();monitor_command(monitor,f'mouse_move {x+260-cx} {y+208-cy}')
            change=len(m.log());monitor_command(monitor,'mouse_button 1');monitor_command(monitor,'mouse_button 0')
            assert m.wait('user: UI_CHANGED',change,10),'pointer activation'
            frame('teal',lambda w,h,rgb:color_at(rgb,w,x+260,y+206)==bytes.fromhex('197571'))
            print('PASS pointer accent choice',flush=True)
            # Disabled effects must neither activate nor change the selected appearance.
            (cx,cy),_=observer.position();monitor_command(monitor,f'mouse_move {x+300-cx} {y+294-cy}')
            change=len(m.log());monitor_command(monitor,'mouse_button 1');monitor_command(monitor,'mouse_button 0')
            time.sleep(.2);assert 'user: UI_CHANGED' not in m.log()[change:],'disabled effect activated'
            print('PASS unavailable effects remain disabled',flush=True)
        done=len(m.log());monitor_command(monitor,'sendkey esc 30')
        assert m.wait('user: UI_DONE=0',done,30),'close and shell return'
        time.sleep(.5);done=len(m.log());login.monitor_type(monitor,'sh /root/'+stem+'-p\n')
        assert m.wait('all filesystems unmounted',done,60);assert m.process.wait(timeout=30)==0
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
        print('PASS native UI gallery, unchanged kernel and independent ext2 audit',flush=True)
    finally:
        if m is not None:m.stop()
        server.shutdown();server.server_close()
if __name__=='__main__':main()
