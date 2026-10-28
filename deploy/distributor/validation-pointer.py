"""Read-only mouse-manager observations with a verified native field-access contract."""
import re
import struct
import subprocess

MANAGER = '__NONGCSTATICSCosmos_Kernel_System_Cosmos_Kernel_System_Mouse_MouseManager'
POLL = 'Zenith_Zenith_Gui_Input__Poll'


def layout(elf):
    result = subprocess.run(['nm','--defined-only',str(elf)],capture_output=True,text=True,timeout=3,check=True)
    matches=[]
    for line in result.stdout.splitlines():
        row=line.split()
        if len(row)==3 and row[2]==MANAGER and row[1] in ('b','B','d','D'):matches.append(int(row[0],16))
    if len(matches)!=1:raise ValueError('mouse manager static slot unavailable')
    address=matches[0]
    code=subprocess.run(['objdump','-d','--disassemble='+POLL,str(elf)],capture_output=True,text=True,timeout=3,check=True).stdout
    # Verify the released x64 poller's actual accesses, rather than assuming a
    # source/declaration layout. Changes are unsupported observations, not guesses.
    if (not re.search(r'mov\s+[^\n]+,%edi\s+#\s*'+format(address,'x')+r'\s+<'+MANAGER+r'>',code)
        or not re.search(r'mov\s+0x4\(%rdx\),%edx',code)
        or not re.search(r'movzbl\s+0xc\(%rdi\),%edi',code)
        or not re.search(r'movzbl\s+0xd\(%rdx\),%edx',code)):
        raise ValueError('mouse manager poll access contract changed')
    return address


def observe(monitor, address, read):
    raw=read(monitor,address,2)
    if len(raw)!=16:raise ValueError('incomplete mouse manager observation')
    x,y,_,left,right,_,_=struct.unpack('<iiiBBBB',raw)
    if not (0<=x<4096 and 0<=y<2160 and left in (0,1) and right in (0,1)):
        raise ValueError('invalid mouse manager observation')
    return dict(x=x,y=y,left=bool(left),right=bool(right),observation_only=True)
