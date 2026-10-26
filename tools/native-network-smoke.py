#!/usr/bin/env python3
"""Signed native network clients, verified TLS and owned cleanup on disposable disks."""
import functools
import hashlib
import http.server
import importlib.util
from pathlib import Path
import socketserver
import subprocess
import sys
import secrets
import threading
import time

from tls_fixture import certificates, context

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('headless',ROOT/'tools/headless-smoke.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)

class Server(http.server.ThreadingHTTPServer): daemon_threads=True
class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self,*args): pass
    def do_GET(self):
        if self.path=='/redirect':
            self.send_response(302);self.send_header('Location','/good');self.send_header('Content-Length','0');self.end_headers();return
        if self.path=='/bad-length':
            self.send_response(200);self.send_header('Content-Length','9999999999999');self.end_headers();return
        if self.path=='/truncated':
            self.send_response(200);self.send_header('Content-Length','10');self.end_headers();self.wfile.write(b'bad');return
        if self.path=='/slow':
            self.send_response(200);self.send_header('Content-Length',str(2*1024*1024));self.end_headers()
            try:self.wfile.write(b'x'*524288);self.wfile.flush();time.sleep(40)
            except (BrokenPipeError,ConnectionResetError):pass
            return
        super().do_GET()


class Dns(socketserver.BaseRequestHandler):
    def handle(self):
        data,sock=self.request
        if len(data)<12:return
        reply=bytearray(data);reply[2:4]=b'\x85\x80';reply[6:8]=b'\x00\x01'
        reply.extend(b'\xc0\x0c\x00\x01\x00\x01\x00\x00\x00\x3c\x00\x04\x0a\x00\x02\x02')
        bad=bytearray(reply);bad[0]^=1;sock.sendto(bad,self.client_address)
        if b'reject' not in data:sock.sendto(reply,self.client_address)

class UdpServer(socketserver.ThreadingUDPServer):daemon_threads=True


def main():
    keep=Path(sys.argv[1]);keep.mkdir(parents=True,exist_ok=False)
    tool=ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'
    subprocess.run(['dotnet','build',str(ROOT/'tools/Zenith.PackageTool'),'-c','Release'],check=True,stdout=subprocess.DEVNULL)
    def package(*args):subprocess.run(['dotnet',str(tool),*map(str,args)],check=True,capture_output=True)
    served=keep/'served';served.mkdir();(served/'good').write_bytes(b'NETWORK_OK\n')
    subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(keep/'source'),'--program','net-tools','--version','0.1.0'],check=True)
    package('keygen',keep/'keys');key=(keep/'keys/public.pem').read_bytes();(served/'key.pem').write_bytes(key)
    package('build-native','zenith-net-tools','0.1.0',keep/'source',keep/'bundle','--repo','core')
    manifest=(keep/'bundle/manifest.tsv').read_text();assert 'requires\tzenith-native-network-abi\t1.0\n' in manifest,'network capability missing'
    package('seal',keep/'bundle',keep/'keys/private.pem',served/'net-tools.zpk')
    subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(keep/'upgrade-source'),'--program','net-tools','--version','0.1.1'],check=True)
    package('build-native','zenith-net-tools','0.1.1',keep/'upgrade-source',keep/'upgrade-bundle','--repo','core')
    package('seal',keep/'upgrade-bundle',keep/'keys/private.pem',served/'net-update.zpk')
    package('kernel-seal','1.0.0',ROOT/'output-x64/Zenith.elf',keep/'keys/private.pem',served/'kernel.zpk')
    ca,_=certificates(keep/'tls');(served/'tls.pem').write_bytes(ca)
    handler=functools.partial(Handler,directory=str(served))
    plain=Server(('127.0.0.1',0),handler);secure=Server(('127.0.0.1',0),handler);expired=Server(('127.0.0.1',0),handler)
    secure.socket=context(keep/'tls').wrap_socket(secure.socket,server_side=True)
    expired.socket=context(keep/'tls','expired').wrap_socket(expired.socket,server_side=True)
    dns=UdpServer(('127.0.0.1',0),Dns)
    for server in (plain,secure,expired,dns):threading.Thread(target=server.serve_forever,daemon=True).start()
    http='http://10.0.2.2:'+str(plain.server_port);https='https://10.0.2.2:'+str(secure.server_port)
    m=None;checks=0;secret="NET!"+secrets.token_hex(8)
    def command(text,marker,timeout=180):
        nonlocal checks
        offset=len(m.log());m.send(text+'\r')
        assert m.wait(marker,offset,timeout),marker+'; serial='+str(m.serial)
        checks+=1;print('PASS '+marker,flush=True)
    def status(text,label,code=0,timeout=180):command(text+'; echo '+label+'=$? | logger','user: '+label+'='+str(code),timeout)
    def boot(name,disk=None):
        nonlocal m
        m=h.Machine(keep/name,disk,network=['-netdev','user,id=net0','-device','e1000e,netdev=net0'])
        assert m.wait('headless ready',timeout=180),'boot';assert m.wait('network: online address=10.0.2.15',timeout=90),'DHCP'
    try:
        boot('live');status('session set-default headless','NET_SESSION');status('install sata0 --yes','NET_INSTALL',timeout=600)
        command('poweroff','all filesystems unmounted');m.process.wait(timeout=30);disk=m.disk;m.stop();m=None
        boot('installed',disk)
        status('fetch '+http+'/key.pem /root/net-key.pem','NET_KEY')
        status('pkg trust /root/net-key.pem '+hashlib.sha256(key).hexdigest(),'NET_TRUST')
        status('fetch '+http+'/tls.pem /root/net-tls.pem','NET_CA_FETCH')
        status('tls trust /root/net-tls.pem '+hashlib.sha256(ca).hexdigest(),'NET_CA')
        status('fetch '+http+'/net-tools.zpk /root/net-tools.zpk; pkg install /root/net-tools.zpk','NET_PACKAGE')
        command('which fetch | logger','user: fetch: signed native net-tools')
        command('net-tools --version | logger','user: net-tools 0.1.0')
        status('net-tools --abi-bounds','NET_ABI_BOUNDS')
        status('ip','NET_IP');status('ifconfig','NET_IFCONFIG')
        command('nslookup 10.0.2.2 | logger','user: 10.0.2.2 address=10.0.2.2')
        command('nslookup mirror.home 10.0.2.2 '+str(dns.server_address[1])+' | logger','user: mirror.home address=10.0.2.2')
        status('nslookup reject.home 10.0.2.2 '+str(dns.server_address[1]),'NET_DNS_CORRELATION',1)
        status('echo 10.0.2.2 mirror.home >> /etc/hosts','NET_HOST_ALIAS')
        status('ping 10.0.2.2','NET_PING')
        status('nc -z 10.0.2.2 '+str(plain.server_port),'NET_PROBE')
        status('net-tools --connect-bounds 10.0.2.2 '+str(plain.server_port),'NET_HANDLE_BOUNDS')
        status('mkdir -p /home/user/net; chown 1000:1000 /home/user/net','NET_DIRECTORY')
        status('fetch '+http+'/good /home/user/net/plain','NET_HTTP')
        command('cat /home/user/net/plain | logger','user: NETWORK_OK')
        status('fetch '+https+'/good /home/user/net/secure','NET_HTTPS')
        command('cat /home/user/net/secure | logger','user: NETWORK_OK')
        status('fetch https://mirror.home:'+str(secure.server_port)+'/good /home/user/net/hostname','NET_HTTPS_HOSTNAME')
        status('fetch '+https+'/good /home/user/net/secure','NET_NO_REPLACE',1)
        status('fetch --replace '+https+'/good /home/user/net/secure','NET_REPLACE')
        status('ln -s /home/user/net/secure /home/user/net/link','NET_LINK_SETUP')
        status('fetch --replace '+http+'/good /home/user/net/link','NET_LINK_REFUSED',1)
        command('cat /home/user/net/secure | logger','user: NETWORK_OK')
        status('echo 10.0.2.2 mismatch.home >> /etc/hosts','NET_HOSTNAME_SETUP')
        status('fetch --replace https://mismatch.home:'+str(secure.server_port)+'/good /home/user/net/secure','NET_HOSTNAME_DENIED',1)
        command('cat /home/user/net/secure | logger','user: NETWORK_OK')
        for path,label in (('/missing','MISSING'),('/redirect','REDIRECT'),('/bad-length','LENGTH'),('/truncated','TRUNCATED')):
            status('fetch --replace '+http+path+' /home/user/net/secure','NET_'+label,1)
            command('cat /home/user/net/secure | logger','user: NETWORK_OK')
        status('fetch --replace https://10.0.2.2:'+str(expired.server_port)+'/good /home/user/net/secure','NET_EXPIRED',1)
        command('cat /home/user/net/secure | logger','user: NETWORK_OK')
        status('net-tools --fault-connected 10.0.2.2 '+str(plain.server_port),'NET_FAULT',139)
        status('nc -z 10.0.2.2 '+str(plain.server_port),'NET_AFTER_FAULT')
        status('net-tools --abandon-sink /home/user/net/secure','NET_STAGING_FAULT',139)
        command('cat /home/user/net/secure | logger','user: NETWORK_OK')
        command("find /home/user/net -name '*.native-fetch-*' | wc -l | logger",'user: 0')
        offset=len(m.log());m.send('fetch '+http+'/slow /home/user/net/cancelled\r')
        assert m.wait('fetch progress ',offset,120),'slow fetch progress'
        off=len(m.log());m.send('\x03');assert m.wait('[console]',off,60),'cancelled fetch returns to prompt'
        command('echo NET_CANCEL=$? | logger','user: NET_CANCEL=130')
        status('test ! -e /home/user/net/cancelled','NET_CANCEL_NO_DESTINATION')
        command("find /home/user/net -name '*.native-fetch-*' | wc -l | logger",'user: 0')
        status('nc -z 10.0.2.2 '+str(plain.server_port),'NET_AFTER_CANCEL')
        offset=len(m.log())
        status('net-tools --cancel-connected 10.0.2.2 '+str(plain.server_port)+' &','NET_BACKGROUND')
        assert m.wait('NETWORK_READ_READY',offset,30),'background network read readiness'
        status('nc -z 10.0.2.2 '+str(plain.server_port),'NET_BUSY',1)
        status('kill %1; wait %1','NET_BACKGROUND_CANCEL',130)
        status('nc -z 10.0.2.2 '+str(plain.server_port),'NET_AFTER_BACKGROUND')
        # Read and write a real full-size signed kernel through the new transport
        # and sink profile, then verify its signature and complete ELF payload.
        status('fetch --kernel '+https+'/kernel.zpk /root/native-kernel.zpk','NET_FULL_KERNEL',timeout=900)
        status('pkg kernel check /root/native-kernel.zpk','NET_KERNEL_VERIFY',timeout=180)
        status('fetch '+http+'/net-update.zpk /root/net-update.zpk','NET_UPGRADE_FETCH')
        status('pkg install /root/net-update.zpk','NET_UPGRADE')
        command('net-tools --version | logger','user: net-tools 0.1.1')
        # Ordinary-user network use must retain VFS permissions; no USER env bypass.
        command('passwd root','New password: ')
        offset=len(m.log());m.send(secret+'\r');assert m.wait('Repeat password: ',offset,180),'password confirmation'
        offset=len(m.log());m.send(secret+'\r');assert m.wait('password updated for root',offset,600),'password completion'
        status('su user','NET_USER')
        status('fetch '+https+'/good /home/user/net/user-file','NET_USER_DOWNLOAD')
        command('cat /home/user/net/user-file | logger','user: NETWORK_OK')
        status('export USER=root; fetch '+https+'/good /root/net-stolen','NET_USER_DENIED',1)
        status('fetch --replace '+https+'/good /etc/zenith/tls-ca.pem','NET_USER_CA_WRITE_DENIED',1)
        status('fetch '+https+'/good /home/user/net/still-trusted','NET_USER_CA_PRESERVED')
        status('tls trust /root/net-tls.pem '+hashlib.sha256(ca).hexdigest(),'NET_TRUST_DENIED',1)
        command('su root','Password: ')
        offset=len(m.log());m.send(secret+'\r');assert m.wait('authenticated as root',offset,600),'root authentication'
        command('poweroff','all filesystems unmounted');m.process.wait(timeout=30)
        import boot_image
        state=boot_image.verify(m.disk);assert state['current']==hashlib.sha256((ROOT/'output-x64/Zenith.elf').read_bytes()).hexdigest(),'network package changed kernel'
        part=next(p for p in boot_image.partitions(m.disk) if str(p[1])==state['root'])
        with m.disk.open('rb') as src,(keep/'root.ext2').open('wb') as out:
            src.seek(part[2]*512);remaining=(part[3]-part[2]+1)*512
            while remaining:
                data=src.read(min(remaining,1048576));assert data;out.write(data);remaining-=len(data)
        audit=subprocess.run(['e2fsck','-fn',str(keep/'root.ext2')],capture_output=True);(keep/'e2fsck.log').write_bytes(audit.stdout+audit.stderr)
        assert audit.returncode==0,'independent ext2 audit'
        assert all(secret not in p.read_text(errors='replace') for p in keep.glob('*/serial.log')),'secret in transcript'
        print(str(checks)+' native network checks passed; owned cleanup, TLS and ext2 audit passed',flush=True)
    finally:
        if m:m.stop()
        for server in (plain,secure,expired,dns):server.shutdown();server.server_close()

if __name__=='__main__':main()
