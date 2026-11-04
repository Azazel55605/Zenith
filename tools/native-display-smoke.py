#!/usr/bin/env python3
"""Primary display transactions on a disposable installed SVGA guest."""
from pathlib import Path
import argparse,functools,hashlib,http.server,importlib.util,subprocess,sys,threading,time
ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('keep',type=Path);p.add_argument('--prepared',type=Path);args=p.parse_args()
    keep=args.keep.resolve();keep.mkdir(parents=True,exist_ok=False)
    h=load('display_headless',ROOT/'tools/headless-smoke.py');login=load('display_login',ROOT/'tools/login-session-smoke.py')
    load('display_accel',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    profile=load('display_input',ROOT/'deploy/distributor/validation-input.py');profile.install(h.Machine)
    import recovery_console,boot_image
    profile.install_monitor(recovery_console);command=recovery_console.monitor_command
    subprocess.run(['dotnet','build',str(ROOT/'tools/Zenith.PackageTool'),'-c','Release','--nologo'],check=True,stdout=subprocess.DEVNULL)
    tool=['dotnet',str(ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll')]
    def package(*a):subprocess.run([*tool,*map(str,a)],check=True,stdout=subprocess.DEVNULL)
    served=keep/'served';served.mkdir();package('keygen',keep/'keys');key=(keep/'keys/public.pem').read_bytes();(served/'key.pem').write_bytes(key)
    subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(keep/'payload'),'--program','settings'],check=True)
    package('build-native','zenith-settings','0.1.0',keep/'payload',keep/'bundle');package('seal',keep/'bundle',keep/'keys/private.pem',served/'settings.zpk')
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self,*args):pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)))
    threading.Thread(target=server.serve_forever,daemon=True).start()
    net=['-netdev','user,id=n','-device','e1000e,netdev=n'];url='http://10.0.2.2:'+str(server.server_port)
    monitor=keep/'monitor.sock';m=None;display_scale=1
    terminal=load('display_terminal',ROOT/'deploy/distributor/validation-terminal.py')
    def logical_frame():
        import io
        from PIL import Image
        path=keep/'terminal-readiness.ppm';command(monitor,'screendump '+str(path));data=path.read_bytes()
        if display_scale==1:return data
        image=Image.open(io.BytesIO(data));image=image.resize((image.width//display_scale,image.height//display_scale),Image.Resampling.NEAREST)
        out=io.BytesIO();image.save(out,format='PPM');return out.getvalue()
    def public_prompt(data,box):
        import io
        from PIL import Image
        image=Image.open(io.BytesIO(data)).convert('RGB');left,top,_,bottom=box;height=bottom-top
        row=image.crop((max(0,left-8*height),top,left,bottom));green=[];blue=[]
        for i,(r,g,b) in enumerate(row.getdata()):
            x=i%row.width
            if g-r>20 and g-b>10:green.append(x)
            # The unthemed recovery Terminal has a blue-tinted dark background.
            # Require foreground brightness as well as blue hue for its cwd.
            if b>96 and b-g>15 and b-r>15:blue.append(x)
        return len(green)>=12 and len(blue)>=3 and max(green)-min(green)>=2*height and min(blue)>max(green) and min(blue)>=row.width-4*height
    def ready():
        before=logical_frame();(keep/'terminal-before.ppm').write_bytes(before);deadline=time.monotonic()+15;observations=[]
        while time.monotonic()<deadline:
            time.sleep(.1);after=logical_frame();box=terminal.caret_box(before,after)
            observations.append(dict(box=box,public=public_prompt(after,box) if box else False))
            if box is not None and public_prompt(after,box):return
            before=after
        import json
        (keep/'terminal-observations.json').write_text(json.dumps(observations))
        (keep/'terminal-after.ppm').write_bytes(before)
        raise RuntimeError('no focused public Terminal prompt before display command')
    def serial(text,marker,timeout=180):
        off=len(m.log());m.send(text+'\r');assert m.wait(marker,off,timeout),marker;print('PASS '+marker,flush=True);return off
    def keyboard(text,marker,timeout=60):
        ready();off=len(m.log());login.monitor_type(monitor,text+'\n');assert m.wait(marker,off,timeout),marker;print('PASS '+marker,flush=True);return off
    def pixels(name,w,h,client=False):
        end=time.monotonic()+15
        while True:
            path=keep/(name+'.ppm');command(monitor,'screendump '+str(path));width,height,rgb=profile.frame_pixels(path.read_bytes())
            if(width,height)==(w,h) and len(set(rgb[::3072]))>4 and (not client or rgb.count(bytes.fromhex('202125'))>50000):return rgb
            if time.monotonic()>end:raise AssertionError('presented '+name+' '+str((width,height)))
            time.sleep(.1)
    def shutdown(graphical=False):
        if graphical:keyboard('poweroff','all filesystems unmounted')
        else:serial('poweroff','all filesystems unmounted')
        assert m.process.wait(timeout=30)==0;disk=m.disk;m.stop();return disk
    try:
        if args.prepared:
            boot_image.verify(args.prepared,ROOT/'output-x64/Zenith.elf');disk=args.prepared
        else:
            m=h.Machine(keep/'live',network=net);assert m.wait('headless ready');assert m.wait('network: online address=10.0.2.15')
            serial('session set-default headless; echo D_SESSION=$? | logger','user: D_SESSION=0');serial('install sata0 --yes; echo D_INSTALL=$? | logger','user: D_INSTALL=0')
            disk=shutdown();m=None
            m=h.Machine(keep/'setup',disk,network=net);assert m.wait('headless ready')
            serial('fetch '+url+'/key.pem /root/display-key.pem','fetch complete')
            serial('pkg trust /root/display-key.pem '+hashlib.sha256(key).hexdigest()+'; echo D_TRUST=$? | logger','user: D_TRUST=0')
            serial('fetch '+url+'/settings.zpk /root/display.zpk; echo D_FETCH=$? | logger','user: D_FETCH=0')
            serial('pkg install /root/display.zpk; echo D_PACKAGE=$? | logger','user: D_PACKAGE=0')
            serial('native run settings --bounds; echo DISPLAY_BOUNDS=$? | logger','user: DISPLAY_BOUNDS=0')
            disk=shutdown();m=None
        m=h.Machine(keep/'desktop',disk,'session=de smp=native',cpus=4,network=net,monitor=monitor,vga='vmware');assert m.wait('desktop ready (installed)')
        # A client-boundary-aware controller replaces this helper in distributor runs.
        time.sleep(1)
        keyboard('display | logger','DISPLAY VMWare');pixels('initial',1024,768)
        keyboard('native run settings --display-test 1600 1200 2 keep | logger','DISPLAY_FINISHED')
        display_scale=2
        pixels('kept-scale',1600,1200)
        keyboard('display | logger','1600x1200 scale=2 phase=0')
        keyboard('native run settings --display-test 1280 800 1 exit | logger','DISPLAY_PREVIEW')
        keyboard('display | logger','1600x1200 scale=2 phase=0')
        pixels('exit-restored',1600,1200)
        trial=keyboard('display test 1280 800 1','display: preview 1280x800 scale=1')
        display_scale=1
        pixels('timeout-preview',1280,800)
        assert m.wait('display: restored',trial,timeout=30)
        display_scale=2
        pixels('timeout-restored',1600,1200)
        keyboard('display | logger','1600x1200 scale=2 phase=0')
        keyboard('native run settings --page display.configuration | logger','user: SETTINGS_READY')
        assert m.wait('native surface opened 640x480',timeout=30)
        pixels('meridian-display',1600,1200,client=True)
        # Fresh client has no keyboard focus target. Reverse Tab selects the
        # last enabled control (resolution combo); Home picks the boot mode.
        changed=len(m.log())
        for key in ('shift-tab','ret','home','ret'):
            command(monitor,'sendkey '+key+' 30');time.sleep(.15)
        assert m.wait('user: SETTINGS_UNSAVED',changed,15),'display draft from real keyboard input'
        trial=len(m.log())
        for key in ('tab','ret'):
            command(monitor,'sendkey '+key+' 30');time.sleep(.15)
        assert m.wait('display: preview 1024x768 scale=1',trial,15),'Meridian Apply request'
        display_scale=1
        assert m.wait('user: DISPLAY_STATE phase=2',trial,10)
        pixels('meridian-confirm',1024,768)
        command(monitor,'sendkey esc 30');assert m.wait('display: restored',trial,15),'Meridian Revert'
        display_scale=2
        pixels('meridian-reverted',1600,1200,client=True)
        assert m.wait('user: DISPLAY_STATE phase=0',trial,10)
        # A second, explicitly requested trial exercises Keep through Meridian.
        trial=len(m.log())
        for key in ('shift-tab','ret','home','ret','tab','ret'):
            command(monitor,'sendkey '+key+' 30');time.sleep(.15)
        assert m.wait('display: preview 1024x768 scale=1',trial,15)
        display_scale=1
        assert m.wait('user: DISPLAY_STATE phase=2',trial,10)
        pixels('meridian-keep-confirm',1024,768)
        command(monitor,'sendkey ret 30');assert m.wait('display: kept',trial,15)
        assert m.wait('user: DISPLAY_STATE phase=0',trial,10)
        pixels('meridian-kept',1024,768,client=True)
        command(monitor,'sendkey ctrl-q 30');assert m.wait('native surface retired',timeout=30)
        keyboard('native run settings --display-test 1600 1200 2 keep | logger','DISPLAY_FINISHED')
        display_scale=2
        disk=shutdown(True);m=None
        boot_image.verify(disk,ROOT/'output-x64/Zenith.elf')
        m=h.Machine(keep/'reboot',disk,'session=de smp=native',cpus=4,network=net,monitor=monitor,vga='vmware');assert m.wait('desktop ready (installed)')
        pixels('reboot-kept',1600,1200);time.sleep(1)
        keyboard('display | logger','1600x1200 scale=2 phase=0');disk=shutdown(True);m=None
        # Driver without runtime mode support still boots the saved configuration safely.
        m=h.Machine(keep/'fixed',disk,'session=de smp=native',cpus=4,network=net+['-device','virtio-gpu-pci'],monitor=monitor);assert m.wait('desktop ready (installed)')
        display_scale=1
        time.sleep(1);keyboard('display | logger','DISPLAY VirtioGpu');keyboard('display test 1600 1200 1; echo D_FIXED_DENIED=$? | logger','user: D_FIXED_DENIED=1')
        disk=shutdown(True);m=None
        m=h.Machine(keep/'safe',disk,'session=de display=safe smp=native',cpus=4,network=net,monitor=monitor,vga='vmware');assert m.wait('desktop ready (installed)')
        pixels('safe-mode',1024,768);keyboard('display | logger','1024x768 scale=1 phase=0')
        keyboard('su user; native run settings --display-test 1600 1200 2 keep | logger','DISPLAY_REFUSED code=13')
        command(monitor,'sendkey ctrl-alt-t 30')
        keyboard('id | logger','uid=0(root)')
        disk=shutdown(True);m=None
        state=boot_image.verify(disk,ROOT/'output-x64/Zenith.elf');part=next(p for p in boot_image.partitions(disk) if str(p[1])==state['root'])
        root=keep/'root.ext2'
        with disk.open('rb') as src,root.open('wb') as dst:
            src.seek(part[2]*512);left=(part[3]-part[2]+1)*512
            while left:
                data=src.read(min(left,1048576));assert data;dst.write(data);left-=len(data)
        audit=subprocess.run(['e2fsck','-fn',str(root)],capture_output=True);(keep/'e2fsck.log').write_bytes(audit.stdout+audit.stderr);assert audit.returncode==0
        print('PASS display mode/scale, client exit, timeout, Keep/reboot, fixed-mode fallback and ext2 audit',flush=True)
    finally:
        if m is not None:m.stop()
        server.shutdown();server.server_close()
if __name__=='__main__':main()
