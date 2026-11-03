# C2 CPU-local IRQ/switch state. This accessor never allocates, uses FP, reads
# GS or changes interrupt enable. It is safe before managed scheduler startup
# and while the process bridge has cleared user FS/GS bases. CPUID leaf 1 IDs
# address 256 cache-line-separated slots; C1 admits only those legacy APIC IDs.
.text
.globl zenith_execution_slot
.type zenith_execution_slot,@function
zenith_execution_slot:
    push %rbx
    push %rcx
    push %rdx
    pushfq
    mov $1,%eax
    cpuid
    shr $24,%ebx
    shl $6,%ebx
    lea zenith_execution_slots(%rip),%rax
    add %rbx,%rax
    popfq
    pop %rdx
    pop %rcx
    pop %rbx
    ret
.size zenith_execution_slot,.-zenith_execution_slot

# Reject a non-BSP raw process entry before touching any shared bridge state.
# Return ENOTSUP using the existing Context layout; preserve callee-saved GPRs.
.globl zenith_execution_process_allowed
.type zenith_execution_process_allowed,@function
zenith_execution_process_allowed:
    push %rbx
    push %rdi
    sub $8,%rsp
    call zenith_cpu_mp_response
    test %rax,%rax
    jz .Lprocess_deny
    mov 12(%rax),%edi
    mov $1,%eax
    cpuid
    shr $24,%ebx
    xor %eax,%eax
    cmp %edi,%ebx
    sete %al
    jmp .Lprocess_done
.Lprocess_deny:
    xor %eax,%eax
.Lprocess_done:
    add $8,%rsp
    pop %rdi
    pop %rbx
    ret
.size zenith_execution_process_allowed,.-zenith_execution_process_allowed
.bss
.balign 64
.globl zenith_execution_slots
zenith_execution_slots: .zero 16384
.section .note.GNU-stack,"",@progbits

.text
# Native-only C2 ownership probe. Uses real adapted switch setters/getter on
# this AP, checks CPU-local slot storage and full FXSAVE restoration around the
# native wake interrupt. Never supplies a live managed switch target.
.globl zenith_execution_test
.type zenith_execution_test,@function
zenith_execution_test:
    push %rbx
    push %r12
    push %r13
    sub $1040,%rsp
    fxsave64 (%rsp)
    call zenith_execution_slot
    mov %rax,%r12
    mov (%r12),%r13
    mov 8(%r12),%rbx
    # Exercise the real first-entry AP refusal, with a private dummy context.
    # It must return before CLI/table/global bridge writes and retain the canary.
    movq $0x1234,512(%rsp)
    movq $0,576(%rsp)
    movq $0,608(%rsp)
    lea 512(%rsp),%rdi
    call zenith_process_run
    cmpq $95,576(%rsp)
    jne .Lexecution_fail
    cmpq $5,608(%rsp)
    jne .Lexecution_fail
    cmpq $0x1234,512(%rsp)
    jne .Lexecution_fail
    mov %r12,%rdi
    call _native_set_context_switch_sp
    mov $1,%edi
    call _native_set_context_switch_new_thread
    call _native_get_context_switch_sp
    cmp %r12,%rax
    jne .Lexecution_fail
    cmpq $1,8(%r12)
    jne .Lexecution_fail
    mov $0x5a123456,%eax
    movq %rax,%xmm0
    movq %r12,%xmm15
    movl $0x3f80,1024(%rsp)
    ldmxcsr 1024(%rsp)
    movw $0x77f,1028(%rsp)
    fldcw 1028(%rsp)
    fld1
    int $0xf1
    call zenith_cpu_timer_probe
    cmp $1,%rax
    jne .Lexecution_fail
    movq %xmm0,%rax
    cmp $0x5a123456,%rax
    jne .Lexecution_fail
    movq %xmm15,%rax
    cmp %r12,%rax
    jne .Lexecution_fail
    stmxcsr 1024(%rsp)
    cmpl $0x3f80,1024(%rsp)
    jne .Lexecution_fail
    fnstcw 1028(%rsp)
    cmpw $0x77f,1028(%rsp)
    jne .Lexecution_fail
    fstpl 1032(%rsp)
    movabs $0x3ff0000000000000,%rax
    cmp %rax,1032(%rsp)
    jne .Lexecution_fail
    mov $1,%eax
    jmp .Lexecution_restore
.Lexecution_fail:
    xor %eax,%eax
.Lexecution_restore:
    mov %r13,(%r12)
    mov %rbx,8(%r12)
    fxrstor64 (%rsp)
    add $1040,%rsp
    pop %r13
    pop %r12
    pop %rbx
    ret
.size zenith_execution_test,.-zenith_execution_test
.text
# BSP regression: retain non-default x87/MXCSR plus XMM values through sixteen
# real interrupt returns (which may dispatch other managed threads). No managed
# calls while test FP values are live. A TSC bound prevents waiting indefinitely.
.globl zenith_execution_fp_preempt_test
.type zenith_execution_fp_preempt_test,@function
zenith_execution_fp_preempt_test:
    push %rbx
    push %rbp
    push %r12
    push %r13
    push %r14
    push %r15
    pushfq
    sub $528,%rsp
    fxsave64 (%rsp)
    call zenith_execution_slot
    mov %rax,%r12
    mov 24(%r12),%r13
    rdtsc
    shl $32,%rdx
    or %rdx,%rax
    mov %rax,%r14
    movabs $10000000000,%r15
    movw $0x77f,516(%rsp)
    fldcw 516(%rsp)
    fld1
    movl $0x3f80,512(%rsp)
    ldmxcsr 512(%rsp)
    mov $0x11223344,%eax
    movq %rax,%xmm0
    mov $0x55667788,%eax
    movq %rax,%xmm15
    sti
.Lfp_interrupt_wait:
    mov 24(%r12),%rax
    sub %r13,%rax
    cmp $16,%rax
    jae .Lfp_interrupt_observed
    rdtsc
    shl $32,%rdx
    or %rdx,%rax
    sub %r14,%rax
    cmp %r15,%rax
    ja .Lfp_preempt_fail
    pause
    jmp .Lfp_interrupt_wait
.Lfp_interrupt_observed:
    cli
    movq %xmm0,%rax
    cmp $0x11223344,%rax
    jne .Lfp_preempt_fail
    movq %xmm15,%rax
    cmp $0x55667788,%rax
    jne .Lfp_preempt_fail
    stmxcsr 512(%rsp)
    cmpl $0x3f80,512(%rsp)
    jne .Lfp_preempt_fail
    fnstcw 516(%rsp)
    cmpw $0x77f,516(%rsp)
    jne .Lfp_preempt_fail
    fstpl 520(%rsp)
    movabs $0x3ff0000000000000,%rax
    cmp %rax,520(%rsp)
    jne .Lfp_preempt_fail
    mov $1,%eax
    jmp .Lfp_preempt_restore
.Lfp_preempt_fail:
    cli
    xor %eax,%eax
.Lfp_preempt_restore:
    fxrstor64 (%rsp)
    add $528,%rsp
    popfq
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %rbp
    pop %rbx
    ret
.size zenith_execution_fp_preempt_test,.-zenith_execution_fp_preempt_test
