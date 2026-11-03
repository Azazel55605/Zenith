# Limine MP revision-0 request. APs enter only the freestanding C1 runtime.
.data
.balign 8
.Lmp_request:
    .quad 0xc7b1dd30df4c8b88, 0x0a82e883a194f07b
    .quad 0x95a67b819a1b857e, 0xa0b61b723b6a73e0
    .quad 0, 0, 0
.text
.globl zenith_cpu_mp_response
.type zenith_cpu_mp_response,@function
zenith_cpu_mp_response:
    mov .Lmp_request+40(%rip), %rax
    ret
.size zenith_cpu_mp_response,.-zenith_cpu_mp_response
.globl zenith_cpu_ap_entry
.type zenith_cpu_ap_entry,@function
zenith_cpu_ap_entry:
    cli
    cld
    mov 24(%rdi), %r12
    mov 8(%r12), %rsp
    mov 64(%r12), %rax
    mov %rax, %cr3
    lgdt 80(%r12)
    pushq $8
    lea .Lsegments(%rip), %rax
    push %rax
    lretq
.Lsegments:
    mov $16, %ax
    mov %ax, %ds
    mov %ax, %es
    mov %ax, %ss
    xor %eax,%eax
    mov %ax,%fs
    mov %ax,%gs
    mov $0xc0000101,%ecx
    mov %r12,%rax
    mov %rax,%rdx
    shr $32,%rdx
    wrmsr
    mov $24,%ax
    ltr %ax
    # Each AP owns its FP environment; never inherit a bootloader lazy-FP trap.
    mov %cr0,%rax
    and $-13,%rax
    or $2,%rax
    mov %rax,%cr0
    mov %cr4,%rax
    or $0x600,%rax
    mov %rax,%cr4
    fninit
    sub $16,%rsp
    movl $0x1f80,(%rsp)
    ldmxcsr (%rsp)
    add $16,%rsp
    lidt 90(%r12)
    mov %r12,%rdi
    call zenith_cpu_worker
    ud2
.size zenith_cpu_ap_entry,.-zenith_cpu_ap_entry

# All maskable AP interrupts use IST1. Preserve every interrupted integer
# register; C is built general-registers-only and never touches FP/SIMD state.
.globl zenith_cpu_wake_entry
zenith_cpu_wake_entry:
    pushq $0xf1
    jmp .Lnative_interrupt
.globl zenith_cpu_reschedule_entry
zenith_cpu_reschedule_entry:
    pushq $0xf3
    jmp .Lnative_interrupt
.globl zenith_cpu_timer_entry
zenith_cpu_timer_entry:
    pushq $0xf4
.Lnative_interrupt:
    cld
    push %rax
    push %rbx
    push %rcx
    push %rdx
    push %rsi
    push %rdi
    push %rbp
    push %r8
    push %r9
    push %r10
    push %r11
    push %r12
    push %r13
    push %r14
    push %r15
    mov 120(%rsp),%rdx
    mov %rsp,%rsi
    mov %rsp,%rbx
    and $-16,%rsp
    mov %gs:72,%rdi
    call zenith_cpu_interrupt
    mov %rbx,%rsp
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %r11
    pop %r10
    pop %r9
    pop %r8
    pop %rbp
    pop %rdi
    pop %rsi
    pop %rdx
    pop %rcx
    pop %rbx
    pop %rax
    add $8,%rsp
    iretq
# BSP-only completion doorbell. Its sole C callee is freestanding and compiled
# general-registers-only; no FP register or managed/scheduler state is touched.
# Hardware saves flags/CS/RIP; preserve all GPRs and align either stack entry.
.globl zenith_cpu_completion_entry
zenith_cpu_completion_entry:
    cld
.irp reg,rax,rbx,rcx,rdx,rsi,rdi,rbp,r8,r9,r10,r11,r12,r13,r14,r15
    push %\reg
.endr
    mov %rsp,%rbx
    and $-16,%rsp
    call zenith_cpu_completion_eoi
    mov %rbx,%rsp
.irp reg,r15,r14,r13,r12,r11,r10,r9,r8,rbp,rdi,rsi,rdx,rcx,rbx,rax
    pop %\reg
.endr
    iretq
.globl zenith_cpu_spurious_entry
zenith_cpu_spurious_entry:
    # Spurious APIC delivery sets no ISR bit: do not issue EOI.
    iretq

.altmacro
.macro CPU_FAULT number
.Lfault_\number:
    cli
    pushq $\number
    jmp .Lfault_common
.endm
.set number,0
.rept 256
    CPU_FAULT %number
    .set number,number+1
.endr
.noaltmacro
.Lfault_common:
    cld
    mov (%rsp),%rsi
    mov %rsp,%rdx
    mov %gs:72,%rdi
    and $-16,%rsp
    call zenith_cpu_fault
    ud2
.section .rodata
.balign 8
.globl zenith_cpu_fault_entries
zenith_cpu_fault_entries:
.altmacro
.macro CPU_FAULT_ADDRESS number
    .quad .Lfault_\number
.endm
.set number,0
.rept 256
    CPU_FAULT_ADDRESS %number
    .set number,number+1
.endr
.noaltmacro
.section .note.GNU-stack,"",@progbits
