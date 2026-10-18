#!/usr/bin/env python3
"""Protected foreground ELF package execution, credentials, upgrade and fault/resource containment."""
import functools,hashlib,http.server,importlib.util,subprocess,sys,threading,secrets,socket,time
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'tools'))
from guest_pipeline import check as check_pipeline, check_stderr
from guest_auth import COMPLETION_SECONDS
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
    m=h.Machine(keep/name,disk,argument='smp=native' if disk and cpu_workers else None,network=net,cpus=2 if cpu_workers else 1,monitor=keep/name/'monitor.sock')
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
    status('echo stdin-probe | sunbeam --io-input-buffers','N_IO_INPUT_POINTERS')
    command('echo PIPE_INPUT | sunbeam cat | logger','user: PIPE_INPUT')
    status('sunbeam cat < /dev/null','N_IO_EMPTY_INPUT')
    # Require observable output before stdin arrives or the process can exit.
    offset=len(m.log());m.send('sunbeam --stream-probe; echo N_STREAM_LIVE=$? | logger\r')
    assert m.wait('STREAM_READY',offset,30),'live stdout before input'
    assert 'user: N_STREAM_LIVE=' not in m.log()[offset:],'process still waiting for stdin'
    m.send('interactive-line\r');assert m.wait('STREAM_STDERR',offset,30),'separate live stderr'
    assert m.wait('user: N_STREAM_LIVE=0',offset,30),'canonical input read'
    offset=len(m.log());m.send('sunbeam cat; echo N_STREAM_EOF=$? | logger\r');time.sleep(.3)
    m.send('CANONICAL_ECHO\r');assert m.wait('\nCANONICAL_ECHO\n',offset,30),'interactive cat streams complete input line'
    m.send('\x04');assert m.wait('user: N_STREAM_EOF=0',offset,30),'Ctrl+D returns EOF'
    offset=len(m.log());m.send('sunbeam cat\r');time.sleep(.3);m.send('\x03')
    assert m.wait('^C',offset,30),'interactive stdin cancellation'
    command('echo N_STREAM_CANCEL=$? | logger','user: N_STREAM_CANCEL=130')
    # Exercise repeated backpressure/EOF handoff beyond a single lucky schedule
    # and across the collection pressure that exposed interior-root corruption.
    for attempt in range(64):
        check_pipeline(m,'sunbeam --stream-bytes | sunbeam cat | sunbeam wc -c | logger','163840','N_STREAM_BYTES_'+str(attempt))
    check_pipeline(m,'sunbeam --stream-bytes | sunbeam --stream-fp | sunbeam tee /home/user/native-stream-binary | sunbeam wc -c | logger','163840','N_STREAM_FP_BYTES')
    status('sunbeam --stream-bytes > /home/user/native-stream-redirect','N_STREAM_REDIRECT')
    command('sunbeam --stream-utf8 | sunbeam tee /home/user/native-stream-utf8 | sunbeam wc -c | logger','user: 3')
    status('sunbeam --stream-bytes | sunbeam --stream-fault-in','N_STREAM_FAULT',139)
    status('sunbeam echo after-stream-fault','N_STREAM_FAULT_CLEANUP')
    command('echo EIGHT_STAGES | sunbeam cat | sunbeam cat | sunbeam cat | sunbeam cat | sunbeam cat | sunbeam cat | logger','user: EIGHT_STAGES')
    for delay in (0, .1, .3):
        offset=len(m.log());m.send('logger STREAM_CANCEL_READY; sunbeam cat | sunbeam cat\r')
        assert m.wait('user: STREAM_CANCEL_READY',offset,30),'pipeline cancellation fixture starts'
        time.sleep(delay);m.send('\x03');assert m.wait('^C',offset,30),'blocked native pipeline cancelled'
        command('echo N_STREAM_PIPE_CANCEL=$? | logger','user: N_STREAM_PIPE_CANCEL=130')
    offset=len(m.log());m.send('logger MANAGED_CANCEL_READY; sleep 20 | sleep 20\r')
    assert m.wait('user: MANAGED_CANCEL_READY',offset,30),'managed cancellation fixture starts'
    time.sleep(.1);m.send('\x03');assert m.wait('^C',offset,30),'managed pipeline cancelled'
    command('echo N_MANAGED_PIPE_CANCEL=$? | logger','user: N_MANAGED_PIPE_CANCEL=130')
    status('sunbeam --stream-bytes | sunbeam head -n 1 > /dev/null','N_STREAM_EARLY_EXIT')
    status('sunbeam --stream-fds | logger','N_STREAM_FDS')
    status('sunbeam cat /missing-stream-a | sunbeam cat /missing-stream-b','N_STREAM_FILE_ERRORS',1)
    status('native run missing-stream-a | native run missing-stream-b','N_STREAM_SETUP_ERRORS',1)
    command("printf 'one two\\nthree\\nlast' | sunbeam wc | logger",'user: 2 4 18')
    command("printf 'one\\ntwo\\nthree' | sunbeam tail -n 2 | logger",'user: two')
    command("printf 'one\\ntwo\\nthree' | sunbeam head -n 1 | logger",'user: one')
    status('sunbeam --fs-cpu-state /etc/hostname | sunbeam cat | logger','N_STREAM_FP')
    offset=len(m.log());m.send('sunbeam cat /missing-native-stream-file | sunbeam wc -c | logger; echo N_STREAM_ERR=$? | logger\r')
    assert m.wait('user: N_STREAM_ERR=0',offset,30),'pipeline last status'
    check_stderr(m.log()[offset:],'sunbeam: /missing-native-stream-file: not found','N_STREAM_ERR')
    print('PASS stderr bypasses pipe with zero downstream bytes',flush=True)
    print('PASS live standard streams, EOF/cancel, byte pipeline, early exit, separate stderr and line tools',flush=True)

    status('echo FIRST | sunbeam tee /home/user/native-io','N_IO_CREATE')
    status('echo SECOND | sunbeam tee -a /home/user/native-io','N_IO_APPEND')
    command('sunbeam stat /home/user/native-io | logger','size=13')
    command('sunbeam cat /home/user/native-io | logger','SECOND')
    status('echo THIRD | sunbeam tee /home/user/native-io','N_IO_TRUNCATE')
    command('sunbeam stat /home/user/native-io | logger','size=6')
    status('sunbeam --io-write-buffers /home/user/native-io','N_IO_COPYIN')
    command('sunbeam cat < /home/user/native-io | logger','user: ZHIRD')
    status('sunbeam --io-directions /home/user/native-io','N_IO_DIRECTIONS')
    status('sunbeam --io-budget /home/user/native-big','N_IO_BUDGET')
    command('sunbeam stat /home/user/native-big | logger','size=1048576')
    status('echo X | sunbeam tee -a /home/user/native-big','N_IO_FILE_LIMIT',1)
    command('sunbeam stat /home/user/native-big | logger','size=1048576')
    status('sunbeam --io-fault /home/user/native-fault','N_IO_FAULT',139)
    command('cat /home/user/native-fault | logger','user: F')
    status('echo AFTER_FAULT | sunbeam tee /home/user/native-after-fault','N_IO_FAULT_CLEANUP')
    status('ln -s /home/user/native-io /tmp/native-write-link','N_IO_LINK_SETUP')
    status('echo NO | sunbeam tee /tmp/native-write-link','N_IO_LINK_REFUSED',1)
    command('cat /home/user/native-io | logger','user: ZHIRD')
    status('echo NO | sunbeam tee /dev/zero','N_IO_DEVICE_REFUSED',1)
    status('echo NO | sunbeam tee /tmp','N_IO_DIRECTORY_REFUSED',1)
    offset=len(m.log());m.send('logger IO_CANCEL_READY; sunbeam --io-loop /home/user/native-cancel\r')
    assert m.wait('user: IO_CANCEL_READY',offset,30),'write cancellation fixture starts'
    time.sleep(.1);m.send('\x03');assert m.wait('^C',offset,30),'write service loop cancelled'
    command('echo IO_CANCEL_STATUS=$? | logger','user: IO_CANCEL_STATUS=130')
    status('echo AFTER_CANCEL | sunbeam tee /home/user/native-after-cancel','N_IO_CANCEL_CLEANUP')
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
    status('echo USER_WRITE | sunbeam tee /home/user/native-owned','N_IO_USER_CREATE')
    command('sunbeam stat /home/user/native-owned | logger','uid=1000')
    command('sunbeam stat /home/user/native-owned | logger','user: -rw-r--r--')
    status('chmod 0400 /home/user/native-owned','N_IO_USER_CHMOD')
    status('echo DENIED | sunbeam tee /home/user/native-owned','N_IO_RECHECK',1)
    command('sunbeam cat /home/user/native-owned | logger','user: USER_WRITE')
    status('chmod 0644 /home/user/native-owned; echo USER_APPEND | sunbeam tee -a /home/user/native-owned','N_IO_USER_APPEND')
    status('echo DENIED | sunbeam tee /root/native-secret','N_IO_ROOT_DENIED',1)
    status('echo DENIED | sunbeam tee /home/user/native-io','N_IO_OWNER_DENIED',1)
    status('echo DENIED | sunbeam tee /home/user/native-files/private-dir/public','N_IO_ANCESTOR_DENIED',1)
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
    command('cat /root/native-secret | logger','user: SECRET_ROOT')
    command('cat /home/user/native-io | logger','user: ZHIRD')
    status('pkg verify','N_VERIFY');command('poweroff','all filesystems unmounted');m.process.wait(timeout=30)
    import boot_image
    state=boot_image.verify(m.disk)
    assert state['current']==hashlib.sha256((ROOT/'output-x64/Zenith.elf').read_bytes()).hexdigest(),'native package upgrade changed kernel'
    assert all(secret not in path.read_text(errors='replace') for path in keep.glob('*/serial.log')),'secret-free transcript'
    assert all('[FAILFAST]' not in path.read_text(errors='replace') for path in keep.glob('*/serial.log')),'no runtime fail-fast during stream/fault/cancel checks'
    import struct
    part=next(p for p in boot_image.partitions(m.disk) if str(p[1])==state['root'])
    with m.disk.open('rb') as source,(keep/'root.ext2').open('wb') as destination:
        source.seek(part[2]*512); remaining=(part[3]-part[2]+1)*512
        while remaining:
            data=source.read(min(remaining,1048576));assert data;destination.write(data);remaining-=len(data)
    audit=subprocess.run(['e2fsck','-fn',str(keep/'root.ext2')],capture_output=True)
    (keep/'e2fsck.log').write_bytes(audit.stdout+audit.stderr);assert audit.returncode==0,'ext2 audit'
    for path,expected in (('/home/user/native-io',b'ZHIRD\n'),('/home/user/native-owned',b'USER_WRITE\nUSER_APPEND\n'),('/home/user/native-fault',b'F'),('/home/user/native-stream-binary',bytes(range(256))*640),('/home/user/native-stream-redirect',bytes(range(256))*640),('/home/user/native-stream-utf8',b'\xc3\xbc\n')):
        checked=subprocess.run(['debugfs','-R','cat '+path,str(keep/'root.ext2')],capture_output=True)
        assert checked.returncode==0 and checked.stdout==expected,'independent native write bytes '+path
    print('PASS independent persistent native write contents and ext2 audit',flush=True)
    clean_disk=m.disk;m.stop()
    monitor=keep/'gui-monitor.sock'
    m=h.Machine(keep/'desktop',clean_disk,'session=de smp=native' if cpu_workers else 'session=de',network=net+['-device','virtio-gpu-pci','-device','virtio-keyboard-pci'],monitor=monitor,vga='none',cpus=2 if cpu_workers else 1)
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
    from recovery_console import monitor_command
    def keyboard_streams(label,eof_delay=.3):
        off=len(m.log());login_helper.monitor_type(monitor,'logger '+label+'_READY; sunbeam cat | logger; echo '+label+'_EOF=$? | logger\n')
        assert m.wait('user: '+label+'_READY',off,30),'keyboard stream starts '+label
        time.sleep(.3);login_helper.monitor_type(monitor,label+'_INPUT\n');time.sleep(eof_delay)
        monitor_command(monitor,'sendkey ctrl-d 30')
        assert m.wait('user: '+label+'_INPUT',off,30),'keyboard canonical line '+label
        assert m.wait('user: '+label+'_EOF=0',off,30),'keyboard EOF '+label
        off=len(m.log());login_helper.monitor_type(monitor,'logger '+label+'_CANCEL_READY; sunbeam cat | sunbeam cat\n')
        assert m.wait('user: '+label+'_CANCEL_READY',off,30),'keyboard cancellation starts '+label
        time.sleep(.3);monitor_command(monitor,'sendkey ctrl-c 30');time.sleep(.3)
        off=len(m.log());login_helper.monitor_type(monitor,'echo '+label+'_CANCEL=$? | logger\n')
        assert m.wait('user: '+label+'_CANCEL=130',off,30),'keyboard cancellation returns shell '+label
        print('PASS native canonical input/EOF/pipeline cancellation '+label,flush=True)
    monitor_command(monitor,'sendkey ctrl-alt-t 30');time.sleep(1)
    # Exercise the same queued handoff for hidden input after the GUI clears
    # public type-ahead. Keep the fixture credential unchanged for later login.
    off=len(m.log());login_helper.monitor_type(monitor,'passwd root; echo N_GUI_PASSWORD=$? | logger\n')
    time.sleep(1);login_helper.monitor_type(monitor,secret+'\n')
    time.sleep(.5);login_helper.monitor_type(monitor,secret+'\n')
    assert m.wait('user: N_GUI_PASSWORD=0',off,COMPLETION_SECONDS),'GUI hidden password handoff'
    keyboard_streams('N_GUI')
    keyboard_streams('N_GUI_EARLY',0)
    off=len(m.log());monitor_command(monitor,'sendkey ctrl-alt-l 30')
    assert m.wait('login ready',off,180),'logout after native streams'
    login_helper.monitor_type(monitor,'root\t')
    monitor_command(monitor,'sendkey tab 30');monitor_command(monitor,'sendkey right 30');monitor_command(monitor,'sendkey shift-tab 30')
    off=len(m.log());login_helper.monitor_type(monitor,secret+'\n')
    assert m.wait('headless ready (local text console)',off,180),'local text console login'
    keyboard_streams('N_LOCAL')
    off=len(m.log());login_helper.monitor_type(monitor,'poweroff\n')
    assert m.wait('all filesystems unmounted',off,30),'keyboard stream clean poweroff'
    m.process.wait(timeout=30)
    assert all(secret not in path.read_text(errors='replace') for path in keep.glob('*/serial.log')),'secret-free GUI/local transcripts'
    print('PASS independently installed fonts render desktop; full manuals installed',flush=True)
    print('RESULT native text ELF, arguments, bad-pointer rejection, memory/privilege fault containment, timeout and post-fault shell passed',flush=True)
finally:
    if m:m.stop()
    server.shutdown();server.server_close()
