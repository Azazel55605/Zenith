#!/usr/bin/env python3
"""H2 real HDA output/capture DMA, bounded native PCM ABI and owner teardown.

No host sound device or microphone is accessed. Capture uses QEMU's silent backend;
output is independently inspected from the WAV backend (which has no input driver).
"""
import functools,hashlib,http.server,importlib.util,struct,subprocess,sys,threading
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'tools'))
spec=importlib.util.spec_from_file_location('h',ROOT/'tools/headless-smoke.py');h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)

def main():
    keep=Path(sys.argv[1]);keep.mkdir(parents=True,exist_ok=True)
    tool=ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'
    subprocess.run(['dotnet','build',str(ROOT/'tools/Zenith.PackageTool'),'-c','Release','--nologo'],check=True,stdout=subprocess.DEVNULL)
    def run(*args):subprocess.run(['dotnet',str(tool),*map(str,args)],check=True,stdout=subprocess.DEVNULL)
    source=keep/'payload';subprocess.run([sys.executable,str(ROOT/'tools/build-userland.py'),str(source),'--program','audio-probe'],check=True)
    served=keep/'served';served.mkdir();run('keygen',keep/'keys')
    run('build-native','zenith-audio-probe','0.1.0',source,keep/'bundle','--repo','extra');run('seal',keep/'bundle',keep/'keys/private.pem',served/'audio.zpk')
    key=(keep/'keys/public.pem').read_bytes();(served/'key.pem').write_bytes(key)
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self,*args):pass
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)));threading.Thread(target=server.serve_forever,daemon=True).start()
    url='http://10.0.2.2:'+str(server.server_port);net=['-netdev','user,id=net0','-device','e1000e,netdev=net0'];machine=None
    def boot(name,disk=None,audio=None):
        nonlocal machine
        options=[] if audio is None else ['-audiodev',audio,'-device','intel-hda','-device','hda-duplex,audiodev=sound']
        machine=h.Machine(keep/name,disk,network=net+options,cpus=4 if disk else 1)
        assert machine.wait('headless ready',timeout=180),'audio boot '+name
    def status(text,label,code=0,timeout=90):
        off=len(machine.log());machine.send(text+'; echo '+label+'=$? | logger\r')
        assert machine.wait('user: '+label+'='+str(code),off,timeout),label+'; see '+str(machine.serial)
        print('PASS '+label+'='+str(code),flush=True)
    def stop():
        nonlocal machine
        off=len(machine.log());machine.send('poweroff\r');assert machine.wait('all filesystems unmounted',off,60)
        assert machine.process.wait(timeout=30)==0;disk=machine.disk;machine.stop();machine=None;return disk
    try:
        boot('live')
        status('session set-default headless','A_SESSION');status('install sata0 --yes','A_INSTALL',timeout=300);disk=stop()
        boot('capture',disk,'none,id=sound')
        assert 'endpoints=2' in machine.log(),'real codec endpoints'
        status('fetch '+url+'/key.pem /root/key.pem','A_KEY');status('pkg trust /root/key.pem '+hashlib.sha256(key).hexdigest(),'A_TRUST')
        status('fetch '+url+'/audio.zpk /root/audio.zpk','A_FETCH');status('pkg install /root/audio.zpk','A_PACKAGE')
        status('native run audio-probe','A_PCM')
        assert 'AUDIO_PROBE_OK' in machine.log(),'real input/output PCM'
        status('native run audio-probe --fault','A_FAULT',139);status('native run audio-probe --empty','A_TEARDOWN')
        status('sudo -u user -- native run audio-probe --deny','A_CAPTURE_DENIED')
        status('native run audio-probe --remember','A_REMEMBER');disk=stop()
        for name,mode in [('output','--output'),('muted','--muted')]:
            wav=(keep/(name+'.wav')).resolve()
            boot(name,disk,'wav,id=sound,path='+str(wav)+',out.frequency=48000,out.channels=2,out.format=s16')
            status('native run audio-probe --remembered','A_RESTORED_'+name.upper())
            status('native run audio-probe '+mode,'A_'+name.upper());disk=stop()
            data=wav.read_bytes();assert data[:4]==b'RIFF' and data[8:12]==b'WAVE','actual WAV output'
            start=data.index(b'data',12)+8;pcm=data[start:];assert len(pcm)%2==0
            samples=struct.unpack('<'+'h'*(len(pcm)//2),pcm)
            assert len(samples)>10000,'hardware output advanced'
            if name=='output':assert sum(abs(n)>1000 for n in samples)>10000,'audible PCM reached the HDA backend'
            else:assert not any(samples),'muted app leaked audio samples'
            assert max(map(abs,samples))<=4000,'PCM gain stayed within its requested range'
            print('PASS HDA '+name+' WAV: frames='+str(len(samples)//2)+' peak='+str(max(map(abs,samples))),flush=True)
        boot('absent',disk);status('native run audio-probe --absent','A_ABSENT');stop()
        print('PASS HDA DMA playback/capture, native validation, microphone denial, fault teardown and absent-device boot',flush=True)
    finally:
        if machine is not None:machine.stop()
        server.shutdown();server.server_close()
if __name__=='__main__':main()
