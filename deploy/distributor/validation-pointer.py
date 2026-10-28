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


MOUSE = 'Cosmos_Kernel_HAL_Cosmos_Kernel_HAL_Devices_Input_VirtioMouse'
QUEUE = 'Cosmos_Kernel_HAL_Cosmos_Kernel_HAL_Devices_Virtio_Virtqueue'
DEVICE = 'Cosmos_Kernel_HAL_Interfaces_Cosmos_Kernel_HAL_Interfaces_Devices_IMouseDevice'
LIST = 'S_P_CoreLib_System_Collections_Generic_List_1<'+DEVICE+'>'
ARRAY = '__Array<'+DEVICE+'>'
GC_SLOT = '__GCSTATICSCosmos_Kernel_System_Cosmos_Kernel_System_Mouse_MouseManager'


def device_layout(elf):
    import importlib.util
    from pathlib import Path
    spec=importlib.util.spec_from_file_location('pointer_types',Path(__file__).with_name('validation-scheduler.py'))
    types=importlib.util.module_from_spec(spec);spec.loader.exec_module(types)
    rows=subprocess.run(['nm','--defined-only',str(elf)],capture_output=True,text=True,timeout=3,check=True).stdout
    symbols={}
    for line in rows.splitlines():
        row=line.split()
        if len(row)==3:symbols[row[2]]=int(row[0],16)
    code=subprocess.run(['objdump','-d','--disassemble=Cosmos_Kernel_System_Cosmos_Kernel_System_Mouse_MouseManager__RegisterMouse',str(elf)],capture_output=True,text=True,timeout=3,check=True).stdout
    match=re.search(r'mov\s+0x[0-9a-f]+\(%rip\),%r15\s+#\s*([0-9a-f]+) <'+GC_SLOT+r'>\n[^\n]*cmpq\s+\$0x0,0x([0-9a-f]+)\(%r15\)',code)
    if not match or int(match[1],16)!=symbols.get(GC_SLOT):raise ValueError('mouse device root access changed')
    offset=int(match[2],16)
    if not 8<=offset<=128 or offset%8:raise ValueError('mouse device root offset invalid')
    raw=subprocess.run(['readelf','--debug-dump=info',str(elf)],capture_output=True,text=True,timeout=5,check=True).stdout
    if len(raw)>32*1024*1024:raise ValueError('mouse debug metadata too large')
    return dict(symbols=symbols,types=types.layouts(raw),slot=symbols[GC_SLOT],offset=offset)


def devices(monitor, metadata, read):
    """Inspect only typed mouse objects and DMA ring counters; no event payloads/MMIO."""
    import time
    symbols,types=metadata['symbols'],metadata['types']
    remaining=100;deadline=time.monotonic()+3
    def scalar(address,size=8):
        nonlocal remaining
        remaining-=1
        if remaining<0 or time.monotonic()>deadline or size not in (1,2,4,8):raise ValueError('mouse observation budget exceeded')
        aligned=address & ~7
        if not 0xffff800000000000<=aligned<0xfffffffffffffff8 or address+size>aligned+8:
            raise ValueError('mouse observation address invalid')
        raw=read(monitor,aligned,1)
        if len(raw)!=8:raise ValueError('mouse observation incomplete')
        return int.from_bytes(raw[address-aligned:address-aligned+size],'little')
    def table(name):
        matches=[value for key,value in symbols.items() if re.fullmatch(r'_ZTV\d+'+re.escape(name),key)]
        if len(matches)!=1:raise ValueError('mouse object type unavailable')
        return matches[0]
    def instance(pointer,name):
        if scalar(pointer)!=table(name):raise ValueError('mouse object type mismatch')
        return pointer
    def field(pointer,name,member,size=8,structure=False):
        kind=types[name];offset=kind['fields'][member]
        if not (0<kind['size']<=512 and (0 if structure else 8)<=offset<=kind['size']-size):
            raise ValueError('mouse field layout invalid')
        return scalar(pointer+offset,size)
    statics=scalar(metadata['slot'])
    mice=instance(scalar(statics+metadata['offset']),LIST)
    count=field(mice,LIST,'_size',4);version=field(mice,LIST,'_version',4)
    items=instance(field(mice,LIST,'_items'),ARRAY)
    capacity=field(items,ARRAY,'m_NumComponents',4)
    data=types[ARRAY]['fields']['m_Data']
    if not 0<=count<=8 or not count<=capacity<=32 or data!=types[ARRAY]['size']:
        raise ValueError('mouse device list bounds invalid')
    result=[]
    for index in range(count):
        pointer=scalar(items+data+index*8)
        # Other devices (e.g. PS/2) are skipped without inspecting their payload.
        if scalar(pointer)!=table(MOUSE):continue
        queue=instance(field(pointer,MOUSE,'_eventQueue'),QUEUE)
        used=field(queue,QUEUE,'_used')
        size=field(queue,QUEUE,'_queueSize',4)
        last=field(queue,QUEUE,'_lastUsedIdx',2)
        used_index=field(used,QUEUE+'_VringUsed','Idx',2,structure=True)
        pending=(used_index-last)&0xffff
        free=field(queue,QUEUE,'_numFree',2)
        if not 0<size<=256 or pending>size or free>size:raise ValueError('mouse queue counters unstable or invalid')
        row=dict(queue_size=size,last_used=last,device_used=used_index,pending_used=pending,free_descriptors=free)
        for member in ('_initialized','_irqRegistered','_hasEvents'):
            value=field(pointer,MOUSE,member,1)
            if value not in (0,1):raise ValueError('mouse device flag invalid')
            row[member.lstrip('_')]=bool(value)
        result.append(row)
    if field(mice,LIST,'_version',4)!=version:raise ValueError('mouse device list changed')
    return dict(devices=result,observation_only=True)
