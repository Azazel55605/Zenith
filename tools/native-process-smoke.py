#!/usr/bin/env python3
"""Protected foreground ELF package execution, credentials, upgrade and fault/resource containment."""
import functools,hashlib,http.server,importlib.util,subprocess,sys,threading,secrets,socket,time
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'tools'))
spec=importlib.util.spec_from_file_location('h',ROOT/'tools/headless-smoke.py');h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
cpu_workers='--cpu-workers' in sys.argv[2:]
if any(arg!='--cpu-workers' for arg in sys.argv[2:]): raise SystemExit('usage: native-process-smoke.py KEEP [--cpu-workers]')
keep=Path(sys.argv[1]);keep.mkdir(parents=True,exist_ok=True)
tool=ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'
def run(*args):subprocess.run(['dotnet',str(tool),*map(str,args)],check=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(keep/'source-1'),'--version','0.1.0'],check=True)
subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(keep/'source-2'),'--version','0.1.1'],check=True)
run('keygen',keep/'keys');run('build-native','sunbeam-tools','0.1.0',keep/'source-1',keep/'bundle','--repo','extra')
served=keep/'served';served.mkdir();run('seal',keep/'bundle',keep/'keys/private.pem',served/'sunbeam.zpk')
run('build-native','sunbeam-tools','0.1.1',keep/'source-2',keep/'bundle-2','--repo','extra')
run('seal',keep/'bundle-2',keep/'keys/private.pem',served/'sunbeam-2.zpk')
subprocess.run([sys.executable,str(ROOT/'tools/build-font-package.py'),str(keep/'fonts')],check=True)
subprocess.run(['dotnet','run','--project',str(ROOT/'tools/Zenith.ManualBuilder'),'--',str(keep/'manuals')],check=True,stdout=subprocess.DEVNULL)
for name,source in (('zenith-fonts','fonts'),('zenith-man-pages','manuals')):
    run('build',name,'0.1.0',keep/source,keep/(source+'-bundle'),'--repo','core')
    run('seal',keep/(source+'-bundle'),keep/'keys/private.pem',served/(name+'.zpk'))
key=(keep/'keys/public.pem').read_bytes();(served/'key.pem').write_bytes(key)
class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self,*args):pass
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)));threading.Thread(target=server.serve_forever,daemon=True).start()
url='http://10.0.2.2:'+str(server.server_port);m=None
net=['-netdev','user,id=net0','-device','e1000e,netdev=net0']
def boot(name,disk=None):
    global m
    m=h.Machine(keep/name,disk,argument='smp=native' if disk and cpu_workers else None,network=net,cpus=2 if cpu_workers else 1)
    assert m.wait('headless ready',timeout=180),'boot '+name
    assert m.wait('network: online address=10.0.2.15',timeout=60),'DHCP '+name
    print('PASS boot '+name,flush=True)
def command(text,marker):
    offset=len(m.log());m.send(text+'\r');assert m.wait(marker,offset,timeout=120),text+' missing '+marker
    print('PASS '+marker,flush=True)
def status(text,marker,code=0):command(text+'; echo '+marker+'=$? | logger','user: '+marker+'='+str(code))
try:
    boot('live');status('session set-default headless','N_SESSION');status('install sata0 --yes','N_INSTALL');command('poweroff','all filesystems unmounted');m.process.wait(timeout=30);disk=m.disk;m.stop()
    boot('installed',disk)
    status('cpu irq-test | logger','N_IDT_IRQ_STATE')
    if cpu_workers: status('cpu test 10000000 | logger','N_CPUS_START')
    status('fetch '+url+'/key.pem /root/key.pem','N_KEY');status('pkg trust /root/key.pem '+hashlib.sha256(key).hexdigest(),'N_TRUST')
    status('fetch '+url+'/sunbeam.zpk /root/sunbeam.zpk','N_FETCH');status('pkg install /root/sunbeam.zpk','N_PACKAGE')
    for name in ('zenith-fonts','zenith-man-pages'):
        status('fetch '+url+'/'+name+'.zpk /root/'+name+'.zpk','N_FETCH_'+name)
        status('pkg install /root/'+name+'.zpk','N_INSTALL_'+name)
    command('man pkg | logger','user: '+ 'pkg update [--yes]')
    status('native check /usr/share/zenith/native/sunbeam.elf','N_CHECK')
    status('boot status','N_BOOT_BEFORE')
    status('native run sunbeam','N_RUN')
    command('native run sunbeam id | logger','user: uid=0')
    status('native run sunbeam --cpu-state','N_CPU_STATE')
    status('native run sunbeam echo after-cpu-state','N_CPU_STATE_RESTORED')
    status('native run sunbeam --memory','N_RW_MEMORY')
    status('native run sunbeam --nx','N_NX_MEMORY',139)
    status('native run sunbeam --code-write','N_CODE_READONLY',139)
    status('native run sunbeam echo protected-text-tool','N_ECHO')
    command('native run sunbeam basename /usr/share/doc/// | logger','user: doc')
    command('native run sunbeam basename /tmp/example.txt .txt | logger','user: example')
    command('native run sunbeam dirname /usr/share/doc/// | logger','user: /usr/share')
    status('native run sunbeam --bad-pointer','N_BAD_POINTER')
    status('native run sunbeam --fault','N_FAULT',139)
    status('native run sunbeam echo after-fault','N_AFTER_FAULT')
    status('native run sunbeam --privileged','N_PRIV',139)
    status('native run sunbeam --loop','N_TIMEOUT',124)
    status('native run sunbeam echo after-timeout','N_AFTER_TIMEOUT')
    status('mkdir -p /home/user/native-files; echo FILE_FIRST > /home/user/native-files/readable; chmod 0644 /home/user/native-files/readable; echo SECRET_ROOT > /root/native-secret; chmod 0600 /root/native-secret','N_FS_SETUP')
    command('native run sunbeam cat /home/user/native-files/readable | logger','user: FILE_FIRST')
    command('cd /home/user/native-files; native run sunbeam pwd | logger','user: /home/user/native-files')
    command('native run sunbeam cat readable | logger','user: FILE_FIRST')
    command('native run sunbeam ls -l . | logger','readable')
    status('native run sunbeam --fs-buffers readable','N_FS_POINTERS')
    status('native run sunbeam --fs-limit readable','N_FS_HANDLES')
    status('native run sunbeam --fs-cpu-state readable','N_FS_FP')
    status('native run sunbeam --fs-fault readable','N_FS_FAULT',139)
    status('native run sunbeam --fs-limit readable','N_FS_FAULT_CLEANUP')
    status('native run sunbeam --fs-loop readable','N_FS_TIMEOUT',124)
    status('native run sunbeam --fs-limit readable','N_FS_TIMEOUT_CLEANUP')
    status('sunbeam cat readable','N_FS_ALIAS')
    command('sunbeam stat readable | logger','size=11')
    command('sunbeam stat readable | logger','user: -rw-r--r--')
    command('sunbeam stat /tmp | logger','user: drwxrwxrwt')
    status('native run sunbeam --fs-stale readable','N_FS_STALE')
    status('native run sunbeam --bad-stack','N_BAD_STACK',139)
    status('native run sunbeam cat readable','N_BAD_STACK_CONTAINED')
    status('native run sunbeam --bad-stack-timer','N_BAD_STACK_IRQ',139)
    status('native run sunbeam cat readable','N_BAD_STACK_IRQ_CONTAINED')
    status('native run sunbeam --fs-requests','N_FS_REQUEST_LIMIT',124)
    offset=len(m.log());m.send('logger FS_CANCEL_READY; native run sunbeam --fs-requests\r')
    assert m.wait('user: FS_CANCEL_READY',offset,30),'filesystem cancel fixture starts'
    time.sleep(.02);m.send('\x03');assert m.wait('^C',offset,30),'filesystem request loop cancelled'
    command('echo FS_CANCEL_STATUS=$? | logger','user: FS_CANCEL_STATUS=130')
    status('native run sunbeam --fs-limit readable','N_FS_CANCEL_CLEANUP')
    status('ln -s /root/native-secret /home/user/native-files/private-link; mkdir /home/user/native-files/private-dir; chmod 0700 /home/user/native-files/private-dir; echo ANCESTOR_SECRET > /home/user/native-files/private-dir/public; chmod 0644 /home/user/native-files/private-dir/public','N_FS_LINK_SETUP')

    status('native run sunbeam cat /dev/zero','N_FS_DEVICE_REFUSED',1)
    status('native run sunbeam cat /no-such-native-file','N_FS_MISSING',1)
    status('cd /root','N_FS_RETURN')

    status('cpu irq-test | logger','N_IDT_AFTER_FAULTS')
    if cpu_workers: status('cpu test 10000000 | logger','N_CPUS_AFTER_FAULTS')
    status('fetch '+url+'/sunbeam-2.zpk /root/sunbeam-2.zpk','N_FETCH_UPGRADE')
    status('pkg install /root/sunbeam-2.zpk','N_UPGRADE')
    command('native run sunbeam | logger','user: Sunbeam native tools 0.1.1')
    status('boot status','N_BOOT_AFTER')
    secret='N!'+secrets.token_hex(8)
    command('passwd root','New password: ')
    for marker in ('Repeat password: ','password updated for root'):
        offset=len(m.log());m.send(secret+'\r');assert m.wait(marker,offset,timeout=30),'hidden password input'
    status('su user','N_USER')
    command('native run sunbeam id | logger','user: uid=1000')
    command('native run sunbeam cat /home/user/native-files/readable | logger','user: FILE_FIRST')
    status('native run sunbeam cat /root/native-secret','N_FS_PRIVATE_DENIED',1)
    status('native run sunbeam ls /root','N_FS_DIRECTORY_DENIED',1)
    status('native run sunbeam cat /home/user/native-files/private-link','N_FS_SYMLINK_DENIED',1)
    status('native run sunbeam cat /home/user/native-files/private-dir/public','N_FS_ANCESTOR_DENIED',1)
    command('native run sunbeam stat /home/user/native-files/readable | logger','uid=0')

    status('native run sunbeam ls /dev','N_FS_DIRECTORY_DEV')
    command('native run sunbeam cat /home/user/native-files/readable | logger','user: FILE_FIRST')

    status('native run sunbeam echo unprivileged-text-tool','N_USER_EXEC')
    status('cpu irq-test','N_USER_IRQ_DENIED',1)
    status('native run sunbeam --privileged','N_USER_PRIV_DENIED',139)
    command('login root','Password: ')
    offset=len(m.log());m.send(secret+'\r');assert m.wait('authenticated as root',offset,timeout=30),'root authentication'
    status('pkg verify','N_VERIFY');command('poweroff','all filesystems unmounted');m.process.wait(timeout=30)
    import boot_image
    state=boot_image.verify(m.disk)
    assert state['current']==hashlib.sha256((ROOT/'output-x64/Zenith.elf').read_bytes()).hexdigest(),'native package upgrade changed kernel'
    assert all(secret not in path.read_text(errors='replace') for path in keep.glob('*/serial.log')),'secret-free transcript'
    import struct
    part=next(p for p in boot_image.partitions(m.disk) if str(p[1])==state['root'])
    with m.disk.open('rb') as source,(keep/'root.ext2').open('wb') as destination:
        source.seek(part[2]*512); remaining=(part[3]-part[2]+1)*512
        while remaining:
            data=source.read(min(remaining,1048576));assert data;destination.write(data);remaining-=len(data)
    audit=subprocess.run(['e2fsck','-fn',str(keep/'root.ext2')],capture_output=True)
    (keep/'e2fsck.log').write_bytes(audit.stdout+audit.stderr);assert audit.returncode==0,'ext2 audit'
    clean_disk=m.disk;m.stop()
    monitor=keep/'gui-monitor.sock'
    m=h.Machine(keep/'desktop',clean_disk,'session=de smp=native' if cpu_workers else 'session=de',network=net+['-device','virtio-gpu-pci'],monitor=monitor,vga='none',cpus=2 if cpu_workers else 1)
    for font in ('Inter-Regular.ttf','Inter-SemiBold.ttf','Hack-Regular.ttf'):
        assert m.wait('fonts: loaded packaged '+font,timeout=180),'packaged font '+font
    assert m.wait('login ready',timeout=180),'installed login uses packaged fonts'
    login_spec=importlib.util.spec_from_file_location('login_helper',ROOT/'tools/login-session-smoke.py')
    login_helper=importlib.util.module_from_spec(login_spec);login_spec.loader.exec_module(login_helper)
    login_helper.monitor_type(monitor,'root\t'+secret+'\n')
    assert m.wait('desktop ready (installed)',timeout=180),'authenticated desktop uses installed fonts'
    time.sleep(2)
    with socket.socket(socket.AF_UNIX) as control:
        control.connect(str(monitor)); control.recv(4096)
        control.sendall(('screendump '+str(keep/'desktop.ppm')+'\n').encode())
        time.sleep(1); control.recv(4096)
    assert (keep/'desktop.ppm').stat().st_size>100000,'desktop screenshot'
    # The clean ext2 audit covers the main acceptance disk. This final disposable
    # GUI clone is stopped after rendering; no clean-poweroff claim for that clone.
    print('PASS independently installed fonts render desktop; full manuals installed',flush=True)
    print('RESULT native text ELF, arguments, bad-pointer rejection, memory/privilege fault containment, timeout and post-fault shell passed',flush=True)
finally:
    if m:m.stop()
    server.shutdown();server.server_close()
