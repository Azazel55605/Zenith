#!/usr/bin/env python3
"""Focused 1/2/4-CPU process latency comparison; not full release acceptance.

Copies the clean live-installed baseline and signed Sunbeam fixture from a completed
native-process gate. Each disposable clone installs the same payload. No configured
password is bypassed: the baseline predates account/password tests.
"""
import argparse
import functools
import hashlib
import http.server
import importlib.util
import json
from pathlib import Path
import statistics
import sys
import threading
import time
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'tools'))
spec=importlib.util.spec_from_file_location('latency_headless',ROOT/'tools/headless-smoke.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('keep',type=Path);p.add_argument('--source',type=Path,required=True);a=p.parse_args()
    a.keep.mkdir(parents=True,exist_ok=False)
    source=a.source.resolve();disk=source/'live/disk.img';served=source/'served'
    import boot_image
    boot_image.verify(disk,ROOT/'output-x64/Zenith.elf')
    key=(served/'key.pem').read_bytes()
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self,*args):pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)))
    threading.Thread(target=server.serve_forever,daemon=True).start()
    url='http://10.0.2.2:'+str(server.server_port);results={}
    try:
        for cpus in (1,2,4):
            m=h.Machine(a.keep/str(cpus),disk,argument='smp=native',cpus=cpus,network=['-netdev','user,id=n','-device','e1000e,netdev=n'])
            def status(text,label):
                off=len(m.log());started=time.monotonic()
                m.send(text+'; echo '+label+'=$? | logger\r')
                assert m.wait('user: '+label+'=0',off,timeout=120),label
                return time.monotonic()-started,m.log()[off:]
            try:
                assert m.wait('headless ready',timeout=120),'latency boot'
                assert m.wait('network: online address=10.0.2.15',timeout=60),'latency DHCP'
                status('fetch '+url+'/key.pem /root/key.pem','L_KEY')
                status('pkg trust /root/key.pem '+hashlib.sha256(key).hexdigest(),'L_TRUST')
                status('fetch '+url+'/sunbeam.zpk /root/sunbeam.zpk','L_FETCH')
                status('pkg install /root/sunbeam.zpk','L_INSTALL')
                if cpus>1:status('cpu managed-hold | logger','L_HELD')
                samples=[]
                for run in range(6):
                    elapsed,raw=status('sunbeam --stream-bytes | sunbeam cat | sunbeam wc -c | logger','L_PIPE_'+str(run))
                    assert raw.count('user: 163840')==1,'exact pipeline output'
                    if run:samples.append(round(elapsed,3))
                if cpus>1:status('cpu managed-release | logger','L_RELEASED')
                results[cpus]=dict(seconds=samples,median=statistics.median(samples),managed_ap_roots=cpus-1)
                off=len(m.log());m.send('poweroff\r')
                assert m.wait('all filesystems unmounted',off,timeout=60),'latency clean shutdown'
                assert m.process.wait(timeout=30)==0,'latency poweroff'
                print('PASS pipeline latency '+str(cpus)+' CPUs: '+str(results[cpus]),flush=True)
            finally:
                if m.process.poll() is None:m.process.terminate();m.process.wait(timeout=10)
        (a.keep/'latency.json').write_text(json.dumps(dict(results=results,
            measurement='Host CLI roundtrip including serial input, process/pipeline and status; first sample warm-up excluded',
            elf_sha256=hashlib.sha256((ROOT/'output-x64/Zenith.elf').read_bytes()).hexdigest()),indent=2)+'\n')
    finally:server.shutdown();server.server_close()
if __name__=='__main__':main()
