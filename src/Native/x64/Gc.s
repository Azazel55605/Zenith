# C3 native-only root capture; managed code is never called from the stop IRQ.
# IST3 is distinct from the C1 wake/reschedule/timer IST1 and fault IST2.
.text
.macro GC_SAVE
    push %rax
    push %rcx
    push %rdx
    push %rbx
    push %rbp
    push %rsi
    push %rdi
    push %r8
    push %r9
    push %r10
    push %r11
    push %r12
    push %r13
    push %r14
    push %r15
    sub $512,%rsp
    fxsave64 (%rsp)
.endm
.macro GC_RESTORE
    fxrstor64 (%rsp)
    add $512,%rsp
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %r11
    pop %r10
    pop %r9
    pop %r8
    pop %rdi
    pop %rsi
    pop %rbp
    pop %rbx
    pop %rdx
    pop %rcx
    pop %rax
.endm
.globl zenith_gc_poll
zenith_gc_poll:
    # At a normal call, return address is 8 mod 16; 15 pushes align FXSAVE.
    GC_SAVE
    mov %rsp,%rdi
    lea 632(%rsp),%rsi
    call zenith_gc_park
    GC_RESTORE
    ret
.globl zenith_gc_stop_entry
zenith_gc_stop_entry:
    # IST entry: CPU frame is 8 mod 16, so 15 pushes align FXSAVE.
    cld
    GC_SAVE
    mov %rsp,%rdi
    mov 656(%rsp),%rsi
    call zenith_gc_park
    # EOI is issued by a native-only helper after the epoch resumes.
    call zenith_cpu_gc_eoi
    GC_RESTORE
    iretq
# Unsupported managed AP failures use the already installed native fault IST;
# they never enter the BSP's UART/panic UI or attempt allocator repair.
.globl zenith_cpu_ap_fail_stop
zenith_cpu_ap_fail_stop:
    cli
    ud2
    jmp zenith_gc_fail_stop
.globl zenith_gc_fail_stop
zenith_gc_fail_stop:
    cli
.Lgc_failed:
    hlt
    jmp .Lgc_failed
.section .note.GNU-stack,"",@progbits
