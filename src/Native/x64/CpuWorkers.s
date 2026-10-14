# Limine MP revision 0 layouts: limine-protocol/include/limine.h (0BSD).
# Experimental kernel-owned integer workers. APs never enter managed code.
.data
.balign 8
.Lmp_request:
    .quad 0xc7b1dd30df4c8b88, 0x0a82e883a194f07b
    .quad 0x95a67b819a1b857e, 0xa0b61b723b6a73e0
    .quad 0, 0, 0
.Lgdt:
    .quad 0, 0x00af9a000000ffff, 0x00cf92000000ffff
.Lgdtr:
    .word 23
    .quad .Lgdt
.Lidtr:
    .word 4095
    .quad .Lidt
.bss
.balign 64
.Lslots: .skip 8*64
.Lstacks: .skip 8*16384
.Lidt: .skip 4096
.Lroot: .skip 8
.Lcount: .skip 8
.Literations: .skip 8
.Lgo: .skip 8
.text
.globl zenith_cpu_detected
.type zenith_cpu_detected, @function
zenith_cpu_detected:
    mov .Lmp_request+40(%rip), %rax
    test %rax, %rax
    jz 1f
    mov 16(%rax), %rax
1:  ret
.size zenith_cpu_detected, .-zenith_cpu_detected

.globl zenith_cpu_initialize
.type zenith_cpu_initialize, @function
zenith_cpu_initialize:
    push %rbx
    push %r12
    push %r13
    push %r14
    cmpq $0, .Lcount(%rip)
    jne .Linitialized
    mov .Lmp_request+40(%rip), %r13
    test %r13, %r13
    jz .Linitialized
    cmpq $0, 16(%r13)
    je .Linitialized
    mov %cr3, %rax
    mov %rax, .Lroot(%rip)
    lea .Lidt(%rip), %rdi
    lea .Lap_fault(%rip), %rax
    mov $256, %ecx
.Lmake_idt:
    movw %ax, (%rdi)
    movw $8, 2(%rdi)
    movw $0x8e00, 4(%rdi)
    mov %rax, %rdx
    shr $16, %rdx
    movw %dx, 6(%rdi)
    shr $16, %rdx
    movl %edx, 8(%rdi)
    movl $0, 12(%rdi)
    add $16, %rdi
    loop .Lmake_idt
    lea .Lslots(%rip), %r12
    mov 12(%r13), %eax
    mov %rax, 16(%r12)
    mov $1, %eax
    cpuid
    shr $24, %ebx
    mov %rbx, 48(%r12)
    movq $3, (%r12)
    movq $1, .Lcount(%rip)
    mov 24(%r13), %r14
    xor %ebx, %ebx
.Llaunch:
    cmp 16(%r13), %rbx
    jae .Linitialized
    cmpq $8, .Lcount(%rip)
    jae .Linitialized
    mov (%r14,%rbx,8), %rdi
    inc %rbx
    mov 4(%rdi), %eax
    cmp 12(%r13), %eax
    je .Llaunch
    # Legacy APIC request: don't start unrepresentable IDs in this first slice.
    cmp $255, %eax
    ja .Llaunch
    mov .Lcount(%rip), %rdx
    mov %rdx, %rcx
    shl $6, %rcx
    lea .Lslots(%rip), %r12
    add %rcx, %r12
    mov %rax, 16(%r12)
    inc %rdx
    shl $14, %rdx
    lea .Lstacks(%rip), %rcx
    add %rdx, %rcx
    mov %rcx, 8(%r12)
    mov %r12, 24(%rdi)
    lea .Lap_entry(%rip), %rax
    # Publish initialized context before atomic release of goto_address.
    xchg %rax, 16(%rdi)
    incq .Lcount(%rip)
    jmp .Llaunch
.Linitialized:
    mov .Lcount(%rip), %rax
    pop %r14
    pop %r13
    pop %r12
    pop %rbx
    ret
.size zenith_cpu_initialize, .-zenith_cpu_initialize

.Lap_entry:
    cli
    cld
    mov 24(%rdi), %r12
    mov 8(%r12), %rsp
    mov .Lroot(%rip), %rax
    mov %rax, %cr3
    lgdt .Lgdtr(%rip)
    pushq $8
    lea .Lap_segments(%rip), %rax
    push %rax
    lretq
.Lap_segments:
    mov $16, %ax
    mov %ax, %ds
    mov %ax, %es
    mov %ax, %ss
    xor %eax, %eax
    mov %ax, %fs
    mov %ax, %gs
    lidt .Lidtr(%rip)
    mov $1, %eax
    cpuid
    shr $24, %ebx
    mov %rbx, 48(%r12)
    movq $3, (%r12)
.Lap_idle:
    pause
    cmpq $1, (%r12)
    jne .Lap_idle
    movq $2, (%r12)
.Lap_barrier:
    pause
    cmpq $0, .Lgo(%rip)
    je .Lap_barrier
    call .Lwork
    movq $3, (%r12)
    jmp .Lap_idle
.Lap_fault:
    cli
    movq $4, (%r12)
1:  hlt
    jmp 1b

# Caller serializes tests. Submit only when every AP is idle/ready.
.globl zenith_cpu_submit
.type zenith_cpu_submit, @function
zenith_cpu_submit:
    mov %rdi, .Literations(%rip)
    movq $0, .Lgo(%rip)
    lea .Lslots+64(%rip), %rdx
    mov .Lcount(%rip), %rcx
    dec %rcx
    jle 2f
1:  movq $1, (%rdx)
    add $64, %rdx
    loop 1b
2:  ret
.size zenith_cpu_submit, .-zenith_cpu_submit

.globl zenith_cpu_execute
.type zenith_cpu_execute, @function
zenith_cpu_execute:
    push %rbx
    push %r12
    lea .Lslots(%rip), %r12
    movq $1, .Lgo(%rip)
    call .Lwork
    pop %r12
    pop %rbx
    ret
.size zenith_cpu_execute, .-zenith_cpu_execute

.Lwork:
    lfence
    rdtsc
    shl $32, %rdx
    or %rdx, %rax
    mov %rax, 24(%r12)
    movabs $0xcbf29ce484222325, %rax
    movabs $0x100000001b3, %rbx
    mov .Literations(%rip), %rcx
1:  xor %rcx, %rax
    imul %rbx, %rax
    dec %rcx
    jnz 1b
    mov %rax, 40(%r12)
    lfence
    rdtsc
    shl $32, %rdx
    or %rdx, %rax
    mov %rax, 32(%r12)
    ret

.globl zenith_cpu_field
.type zenith_cpu_field, @function
zenith_cpu_field:
    xor %eax, %eax
    cmp $8, %rdi
    jae 1f
    cmp $7, %rsi
    jae 1f
    shl $6, %rdi
    lea .Lslots(%rip), %rdx
    add %rdi, %rdx
    mov (%rdx,%rsi,8), %rax
1:  ret
.size zenith_cpu_field, .-zenith_cpu_field
.section .note.GNU-stack,"",@progbits
