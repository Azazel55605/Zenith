#!/usr/bin/env python3
"""Replay a failed disposable native fixture; copies its disk and never builds/publishes.

--warmup reuses the current native harness's pre-pipeline sequence and the public
packages already downloaded into the fixture. It is diagnostic, not acceptance.
"""
import argparse
import ast
import functools
import hashlib
import http.server
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
from guest_pipeline import check as check_pipeline
import boot_image


def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module


def warmup(source):
    """Select statements between installed boot and the repeated pipeline loop."""
    tree=ast.parse(source)
    starts=[]
    for block in tree.body:
        if not isinstance(block,ast.Try): continue
        # The focused terminal mode skips the ordinary matrix. Replays always
        # select its normal branch, without running the password/setup shortcut.
        normal=[]
        for statement in block.body:
            if isinstance(statement,ast.If) and isinstance(statement.test,ast.Name) and statement.test.id=='terminal_only':
                normal.extend(statement.orelse)
            else: normal.append(statement)
        for i,node in enumerate(normal):
            if (isinstance(node,ast.Expr) and isinstance(node.value,ast.Call)
                and isinstance(node.value.func,ast.Name) and node.value.func.id=='boot'
                and len(node.value.args)==2 and isinstance(node.value.args[0],ast.Constant)
                and node.value.args[0].value=='installed'):
                starts.append((normal,i))
    if len(starts)!=1: raise ValueError('native warmup boot boundary changed')
    nodes,start=starts[0]
    ends=[i for i,node in enumerate(nodes) if i>start and isinstance(node,ast.For)
          and isinstance(node.target,ast.Name) and node.target.id=='attempt'
          and ast.dump(node.iter)==ast.dump(ast.parse('range(64)',mode='eval').body)]
    if len(ends)!=1: raise ValueError('native warmup pipeline boundary changed')
    body=nodes[start+1:ends[0]]
    if not body: raise ValueError('empty native warmup')
    return compile(ast.Module(body=body,type_ignores=[]),'<native fixture warmup>','exec')


def public_packages(disk,keep):
    state=boot_image.verify(disk)
    part=next(p for p in boot_image.partitions(disk) if str(p[1])==state['root'])
    image=keep/'root.ext2'
    with disk.open('rb') as source,image.open('wb') as target:
        source.seek(part[2]*512);remaining=(part[3]-part[2]+1)*512
        while remaining:
            data=source.read(min(1024*1024,remaining))
            if not data: raise ValueError('truncated fixture root')
            target.write(data);remaining-=len(data)
    served=keep/'served';served.mkdir()
    for name in ('key.pem','sunbeam.zpk','zenith-fonts.zpk','zenith-man-pages.zpk'):
        destination=served/name
        # debugfs -R parses its own arguments; quote its output filename too.
        quoted='"'+str(destination).replace('\\','\\\\').replace('"','\\"')+'"'
        subprocess.run(['debugfs','-R','dump /root/'+name+' '+quoted,str(image)],
                       check=True,capture_output=True,text=True,timeout=15)
        if not destination.is_file() or destination.stat().st_size==0:
            raise ValueError('missing public fixture package: '+name)
    return served


def replay(disk,elf,keep,repeat=8,with_warmup=False):
    keep.mkdir(parents=True,exist_ok=False)
    state=boot_image.verify(disk)
    digest=hashlib.sha256(elf.read_bytes()).hexdigest()
    if state['current']!=digest: raise ValueError('fixture kernel does not match supplied ELF')
    metadata=dict(elf_sha256=digest,source_disk=str(disk),diagnostic_only=True,
                  warmup=with_warmup,repeat=repeat)
    (keep/'replay.json').write_text(json.dumps(metadata,indent=2)+'\n')
    print('Diagnostic replay evidence: '+str(keep),flush=True)
    print('Fixture ELF SHA256: '+digest,flush=True)
    h=load('pipeline_replay_headless',ROOT/'tools/headless-smoke.py')
    controller=ROOT/'deploy/distributor'
    load('pipeline_replay_accelerator',controller/'validation-accelerator.py').install(h)
    load('pipeline_replay_status',controller/'validation-status.py').install(h.Machine)
    load('pipeline_replay_profile',controller/'validation-profile.py').install(h.Machine,elf=elf)
    server=None;machine=None
    try:
        if with_warmup:
            prelude=warmup((ROOT/'tools/native-process-smoke.py').read_text())
            served=public_packages(disk,keep)
            class Handler(http.server.SimpleHTTPRequestHandler):
                def log_message(self,*args): pass
            server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=str(served)))
            threading.Thread(target=server.serve_forever,daemon=True).start()
        machine=h.Machine(keep/'installed',disk,network=['-netdev','user,id=net0','-device','e1000e,netdev=net0'],
                          monitor=keep/'installed/monitor.sock')
        if not machine.wait('headless ready',timeout=180): raise RuntimeError('fixture headless boot failed')
        if not machine.wait('network: online address=10.0.2.15',timeout=60): raise RuntimeError('fixture DHCP failed')
        def command(text,marker):
            # Only the copied fixture's existing public download files are replaced.
            if text.startswith('fetch '): text=text.replace('fetch ','fetch --replace ',1)
            offset=len(machine.log());machine.send(text+'\r')
            if not machine.wait(marker,offset,timeout=120): raise RuntimeError('warmup missing '+marker)
            print('PASS '+marker,flush=True)
        def status(text,marker,code=0): command(text+'; echo '+marker+'=$? | logger','user: '+marker+'='+str(code))
        if with_warmup:
            scope=dict(m=machine,cpu_workers=False,key=(served/'key.pem').read_bytes(),
                       url='http://10.0.2.2:'+str(server.server_port),hashlib=hashlib,time=time,
                       command=command,status=status)
            exec(prelude,scope)
        for attempt in range(repeat):
            check_pipeline(machine,'sunbeam --stream-bytes | sunbeam cat | sunbeam wc -c | logger',
                           '163840','N_STREAM_BYTES_'+str(attempt))
        machine.send('poweroff\r')
        if not machine.wait('all filesystems unmounted',timeout=30): raise RuntimeError('fixture shutdown failed')
        machine.process.wait(timeout=30)
        print('Diagnostic replay passed; this does not qualify or publish a release.',flush=True)
    finally:
        if machine: machine.stop()
        if server: server.shutdown();server.server_close()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--disk',required=True,type=Path,help='retained disposable smoke-fixture disk')
    parser.add_argument('--elf',required=True,type=Path,help='ELF matching the installed fixture kernel')
    parser.add_argument('--keep',type=Path,help='new short evidence directory (must not exist)')
    parser.add_argument('--repeat',type=int,default=8)
    parser.add_argument('--warmup',action='store_true')
    args=parser.parse_args()
    if not 1<=args.repeat<=256: parser.error('--repeat must be 1..256')
    if args.keep is None:
        parent=Path(tempfile.mkdtemp(prefix='zn-',dir='/var/tmp'));keep=parent/'replay'
    else: keep=args.keep.resolve()
    if len(str(keep/'installed/serial.sock').encode())>=104: parser.error('evidence path too long for Unix sockets')
    try: replay(args.disk.resolve(),args.elf.resolve(),keep,args.repeat,args.warmup)
    except (OSError,ValueError,RuntimeError,subprocess.SubprocessError) as error:
        raise SystemExit('Replay failed; evidence: '+str(keep)+'; '+str(error))


if __name__=='__main__':main()
