#!/usr/bin/env python3
"""Types long marked lines into desktop VMs of each given kernel at once and counts damaged lines.

usage: keyboard-loss-replay.py LINES VMS NAME=ISO [NAME=ISO ...]
Each line follows a pipeline, as in the gates. Running kernels side by side keeps host load equal."""
import importlib.util,random,sys,threading,time
from pathlib import Path
ROOT=Path(__file__).resolve().parent.parent;sys.path.insert(0,str(ROOT/'tools'))
spec=importlib.util.spec_from_file_location('keyboard_smoke',ROOT/'tools/smoke-test.py');smoke=importlib.util.module_from_spec(spec);spec.loader.exec_module(smoke)
LINES,VMS=int(sys.argv[1]),int(sys.argv[2]);KERNELS=dict(a.split('=') for a in sys.argv[3:]);results=[]
def run(name,iso,index):
    m,_=smoke.boot(Path(iso),180,'keyboard loss',keyboard='ps2')
    if not m:results.append((name,index,0,0));return
    time.sleep(2);damaged=0;rnd=random.Random(index)
    try:
        for n in range(LINES):
            mark=f'K{n}x'+''.join(rnd.choice('abcdefghijklmnopqrstuvwxyz') for _ in range(96))
            off=len(m.log());m.type(f'echo {mark} | logger; echo K{n}=$? | logger\n')
            if not(m.wait_for(f'user: K{n}=0',8,off) and mark in m.raw_log()[off:]):
                damaged+=1;print(name,index,'damaged',mark,flush=True);m.type('\n');time.sleep(.5)
    finally:
        results.append((name,index,LINES,damaged));m.stop()
threads=[threading.Thread(target=run,args=(k,v,i)) for k,v in KERNELS.items() for i in range(VMS)]
for t in threads:t.start()
for t in threads:t.join()
for name in KERNELS:print(name,'damaged',sum(r[3] for r in results if r[0]==name),'of',sum(r[2] for r in results if r[0]==name))
