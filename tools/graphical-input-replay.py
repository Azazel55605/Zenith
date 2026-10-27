#!/usr/bin/env python3
"""No-build diagnostic replay on a copied disposable graphical fixture.

--full runs the retained harness's desktop sequence, including close, minimized
cancellation, quota, signed probe upgrade and filesystem audit, without compiling
or reinstalling the OS. Only the independent disk copy is changed.
"""
import argparse
import ast
import hashlib
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import time
import boot_image
from guest_graphics import check

ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module


def desktop_plan(source):
    """Select known fixture setup data and its desktop-only assertion sequence.

    Fail on source drift. Never execute preparation/build/key generation/network
    server setup or the live installer during this replay.
    """
    tree=ast.parse(source)
    functions=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='main']
    if len(functions)!=1:raise ValueError('graphical harness main boundary changed')
    data=[];wanted={'gui_texts','quota','texts','scripts'}
    for node in functions[0].body:
        if isinstance(node,ast.Assign) and any(isinstance(t,ast.Name) and t.id in wanted for t in node.targets):data.append(node)
        elif isinstance(node,ast.AugAssign) and isinstance(node.target,ast.Name) and node.target.id=='texts':data.append(node)
    names={t.id for n in data if isinstance(n,ast.Assign) for t in n.targets if isinstance(t,ast.Name)}
    if names!=wanted:raise ValueError('graphical script definition boundary changed')
    blocks=[n for n in functions[0].body if isinstance(n,ast.Try)];starts=[]
    for block in blocks:
        for i,node in enumerate(block.body):
            if not (isinstance(node,ast.Assign) and isinstance(node.value,ast.Call)):continue
            call=node.value
            if (isinstance(call.func,ast.Attribute) and isinstance(call.func.value,ast.Name) and call.func.value.id=='h'
                and call.func.attr=='Machine' and call.args and isinstance(call.args[0],ast.BinOp)
                and isinstance(call.args[0].right,ast.Constant) and call.args[0].right.value=='desktop'):
                starts.append(block.body[i+1:])
    if len(starts)!=1:raise ValueError('graphical desktop boot boundary changed')
    body=starts[0]
    if not any(isinstance(n,ast.Assert) and 'audit.returncode' in ast.unparse(n) for n in body):
        raise ValueError('graphical final filesystem audit boundary changed')
    return compile(ast.Module(body=data,type_ignores=[]),'<graphical fixture definitions>','exec'),compile(ast.Module(body=body,type_ignores=[]),'<graphical desktop replay>','exec')


def full_replay(machine, monitor, keep, harness, elf, login):
    definitions,sequence=desktop_plan(harness.read_text())
    env=dict(m=machine,monitor=monitor,keep=keep,ROOT=elf.resolve().parent.parent,
             login=login,time=time,subprocess=subprocess,hashlib=hashlib,checks=0,gui=True)
    # The tagged sequence's trusted monitor helper receives exactly the same
    # sampled input and visible-frame policy as the distributor gate.
    import recovery_console
    profile=load('full_replay_input',ROOT/'deploy/distributor/validation-input.py')
    profile.install_monitor(recovery_console);env['monitor_command']=recovery_console.monitor_command
    exec(definitions,env)
    def command(text,marker,timeout=180):
        time.sleep(.5);off=len(machine.log());login.monitor_type(monitor,'sh /root/'+env['scripts'][text]+'\n')
        if not machine.wait(marker,off,timeout):raise RuntimeError('desktop replay missing '+marker)
        env['checks']+=1;print('PASS '+marker,flush=True);return machine.log()[off:]
    def status(text,label,code=0):return command(text+'; echo '+label+'=$? | logger','user: '+label+'='+str(code))
    env.update(command=command,status=status)
    exec(sequence,env)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--disk',type=Path,required=True);parser.add_argument('--elf',type=Path,required=True)
    parser.add_argument('--mouse',choices=('virtio','ps2'),default='virtio')
    parser.add_argument('--full',action='store_true');parser.add_argument('--harness',type=Path)
    args=parser.parse_args()
    state=boot_image.verify(args.disk,args.elf)
    print('Verified fixture kernel SHA256: '+state['current'],flush=True)
    harness=args.harness or args.elf.resolve().parent.parent/'tools/native-graphics-smoke.py'
    if args.full:desktop_plan(harness.read_text())  # Validate before starting a VM.
    keep=Path(tempfile.mkdtemp(prefix='zn-gui-',dir='/var/tmp'));print('Replay evidence: '+str(keep),flush=True)
    print(subprocess.check_output([os.environ.get('QEMU','qemu-system-x86_64'),'--version'],text=True).splitlines()[0],flush=True)
    h=load('headless',ROOT/'tools/headless-smoke.py');login=load('login',ROOT/'tools/login-session-smoke.py')
    load('accelerator',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    if args.mouse=='virtio':load('input_profile',ROOT/'deploy/distributor/validation-input.py').install(h.Machine)
    monitor=keep/'monitor.sock';m=None
    try:
        devices=['-device','virtio-gpu-pci','-device','virtio-keyboard-pci']
        if args.full:devices=['-netdev','user,id=net0','-device','e1000e,netdev=net0']+devices
        m=h.Machine(keep/'desktop',args.disk,'session=de',network=devices,monitor=monitor)
        if args.full:
            full_replay(m,monitor,keep,harness,args.elf,login)
            print('PASS full no-build graphical replay; original disk preserved',flush=True)
        else:
            assert m.wait('desktop ready (installed)',timeout=180),'desktop startup'
            for script,marker in (('g0','G_PROBE=0'),('g1','G_EXIT=0'),('g2','G_FAULT=139'),('g3','G_AFTER_FAULT=0')):
                time.sleep(.5);off=len(m.log());login.monitor_type(monitor,'sh /root/'+script+'\n')
                assert m.wait('user: '+marker,off,30),marker
            def start():
                time.sleep(.5);off=len(m.log());login.monitor_type(monitor,'sh /root/g4\n');return off
            check(m,monitor,keep,start)
            print('PASS no-build graphical input replay ('+args.mouse+'); original disk preserved',flush=True)
    finally:
        if m is not None:m.stop()
if __name__=='__main__':main()
