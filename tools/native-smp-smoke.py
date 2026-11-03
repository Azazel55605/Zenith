#!/usr/bin/env python3
"""Installed protected process migration, isolated services and concurrent CPU work."""
import argparse,functools,hashlib,http.server,importlib.util,json,re,subprocess,sys,threading,time
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'tools'))
def load(name,path):
    s=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('keep',type=Path);p.add_argument('--cpus',nargs='+',type=int,choices=range(1,9),default=[1,2,4]);p.add_argument('--prepared',type=Path,help='copy a disposable installed fixture, verify its exact kernel and upgrade only test payloads');a=p.parse_args()
    if len(set(a.cpus))!=len(a.cpus):p.error('each CPU count must have a distinct disposable fixture')
    keep=a.keep;keep.mkdir(parents=True,exist_ok=False)
    h=load('h',ROOT/'tools/headless-smoke.py');load('accel',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    tool=['dotnet',str(ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll')]
    def package(*args):subprocess.run([*tool,*map(str,args)],check=True,stdout=subprocess.DEVNULL)
    version='0.1.1' if a.prepared else '0.1.0'
    subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(keep/'payload'),'--version',version],check=True)
    served=keep/'served';served.mkdir();package('keygen',keep/'keys');key=(keep/'keys/public.pem').read_bytes();(served/'key.pem').write_bytes(key)
    package('build-native','sunbeam-tools',version,keep/'payload',keep/'bundle');package('seal',keep/'bundle',keep/'keys/private.pem',served/'test.zpk')
    subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(keep/'net-payload'),'--program','net-tools','--version',version],check=True)
    package('build-native','zenith-net-tools',version,keep/'net-payload',keep/'net-bundle','--repo','core');package('seal',keep/'net-bundle',keep/'keys/private.pem',served/'net.zpk')
    (served/'data').write_bytes(b'SMP_NETWORK_DATA\n'*4096)
    for cpus in a.cpus:
        masks=[1 if cpus==1 else 1<<(1+i%(cpus-1)) for i in range(3)]
        (served/('work'+str(cpus))).write_text('\n'.join('export ZENITH_CPU_MASK='+str(masks[i])+'\nnative run sunbeam --smp-work > /root/w'+str(i)+' &' for i in range(3))+'\nwait\ncat /root/w0 /root/w1 /root/w2 | logger\n')
    (served/'io').write_text('native run sunbeam --smp-work > /root/w0 &\nnative run net-tools fetch '+ 'URL'+'/data /root/download &\nprintf "PRIVATE_IO\\n" | sunbeam tee /root/io | sunbeam cat | logger\nwait\nwc -c /root/download | logger\ncat /root/w0 | logger\n')
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self,*args):pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)));threading.Thread(target=server.serve_forever,daemon=True).start()
    net=['-netdev','user,id=n','-device','e1000e,netdev=n'];url='http://10.0.2.2:'+str(server.server_port);m=None
    fetch='recovery fetch --replace ' if a.prepared else 'fetch '
    (served/'io').write_text((served/'io').read_text().replace('URL',url))
    def status(text,label,expected=0,timeout=180):
        off=len(m.log());m.send(text+'; echo '+label+'=$? | logger\r')
        assert m.wait('user: '+label+'='+str(expected),off,timeout),label+'; '+str(m.serial)
        print('PASS '+label,flush=True);return off
    def poweroff():
        off=len(m.log());m.send('poweroff\r');assert m.wait('all filesystems unmounted',off,120);assert m.process.wait(timeout=30)==0
        m.stop()
        return m.disk
    def audit(disk):
        import boot_image
        state=boot_image.verify(disk)
        part=next(p for p in boot_image.partitions(disk) if str(p[1])==state['root'])
        root=disk.with_name('root.ext2')
        with disk.open('rb') as source,root.open('wb') as destination:
            source.seek(part[2]*512);remaining=(part[3]-part[2]+1)*512
            while remaining:
                data=source.read(min(1024*1024,remaining));assert data,'short root image'
                destination.write(data);remaining-=len(data)
        result=subprocess.run(['e2fsck','-fn',str(root)],capture_output=True)
        root.with_suffix('.audit.log').write_bytes(result.stdout+result.stderr)
        assert result.returncode==0,'independent root filesystem audit: '+str(root)
    try:
        if a.prepared:
            import boot_image
            boot_image.verify(a.prepared,ROOT/'output-x64/Zenith.elf');disk=a.prepared
            print('Original fixture preserved: '+str(a.prepared),flush=True)
        else:
            m=h.Machine(keep/'live',network=net);assert m.wait('headless ready',timeout=180)
            status('session set-default headless','P_SESSION');status('install sata0 --yes','P_INSTALL');disk=poweroff()
        m=h.Machine(keep/'setup',disk,network=net);assert m.wait('headless ready',timeout=120)
        status(fetch+url+'/key.pem /root/key.pem','P_KEY');status('pkg trust /root/key.pem '+hashlib.sha256(key).hexdigest(),'P_TRUST')
        status(fetch+url+'/test.zpk /root/test.zpk','P_FETCH');status('pkg install /root/test.zpk','P_PACKAGE');disk=poweroff()
        m=h.Machine(keep/'extra',disk,network=net);assert m.wait('headless ready',timeout=120)
        status(fetch+url+'/net.zpk /root/net.zpk','P_FETCH_NET');status('pkg install /root/net.zpk','P_NET_PACKAGE')
        for name in tuple('work'+str(c) for c in a.cpus)+('io',):status(fetch+url+'/'+name+' /root/'+name,'P_SCRIPT_'+name)
        disk=poweroff()
        results={}
        for cpus in a.cpus:
            m=h.Machine(keep/str(cpus),disk,argument='smp=native',network=net,cpus=cpus,monitor=keep/str(cpus)/'monitor.sock');assert m.wait('headless ready',timeout=120)
            mask=1 if cpus==1 else sum(1<<i for i in range(1,cpus))
            status('export ZENITH_CPU_MASK=0; native run sunbeam echo MUST_NOT_RUN','P_INVALID_MASK',1)
            if cpus<8:status('export ZENITH_CPU_MASK=128; native run sunbeam echo MUST_NOT_RUN','P_MISSING_OWNER',95)
            status('export ZENITH_CPU_MASK='+str(mask),'P_MASK')
            status('native run sunbeam echo AP_HELLO > /root/hello','P_HELLO',timeout=60)
            status('cat /root/hello | logger','P_HELLO_READ')
            off=status('native run sunbeam --smp-probe > /root/probe; cat /root/probe | logger','P_PROBE',timeout=120)
            raw=m.log()[off:];seen=set(map(int,re.findall(r'user: SMP_CPU=(\d+)',raw)))
            assert seen and seen<=({0} if cpus==1 else set(range(1,cpus))),'affinity / actual CPU witness: '+str(seen)
            # Warm, short contexts may legally remain on one owner. Actual
            # Ready transfers are forced in the production queue tests; this
            # guest witness measures movement without demanding pointless moves.
            status('native run sunbeam --cpu-state','P_FP')
            status('native run sunbeam --smp-state','P_REGISTER_FP')
            status('native run sunbeam --fs-cpu-state /etc/hostname','P_SERVICE_FP')
            measurements=[]
            for round in range(3):
                start=time.monotonic();off=status('sh /root/work'+str(cpus),'P_WORK_'+str(round),timeout=180)
                raw=m.log()[off:];records=[tuple(map(int,x)) for x in re.findall(r'user: SMP_WORK cpu=(\d+) start=(\d+) end=(\d+) hash=(\d+)',raw)]
                # Independent Python reference: FNV recurrence for i=0..19999999,
                # explicitly wrapping to 64 bits after each multiplication.
                assert len(records)==3 and {r[3] for r in records}=={6678758953595806245},'complete equal-work checksums: '+str(records)
                assert all((mask&(1<<r[0])) and r[2]>r[1] for r in records),'work affinity / timing'
                overlap=any(a[0]!=b[0] and max(a[1],b[1])<min(a[2],b[2]) for i,a in enumerate(records) for b in records[i+1:])
                if cpus>2:assert overlap,'no simultaneous useful user execution: '+str(records)
                measurements.append(dict(seconds=time.monotonic()-start,records=records,overlap=overlap))
            off=status('sh /root/io','P_IO',timeout=180)
            raw=m.log()[off:];assert 'PRIVATE_IO' in raw and '69632 /root/download' in raw,'concurrent files/network output'
            status('native run sunbeam --fault','P_FAULT',139)
            status('native run sunbeam --privileged','P_PRIV',139)
            status('native run sunbeam --nx','P_NX',139)
            status('native run sunbeam --code-write','P_CODE',139)
            status('native run sunbeam --bad-pointer','P_BAD_POINTER')
            status('native run sunbeam --loop','P_TIMEOUT',124,timeout=60)
            status('native run sunbeam echo AFTER_FAULT | logger','P_AFTER')
            if cpus>1:
                off=len(m.log());m.send('native run sunbeam --loop\r');time.sleep(.15);cancelled_at=time.monotonic();m.send('\x03')
                assert m.wait('^C',off,30),'running AP cancellation signal'
                assert time.monotonic()-cancelled_at<3,'AP cancellation waited for execution timeout'
                off=len(m.log());m.send('echo P_CANCEL=$? | logger\r')
                assert m.wait('user: P_CANCEL=130',off,30),'running AP cancellation acknowledgement'
                status('native run sunbeam --smp-state','P_AFTER_CANCEL')
            off=status('ps --cpu | logger','P_PLACEMENT')
            results[cpus]=dict(observed=sorted(seen),migration_observed=len(seen)>1,work=measurements)
            audit(poweroff());print('PASS protected CPU placement/migration and containment at '+str(cpus)+' CPUs',flush=True)
        if 4 in a.cpus:
            m=h.Machine(keep/'disabled',disk,argument='session=headless',network=net,cpus=4)
            assert m.wait('headless ready',timeout=120)
            off=status('native run sunbeam --smp-probe > /root/probe; cat /root/probe | logger','P_DISABLED')
            assert set(map(int,re.findall(r'user: SMP_CPU=(\d+)',m.log()[off:])))=={0},'default disabled SMP changed'
            audit(poweroff());print('PASS default-disabled four-CPU fallback',flush=True)
        (keep/'results.json').write_text(json.dumps(results,indent=2)+'\n')
    finally:
        if m is not None and m.process.poll() is None:m.stop()
        server.shutdown();server.server_close()
if __name__=='__main__':main()
