#!/usr/bin/env python3
"""Installed graphical login, local console, session teardown and signed styling upgrades."""
import argparse, functools, hashlib, http.server, importlib.util, re, secrets, socket, subprocess, sys, threading, time
from pathlib import Path
from recovery_console import RecoveryConsole, monitor_command

ROOT=Path(__file__).resolve().parents[1]; sys.path.insert(0,str(ROOT/'tools'))
spec=importlib.util.spec_from_file_location('headless',ROOT/'tools/headless-smoke.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)

def monitor_type(path, text, german=False):
    for ch in text:
        if german and ch in 'yz': ch={'y':'z','z':'y'}[ch]
        key=h.efi.smoke.KEYS.get(ch)
        if key is None: key='shift-'+h.efi.smoke.SHIFTED[ch] if ch in h.efi.smoke.SHIFTED else 'shift-'+ch.lower() if ch.isupper() else ch
        monitor_command(path,'sendkey '+key+' 30');time.sleep(.05)

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('keep',type=Path);a=p.parse_args()
    keep=a.keep.resolve();keep.mkdir(parents=True,exist_ok=True)
    served=keep/'served';served.mkdir()
    tool=['dotnet',str(ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll')]
    def run(*args):subprocess.run([*tool,*map(str,args)],check=True,stdout=subprocess.DEVNULL)
    run('keygen',keep/'keys'); key=(keep/'keys/public.pem').read_bytes();(served/'public.pem').write_bytes(key)
    def package(name,version,source,meta=False):
        bundle=keep/(name+'-'+version);run('build-meta' if meta else 'build',name,version,source,bundle,'--repo','core')
        archive=served/(name+'-'+version+'.zpk');run('seal',bundle,keep/'keys/private.pem',archive);return archive
    for name in ('zenith-session','zenith-theme-default','zenith-style'):package(name,'0.1.0',ROOT/'packaging'/name,name=='zenith-style')
    subprocess.run([sys.executable,str(ROOT/'tools/build-font-package.py'),str(keep/'fonts')],check=True,stdout=subprocess.DEVNULL)
    package('zenith-fonts','0.1.0',keep/'fonts')
    upgrade=keep/'theme-upgrade/usr/share/zenith/appearance/default';upgrade.mkdir(parents=True)
    (upgrade/'theme.conf').write_text('VERSION=1\nNAME=Sunrise Test\nAccent=FFFFBC70\nAccentSoft=33FFBC70\n')
    package('zenith-theme-default','0.1.1',keep/'theme-upgrade')
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self,*args):pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)))
    threading.Thread(target=server.serve_forever,daemon=True).start();url='http://10.0.2.2:'+str(server.server_port)
    net=['-netdev','user,id=n','-device','e1000e,netdev=n'];m=None;checks=0;secret='L!'+secrets.token_hex(8)
    def check(ok,label):
        nonlocal checks
        if not ok:
            if (keep/'monitor.sock').exists():
                try: monitor_command(keep/'monitor.sock','screendump '+str(keep/'failure.ppm'))
                except (OSError, RuntimeError): pass
            raise RuntimeError(label)
        checks+=1;print('ok   '+label,flush=True)
    def command(text,marker,gui=False,timeout=300,german=False):
        off=len(m.log())
        if gui:monitor_type(keep/'monitor.sock',text+'\n',german)
        else:m.send(text+'\r')
        check(m.wait(marker,off,timeout),marker);time.sleep(.3);return m.log()[off:]
    def status(text,name,gui=False,code=0):return command(text+'; echo '+name+'=$? | logger','user: '+name+'='+str(code),gui)
    def shutdown(gui=False):
        command('poweroff','all filesystems unmounted',gui);check(m.process.wait(timeout=30)==0,'clean poweroff');disk=m.disk;m.stop();return disk
    def login(name,mode):
        check(m.wait('login ready',timeout=180),'login ready')
        monitor_type(keep/'monitor.sock',name+'\t')
        if mode=='headless':
            monitor_command(keep/'monitor.sock','sendkey tab 30');monitor_command(keep/'monitor.sock','sendkey right 30');monitor_command(keep/'monitor.sock','sendkey shift-tab 30')
        off=len(m.log());monitor_type(keep/'monitor.sock',secret+'\n')
        check(m.wait('headless ready (local text console)' if mode=='headless' else 'desktop ready (installed)',off,180),mode+' authenticated')
    try:
        m=h.Machine(keep/'live',network=net);check(m.wait('headless ready',timeout=180),'live serial fallback')
        status('session set-default headless','LOGIN_DEFAULT');status('install sata0 --yes','LOGIN_INSTALL');disk=shutdown()
        m=h.Machine(keep/'setup',disk,'session=headless',network=net);check(m.wait('headless ready',timeout=180),'installed serial recovery')
        for user in ('root','user'):
            command('passwd '+user,'New password: ')
            command(secret,'Repeat password: ');command(secret,'password updated for '+user)
        status('fetch '+url+'/public.pem /tmp/key','LOGIN_KEY_FETCH');status('pkg trust /tmp/key '+hashlib.sha256(key).hexdigest(),'LOGIN_KEY')
        for name in ('zenith-theme-default','zenith-fonts','zenith-session','zenith-style'):
            path='/tmp/'+name+'.zpk';status('fetch '+url+'/'+name+'-0.1.0.zpk '+path,'LOGIN_FETCH_'+name.replace('-','_'))
            status('pkg install '+path,'LOGIN_PKG_'+name.replace('-','_'))
        status('pkg verify','LOGIN_VERIFY');disk=shutdown()
        # Match the standard VGA recovery-menu transport before the long kernel
        # gate. A display changes configured Headless startup into a real greeter.
        mon=keep/'recovery-display/monitor.sock'
        m=h.Machine(keep/'recovery-display',disk,network=net,monitor=mon,vga='std')
        recovery=RecoveryConsole(m,mon,h)
        check(recovery.wait_startup(180),'displayed recovery startup recognized')
        check(recovery.graphical,'displayed recovery requires greeter authentication')
        check(recovery.login('root',secret),'displayed recovery authenticates to local console')
        offset=len(m.log());recovery.send('id | logger; boot status | logger\r')
        check(m.wait('uid=0',offset,60),'displayed recovery has root identity')
        expected=hashlib.sha256((ROOT/'output-x64/Zenith.elf').read_bytes()).hexdigest()
        check(m.wait('running-kernel='+expected,offset,60),'displayed recovery checks exact kernel generation')
        offset=len(m.log());recovery.send('poweroff\r')
        check(m.wait('all filesystems unmounted',offset,60),'displayed recovery synced shutdown')
        check(m.process.wait(timeout=30)==0,'displayed recovery clean poweroff')
        disk=m.disk;m.stop();m=None
        m=h.Machine(keep/'gui',disk,'session=de',network=net+['-device','virtio-gpu-pci'],monitor=keep/'monitor.sock')
        check(m.wait('login ready',timeout=180),'installed starts at greeter')
        check('desktop ready (installed)' not in m.log(),'no desktop before authentication')
        monitor_command(keep/'monitor.sock','screendump '+str(keep/'login.ppm'))
        off=len(m.log());monitor_type(keep/'monitor.sock','user\twrong\n');check(m.wait('session: authentication failed',off,120),'wrong password completes rejection')
        check('authenticated session started' not in m.log(),'wrong password refused')
        monitor_command(keep/'monitor.sock','screendump '+str(keep/'failed.ppm'))
        off=len(m.log());monitor_type(keep/'monitor.sock',secret+'\n');check(m.wait('desktop ready (installed)',off,180),'user desktop authenticated')
        monitor_command(keep/'monitor.sock','sendkey ctrl-alt-t 30');time.sleep(1)
        output=status('id | logger','LOGIN_USER',True);check('uid=1000' in output,'desktop binds ordinary-user credentials')
        off=len(m.log());monitor_type(keep/'monitor.sock','sleep 5\n');time.sleep(.5)
        monitor_command(keep/'monitor.sock','sendkey ctrl-alt-l 30')
        check(m.wait('logout refused: A terminal command is still running.',off,60),'busy command blocks logout')
        monitor_command(keep/'monitor.sock','sendkey ctrl-c 30');time.sleep(1)
        status('open Editor /home/user/unsaved.txt','LOGIN_EDITOR',True);time.sleep(1);monitor_type(keep/'monitor.sock','keep this draft')
        off=len(m.log());monitor_command(keep/'monitor.sock','sendkey ctrl-alt-l 30');time.sleep(1)
        check(m.wait('logout refused: Save or close the editor',off,60),'unsaved editor blocks logout')
        off=len(m.log());monitor_command(keep/'monitor.sock','sendkey ctrl-q 30')
        check(m.wait('editor: close requires discard confirmation',off,30),'editor requests explicit discard')
        off=len(m.log());monitor_command(keep/'monitor.sock','sendkey ctrl-q 30')
        check(m.wait('editor: closed',off,30),'confirmed discard closes editor')
        off=len(m.log());monitor_command(keep/'monitor.sock','sendkey ctrl-alt-l 30');check(m.wait('login ready',off,180),'logout returns to greeter')
        monitor_command(keep/'monitor.sock','screendump '+str(keep/'logout.ppm'))
        login('root','headless')
        output=status('id | logger','LOGIN_ROOT',True);check('uid=0' in output,'local console binds root credentials')
        command('echo HISTORY_OK | logger','user: HISTORY_OK',True)
        off=len(m.log());monitor_command(keep/'monitor.sock','sendkey up 30');monitor_command(keep/'monitor.sock','sendkey ret 30');check(m.wait('user: HISTORY_OK',off,60),'local history recalls command')
        status('settings set keyboard de','LOGIN_DE',True)
        off=len(m.log());monitor_type(keep/'monitor.sock','echo ');monitor_command(keep/'monitor.sock','sendkey shift-7 30');monitor_command(keep/'monitor.sock','sendkey ret 30');time.sleep(1)
        cleaned=h.efi.smoke.COSMOS_NOISE.sub('',m.log()[off:])
        check(re.search(r'(?:^|\r?\n)/\r?\n',cleaned) is not None,'German Shift+7 yields slash output')
        command('settings set keyboard us','saved keyboard',True,german=True)
        status('fetch '+url+'/zenith-theme-default-0.1.1.zpk /tmp/theme-new.zpk','LOGIN_UP_FETCH',True)
        status('pkg install /tmp/theme-new.zpk','LOGIN_UPGRADE',True)
        output=status('boot status | logger','LOGIN_SAME_KERNEL',True);check(hashlib.sha256((ROOT/'output-x64/Zenith.elf').read_bytes()).hexdigest() in output,'theme upgrade retains running kernel')
        off=len(m.log());monitor_type(keep/'monitor.sock','session switch de\n');check(m.wait('login ready',off,180),'local console switches through login')
        check('theme=Sunrise Test' in m.log()[off:],'updated signed theme loaded at logout')
        monitor_command(keep/'monitor.sock','screendump '+str(keep/'theme-upgraded.ppm'))
        pixels=(keep/'theme-upgraded.ppm').read_bytes().split(b'\n',3)[-1]
        check(pixels.count(bytes((255,188,112)))>1000,'theme upgrade visibly changes login accent')
        login('root','desktop');monitor_command(keep/'monitor.sock','sendkey ctrl-alt-t 30');time.sleep(1)
        status('pkg remove zenith-style','LOGIN_META_REMOVE',True)
        output=status('pkg list | logger','LOGIN_DEPS_RETAINED',True);check('zenith-fonts' in output and 'zenith-theme-default' in output,'removing meta retains dependencies')
        status('pkg verify','LOGIN_FINAL_VERIFY',True);disk=shutdown(True);m=None
        import boot_image
        state=boot_image.verify(disk);part=next(p for p in boot_image.partitions(disk) if str(p[1])==state['root'])
        with disk.open('rb') as source,(keep/'root.ext2').open('wb') as target:
            source.seek(part[2]*512);remaining=(part[3]-part[2]+1)*512
            while remaining:
                data=source.read(min(1048576,remaining));target.write(data);remaining-=len(data)
        audit=subprocess.run(['e2fsck','-fn',str(keep/'root.ext2')],capture_output=True);(keep/'e2fsck.log').write_bytes(audit.stdout+audit.stderr)
        check(audit.returncode==0,'independent clean installed ext2')
        check(all(secret not in f.read_text(errors='replace') for f in keep.glob('*/serial.log')),'credentials absent from serial transcripts')
        print('RESULT: '+str(checks)+' login/session/styling checks passed',flush=True)
    finally:
        if m:m.stop()
        server.shutdown();server.server_close()

if __name__=='__main__':main()
