.section .text.start,"ax",@progbits
.globl _start
_start:
    cld
    mov (%rsp), %rdi
    lea 8(%rsp), %rsi
    lea 16(%rsp,%rdi,8), %rdx
    and $-16, %rsp
    call main
    mov %eax, %edi
    mov $60, %eax
    int $0xfe
    ud2
.globl zenith_smp_state
zenith_smp_state:
    push %rbx
    push %rbp
    push %r12
    push %r13
    push %r14
    push %r15
    sub $32,%rsp
    fnstcw (%rsp)
    stmxcsr 4(%rsp)
    movw $0x077f,8(%rsp)
    movl $0x3f80,12(%rsp)
    fldcw 8(%rsp)
    ldmxcsr 12(%rsp)
    mov $0x12345678,%ebx
    mov $0x23456789,%ebp
    mov $0x3456789a,%r12d
    mov $0x456789ab,%r13d
    mov $0x56789abc,%r15d
    mov $0x13579bdf,%ecx
    mov $0x2468ace0,%edx
    mov $0x10293847,%esi
    mov $0x11223344,%r8d
    mov $0x22334455,%r9d
    mov $0x33445566,%r10d
    mov $0x44556677,%r11d
    mov $39,%eax
    int $0xfe
    mov %rax,%r14
    mov $0x6789abcd,%eax
    movq %rax,%xmm0
    movq %rax,%xmm15
    fld1
    movl $64,16(%rsp)
1:
    # isatty is brokered, unlike the fast identity calls. This forces a complete
    # save/service/requeue/resume while the register and FP sentinels are live.
    mov $400,%eax
    mov $1,%edi
    int $0xfe
    test %rax,%rax
    js 2f
    mov $39,%eax
    int $0xfe
    cmp %r14,%rax
    jne 2f
    cmp $0x12345678,%rbx
    jne 2f
    cmp $0x23456789,%rbp
    jne 2f
    cmp $0x3456789a,%r12
    jne 2f
    cmp $0x456789ab,%r13
    jne 2f
    cmp $0x56789abc,%r15
    jne 2f
    cmp $0x13579bdf,%rcx
    jne 2f
    cmp $0x2468ace0,%rdx
    jne 2f
    cmp $0x10293847,%rsi
    jne 2f
    cmp $0x11223344,%r8
    jne 2f
    cmp $0x22334455,%r9
    jne 2f
    cmp $0x33445566,%r10
    jne 2f
    cmp $0x44556677,%r11
    jne 2f
    cmp $1,%rdi
    jne 2f
    movq %xmm0,%rax
    cmp $0x6789abcd,%rax
    jne 2f
    movq %xmm15,%rax
    cmp $0x6789abcd,%rax
    jne 2f
    fnstcw 20(%rsp)
    cmpw $0x077f,20(%rsp)
    jne 2f
    stmxcsr 20(%rsp)
    cmpl $0x3f80,20(%rsp)
    jne 2f
    subl $1,16(%rsp)
    jnz 1b
    fstpl 24(%rsp)
    movabs $0x3ff0000000000000,%rax
    cmp %rax,24(%rsp)
    jne 2f
    xor %eax,%eax
    jmp 3f
2:
    mov $1,%eax
    fninit
3:
    fldcw (%rsp)
    ldmxcsr 4(%rsp)
    add $32,%rsp
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %rbp
    pop %rbx
    ret
.section .note.zenith,"a",@note
.p2align 2
.long 7,4,1
.asciz "ZENITH"
.p2align 2
.long 1
.section .note.GNU-stack,"",@progbits
