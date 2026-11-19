#!/usr/bin/env python3
"""Derive seven private AP bridges from the qualified BSP ABI; never patch it."""
import argparse
import hashlib
from pathlib import Path
import re
ROOT=Path(__file__).resolve().parents[1]
# Audited change: only service admission extends 640..644 to 640..659 (646: resize refusal, 647/648: disks, 649: activity, 650..653: portal, 654..658: files,
# 659: clipboard paste, 660: thumbnails, 661..666: copied audio, 667: network center, 668: package center; no frame or state changes) and 613..638 to 613..639 (639: clipboard copy). The derivation below never touches those ranges.
SOURCE_SHA='837407af4851f591cae10a0f06ec66d93beaee067659090aa3572474111d6696'

def once(s,a,b):
    if s.count(a)!=1:raise RuntimeError('process bridge source contract changed: '+a[:70])
    return s.replace(a,b,1)

def variant(original,cpu):
    if hashlib.sha256(original.encode()).hexdigest()!=SOURCE_SHA:raise RuntimeError('BSP process ABI source changed; audit AP derivation')
    s=original.replace('zenith_process_run',f'zenith_process_run_ap{cpu}').replace('zenith_execution_process_allowed','zenith_cpu_process_allowed')
    # Validated context capability before any privileged/state mutation.
    s=once(s,'    push %rdi\n    call zenith_cpu_process_allowed','    push %rdi\n    call zenith_cpu_process_allowed')
    s=once(s,'    cld\n    mov %ds, z_old_ds_selector', '''    cld
    mov $0xc0000101,%ecx
    rdmsr
    mov %eax,z_old_gs(%rip)
    mov %edx,z_old_gs+4(%rip)
    mov %ds, z_old_ds_selector''')
    s=once(s,'    lea z_interrupt_stack+69632(%rip), %rax', '''    lea z_tss(%rip),%rdi
    call zenith_cpu_process_prepare
    test %rax,%rax
    jz z_unsupported
    mov %rax,z_cancel(%rip)
    call zenith_cpu_process_period
    mov %eax,z_period(%rip)
    lea z_interrupt_stack+69632(%rip), %rax''')
    s=once(s,'    testb $7, 4(%rbx)\n    jnz z_unsupported','    # AP native IST ownership is copied into this private TSS/IDT.')
    s=once(s,'    movb $0, 4(%rbx)','    # Preserve private native fault/IRQ/GC IST selections.')
    s=once(s,'    movb $0xee, 5(%rbx)','    movb $1, 4(%rbx)\n    movb $0xee, 5(%rbx)')
    # GS stays native while the original AP IDT is active, including NMI paths.
    block='''    mov $0xc0000101, %ecx
    rdmsr
    mov %eax, z_old_gs(%rip)
    mov %edx, z_old_gs+4(%rip)
    xor %eax, %eax
    xor %edx, %edx
    wrmsr'''
    s=once(s,block,'    # Original native GS remains active until the raw IDT is loaded.')
    s=once(s,'    lidt z_idtr(%rip)', '''    lidt z_idtr(%rip)
    mov $0xc0000101,%ecx
    xor %eax,%eax
    xor %edx,%edx
    wrmsr
    call z_rearm''')
    a=s.index('    mov 24(%rsp), %rax\n    cmp $255, %eax');b=s.index('6:\n    pop %rdx',a)
    s=s[:a]+'''    mov 24(%rsp),%rax
    cmp $255,%eax
    je 6f
    call z_eoi
    mov z_cancel(%rip),%rax
    cmpq $0,(%rax)
    jne z_cancelled
    mov 24(%rsp),%rax
    cmp $0xf4,%eax
    je 4f
    cmp $0xf1,%eax
    je 6f
    # C5.3 (smp=managed): a scheduler tick or kick that lands in user code means nothing to the slice; a kick
    # also left managed_pending set, so it is not lost.
    cmp $0xef,%eax
    je 6f
    # A GC stop parks on the caller's stack: end the slice and re-deliver the stop there (z_exit).
    cmp $0xf5,%eax
    je 7f
    cmp $0xf3,%eax
    jne z_bad_irq
    pop %rdx
    pop %rcx
    pop %rax
    jmp z_device_yield
4:
    mov z_context(%rip),%rax
    subq $1,72(%rax)
    jz z_timed_out
    # End one timer quantum at a complete user-frame boundary. The BSP broker
    # can requeue this context behind other Ready work without moving its stack.
    pop %rdx
    pop %rcx
    pop %rax
    jmp z_device_yield
7:
    movq $0xf5,z_gc_vector(%rip)
    pop %rdx
    pop %rcx
    pop %rax
    jmp z_device_yield
z_cancelled:
    mov z_context(%rip),%rax
    movq $130,64(%rax)
    jmp z_exit
z_timed_out:
    movq $124,64(%rax)
    jmp z_exit
z_bad_irq:
    mov 24(%rsp),%rdi
    call zenith_cpu_process_bad_irq
    ud2
''' +s[b:]
    # No AP device forwarding. A reschedule yield preserves the user's RAX/frame.
    # Only a GC stop is forwarded, to its original native handler on the caller's stack.
    s=once(s,'    mov %rax, z_pending_vector(%rip)\n    mov %rsp, %rsi', '    mov z_gc_vector(%rip), %rax\n    mov %rax, z_pending_vector(%rip)\n    movq $0, z_gc_vector(%rip)\n    mov %rsp, %rsi')
    a=s.index('z_invalid_user_stack:');b=s.index('z_user_fault:',a)
    s=s[:a]+'''z_invalid_user_stack:
    cmpq $32,(%rsp)
    jb 13f
    cmpq $254,(%rsp)
    je 13f
    cmpq $255,(%rsp)
    je 13f
    call z_eoi
13:
    mov z_context(%rip),%rax
    movq $13,96(%rax)
    movq $139,64(%rax)
    jmp z_exit
''' +s[b:]
    s=once(s,'z_forward:\n','''z_forward:
    # A kernel-origin fault must enter the original private native fault path
    # with its CPU pointer restored. This path cannot return to a damaged bridge.
    push %rax
    push %rcx
    push %rdx
    mov $0xc0000101,%ecx
    mov z_old_gs(%rip),%eax
    mov z_old_gs+4(%rip),%edx
    wrmsr
    pop %rdx
    pop %rcx
    pop %rax
''')
    s=once(s,'    lidt z_old_idtr(%rip)','    # Restore the native IDT only after restoring its native GS below.')
    s=once(s,'    mov z_pending_vector(%rip), %rax\n    test %rax, %rax','    lidt z_old_idtr(%rip)\n    mov z_pending_vector(%rip), %rax\n    test %rax, %rax')
    # Helpers touch only RAX/RCX/RDX, already saved by the user IRQ bridge.
    s+='''
.text
z_rearm:
    testl $0x400,z_apic_flags(%rip)
    jz 1f
    mov $0x838,%ecx
    mov z_period(%rip),%eax
    xor %edx,%edx
    wrmsr
    ret
1:
    mov z_apic_address(%rip),%rax
    mov z_period(%rip),%edx
    mov %edx,0x380(%rax)
    ret
.bss
.p2align 3
z_cancel: .zero 8
z_gc_vector: .zero 8
z_period: .zero 8
'''
    s=re.sub(r'\bz_[A-Za-z0-9_]+',lambda m:f'p{cpu}_'+m[0],s)
    s=s.replace('.Lprocess_',f'.Lap{cpu}_').replace('Z_FORWARD_ADDRESS',f'P{cpu}_FORWARD_ADDRESS').replace('Z_STUB_ADDRESS',f'P{cpu}_STUB_ADDRESS').replace('Z_STUB',f'P{cpu}_STUB')
    return s

def build(destination):
    original=(ROOT/'src/Native/x64/Process.s').read_text()
    text='''# Generated private AP raw bridges. BSP ABI remains untouched.
.text
.globl zenith_process_ap_dispatch
zenith_process_ap_dispatch:
    push %rdi
    call zenith_cpu_logical_id
    pop %rdi
    cmp $1,%eax
    jb 1f
    cmp $7,%eax
    ja 1f
    lea .Lap_process_table(%rip),%rcx
    jmp *(%rcx,%rax,8)
1:
    movq $95,64(%rdi)
    movq $5,96(%rdi)
    mov $95,%eax
    ret
.section .rodata
.balign 8
.Lap_process_table:
    .quad 0
'''+''.join(f'    .quad zenith_process_run_ap{cpu}\n' for cpu in range(1,8))
    text+=''.join(variant(original,cpu) for cpu in range(1,8))
    destination.parent.mkdir(parents=True,exist_ok=True)
    if not destination.exists() or destination.read_text()!=text:destination.write_text(text)
if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('output',type=Path);build(p.parse_args().output)
