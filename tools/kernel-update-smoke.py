#!/usr/bin/env python3
"""Signed kernel upgrades, actual UEFI activation, manual fallback and power-cut undo."""
import argparse
import functools
import hashlib
import http.server
import importlib.util
import socket
import secrets
import shutil
import re
import struct
import subprocess
import threading
import time
from pathlib import Path
import boot_image

spec_failure = importlib.util.spec_from_file_location('kernel_failure', Path(__file__).with_name('kernel-failure-fixture.py'))
failure = importlib.util.module_from_spec(spec_failure); spec_failure.loader.exec_module(failure)

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py')
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
class Server(http.server.ThreadingHTTPServer): daemon_threads = True
class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args): pass

def check_esp(image, directory):
    part=boot_image.esp(image); volume=directory/'esp.fat'
    with image.open('rb') as src,volume.open('wb') as out:
        src.seek(part[2]*512); remaining=(part[3]-part[2]+1)*512
        while remaining:
            data=src.read(min(remaining,1048576))
            if not data: raise RuntimeError('short ESP image')
            out.write(data); remaining-=len(data)
    result=subprocess.run(['fsck.fat','-n',str(volume)],capture_output=True)
    (directory/'fsck-fat.log').write_bytes(result.stdout+result.stderr)
    if result.returncode==0:return True
    # Cosmos formats an EFI boot label without a directory label and leaves the
    # optional FAT32 free-count hint unknown. Accept only these recorded profile
    # warnings; any chain, directory, allocation, dirty-bit or I/O error still fails.
    patterns=[r'fsck\.fat [^\n]+',r"Label in boot sector is 'EFI', but there is no volume label in root directory\.",
        r'Auto-removing label from boot sector\.',r'Free cluster summary uninitialized \(should be \d+\)',
        r'Leaving filesystem unchanged\.',re.escape(str(volume))+r': \d+ files, \d+/\d+ clusters']
    lines=(result.stdout+result.stderr).decode(errors='replace').splitlines()
    return result.returncode==1 and all(not line.strip() or any(re.fullmatch(pattern,line.strip()) for pattern in patterns) for line in lines)

def main():
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('keep', type=Path); args = p.parse_args()
    keep = args.keep.resolve(); keep.mkdir(parents=True, exist_ok=True); served = keep/'served'; served.mkdir()
    subprocess.run(['dotnet','build',str(ROOT/'tools/Zenith.PackageTool'),'-c','Release'],check=True,stdout=subprocess.DEVNULL)
    tool = ['dotnet',str(ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll')]
    def run(*args): subprocess.run([*tool,*map(str,args)],check=True,stdout=subprocess.DEVNULL)
    run('keygen',keep/'keys'); key=(keep/'keys/public.pem').read_bytes(); (served/'key.pem').write_bytes(key)
    original=(ROOT/'output-x64/Zenith.elf').read_bytes(); original_id=hashlib.sha256(original).hexdigest()
    # Real ELF load paths, benign trailing release markers and a signed but broken startup fixture.
    good=original+b'\nZenith kernel upgrade acceptance release 1\n'; (keep/'good.elf').write_bytes(good)
    good_id=hashlib.sha256(good).hexdigest(); run('kernel-seal','1.0.0',keep/'good.elf',keep/'keys/private.pem',served/'one.zpk')
    run('kernel-check',served/'one.zpk',keep/'keys/public.pem')
    # A real full-size healthy ELF is still delivered/activated above. The broken
    # startup case needs only a valid signed ELF that executes its entry then loops.
    failure.build(keep/'bad.elf')
    run('kernel-seal','2.0.0',keep/'bad.elf',keep/'keys/private.pem',served/'bad.zpk')
    run('kernel-check',served/'bad.zpk',keep/'keys/public.pem')
    tiny=bytearray(128);tiny[:7]=b'\x7fELF\x02\x01\x01';tiny[16]=2;tiny[18]=62;tiny[20]=1;tiny[52]=64;tiny[54]=56;tiny[56]=1
    struct.pack_into('<QQ',tiny,24,0x1000,64);struct.pack_into('<IIQQQQQ',tiny,64,1,1,120,0x1000,0,8,8)
    (keep/'tiny.elf').write_bytes(tiny);run('kernel-seal','1.0.0',keep/'tiny.elf',keep/'keys/private.pem',keep/'tiny.zpk')
    tampered=bytearray((keep/'tiny.zpk').read_bytes());tampered[-1]^=1;(served/'tampered.zpk').write_bytes(tampered)
    server=Server(('127.0.0.1',0),functools.partial(Handler,directory=str(served))); threading.Thread(target=server.serve_forever,daemon=True).start()
    url=f'http://10.0.2.2:{server.server_port}'; checks=0; m=None; configured=False; secret="K!"+secrets.token_hex(8)
    def check(ok,label):
        nonlocal checks
        checks+=1; print(('ok   ' if ok else 'FAIL ')+label,flush=True)
        if not ok: raise RuntimeError(label)
    def boot(name,disk=None,argument=None,monitor=None,vga='none',verify=True):
        machine=h.Machine(keep/name,disk,argument,network=['-netdev','user,id=net0','-device','e1000e,netdev=net0'],monitor=monitor,vga=vga,verify=verify)
        check(machine.wait('headless ready',timeout=600),name+' headless boot')
        check(machine.wait('network: online address=10.0.2.15',timeout=90),name+' DHCP')
        if disk and configured:
            offset=len(machine.log());machine.send('login root\r');check(machine.wait('Password: ',offset),name+' login prompt')
            offset=len(machine.log());machine.send(secret+'\r');check(machine.wait('authenticated as root',offset),name+' authenticated')
        return machine
    def command(text,marker,timeout=600):
        off=len(m.log());m.send(text+'\r');check(m.wait(marker,off,timeout),marker);time.sleep(.2)
        raw=m.log()[off:];return raw+'\n'+h.efi.smoke.COSMOS_NOISE.sub('',raw)
    def status(text,name,code=0):
        raw=command(text+'; echo '+name+'=$? | logger','user: '+name+'=');check('user: '+name+'='+str(code) in raw,name+' expected status '+str(code));return raw
    def shutdown(reboot=False):
        command('reboot' if reboot else 'poweroff','all filesystems unmounted');check(m.process.wait(timeout=60)==0,'clean '+('reboot' if reboot else 'poweroff')+' without manual sync');disk=m.disk;m.stop();return disk
    def monitor_command(path,text):
        with socket.socket(socket.AF_UNIX) as sock:
            sock.connect(str(path));sock.recv(4096);sock.sendall((text+'\n').encode());time.sleep(.1)
    try:
        m=boot('live');status('pkg kernel install /missing','K_LIVE_DENIED',1)
        status('session set-default headless','K_SESSION');status('install sata0 --yes','K_INSTALL')
        base=shutdown();m=boot('prepared',base)
        status('umount /boot/efi','K_ESP_UNMOUNT')
        status('boot status','K_MISSING_ESP_REFUSED',1)
        status('mount sata0p0 /boot/efi','K_ESP_REMOUNT')
        status('boot status','K_ESP_RESTORED')
        for remote,path in [('key.pem','/root/key.pem'),('one.zpk','/root/one.zpk'),('tampered.zpk','/root/tampered.zpk'),('bad.zpk','/root/bad.zpk')]:
            status(f'fetch --kernel {url}/{remote} {path}','K_FETCH_'+remote.replace('.','_'))
        status('pkg trust /root/key.pem '+hashlib.sha256(key).hexdigest(),'K_TRUST')
        status('pkg kernel check /root/tampered.zpk','K_TAMPERED',1);status('pkg install /root/one.zpk','K_DATA_REFUSED',1)
        command('passwd root','New password: ');command(secret,'Repeat password: ');command(secret,'password updated for root');configured=True
        status('su user','K_USER');status('pkg kernel install /root/one.zpk','K_USER_DENIED',1)
        command('login root','Password: ');command(secret,'authenticated as root')
        status('echo persistent > /root/kernel-proof','K_PROOF');prepared=shutdown()
        # Real guest termination after durable staging, and during menu rename with no primary menu.
        for phase in ('staged','menu-gap'):
            m=boot('cut-'+phase,prepared,'session=headless kernel-test-stop='+phase)
            off=len(m.log());m.send('pkg kernel install /root/one.zpk\r')
            check(m.wait('power-cut checkpoint '+phase,off,600),'durable '+phase+' checkpoint')
            m.process.kill();m.process.wait(timeout=30);disk=m.disk;m.stop()
            if phase=='staged':
                # Negative fixture only: corrupt a copy's root count; never repair boot artifacts.
                corrupt=keep/'corrupt-root.img';shutil.copyfile(disk,corrupt)
                root_id=boot_image.verify(corrupt)['root'];part=next(p for p in boot_image.partitions(corrupt) if str(p[1])==root_id)
                with corrupt.open('r+b') as volume:
                    position=part[2]*512+1024+12;volume.seek(position);count=struct.unpack('<I',volume.read(4))[0]
                    volume.seek(position);volume.write(struct.pack('<I',count+1))
                m=h.Machine(keep/'corrupt-dirty-root',corrupt)
                check(m.wait('requested root UUID is unavailable or invalid',timeout=180),'corrupt dirty root refused before writable startup')
                check('headless ready' not in m.log(),'corrupt root never starts a session');m.stop()
                # External ISO against an unchanged interrupted disk, with no host repair.
                m=h.Machine(keep/'iso-recovery',extra_disks=(disk,),network=['-netdev','user,id=net0','-device','e1000e,netdev=net0'])
                check(m.wait('headless ready',timeout=600),'external ISO boots checked interrupted root')
                command('login root','Password: ');command(secret,'authenticated as root')
                result=status('boot status','K_ISO_RECOVERY_STATUS')
                check('kernel-recovery-required=true' in result,'ISO sees durable undo on installed disk')
                status('boot recover','K_ISO_RECOVER');shutdown();boot_image.verify(m.directory/'secondary.img')
            if phase=='menu-gap':
                # No host boot-file repairs: Limine must find the retained secondary configuration.
                m=boot('recover-'+phase,disk,verify=False)
            else:m=boot('recover-'+phase,disk)
            result=status('boot status','K_UNDO_STATUS');check('running-kernel='+original_id in result,'old kernel boots after '+phase+' cut')
            check('kernel-recovery-required=true' in result,'durable undo survives '+phase+' cut')
            status('boot recover','K_RECOVER');result=status('boot status','K_RECOVERED_STATUS')
            check('next-kernel='+original_id in result and 'kernel-recovery-required=false' in result,'undo restores old activation')
            status('test ! -e /boot/efi/boot/zenith/'+good_id+'/kernel.elf','K_CANDIDATE_REMOVED')
            shutdown()
        m=boot('upgrade',prepared);status('pkg kernel install /root/one.zpk','K_UPDATE')
        result=status('boot status','K_PENDING');check('running-kernel='+original_id in result and 'next-kernel='+good_id in result,'loaded old kernel remains after install')
        check('pending-confirmation=true' in result and 'reboot-required=true' in result,'activation awaits reboot and confirmation')
        status('boot confirm','K_EARLY_CONFIRM',1);disk=shutdown(reboot=True);boot_image.verify(disk)
        m=boot('new-kernel',disk);result=status('boot status','K_NEW');check('running-kernel='+good_id in result,'new signed ELF actually loaded')
        result=status('cat /root/kernel-proof | logger','K_PERSIST');check('user: persistent' in result,'user data survives kernel activation')
        status('boot confirm','K_CONFIRM');status('pkg kernel install /root/one.zpk','K_REPEAT_REFUSED',1)
        result=status('boot status','K_CONFIRMED');check('pending-confirmation=false' in result and 'previous-kernel='+original_id in result,'confirmed with previous retained')
        status('boot rollback','K_ROLLBACK');disk=shutdown();m=boot('previous',disk)
        result=status('boot status','K_PREVIOUS');check('running-kernel='+original_id in result,'rollback actually boots retained kernel')
        status('boot confirm','K_ROLLBACK_CONFIRM')
        status('pkg kernel install /root/one.zpk','K_ROLLBACK_NO_DOWNGRADE',1)
        status('pkg kernel install /root/bad.zpk','K_BAD_STAGE');disk=shutdown()
        # This signed image has valid ELF bounds but loops at entry: no healthy Zenith startup.
        m=h.Machine(keep/'broken',disk,network=['-netdev','user,id=net0','-device','e1000e,netdev=net0'])
        check(m.wait(failure.MARKER,timeout=180),'signed broken ELF entry actually executes')
        check(not m.wait('headless ready',timeout=20),'signed broken kernel cannot complete startup');m.stop()
        # Select the real second entry using firmware/bootloader keyboard input, not host file editing.
        selected=False
        for attempt,delay in enumerate((3,5,1)):
            mon=keep/f'fallback-{attempt}'/'monitor.sock';m=h.Machine(keep/f'fallback-{attempt}',disk,network=['-netdev','user,id=net0','-device','e1000e,netdev=net0'],monitor=mon,vga='std')
            time.sleep(delay);monitor_command(mon,'screendump '+str(keep/f'fallback-menu-{attempt}.ppm'));monitor_command(mon,'sendkey down');monitor_command(mon,'sendkey ret')
            if m.wait('headless ready',timeout=60):selected=True;break
            m.stop()
        check(selected,'manual previous entry boots after failed new kernel')
        command('login root','Password: ');command(secret,'authenticated as root')
        result=status('boot status','K_FAILED_BOOT_FALLBACK');check('running-kernel='+original_id in result,'fallback uses exact previous generation')
        status('boot rollback','K_BAD_ROLLBACK');status('boot confirm','K_PREVIOUS_CONFIRM');final=shutdown();boot_image.verify(final)
        # Independent ext2 check and unchanged bootloader identity.
        root_id=boot_image.verify(final)['root'];part=next(p for p in boot_image.partitions(final) if str(p[1])==root_id);volume=keep/'root.ext2'
        with final.open('rb') as src,volume.open('wb') as out:
            src.seek(part[2]*512);remaining=(part[3]-part[2]+1)*512
            while remaining:data=src.read(min(remaining,1048576));out.write(data);remaining-=len(data)
        fsck=subprocess.run(['e2fsck','-f','-n',str(volume)],capture_output=True);(keep/'e2fsck.log').write_bytes(fsck.stdout+fsck.stderr);check(fsck.returncode==0,'independent clean installed ext2 after upgrades')
        check(check_esp(final,keep),'EFI FAT allocation/directory audit (known formatter warnings only)')
        check(all(secret not in p.read_text(errors='replace') for p in keep.glob('*/serial.log')),'credentials absent from transcripts')
        print(f'{checks}/{checks} kernel checks passed',flush=True)
    finally:
        if m:m.stop()
        server.shutdown();server.server_close()
if __name__=='__main__':main()
