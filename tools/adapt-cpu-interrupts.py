#!/usr/bin/env python3
"""Adapt only the exact Cosmos 3.0.89 IRQ source into a private build copy."""
import argparse
import hashlib
from pathlib import Path
PINNED = '224c009940790bb948c4d7d696b8eab471c4039bff18db6295834fa3e8616cfa'

def adapt(source):
    if hashlib.sha256(source.encode()).hexdigest() != PINNED:
        raise RuntimeError('Cosmos 3.0.89 interrupt source changed; refusing per-CPU adaptation')
    def once(old,new):
        nonlocal source
        if source.count(old)!=1: raise RuntimeError('Interrupt adaptation contract changed: '+old)
        source=source.replace(old,new)
    once('    mov [rip + _context_switch_target_rsp], rdi',
         '    call zenith_execution_slot\n    mov [rax], rdi')
    once('    mov rax, [rip + _context_switch_target_rsp]\n    ret',
         '    call zenith_execution_slot\n    mov rax, [rax]\n    ret')
    once('    mov rax, [rip + _irq_last_error_code]',
         '    call zenith_execution_slot\n    mov rax, [rax + 16]')
    once('    mov [rip + _context_switch_is_new_thread], rdi',
         '    call zenith_execution_slot\n    mov [rax + 8], rdi')
    once('    pop qword ptr [rip + _irq_last_error_code]', '''    push rax
    call zenith_execution_slot
    push rdx
    mov rdx, [rsp + 16]
    mov [rax + 16], rdx
    pop rdx
    pop rax
    add rsp, 8''')
    once('''    mov rax, [rip + _context_switch_target_rsp]
    test rax, rax''', '''    call zenith_execution_slot
    add qword ptr [rax + 24], 1
    mov rcx, [rax]
    test rcx, rcx''')
    once('''    xor rcx, rcx
    mov [rip + _context_switch_target_rsp], rcx''', '    mov qword ptr [rax], 0')
    once('''    mov rdx, [rip + _context_switch_is_new_thread]
    mov [rip + _context_switch_is_new_thread], rcx''',
         '''    mov rdx, [rax + 8]
    mov qword ptr [rax + 8], 0''')
    once('    mov rsp, rax\n\n    // Store flag', '    mov rsp, rcx\n\n    // Store flag')
    once('''    mov rax, [rsp + 256 + 120 + 24]  // Read TempRcx (is_new_thread flag)
    mov [rip + _temp_is_new_thread], rax  // Save it in a global variable''',
         '    // TempRcx stays on this thread stack until the exit decision.')
    once('    cmp qword ptr [rip + _temp_is_new_thread], 0',
         '    cmp qword ptr [rsp - 8], 0')
    # Change all frame offsets together; IRQContext still starts at the GPRs.
    start = source.index('    // Save XMM registers (SSE/SIMD state)')
    end = source.index('    // === CALL HANDLER ===',start)
    source = source[:start] + '    sub rsp, 512\n    fxsave64 [rsp]\n\n' + source[end:]
    start = source.index('    // Restore XMM')
    end = source.index('    // Restore GPRs',start)
    source = source[:start] + '    fxrstor64 [rsp]\n    add rsp, 512\n\n' + source[end:]
    once('lea rdi, [rsp + 256]','lea rdi, [rsp + 512]')
    once('mov [rsp + 256 + 120 + 24], rdx','mov [rsp + 512 + 120 + 24], rdx')
    # No obsolete shared scratch symbols survive the link, even unused ones.
    for name in ('_context_switch_target_rsp','_context_switch_is_new_thread','_temp_is_new_thread','_irq_last_error_code'):
        source=source.replace('.global '+name+'\n','').replace(name+': .zero 8','')
        if name in source: raise RuntimeError('Shared IRQ scratch reference remains: '+name)
    return '// Zenith C2: signature-checked per-CPU IRQ state; see docs/MULTICORE.md\n'+source

def adapt_exceptions(source):
    expected='db1d8dc08fc54d055e330fb3184d1ccd8c291b95e1b1427b008a3dd2634f3db5'
    if hashlib.sha256(source.encode()).hexdigest()!=expected:
        raise RuntimeError('Cosmos 3.0.89 exception source changed; refusing CPU-chain adaptation')
    if source.count('    lea     rax, [rip + __cosmos_exinfo_stack_head]')!=2 or source.count('    lea     r11, [rip + __cosmos_exinfo_stack_head]')!=1:
        raise RuntimeError('Cosmos exception chain access contract changed')
    source=source.replace('    lea     rax, [rip + __cosmos_exinfo_stack_head]',
                          '    call    zenith_execution_exception_slot')
    source=source.replace('    lea     r11, [rip + __cosmos_exinfo_stack_head]',
                          '    push    rax\n    call    zenith_execution_exception_slot\n    mov     r11, rax\n    pop     rax')
    source=source.replace('.global __cosmos_exinfo_stack_head\n__cosmos_exinfo_stack_head: .quad 0','')
    return '// Zenith C3: CPU-owned exception chain for pinned execution.\n'+source

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('source',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
    result=(adapt_exceptions if a.source.name=='ExceptionHandling.s' else adapt)(a.source.read_text())
    a.output.parent.mkdir(parents=True,exist_ok=True)
    if not a.output.exists() or a.output.read_text()!=result:a.output.write_text(result)
if __name__=='__main__':main()
