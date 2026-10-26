#!/usr/bin/env python3
"""Focused protected CLI acceptance on a disposable installed disk; no release publication."""
import functools
import hashlib
import http.server
import importlib.util
from pathlib import Path
import subprocess
import sys
import threading
import time
import secrets
import re

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('h',ROOT/'tools/headless-smoke.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)

def main():
    keep=Path(sys.argv[1]);keep.mkdir(parents=True,exist_ok=False)
    tool=ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'
    def package(*args):
        subprocess.run(['dotnet',str(tool),*map(str,args)],check=True,capture_output=True)
    subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(keep/'source'),'--version','0.5.0'],check=True)
    package('keygen',keep/'keys')
    package('build-native','sunbeam-tools','0.5.0',keep/'source',keep/'bundle','--repo','extra')
    served=keep/'served';served.mkdir()
    package('seal',keep/'bundle',keep/'keys/private.pem',served/'tools.zpk')
    for program,version in (('base-tools','0.5.0'),('fs-tools','0.5.0'),('base-tools','0.5.1')):
        label=program+'-'+version
        subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(keep/label),'--version',version,'--program',program],check=True)
        package('build-native','zenith-'+program,version,keep/label,keep/(label+'-bundle'),'--repo','core')
        package('seal',keep/(label+'-bundle'),keep/'keys/private.pem',served/(label+'.zpk'))
    key=(keep/'keys/public.pem').read_bytes();(served/'key.pem').write_bytes(key)
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self,*args): pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)))
    threading.Thread(target=server.serve_forever,daemon=True).start()
    url='http://10.0.2.2:'+str(server.server_port)
    net=['-netdev','user,id=net0','-device','e1000e,netdev=net0'];m=None
    checks=0
    def command(text,marker,timeout=120):
        nonlocal checks
        offset=len(m.log());m.send(text+'\r')
        if not m.wait(marker,offset,timeout): raise RuntimeError(marker+'; retained serial: '+str(m.serial))
        checks+=1;print('PASS '+marker,flush=True)
    def status(text,label,code=0,timeout=120):
        command(text+'; echo '+label+'=$? | logger','user: '+label+'='+str(code),timeout)
    def recovery_status(text,label,code=0):
        # Deliberate executable corruption must not depend on the command under
        # test to read the controller's saved status file.
        command(text+'; recovery echo '+label+'=$? | logger','user: '+label+'='+str(code))
    def boot(name,disk=None):
        nonlocal m
        m=h.Machine(keep/name,disk,network=net,monitor=keep/name/'monitor.sock')
        assert m.wait('headless ready',timeout=180),'headless boot'
        assert m.wait('network: online address=10.0.2.15',timeout=90),'DHCP'
    try:
        boot('live');status('session set-default headless','CLI_SESSION')
        status('install sata0 --yes','CLI_INSTALL',timeout=600)
        command('poweroff','all filesystems unmounted');m.process.wait(timeout=30);disk=m.disk;m.stop();m=None
        boot('installed',disk)
        status('fetch '+url+'/key.pem /root/key.pem','CLI_KEY')
        status('pkg trust /root/key.pem '+hashlib.sha256(key).hexdigest(),'CLI_TRUST')
        status('fetch '+url+'/tools.zpk /root/tools.zpk','CLI_FETCH')
        status('pkg install /root/tools.zpk','CLI_PACKAGE')
        command('PRIVATE=hidden; export PUBLIC=visible; sunbeam env | logger','user: PUBLIC=visible')
        offset=len(m.log());status('sunbeam env | grep PRIVATE','CLI_ENV_PRIVATE',1)
        assert 'PRIVATE=hidden' not in m.log()[offset:],'private shell variable leaked'
        status('sunbeam --process-bounds','CLI_PROCESS_BOUNDS')
        status('sunbeam --tty-bounds','CLI_TTY_BOUNDS')
        offset=len(m.log());m.send('sunbeam --tty-raw; echo CLI_RAW=$? | logger\r')
        assert m.wait('TTY_RAW_READY',offset,30),'raw readiness'
        m.send('\x1b[A\x03\x04')
        assert m.wait('user: CLI_RAW=0',offset,30),'raw keys delivered without canonical newline'
        status('sunbeam --tty-bounds','CLI_RAW_RESTORE')
        offset=len(m.log());m.send('sunbeam --tty-binary; echo CLI_BINARY=$? | logger\r')
        assert m.wait('TTY_BINARY_READY',offset,30),'binary raw readiness'
        for value in (0xc3,0xbc,0xff,0): m.socket.sendall(bytes([value]));time.sleep(.02)
        assert m.wait('user: CLI_BINARY=0',offset,30),'UART preserves UTF-8, high and NUL bytes'

        status('sunbeam --tty-fault','CLI_RAW_FAULT',139)
        status('sunbeam --tty-bounds','CLI_FAULT_RESTORE')
        offset=len(m.log());m.send('sunbeam --tty-cancel\r')
        assert m.wait('TTY_CANCEL_READY',offset,30),'raw signal readiness'
        cancelled_at=len(m.log())
        m.send('\x03');assert m.wait('[console]',cancelled_at,30),'raw cancellation returns a new terminal prompt'
        command('echo CLI_CANCEL=$? | logger','user: CLI_CANCEL=130')
        status('sunbeam --tty-bounds','CLI_CANCEL_RESTORE')
        status('sunbeam mkdir -p /home/user/cli/a/b','CLI_MKDIR')
        status('echo KEEP > /home/user/cli/source; sunbeam cp /home/user/cli/source /home/user/cli/copy','CLI_COPY')
        command('sunbeam cat /home/user/cli/copy | logger','user: KEEP')
        status('sunbeam cp /home/user/cli/source /home/user/cli/./source','CLI_SAME_FILE',1)
        command('sunbeam cat /home/user/cli/source | logger','user: KEEP')
        status('sunbeam --path-bounds','CLI_PATH_BOUNDS')
        status('test ! -e /home/user/cli/MUST_NOT_EXIST','CLI_PATH_NO_MUTATION')
        status('sunbeam --seek-probe /home/user/cli/source','CLI_SEEK')
        status('sunbeam cp -r /home/user/cli /home/user/cli/inside','CLI_DESCENDANT',1)
        status('sunbeam ln -s /home/user/cli/source /home/user/cli/link','CLI_LINK')
        status('sunbeam rm /home/user/cli/link','CLI_UNLINK')
        command('sunbeam cat /home/user/cli/source | logger','user: KEEP')
        status('sunbeam mv /home/user/cli/copy /home/user/cli/moved','CLI_MOVE')
        status('sunbeam chmod 0600 /home/user/cli/moved','CLI_MODE')
        status('sunbeam chown 1000:1000 /home/user/cli/moved','CLI_OWNER')
        status('sunbeam touch /home/user/cli/moved','CLI_TOUCH')
        status('sunbeam rm -r /home/user/cli/a','CLI_REMOVE_TREE')
        status('sunbeam rm -r /','CLI_ROOT_REFUSED',1)
        status('sleep 0.1 & wait %1','CLI_BACKGROUND_WAIT')
        status('sunbeam cat > /home/user/cli/empty & wait %2','CLI_BACKGROUND_EOF')
        status('sleep 60 & kill %3; wait %3','CLI_BACKGROUND_CANCEL',130)
        status('sunbeam echo after-job','CLI_AFTER_JOB')
        recovery_status('recovery mkdir /usr/share/zenith/native/base-tools.elf','CLI_UNOWNED_SETUP')
        recovery_status('cat /home/user/cli/source','CLI_UNOWNED_REFUSED',1)
        recovery_status('recovery rmdir /usr/share/zenith/native/base-tools.elf','CLI_UNOWNED_CLEANUP')
        for label in ('base-tools-0.5.0','fs-tools-0.5.0'):
            status('fetch '+url+'/'+label+'.zpk /root/'+label+'.zpk','CLI_FETCH_'+label.replace('-','_').replace('.','_'))
            status('pkg install /root/'+label+'.zpk','CLI_INSTALL_'+label.replace('-','_').replace('.','_'))
        status('mkdir /home/user/cli/native-default; cp /home/user/cli/source /home/user/cli/native-default/copy','CLI_DISPATCH')
        command('cat /home/user/cli/native-default/copy | logger','user: KEEP')
        status('recovery echo BUILTIN','CLI_RECOVERY')
        command('which cat | logger','user: cat: signed native base-tools')
        command('wc -c /home/user/cli/source /home/user/cli/native-default/copy | logger','user: 10 total')
        command('head -1 /home/user/cli/source /home/user/cli/native-default/copy | wc -l | logger','user: 4')
        command('tail -1 /home/user/cli/source /home/user/cli/native-default/copy | wc -l | logger','user: 4')
        command('ls /home/user/cli/source | logger','user: /home/user/cli/source')
        command('stat /home/user/cli/source /home/user/cli/moved | wc -l | logger','user: 2')
        status('ln -s /home/user/cli/source /home/user/cli/again','CLI_STAT_LINK_SETUP')
        command('stat /home/user/cli/again | logger','user: lrwxrwxrwx')

        # Text/traversal algorithms are independent signed package code. Exercise
        # normal command dispatch plus statuses; fixtures stay under /home/user.
        command('which grep find | logger','user: find: signed native fs-tools')
        status("printf 'b\\na\\na\\n' > /home/user/cli/lines",'CLI_TEXT_SETUP')
        command('sort /home/user/cli/lines | uniq | wc -l | logger','user: 2')
        command("grep -En '^(a|b)+$' /home/user/cli/lines | wc -l | logger",'user: 3')
        status('grep MISSING /home/user/cli/lines','CLI_GREP_ABSENT',1)
        status("grep -E '[' /home/user/cli/lines",'CLI_REGEX_INVALID',2)
        command("seq 3 -1 1 | tr -d '\\n' | logger",'user: 321')
        command("printf 'a:b:c\\n' | cut -d: -f1,3 | logger",'user: a:c')
        command("printf 'aaab\\n' | sed 's/a+/[&]/' | logger",'user: [aaa]b')
        command("sed -n '2p' /home/user/cli/lines | logger",'user: a')
        command("sed '/a/d' /home/user/cli/lines | logger",'user: b')
        status("seq 1 0 3",'CLI_SEQ_INVALID',2)
        status("printf '%%' unused > /home/user/cli/percent",'CLI_PRINTF_NO_LOOP')
        command('wc -c /home/user/cli/percent | logger','user: 1 /home/user/cli/percent')
        status('diff /home/user/cli/source /home/user/cli/source','CLI_DIFF_EQUAL')
        status('diff /home/user/cli/source /home/user/cli/lines','CLI_DIFF_CHANGED',1)
        command("find /home/user/cli/native-default -type f -name 'c*' | logger",'user: /home/user/cli/native-default/copy')
        status('ln -s /home/user/cli /home/user/cli/native-default/cycle','CLI_WALK_LINK')
        command('find /home/user/cli/native-default -type l | logger','user: /home/user/cli/native-default/cycle')
        command('find /home/user/cli/native-default | wc -l | logger','user: 3')
        command('du -s /home/user/cli/native-default | logger','user: 1')
        command('tree /home/user/cli/native-default | logger','user: 0 directories, 2 files')
        status('find /home/user/cli -exec echo unsafe','CLI_FIND_UNSUPPORTED',2)

        status('fetch '+url+'/base-tools-0.5.1.zpk /root/update.zpk; pkg install /root/update.zpk','CLI_BASE_UPGRADE')
        command('base-tools --version | logger','user: base-tools 0.5.1')
        # Native PID cancellation must target its background job, not the interactive shell.
        status('sleep 60 | sunbeam cat &','CLI_NATIVE_JOB')
        offset=len(m.log());status('ps | logger','CLI_PS')
        clean=re.sub(r'\x1b\[[0-9;]*m','',m.log()[offset:])
        rows=re.findall(r'user: ([0-9]+) 0 (?:waiting|running|starting) - sunbeam',clean)
        if not rows: raise RuntimeError('owned native process missing from ps snapshot')
        status('kill '+rows[-1],'CLI_NATIVE_KILL')
        status('wait %4','CLI_NATIVE_WAIT',130)
        status('echo after-native-kill','CLI_FOREGROUND_SURVIVES')
        status('chown 1000:1000 /home/user/cli; echo PRIVATE_ROOT > /root/cli-private; chmod 0600 /root/cli-private','CLI_PERMISSIONS_SETUP')
        secret='CLI!'+secrets.token_hex(8)
        command('passwd root','New password: ')
        offset=len(m.log());m.send(secret+'\r');assert m.wait('Repeat password: ',offset,120),'password confirmation'
        offset=len(m.log());m.send(secret+'\r');assert m.wait('password updated for root',offset,600),'password completion'
        status('su user','CLI_USER')
        status('mkdir /home/user/cli/user-dir; touch /home/user/cli/user-dir/file','CLI_USER_CREATE')
        status('grep PRIVATE_ROOT /root/cli-private','CLI_TEXT_READ_DENIED',2)
        status('find /root','CLI_WALK_READ_DENIED',1)
        status('cp /root/cli-private /home/user/cli/stolen','CLI_USER_READ_DENIED',1)
        status('chmod 0644 /root/cli-private','CLI_USER_MODE_DENIED',1)
        status('chown 0 /home/user/cli/user-dir/file','CLI_USER_OWNER_DENIED',1)
        status('rm -r /root','CLI_USER_REMOVE_DENIED',1)
        status('export USER=root; fs-tools chmod 0644 /root/cli-private','CLI_ENV_CANNOT_GRANT_ROOT',1)
        command('su root','Password: ')
        offset=len(m.log());m.send(secret+'\r');assert m.wait('authenticated as root',offset,600),'root login'
        recovery_status('recovery echo tampered >> /usr/share/zenith/native/base-tools.elf; cat /home/user/cli/source','CLI_TAMPER_REFUSED',1)
        command('poweroff','all filesystems unmounted');m.process.wait(timeout=30)
        import boot_image
        state=boot_image.verify(m.disk)
        assert state['current']==hashlib.sha256((ROOT/'output-x64/Zenith.elf').read_bytes()).hexdigest(),'tool upgrade changed kernel'
        part=next(p for p in boot_image.partitions(m.disk) if str(p[1])==state['root'])
        with m.disk.open('rb') as source,(keep/'root.ext2').open('wb') as destination:
            source.seek(part[2]*512);remaining=(part[3]-part[2]+1)*512
            while remaining:
                data=source.read(min(remaining,1048576));assert data;destination.write(data);remaining-=len(data)
        audit=subprocess.run(['e2fsck','-fn',str(keep/'root.ext2')],capture_output=True)
        (keep/'e2fsck.log').write_bytes(audit.stdout+audit.stderr);assert audit.returncode==0,'independent ext2 audit'
        assert all(secret not in p.read_text(errors='replace') for p in keep.glob('*/serial.log')),'secret in transcript'
        print(str(checks)+' CLI command checks passed; raw input/fault/cancel restoration passed',flush=True)
    finally:
        if m: m.stop()
        server.shutdown();server.server_close()

if __name__=='__main__': main()
