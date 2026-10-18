import importlib.util
from pathlib import Path
import struct
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('queue_diagnostics',ROOT/'deploy/distributor/validation-scheduler.py')
q=importlib.util.module_from_spec(spec);spec.loader.exec_module(q)
P=q.PREFIX

class QueueTests(unittest.TestCase):
    def setUp(self):
        self.types={};self.symbols={};self.memory={};self.reads=[]
        self.next_table=0xffffffff80400000
        def layout(name,size,fields):
            self.types[name]=dict(size=size,fields=fields)
            self.symbols['_ZTV'+str(len(name))+name]=self.next_table;self.next_table+=256
        self.layout=layout
        layout(P+'SchedulerExtensible',16,{'<SchedulerData>k__BackingField':8})
        layout(P+'PerCpuState',48,{'<CurrentThread>k__BackingField':16})
        layout(P+'Stride_StrideCpuData',40,{'<RunQueue>k__BackingField':8,'<TotalTickets>k__BackingField':16})
        layout(P+'Stride_StrideThreadData',56,{'<Tickets>k__BackingField':8,'<Pass>k__BackingField':24})
        layout(P+'SchedulerThread',136,{'<Id>k__BackingField':80,'<State>k__BackingField':90,'<Flags>k__BackingField':88,
            '<TotalRuntime>k__BackingField':56,'<WakeupTime>k__BackingField':72,'<LastScheduledAt>k__BackingField':64,
            '<StackBase>k__BackingField':40,'<StackSize>k__BackingField':48,'<StackPointer>k__BackingField':24})
        self.list_type='S_P_CoreLib_System_Collections_Generic_List_1<'+P+'SchedulerThread>'
        layout(self.list_type,24,{'_size':16,'_items':8,'_version':20})
        for name in (P+'PerCpuState',P+'SchedulerThread'):
            layout('__Array<'+name+'>',16,{'m_NumComponents':8,'m_Data':16})
        self.slot=0xffffffff80500000;self.base=0xffff800000100000
        for key,address in dict(cpus=0x200000,cpu=0x210000,data=0x220000,queue=0x230000,items=0x240000,
                                ready=0x250000,current=0x260000,policy1=0x270000,policy2=0x280000).items():
            setattr(self,key,0xffff800000000000+address)
        self.put(self.slot,self.base);self.put(self.base+16,self.cpus)
        self.object(self.cpus,'__Array<'+P+'PerCpuState>');self.put(self.cpus+8,1);self.put(self.cpus+16,self.cpu)
        self.object(self.cpu,P+'PerCpuState');self.put(self.cpu+16,self.current);self.put(self.cpu+8,self.data)
        self.object(self.data,P+'Stride_StrideCpuData');self.put(self.data+8,self.queue);self.put(self.data+16,200)
        self.object(self.queue,self.list_type);self.put(self.queue+8,self.items);self.put(self.queue+16,1,4);self.put(self.queue+20,2,4)
        self.object(self.items,'__Array<'+P+'SchedulerThread>');self.put(self.items+8,2);self.put(self.items+16,self.ready)
        for pointer,identity,state,policy in ((self.ready,78,1,self.policy1),(self.current,79,2,self.policy2)):
            self.object(pointer,P+'SchedulerThread');self.put(pointer+80,identity,4);self.put(pointer+90,state,1)
            self.put(pointer+8,policy);self.object(policy,P+'Stride_StrideThreadData');self.put(policy+8,100);self.put(policy+24,3000)
        self.tools=[SimpleNamespace(stdout='ffffffff80001000 t first\nffffffff80002000 t waiter\nffffffff80003000 t caller\nffffffff80004000 t end\n'),
                    SimpleNamespace(stdout='48 89 b7 98 01 00 00\n48 89 87 50 01 00 00\n4c 89 87 b0 01 00 00')]

    def put(self,address,value,size=8):
        for i,b in enumerate(value.to_bytes(size,'little')):self.memory[address+i]=b
    def object(self,address,name):self.put(address,self.symbols['_ZTV'+str(len(name))+name])
    def read(self,monitor,address,count):
        self.reads.append((address,count));return bytes(self.memory.get(address+i,0) for i in range(count*8))
    def observe(self,reader=None):
        with patch.object(q,'metadata',return_value=(self.symbols,self.types,self.slot,16)),patch.object(q.subprocess,'run',side_effect=self.tools):
            return q.observe('monitor',Path('/exact.elf'),reader or self.read)

    def test_queue_membership_and_accounting_are_not_inferred_from_thread_state(self):
        result=self.observe()
        self.assertEqual(79,result['current']['id']);self.assertEqual([78],[t['id'] for t in result['queue']])
        self.assertEqual(200,result['total_tickets']);self.assertTrue(result['observation_only'])
        self.assertEqual(3000,result['queue'][0]['stride_pass'])
        self.assertNotIn('0xffff',str(result))

    def test_negative_pass_is_preserved_as_signed(self):
        self.put(self.policy1+24,(1<<64)-20)
        self.assertEqual(-20,self.observe()['queue'][0]['stride_pass'])

    def test_unknown_object_type_is_refused_before_following_fields(self):
        self.put(self.ready,0x534543524554)
        with self.assertRaisesRegex(ValueError,'type mismatch'):self.observe()
        self.assertNotIn((self.ready+80,1),self.reads)

    def test_oversize_queue_and_wrong_array_bounds_are_refused(self):
        self.put(self.queue+16,17,4)
        with self.assertRaisesRegex(ValueError,'bound exceeded'):self.observe()
        self.assertNotIn((self.items,1),self.reads)
        self.put(self.queue+16,3,4)
        with self.assertRaisesRegex(ValueError,'queue count'):self.observe()

    def test_torn_queue_does_not_become_a_consistent_snapshot(self):
        self.version_reads=0
        def read(monitor,address,count):
            if address==self.queue+16:
                self.version_reads+=1
                if self.version_reads==4:self.put(self.queue+20,4,4)
            return self.read(monitor,address,count)
        self.assertFalse(self.observe(read)['stable_endpoints'])

    def test_only_bounded_saved_frame_links_and_code_locations_are_returned(self):
        low=0xffff800000300000;sp=low+0x100;fp=low+0x800
        self.put(self.ready+40,low);self.put(self.ready+48,4096);self.put(self.ready+24,sp)
        self.put(sp+336,fp);self.put(sp+408,0xffffffff80002001)
        self.put(fp,0);self.put(fp+8,0xffffffff80003010)
        self.put(fp+16,0x534543524554)
        row=self.observe()['queue'][0]
        self.assertEqual(['waiter','caller'],[f['symbol'] for f in row['saved_frames']])
        self.assertNotIn((fp+16,1),self.reads);self.assertNotIn('534543',str(row))
        self.assertEqual('saved_context_not_live_backtrace',row['context_kind'])

    def test_unverified_context_layout_does_not_read_stack(self):
        self.tools[1]=SimpleNamespace(stdout='changed native ABI')
        low=0xffff800000300000;self.put(self.ready+40,low);self.put(self.ready+48,4096);self.put(self.ready+24,low)
        self.assertNotIn('saved_frames',self.observe()['queue'][0])
        self.assertFalse(any(low<=a<low+4096 for a,n in self.reads))

    def test_dwarf_members_must_stay_inside_their_declared_object(self):
        self.types[P+'SchedulerThread']['fields']['<Id>k__BackingField']=4096
        with self.assertRaisesRegex(ValueError,'member offset'):self.observe()
        self.assertNotIn((self.ready+4096,1),self.reads)

class LayoutTests(unittest.TestCase):
    def test_complete_class_wins_over_forward_declaration_and_static_members_are_ignored(self):
        raw=''' <1><1>: Abbrev Number: 4 (DW_TAG_class_type)
 <2> DW_AT_name : Example
 <3> DW_AT_byte_size : 16
 <2><4>: Abbrev Number: 5 (DW_TAG_member)
 <5> DW_AT_name : field
 <6> DW_AT_data_member_location: 0x8
 <2><7>: Abbrev Number: 16 (DW_TAG_member)
 <8> DW_AT_name : static_field
 <9> DW_AT_external : 1
 <1><10>: Abbrev Number: 2 (DW_TAG_class_type)
 <11> DW_AT_name : Example
 <12> DW_AT_declaration : 1
 <1><13>: Abbrev Number: 0
'''
        self.assertEqual({'field':8},q.layouts(raw)['Example']['fields'])

class MetadataTests(unittest.TestCase):
    def test_static_slot_and_offset_must_be_verified_by_exact_getter(self):
        slot='__GCSTATICS'+P+'SchedulerManager'
        nm='ffffffff80500000 d '+slot+'\n'
        code='mov 0x1234(%rip),%rax # ffffffff80500000 <'+slot+'>\n 00: mov 0x10(%rax),%rax\n'
        with patch.object(q.subprocess,'run',side_effect=[SimpleNamespace(stdout=nm),SimpleNamespace(stdout=code),SimpleNamespace(stdout='')]) as run:
            self.assertEqual((0xffffffff80500000,16),q.metadata(Path('/exact.elf'))[2:])
            self.assertTrue(all('shell' not in call.kwargs for call in run.call_args_list))
        for invalid in (code.replace('0x10','0x11'),code.replace('ffffffff80500000','ffffffff80500008'),code.replace('mov 0x1234','lea 0x1234')):
            with patch.object(q.subprocess,'run',side_effect=[SimpleNamespace(stdout=nm),SimpleNamespace(stdout=invalid)]) as run:
                with self.assertRaises(ValueError):q.metadata(Path('/exact.elf'))
                self.assertEqual(2,run.call_count)

if __name__=='__main__':unittest.main()
