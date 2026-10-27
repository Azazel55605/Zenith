"""Observe real graphical input prerequisites; never replay an input or mutation."""
import json
import importlib.util
from pathlib import Path
import re
import time
import recovery_console
spec = importlib.util.spec_from_file_location('graphics_pointer_profile', Path(__file__).resolve().parents[1]/'deploy/distributor/validation-input.py')
input_profile = importlib.util.module_from_spec(spec); spec.loader.exec_module(input_profile)
monitor_command = recovery_console.monitor_command

TRACE = re.compile(r'\[(?:SCHED|STRIDE|LAPIC|CV|LowLevelMonitor|Kernel|PageAllocator|SmallHeap)\][^\n]*\n')
RECORD = re.compile(r'(?:^|\n)(?:\[zenith\] \[[^\]\n]*\] )?user: (GUI_[A-Z_]+|G_INTERACTIVE=\d+)\r?(?=\n|$)')


def records(text):
    return RECORD.findall(TRACE.sub('', text))


def check(machine, monitor, keep, start):
    input_profile.install_monitor(recovery_console)
    global monitor_command
    monitor_command = recovery_console.monitor_command
    keep = Path(keep); off = start(); phase = 'surface-admission'; geometry = {}
    def require(marker, timeout=10):
        if not machine.wait(marker, off, timeout): raise RuntimeError('missing '+marker)
    try:
        require('native surface opened', 30); time.sleep(1)
        phase='visible-first-frame'
        monitor_command(monitor, 'screendump '+str(keep/'client.ppm'))
        ppm=(keep/'client.ppm').read_bytes();parts=ppm.split(b'\n',3)
        w,height=map(int,parts[1].split());rgb=parts[3]
        locations=[i//3 for i in range(0,len(rgb),3) if rgb[i:i+3]==bytes.fromhex('6b8cff')]
        if len(locations)<5000: raise RuntimeError('native pixels not visible')
        left=min(i%w for i in locations)-20;top=min(i//w for i in locations)-20
        x,y=left+160,top+100;geometry=dict(screen=[w,height],content=[left,top,320,200])
        phase='pointer-delivery'
        monitor_command(monitor,'mouse_move '+str(x-w//2)+' '+str(y-height//2));time.sleep(.2)
        monitor_command(monitor,'mouse_button 1');time.sleep(.15);monitor_command(monitor,'mouse_button 0')
        require('user: GUI_POINTER')
        phase='focus-loss'
        tx=(w-740)//2+28+15;ty=(height-64-470)//2+28+19
        monitor_command(monitor,'mouse_move '+str(tx-x)+' '+str(ty-y));time.sleep(.15)
        monitor_command(monitor,'mouse_button 1');time.sleep(.15);monitor_command(monitor,'mouse_button 0')
        require('user: GUI_FOCUS_OUT')
        monitor_command(monitor,'sendkey f12 30');time.sleep(.2)
        phase='focus-return'
        x,y=200,height-32
        monitor_command(monitor,'mouse_move '+str(x-tx)+' '+str(y-ty));time.sleep(.15)
        monitor_command(monitor,'mouse_button 1');time.sleep(.15);monitor_command(monitor,'mouse_button 0');time.sleep(.2)
        # Initial focus and restored focus must both be observed before key input.
        deadline=time.monotonic()+10
        while records(machine.log()[off:]).count('GUI_FOCUS_IN')<2:
            if time.monotonic()>=deadline: raise RuntimeError('missing restored focus')
            time.sleep(.05)
        phase='focused-keys'
        monitor_command(monitor,'sendkey a 30');time.sleep(.2);monitor_command(monitor,'sendkey esc 30')
        require('user: G_INTERACTIVE=0',60)
        output=records(machine.log()[off:])
        if 'GUI_POINTER' not in output or output.count('GUI_KEY')!=2 or 'GUI_FOCUS_OUT' not in output:
            raise RuntimeError('wrong focus-filtered events')
        print('PASS native pixels, pointer acknowledgement and focused-key isolation',flush=True)
        return x,y,4
    except BaseException:
        output=records(machine.log()[off:])
        report=dict(phase=phase,geometry=geometry,counts={name:output.count(name) for name in
            ('GUI_POINTER','GUI_KEY','GUI_FOCUS_IN','GUI_FOCUS_OUT','G_INTERACTIVE=0')},serial=str(machine.serial),observation_only=True)
        (keep/'graphics-input-failure.json').write_text(json.dumps(report,indent=2)+'\n')
        print('Graphical input failure: '+json.dumps(report),flush=True)
        try:monitor_command(monitor,'screendump '+str(keep/'input-failure.ppm'))
        except (OSError,RuntimeError):pass
        raise
