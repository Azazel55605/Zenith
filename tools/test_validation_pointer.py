import importlib.util
from pathlib import Path
import struct
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('pointer',ROOT/'deploy/distributor/validation-pointer.py')
pointer=importlib.util.module_from_spec(spec);spec.loader.exec_module(pointer)

class PointerTests(unittest.TestCase):
    address=0xffffffff804ee6d8
    def code(self):
        return ('mov 0x35f5fd(%rip),%edi # '+format(self.address,'x')+' <'+pointer.MANAGER+'>\n'
                'mov 0x4(%rdx),%edx\nrex movzbl 0xc(%rdi),%edi\nmovzbl 0xd(%rdx),%edx\n')
    def test_exact_native_access_contract_is_required(self):
        rows=format(self.address,'x')+' d '+pointer.MANAGER+'\n'
        with patch.object(pointer.subprocess,'run',side_effect=[SimpleNamespace(stdout=rows),SimpleNamespace(stdout=self.code())]) as run:
            self.assertEqual(self.address,pointer.layout(Path('fixture.elf')))
        self.assertEqual('nm',run.call_args_list[0].args[0][0])
        self.assertEqual('objdump',run.call_args_list[1].args[0][0])
        for before,after in (('0x4(%rdx)','0x8(%rdx)'),('0xc(%rdi)','0xe(%rdi)'),(format(self.address,'x'),'ffffffff804ee000')):
            with patch.object(pointer.subprocess,'run',side_effect=[SimpleNamespace(stdout=rows),SimpleNamespace(stdout=self.code().replace(before,after))]):
                with self.assertRaises(ValueError):pointer.layout(Path('fixture.elf'))
    def test_missing_or_ambiguous_slot_is_not_guessed(self):
        for text in ('', (format(self.address,'x')+' d '+pointer.MANAGER+'\n')*2):
            with patch.object(pointer.subprocess,'run',return_value=SimpleNamespace(stdout=text)):
                with self.assertRaises(ValueError):pointer.layout(Path('fixture.elf'))
    def test_read_is_bounded_to_mouse_coordinates_and_buttons(self):
        calls=[]
        def read(monitor,address,count):
            calls.append((monitor,address,count));return struct.pack('<iiiBBBB',910,156,123,1,0,255,255)
        row=pointer.observe('monitor',self.address,read)
        self.assertEqual([('monitor',self.address,2)],calls)
        self.assertEqual(dict(x=910,y=156,left=True,right=False,observation_only=True),row)
        self.assertNotIn('scroll',row);self.assertNotIn('middle',row)
    def test_invalid_or_incomplete_state_is_not_reported(self):
        for raw in (b'',struct.pack('<iiiBBBB',-1,156,0,1,0,0,0),struct.pack('<iiiBBBB',10,156,0,7,0,0,0)):
            with self.assertRaises(ValueError):pointer.observe('monitor',self.address,lambda *args:raw)


class DeviceQueueTests(unittest.TestCase):
    def setUp(self):
        self.base=0xffff800000010000;self.memory={};self.reads=[]
        self.addresses={name:self.base+offset for name,offset in (('slot',0),('statics',0x100),('list',0x200),('array',0x300),('mouse',0x400),('queue',0x500),('used',0x600))}
        types={pointer.LIST:dict(size=24,fields={'_items':8,'_size':16,'_version':20}),pointer.ARRAY:dict(size=16,fields={'m_NumComponents':8,'m_Data':16}),pointer.MOUSE:dict(size=80,fields={'_eventQueue':40,'_initialized':31,'_irqRegistered':68,'_hasEvents':72}),pointer.QUEUE:dict(size=64,fields={'_used':32,'_queueSize':48,'_lastUsedIdx':54,'_numFree':56}),pointer.QUEUE+'_VringUsed':dict(size=8,fields={'Idx':2})}
        symbols={}
        for index,(key,name) in enumerate((('list',pointer.LIST),('array',pointer.ARRAY),('mouse',pointer.MOUSE),('queue',pointer.QUEUE))):
            value=self.base+0x1000+index*0x100;symbols['_ZTV'+str(len(name))+name]=value;self.put(self.addresses[key],value,8)
        self.metadata=dict(symbols=symbols,types=types,slot=self.addresses['slot'],offset=8)
        for where,value,size in (('slot',self.addresses['statics'],8),('statics+8',self.addresses['list'],8),('list+8',self.addresses['array'],8),('list+16',1,4),('list+20',1,4),('array+8',1,4),('array+16',self.addresses['mouse'],8),('mouse+40',self.addresses['queue'],8),('mouse+31',1,1),('mouse+68',1,1),('mouse+72',0,1),('queue+32',self.addresses['used'],8),('queue+48',64,4),('queue+54',12000,2),('queue+56',32,2),('used+2',12002,2)):
            name,_,off=where.partition('+');self.put(self.addresses[name]+int(off or 0),value,size)
    def put(self,address,value,size):
        for offset,byte in enumerate(value.to_bytes(size,'little')):self.memory[address+offset]=byte
    def read(self,monitor,address,count):
        self.reads.append((address,count))
        return bytes(self.memory.get(address+offset,0) for offset in range(count*8))
    def observe(self):return pointer.devices('monitor',self.metadata,self.read)
    def test_pending_queue_is_reported_without_reading_event_payloads_or_mmio(self):
        row=self.observe()['devices'][0]
        self.assertEqual((12000,12002,2,32),(row['last_used'],row['device_used'],row['pending_used'],row['free_descriptors']))
        self.assertTrue(row['initialized']);self.assertTrue(row['irqRegistered'])
        self.assertFalse(row['hasEvents'])
        self.assertTrue(all(count==1 for _,count in self.reads))
        self.assertFalse(any(address<=self.addresses['mouse']+48<address+8 for address,_ in self.reads))
    def test_empty_and_wrapping_queues_are_distinct(self):
        for last,used,expected in ((12000,12000,0),(65535,1,2)):
            self.put(self.addresses['queue']+54,last,2);self.put(self.addresses['used']+2,used,2)
            self.assertEqual(expected,self.observe()['devices'][0]['pending_used'])
    def test_wrong_queue_type_or_impossible_ring_counts_are_rejected(self):
        self.put(self.addresses['queue'],123,8)
        with self.assertRaisesRegex(ValueError,'type mismatch'):self.observe()
        self.setUp();self.put(self.addresses['used']+2,12065,2)
        with self.assertRaisesRegex(ValueError,'counters'):self.observe()
    def test_other_device_types_are_skipped_without_inspection(self):
        self.put(self.addresses['mouse'],123,8)
        self.assertEqual([],self.observe()['devices'])
        self.assertFalse(any(self.addresses['mouse']<address<self.addresses['mouse']+80 for address,_ in self.reads))
    def test_list_size_and_member_offsets_are_bounded(self):
        self.put(self.addresses['list']+16,100,4)
        with self.assertRaisesRegex(ValueError,'list bounds'):self.observe()
        self.setUp();self.metadata['types'][pointer.MOUSE]['fields']['_eventQueue']=800
        with self.assertRaisesRegex(ValueError,'field layout'):self.observe()

if __name__=='__main__':unittest.main()
