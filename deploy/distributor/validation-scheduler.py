"""Read typed scheduler queue metadata from an exact ELF; no guest execution changes.

DWARF supplies instance offsets; a verified getter supplies the GC static slot.
Only numeric metadata is returned. This is a live observation, not an atomic dump.
"""
import bisect
import time
import re
import struct
import subprocess

PREFIX = 'Cosmos_Kernel_Core_Cosmos_Kernel_Core_Scheduler_'


def layouts(text):
    """Only complete top-level classes and their immediate instance members."""
    result = {}; current = None; member = None
    for line in text.splitlines():
        die = re.match(r'\s*<(\d+)><[0-9a-f]+>: Abbrev Number: \d+(?: \(([^)]+)\))?', line)
        if die:
            depth, tag = int(die[1]), die[2]
            if depth <= 1:
                if current and current.get('name') and current.get('size') and current['fields']:
                    result[current['name']] = current
                current = dict(fields={}) if depth == 1 and tag == 'DW_TAG_class_type' else None
            member = {} if current is not None and depth == 2 and tag == 'DW_TAG_member' else None
            continue
        if current is None: continue
        name = re.search(r'DW_AT_name\s*: (?:\(indirect string, offset: 0x[0-9a-f]+\): )?(.*)$',line)
        if name:
            (member if member is not None else current)['name'] = name[1]
        size = re.search(r'DW_AT_byte_size\s*: (0x[0-9a-f]+|[0-9]+)\s*$',line)
        if size and member is None: current['size'] = int(size[1],0)
        offset = re.search(r'DW_AT_data_member_location:\s*(0x[0-9a-f]+|[0-9]+)\s*$',line)
        if offset and member is not None and member.get('name'):
            current['fields'][member['name']] = int(offset[1],0)
    return result


def metadata(elf):
    raw = subprocess.run(['nm','-n','--defined-only',str(elf)],capture_output=True,text=True,timeout=3,check=True).stdout
    symbols = {}
    for line in raw.splitlines():
        parts=line.split(maxsplit=2)
        if len(parts)==3: symbols[parts[2]]=int(parts[0],16)
    getter=PREFIX+'SchedulerManager__get_CurrentCpuState'
    slot='__GCSTATICS'+PREFIX+'SchedulerManager'
    code=subprocess.run(['objdump','-d','--disassemble='+getter,str(elf)],capture_output=True,text=True,timeout=3,check=True).stdout
    # The exact x64 getter loads GC statics, then the CPU array, then element 0.
    match=re.search(r'mov\s+0x[0-9a-f]+\(%rip\),%rax\s+#\s+([0-9a-f]+) <'+re.escape(slot)+r'>\n'
                    r'[^\n]*mov\s+0x([0-9a-f]+)\(%rax\),%rax',code)
    if not match or int(match[1],16)!=symbols.get(slot): raise ValueError('unverified scheduler static getter')
    cpu_offset=int(match[2],16)
    if cpu_offset%8 or not 8<=cpu_offset<=128: raise ValueError('invalid scheduler static offset')
    text=subprocess.run(['readelf','--debug-dump=info',str(elf)],capture_output=True,text=True,timeout=5,check=True).stdout
    if len(text)>32*1024*1024: raise ValueError('scheduler debug metadata too large')
    return symbols,layouts(text),symbols[slot],cpu_offset


def observe(monitor,elf,read_words):
    symbols,types,slot,cpu_offset=metadata(elf)
    code_rows=[]
    raw_symbols=subprocess.run(['nm','-n','--defined-only',str(elf)],capture_output=True,text=True,timeout=3,check=True).stdout
    for line in raw_symbols.splitlines():
        parts=line.split(maxsplit=2)
        if len(parts)==3 and parts[1] in ('t','T'): code_rows.append((int(parts[0],16),parts[2]))
    addresses=[p for p,n in code_rows]
    context_code=subprocess.run(['objdump','-d','--disassemble='+PREFIX+'ThreadContext__Initialize',str(elf)],capture_output=True,text=True,timeout=3,check=True).stdout
    context_verified=all(pattern in context_code for pattern in (
        '48 89 b7 98 01 00 00', '48 89 87 50 01 00 00', '4c 89 87 b0 01 00 00'))
    def location(pc):
        i=bisect.bisect_right(addresses,pc)-1
        if not 0<=i<len(code_rows)-1: return None
        return dict(symbol=code_rows[i][1],offset=pc-code_rows[i][0])
    reads=0;deadline=time.monotonic()+3
    def scalar(address,size=8):
        nonlocal reads
        reads+=1
        if reads>640 or time.monotonic()>deadline or size not in (1,2,4,8): raise ValueError('scheduler read budget exceeded')
        # Read the containing aligned metadata word; never return uninterpreted bytes.
        aligned=address & ~7
        if address+size>aligned+8: raise ValueError('unaligned scheduler field')
        raw=read_words(monitor,aligned,1)
        return int.from_bytes(raw[address-aligned:address-aligned+size],'little')
    def info(name):
        value=types[name]
        if not 0<value['size']<=512: raise ValueError('invalid scheduler object size')
        return value
    def field(pointer,name,member,size=8):
        layout=info(name);offset=layout['fields'][member]
        if not 8<=offset<=layout['size']-size: raise ValueError('invalid scheduler member offset')
        return scalar(pointer+offset,size)
    def table(name):
        matches=[v for k,v in symbols.items() if re.fullmatch(r'_ZTV\d+'+re.escape(name),k)]
        if len(matches)!=1: raise ValueError('scheduler method table unavailable')
        return matches[0]
    def instance(pointer,name):
        if scalar(pointer)!=table(name): raise ValueError('scheduler object type mismatch')
        return pointer
    def array(pointer,element,limit):
        name='__Array<'+element+'>';instance(pointer,name)
        count=field(pointer,name,'m_NumComponents',4)
        if count>limit: raise ValueError('scheduler array bound exceeded')
        offset=info(name)['fields']['m_Data']
        if offset!=info(name)['size']: raise ValueError('scheduler array data contract changed')
        return count,pointer+offset
    thread_data_type=PREFIX+'Stride_StrideThreadData'
    thread=PREFIX+'SchedulerThread';cpu_type=PREFIX+'PerCpuState';data_type=PREFIX+'Stride_StrideCpuData'
    base_type=PREFIX+'SchedulerExtensible';list_type='S_P_CoreLib_System_Collections_Generic_List_1<'+thread+'>'
    statics=scalar(slot);cpus=scalar(statics+cpu_offset)
    cpu_count,cpu_entries=array(cpus,cpu_type,8)
    if cpu_count!=1: raise ValueError('scheduler inspection requires managed BSP only')
    cpu=instance(scalar(cpu_entries),cpu_type)
    current=field(cpu,cpu_type,'<CurrentThread>k__BackingField')
    data=instance(field(cpu,base_type,'<SchedulerData>k__BackingField'),data_type)
    queue=instance(field(data,data_type,'<RunQueue>k__BackingField'),list_type)
    count=field(queue,list_type,'_size',4);version=field(queue,list_type,'_version',4)
    if count>16: raise ValueError('pipeline run queue bound exceeded')
    items=field(queue,list_type,'_items');capacity,entries=array(items,thread,256)
    if count>capacity: raise ValueError('invalid scheduler queue count')
    pointers=[scalar(entries+i*8) for i in range(count)]
    def describe(pointer):
        instance(pointer,thread)
        policy=instance(field(pointer,base_type,'<SchedulerData>k__BackingField'),thread_data_type)
        passed=field(policy,thread_data_type,'<Pass>k__BackingField')
        row=dict(id=field(pointer,thread,'<Id>k__BackingField',4),
                    tickets=field(policy,thread_data_type,'<Tickets>k__BackingField'),
                    stride_pass=passed if passed<1<<63 else passed-(1<<64),
                    scheduled_at=field(pointer,thread,'<LastScheduledAt>k__BackingField'),
                    state=field(pointer,thread,'<State>k__BackingField',1),
                    flags=field(pointer,thread,'<Flags>k__BackingField',2),
                    runtime_ns=field(pointer,thread,'<TotalRuntime>k__BackingField'),
                    wake_deadline=field(pointer,thread,'<WakeupTime>k__BackingField'))
        if context_verified:
            low=field(pointer,thread,'<StackBase>k__BackingField');size=field(pointer,thread,'<StackSize>k__BackingField')
            sp=field(pointer,thread,'<StackPointer>k__BackingField');high=low+size
            # Root/idle stacks may have no owned allocation. Never follow those.
            if 0<size<=2*1024*1024 and low<=sp<=high-448:
                frame=scalar(sp+336);pc=scalar(sp+408)
                frames=[];initial=location(pc)
                if initial: frames.append(initial)
                for _ in range(8):
                    if frame%8 or not low<=frame<=high-16: break
                    parent=scalar(frame);returned=scalar(frame+8);label=location(returned)
                    if label is None: break
                    frames.append(label)
                    if parent<=frame: break
                    frame=parent
                row['saved_frames']=frames
                row['context_kind']='saved_context_not_live_backtrace'
        return row
    queue_rows=[describe(p) for p in pointers]
    current_row=describe(current)
    tickets=field(data,data_type,'<TotalTickets>k__BackingField')
    stable=(field(queue,list_type,'_size',4)==count and field(queue,list_type,'_version',4)==version
            and field(cpu,cpu_type,'<CurrentThread>k__BackingField')==current)
    if any(t['state']>5 or t['id']>65535 for t in queue_rows+[current_row]): raise ValueError('invalid scheduler thread metadata')
    return dict(format='ZenithSchedulerQueue-v1',current=current_row,queue=queue_rows,
                total_tickets=tickets,stable_endpoints=stable,observation_only=True)
