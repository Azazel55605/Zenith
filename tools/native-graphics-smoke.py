#!/usr/bin/env python3
"""Owned graphical clients on a disposable installed disk and real compositor."""
import functools, hashlib, http.server, importlib.util, subprocess, sys, threading, time
from pathlib import Path
from recovery_console import monitor_command
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('h',ROOT/'tools/headless-smoke.py');h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
spec=importlib.util.spec_from_file_location('login',ROOT/'tools/login-session-smoke.py');login=importlib.util.module_from_spec(spec);spec.loader.exec_module(login)

def main():
    keep=Path(sys.argv[1]);keep.mkdir(parents=True,exist_ok=False)
    tool=ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'
    subprocess.run(['dotnet','build',str(ROOT/'tools/Zenith.PackageTool'),'-c','Release'],check=True,stdout=subprocess.DEVNULL)
    def package(*args):subprocess.run(['dotnet',str(tool),*map(str,args)],check=True,capture_output=True)
    served=keep/'served';served.mkdir();package('keygen',keep/'keys')
    key=(keep/'keys/public.pem').read_bytes();(served/'key.pem').write_bytes(key)
    for version in ('0.1.0','0.1.1'):
        source=keep/version
        subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(source),'--program','gui-probe','--version',version],check=True)
        package('build-native','zenith-gui-probe',version,source,keep/('bundle-'+version),'--repo','extra')
        manifest=(keep/('bundle-'+version)/'manifest.tsv').read_text();assert 'requires\tzenith-native-graphics-abi\t1.0\n' in manifest
        package('seal',keep/('bundle-'+version),keep/'keys/private.pem',served/(version+'.zpk'))
    # Store executable test text during serial setup. Graphical interaction
    # launches only short lowercase paths: do not depend on long shifted labels
    # surviving synthetic keyboard injection while the compositor is rendering.
    gui_texts = [
        ('native run gui-probe --probe | logger','G_PROBE'),
        ('native run gui-probe --exit | logger','G_EXIT'),
        ('native run gui-probe --fault','G_FAULT'),
        ('native run gui-probe --probe | logger','G_AFTER_FAULT'),
        ('native run gui-probe | logger','G_INTERACTIVE'),
        ('native run gui-probe | logger','G_CLOSE'),
        ('native run gui-probe > /tmp/gui-cancel & sleep 8; kill %1; wait %1','G_CANCEL'),
        ('native run gui-probe --probe','G_AFTER_CANCEL'),
        ('native run gui-probe --probe','G_AFTER_LIMIT'),
        ('pkg install /root/0.1.1.zpk','G_UPGRADE'),
        ('native run gui-probe --probe','G_UPGRADED_PROBE'),
        ('su user; export USER=root; native run gui-probe --probe','G_OTHER_UID'),
        ('native run gui-probe --probe','G_SESSION_UID'),
    ]
    quota='native run gui-probe > /tmp/g2 & native run gui-probe > /tmp/g3 & native run gui-probe > /tmp/g4 & native run gui-probe > /tmp/g5 & sleep 5; native run gui-probe --probe; echo G_LIMIT=$? | logger; kill %2; kill %3; kill %4; kill %5; wait; echo G_DRAIN=$? | logger'
    texts=[text+'; echo '+label+'=$? | logger' for text,label in gui_texts]
    texts += [quota,'native run gui-probe --version | logger','poweroff']
    scripts={text:'g'+str(i) for i,text in enumerate(texts)}
    for text,name in scripts.items(): (served/name).write_text(text+'\n')
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self,*args):pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)))
    threading.Thread(target=server.serve_forever,daemon=True).start()
    url='http://10.0.2.2:'+str(server.server_port)
    net=['-netdev','user,id=net0','-device','e1000e,netdev=net0'];m=None;checks=0;gui=False
    monitor=keep/'monitor.sock'
    def command(text,marker,timeout=180):
        nonlocal checks
        off=len(m.log())
        if gui:
            # Status output can precede window removal/focus restoration by two
            # compositor ticks. Do not deliver the next command to the dead client.
            time.sleep(.5)
            login.monitor_type(monitor,'sh /root/'+scripts[text]+'\n')
        else:m.send(text+'\r')
        assert m.wait(marker,off,timeout),marker+'; serial='+str(m.serial)
        checks+=1;print('PASS '+marker,flush=True)
        return m.log()[off:]
    def status(text,label,code=0):return command(text+'; echo '+label+'=$? | logger','user: '+label+'='+str(code))
    try:
        m=h.Machine(keep/'live',network=net);assert m.wait('headless ready',timeout=180)
        assert m.wait('network: online address=10.0.2.15',timeout=90)
        status('session set-default headless','G_SESSION');status('install sata0 --yes','G_INSTALL')
        command('poweroff','all filesystems unmounted');m.process.wait(timeout=30);disk=m.disk;m.stop();m=None
        m=h.Machine(keep/'setup',disk,network=net);assert m.wait('headless ready',timeout=180)
        status('fetch '+url+'/key.pem /root/key.pem','G_KEY')
        status('pkg trust /root/key.pem '+hashlib.sha256(key).hexdigest(),'G_TRUST')
        for v in ('0.1.0','0.1.1'):status('fetch '+url+'/'+v+'.zpk /root/'+v+'.zpk','G_FETCH_'+v.replace('.',''))
        for name in scripts.values():status('fetch '+url+'/'+name+' /root/'+name,'G_SCRIPT_'+name)
        status('pkg install /root/0.1.0.zpk','G_PACKAGE')
        status('native run gui-probe --bounds | logger','G_BOUNDS')
        status('native run gui-probe --probe','G_HEADLESS',1)
        command('poweroff','all filesystems unmounted');m.process.wait(timeout=30);disk=m.disk;m.stop();m=None
        m=h.Machine(keep/'desktop',disk,'session=de',network=net+['-device','virtio-gpu-pci','-device','virtio-keyboard-pci'],monitor=monitor)
        assert m.wait('desktop ready (installed)',timeout=180);gui=True;time.sleep(2)
        status('native run gui-probe --probe | logger','G_PROBE')
        status('native run gui-probe --exit | logger','G_EXIT')
        status('native run gui-probe --fault','G_FAULT',139)
        status('native run gui-probe --probe | logger','G_AFTER_FAULT')
        # Actual pixels and focused input through WindowManager, not synthetic
        # event injection or a kernel-linked application.
        time.sleep(.5)
        off=len(m.log());login.monitor_type(monitor,'sh /root/'+scripts['native run gui-probe | logger; echo G_INTERACTIVE=$? | logger']+'\n')
        assert m.wait('native surface opened',off,30),'surface admission';time.sleep(1)
        monitor_command(monitor,'screendump '+str(keep/'client.ppm'))
        ppm=(keep/'client.ppm').read_bytes();assert ppm.count(bytes.fromhex('6b8cff'))>=5000,'native pixels visible'
        parts=ppm.split(b'\n',3);w,height=map(int,parts[1].split());rgb=parts[3]
        locations=[i//3 for i in range(0,len(rgb),3) if rgb[i:i+3]==bytes.fromhex('6b8cff')]
        left=min(i%w for i in locations)-20;top=min(i//w for i in locations)-20
        cursor_x=left+160;cursor_y=top+100
        monitor_command(monitor,'mouse_move '+str(cursor_x-w//2)+' '+str(cursor_y-height//2))
        time.sleep(.2)
        monitor_command(monitor,'mouse_button 1');time.sleep(.15);monitor_command(monitor,'mouse_button 0');time.sleep(.2)
        # Focus the underlying terminal. F12 must not reach the native window.
        tx=(w-740)//2+28+15;ty=(height-64-470)//2+28+19
        monitor_command(monitor,'mouse_move '+str(tx-cursor_x)+' '+str(ty-cursor_y));time.sleep(.15)
        monitor_command(monitor,'mouse_button 1');time.sleep(.15);monitor_command(monitor,'mouse_button 0');time.sleep(.15)
        monitor_command(monitor,'sendkey f12 30');time.sleep(.2)
        # The raised terminal covers the client. Select the second taskbar pill
        # (Welcome, native client, terminal), rather than clicking through it.
        cursor_x=200;cursor_y=height-32
        monitor_command(monitor,'mouse_move '+str(cursor_x-tx)+' '+str(cursor_y-ty));time.sleep(.15)
        monitor_command(monitor,'mouse_button 1');time.sleep(.15);monitor_command(monitor,'mouse_button 0');time.sleep(.15)
        monitor_command(monitor,'sendkey a 30');time.sleep(.2);monitor_command(monitor,'sendkey esc 30')
        assert m.wait('user: G_INTERACTIVE=0',off,60),'interactive completion'
        raw=m.log()[off:];assert 'GUI_POINTER' in raw and raw.count('GUI_KEY')==2,'focus-filtered events'
        assert 'GUI_FOCUS_OUT' in raw and 'GUI_FOCUS_IN' in raw,'focus transitions';checks+=4
        print('PASS native pixels, pointer and focused key input',flush=True)
        # A chrome close must be delivered without exposing compositor controls.
        time.sleep(.5)
        off=len(m.log());login.monitor_type(monitor,'sh /root/'+scripts['native run gui-probe | logger; echo G_CLOSE=$? | logger']+'\n')
        assert m.wait('native surface opened',off,30);time.sleep(.5)
        monitor_command(monitor,'screendump '+str(keep/'close.ppm'))
        parts=(keep/'close.ppm').read_bytes().split(b'\n',3);w,height=map(int,parts[1].split());rgb=parts[3]
        locations=[i//3 for i in range(0,len(rgb),3) if rgb[i:i+3]==bytes.fromhex('6b8cff')]
        assert locations,'close fixture pixels'
        left=min(i%w for i in locations)-20;top=min(i//w for i in locations)-20
        x=left+320-20;y=top-19
        monitor_command(monitor,'mouse_move '+str(x-cursor_x)+' '+str(y-cursor_y))
        time.sleep(.2);monitor_command(monitor,'mouse_button 1');time.sleep(.2);monitor_command(monitor,'mouse_button 0')
        assert m.wait('user: G_CLOSE=0',off,30),'chrome close completion'
        assert 'GUI_CLOSE' in m.log()[off:],'close event';checks+=1
        time.sleep(.5);off=len(m.log())
        cancel='native run gui-probe > /tmp/gui-cancel & sleep 8; kill %1; wait %1; echo G_CANCEL=$? | logger'
        login.monitor_type(monitor,'sh /root/'+scripts[cancel]+'\n')
        assert m.wait('native surface opened',off,30);time.sleep(.3)
        monitor_command(monitor,'screendump '+str(keep/'minimized.ppm'))
        parts=(keep/'minimized.ppm').read_bytes().split(b'\n',3);w,height=map(int,parts[1].split());rgb=parts[3]
        locations=[i//3 for i in range(0,len(rgb),3) if rgb[i:i+3]==bytes.fromhex('6b8cff')]
        left=min(i%w for i in locations)-20;top=min(i//w for i in locations)-20
        mx=left+270;my=top-19
        monitor_command(monitor,'mouse_move '+str(mx-x)+' '+str(my-y));time.sleep(.15)
        monitor_command(monitor,'mouse_button 1');time.sleep(.15);monitor_command(monitor,'mouse_button 0')
        assert m.wait('user: G_CANCEL=130',off,30),'minimized cancellation'
        assert m.wait('native surface retired',off,30),'minimized client retirement';checks+=2
        print('PASS minimized cancellation and window retirement',flush=True)
        status('native run gui-probe --probe','G_AFTER_CANCEL')
        # Four independently running clients exhaust the desktop admission quota.
        command('native run gui-probe > /tmp/g2 & native run gui-probe > /tmp/g3 & native run gui-probe > /tmp/g4 & native run gui-probe > /tmp/g5 & sleep 5; native run gui-probe --probe; echo G_LIMIT=$? | logger; kill %2; kill %3; kill %4; kill %5; wait; echo G_DRAIN=$? | logger','user: G_DRAIN=130')
        assert 'user: G_LIMIT=1' in m.log(),'desktop quota';checks+=1
        status('native run gui-probe --probe','G_AFTER_LIMIT')
        status('pkg install /root/0.1.1.zpk','G_UPGRADE')
        command('native run gui-probe --version | logger','user: gui-probe 0.1.1')
        status('native run gui-probe --probe','G_UPGRADED_PROBE')
        status('su user; export USER=root; native run gui-probe --probe','G_OTHER_UID',1)
        monitor_command(monitor,'sendkey ctrl-alt-t 30');time.sleep(1)
        status('native run gui-probe --probe','G_SESSION_UID')
        command('poweroff','all filesystems unmounted');m.process.wait(timeout=30)
        import boot_image
        state=boot_image.verify(m.disk)
        assert state['current']==hashlib.sha256((ROOT/'output-x64/Zenith.elf').read_bytes()).hexdigest(),'package upgrade changed kernel'
        part=next(p for p in boot_image.partitions(m.disk) if str(p[1])==state['root'])
        root=keep/'root.ext2'
        with m.disk.open('rb') as src,root.open('wb') as out:
            src.seek(part[2]*512);remaining=(part[3]-part[2]+1)*512
            while remaining:
                data=src.read(min(remaining,1048576));assert data;out.write(data);remaining-=len(data)
        audit=subprocess.run(['e2fsck','-fn',str(root)],capture_output=True)
        (keep/'e2fsck.log').write_bytes(audit.stdout+audit.stderr);assert audit.returncode==0,'ext2 audit'
        print(str(checks)+' graphical client checks passed; independent ext2 audit passed',flush=True)
    finally:
        if m is not None:m.stop()
        server.shutdown();server.server_close()
if __name__=='__main__':main()
