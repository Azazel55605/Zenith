# Zenith foreground x86_64 process entry and syscall/fault boundary.
# Layout: NativeProcess.Context; ABI details in docs/NATIVE-PROGRAMS.md.
.text
.globl zenith_process_run
.type zenith_process_run,@function
zenith_process_run:
    push %rbp
    push %rbx
    push %r12
    push %r13
    push %r14
    push %r15
    pushfq
    cli
    cld
    mov %ds, z_old_ds_selector(%rip)
    mov %es, z_old_es_selector(%rip)
    mov %fs, z_old_fs_selector(%rip)
    mov %gs, z_old_gs_selector(%rip)
    mov %ss, z_old_ss_selector(%rip)
    mov %rsp, z_saved_rsp(%rip)
    mov %rdi, z_context(%rip)
    mov %cr3, %rax
    mov %rax, z_old_cr3(%rip)
    mov %cr4, %rax
    mov %rax, z_old_cr4(%rip)
    mov $1, %r9d
    test $0x1000, %eax
    jnz z_unsupported
    mov $2, %r9d
    mov $0x80000000, %eax
    cpuid
    cmp $0x80000001, %eax
    jb z_unsupported
    mov $0x80000001, %eax
    cpuid
    test $0x100000, %edx
    jz z_unsupported
    sgdt z_old_gdtr(%rip)
    sidt z_old_idtr(%rip)
    mov $3, %r9d
    cmpw $4095, z_old_idtr(%rip)
    jne z_unsupported
    mov $4, %r9d
    movzwl z_old_gdtr(%rip), %eax
    cmp $959, %eax
    ja z_unsupported
    and $7, %eax
    cmp $7, %eax
    jne z_unsupported
    mov $5, %r9d
    mov $1, %eax
    cpuid
    shr $24, %ebx
    cmpl $0, z_initialized(%rip)
    je 1f
    cmp z_owner_cpu(%rip), %ebx
    jne z_unsupported
1:
    mov %ebx, z_owner_cpu(%rip)
    mov z_old_gdtr+2(%rip), %rax
    lea z_gdt(%rip), %rcx
    cmp %rcx, %rax
    jne z_copy_kernel_gdt
    mov z_kernel_gdt_bytes(%rip), %r12
    jmp z_append_user_gdt
z_copy_kernel_gdt:
    movzwl z_old_gdtr(%rip), %ecx
    inc %ecx
    mov %ecx, %r12d
    mov %r12, z_kernel_gdt_bytes(%rip)
    mov z_old_gdtr+2(%rip), %rsi
    lea z_gdt(%rip), %rdi
    rep movsb
z_append_user_gdt:
    lea z_gdt(%rip), %rbx
    add %r12, %rbx
    movabs $0x00affb000000ffff, %rax
    mov %rax, (%rbx)
    movabs $0x00cff3000000ffff, %rax
    mov %rax, 8(%rbx)
    lea z_tss(%rip), %rax
    movw $103, 16(%rbx)
    mov %ax, 18(%rbx)
    shr $16, %rax
    mov %al, 20(%rbx)
    movb $0x89, 21(%rbx)
    movb $0, 22(%rbx)
    shr $8, %rax
    mov %al, 23(%rbx)
    shr $8, %rax
    mov %eax, 24(%rbx)
    movl $0, 28(%rbx)
    lea z_interrupt_stack+69632(%rip), %rax
    mov %rax, z_tss+4(%rip)
    movw $104, z_tss+102(%rip)
    lea z_gdt(%rip), %rax
    mov %rax, z_gdtr+2(%rip)
    lea 31(%r12), %eax
    mov %ax, z_gdtr(%rip)
    lea 16(%r12), %eax
    mov %ax, z_own_tr(%rip)
    mov %r12w, z_user_cs(%rip)
    orw $3, z_user_cs(%rip)
    lea 8(%r12), %eax
    or $3, %eax
    mov %ax, z_user_ss(%rip)
    lea z_idt(%rip), %rdi
    mov z_old_idtr+2(%rip), %rsi
    mov $4096, %ecx
    rep movsb
    xor %r12d, %r12d
    lea z_old_stubs(%rip), %r13
    lea z_stub_table(%rip), %r14
    lea z_idt(%rip), %rbx
2:
    mov $6, %r9d
    testb $7, 4(%rbx)
    jnz z_unsupported
    movzwl (%rbx), %eax
    movzwl 6(%rbx), %edx
    shl $16, %rdx
    or %rdx, %rax
    mov 8(%rbx), %edx
    shl $32, %rdx
    or %rdx, %rax
    mov %rax, (%r13,%r12,8)
    mov (%r14,%r12,8), %rax
    mov %ax, (%rbx)
    shr $16, %rax
    mov %ax, 6(%rbx)
    shr $16, %rax
    mov %eax, 8(%rbx)
    mov %cs, %ax
    mov %ax, 2(%rbx)
    movb $0, 4(%rbx)
    movb $0x8e, 5(%rbx)
    cmp $254, %r12d
    jne 3f
    movb $0xee, 5(%rbx)
3:
    add $16, %rbx
    inc %r12d
    cmp $256, %r12d
    jne 2b
    lea z_idt(%rip), %rax
    mov %rax, z_idtr+2(%rip)
    movw $4095, z_idtr(%rip)
    # Preserve the caller's floating-point environment. A native client may use
    # x87/SSE even though the initial freestanding tool is compiled without them.
    mov %cr0, %rax
    mov %rax, z_old_cr0(%rip)
    clts
    fxsave64 z_old_fp(%rip)
    movw $0x037f, z_user_fp(%rip)
    movl $0x1f80, z_user_fp+24(%rip)
    fxrstor64 z_user_fp(%rip)
    mov $0xc0000080, %ecx
    rdmsr
    mov %eax, z_old_efer(%rip)
    mov %edx, z_old_efer+4(%rip)
    and $-2, %eax
    or $0x800, %eax
    wrmsr
    mov $0x174, %ecx
    rdmsr
    mov %eax, z_old_sysenter(%rip)
    mov %edx, z_old_sysenter+4(%rip)
    xor %eax, %eax
    xor %edx, %edx
    wrmsr
    mov $0xc0000100, %ecx
    rdmsr
    mov %eax, z_old_fs(%rip)
    mov %edx, z_old_fs+4(%rip)
    xor %eax, %eax
    xor %edx, %edx
    wrmsr
    mov $0xc0000101, %ecx
    rdmsr
    mov %eax, z_old_gs(%rip)
    mov %edx, z_old_gs+4(%rip)
    xor %eax, %eax
    xor %edx, %edx
    wrmsr
    mov $0x1b, %ecx
    rdmsr
    mov %eax, z_apic_flags(%rip)
    and $0xfffff000, %eax
    shl $32, %rdx
    or %rdx, %rax
    mov z_context(%rip), %rdx
    add 88(%rdx), %rax
    mov %rax, z_apic_address(%rip)
    mov z_old_cr4(%rip), %rax
    # Disable OSXSAVE for this initial ABI: no AVX/extended-state exposure.
    and $-6619137, %rax
    mov %rax, %cr4
    lgdt z_gdtr(%rip)
    mov z_own_tr(%rip), %ax
    ltr %ax
    movl $1, z_initialized(%rip)
    lidt z_idtr(%rip)
    mov z_context(%rip), %rbx
    mov (%rbx), %rax
    mov %rax, %cr3
    cmpq $0, 128(%rbx)
    jne z_resume_user
    movzwq z_user_ss(%rip), %rax
    push %rax
    pushq 16(%rbx)
    pushq $0x202
    movzwq z_user_cs(%rip), %rax
    push %rax
    pushq 8(%rbx)
    xor %eax, %eax
    xor %ebx, %ebx
    xor %ecx, %ecx
    xor %edx, %edx
    xor %esi, %esi
    xor %edi, %edi
    xor %ebp, %ebp
    xor %r8d, %r8d
    xor %r9d, %r9d
    xor %r10d, %r10d
    xor %r11d, %r11d
    xor %r12d, %r12d
    xor %r13d, %r13d
    xor %r14d, %r14d
    xor %r15d, %r15d
    iretq
# Resume the exact saved interrupt frame and all registers after a managed call.
z_resume_user:
    mov 176(%rbx), %rsi
    mov 168(%rbx), %rax
    mov %rax, 112(%rsi)
    lea z_interrupt_stack+69632-176(%rip), %rdi
    mov %rdi, %rsp
    mov $22, %ecx
    rep movsq
    mov 184(%rbx), %rax
    fxrstor64 (%rax)
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
    add $16, %rsp
    iretq
z_unsupported:
    mov z_context(%rip), %rax
    movq $95, 64(%rax)
    mov %r9, 96(%rax)
    jmp z_restore_registers
z_bridge:
    push %rax
    mov 32(%rsp), %rax
    and $3, %eax
    cmp $3, %eax
    pop %rax
    jne z_forward
    # IRET with a noncanonical user RSP would fault in ring 0. Contain it
    # before every return/yield, including raw timer interrupts.
    push %rax
    mov 48(%rsp), %rax
    shr $47, %rax
    test %rax, %rax
    pop %rax
    jnz z_invalid_user_stack
    cmpq $254, (%rsp)
    je z_syscall
    cmpq $32, (%rsp)
    jb z_user_fault
    push %rax
    push %rcx
    push %rdx
    mov 24(%rsp), %rax
    cmp $255, %eax
    je 6f
    cmp $239, %eax
    je 4f
    cmp $32, %eax
    jne 5f
4:
    mov z_context(%rip), %rax
    subq $1, 72(%rax)
5:
    call z_eoi
    mov z_context(%rip), %rax
    cmpq $0, 72(%rax)
    jne 6f
    movq $124, 64(%rax)
    jmp z_exit
6:
    pop %rdx
    pop %rcx
    pop %rax
    add $16, %rsp
    iretq
z_eoi:
    testl $0x800, z_apic_flags(%rip)
    jz 7f
    testl $0x400, z_apic_flags(%rip)
    jz 8f
    mov $0x80b, %ecx
    xor %eax, %eax
    xor %edx, %edx
    wrmsr
    ret
8:
    mov z_apic_address(%rip), %rax
    movl $0, 0xb0(%rax)
    ret
7:
    mov $0x20, %al
    out %al, $0xa0
    out %al, $0x20
    ret
z_invalid_user_stack:
    cmpq $32, (%rsp)
    jb 13f
    cmpq $254, (%rsp)
    je 13f
    cmpq $255, (%rsp)
    je 13f
    call z_eoi
13:
    mov z_context(%rip), %rax
    movq $13, 96(%rax)
    movq $139, 64(%rax)
    jmp z_exit
z_user_fault:
    mov z_context(%rip), %rax
    mov (%rsp), %rcx
    mov %rcx, 96(%rax)
    movq $139, 64(%rax)
    jmp z_exit
z_forward:
    push %rax
    push %rcx
    mov 16(%rsp), %rax
    lea z_forward_table(%rip), %rcx
    mov (%rcx,%rax,8), %rax
    mov %rax, 16(%rsp)
    pop %rcx
    pop %rax
    ret
z_syscall:
    cmp $74, %rax
    je z_service
    cmp $1, %rax
    je z_service
    cmp $400, %rax
    je z_service
    cmp $401, %rax
    je z_service
    cmp $402, %rax
    je z_service
    cmp $403, %rax
    je z_service
    cmp $404, %rax
    je z_service
    cmp $405, %rax
    je z_service
    cmp $406, %rax
    je z_service
    cmp $407, %rax
    je z_service
    cmp $408, %rax
    je z_service
    cmp $0, %rax
    je z_service
    cmp $2, %rax
    je z_service
    cmp $3, %rax
    je z_service
    cmp $4, %rax
    je z_service
    cmp $79, %rax
    je z_service
    cmp $217, %rax
    je z_service
    cmp $39, %rax
    je z_get_pid
    cmp $102, %rax
    je z_get_uid
    cmp $104, %rax
    je z_get_gid
    cmp $60, %rax
    jne 9f
    mov z_context(%rip), %rax
    and $255, %edi
    mov %rdi, 64(%rax)
    jmp z_exit
9:
    mov $-38, %rax
    add $16, %rsp
    iretq
z_get_pid:
    mov z_context(%rip), %rax
    mov 112(%rax), %rax
    add $16, %rsp
    iretq
z_get_uid:
    mov z_context(%rip), %rax
    mov 104(%rax), %rax
    add $16, %rsp
    iretq
z_get_gid:
    mov z_context(%rip), %rax
    mov 120(%rax), %rax
    add $16, %rsp
    iretq
# Yield only to the original managed call stack/CR3/IDT. Never invoke managed
# filesystem or driver code on a raw user interrupt frame.
z_service:
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
    cld
    mov z_context(%rip), %rbx
    mov 112(%rsp), %rax
    mov %rax, 136(%rbx)
    mov 72(%rsp), %rax
    mov %rax, 144(%rbx)
    mov 80(%rsp), %rax
    mov %rax, 152(%rbx)
    mov 88(%rsp), %rax
    mov %rax, 160(%rbx)
    mov %rsp, %rsi
    mov 176(%rbx), %rdi
    mov $22, %ecx
    rep movsq
    mov 184(%rbx), %rax
    fxsave64 (%rax)
    movq $1, 128(%rbx)
    movq $4096, 64(%rbx)
    jmp z_exit
z_exit:
    cli
    cld
    mov z_old_cr3(%rip), %rax
    mov %rax, %cr3
    lidt z_old_idtr(%rip)
    # Retain the kernel-owned GDT/TSS; its interrupt stack has permanent lifetime.
    mov z_old_cr4(%rip), %rax
    mov %rax, %cr4
    mov $0xc0000080, %ecx
    mov z_old_efer(%rip), %eax
    mov z_old_efer+4(%rip), %edx
    wrmsr
    mov $0x174, %ecx
    mov z_old_sysenter(%rip), %eax
    mov z_old_sysenter+4(%rip), %edx
    wrmsr
    mov z_old_ds_selector(%rip), %ax
    mov %ax, %ds
    mov z_old_es_selector(%rip), %ax
    mov %ax, %es
    mov z_old_fs_selector(%rip), %ax
    mov %ax, %fs
    mov z_old_gs_selector(%rip), %ax
    mov %ax, %gs
    mov z_old_ss_selector(%rip), %ax
    mov %ax, %ss
    fxrstor64 z_old_fp(%rip)
    mov z_old_cr0(%rip), %rax
    mov %rax, %cr0
    mov $0xc0000100, %ecx
    mov z_old_fs(%rip), %eax
    mov z_old_fs+4(%rip), %edx
    wrmsr
    mov $0xc0000101, %ecx
    mov z_old_gs(%rip), %eax
    mov z_old_gs+4(%rip), %edx
    wrmsr
z_restore_registers:
    mov z_context(%rip), %rax
    mov 64(%rax), %rax
    mov z_saved_rsp(%rip), %rsp
    popfq
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %rbx
    pop %rbp
    ret

# All stubs have vector,error,RIP,CS,RFLAGS,RSP,SS on entry to z_bridge.
# Kernel-origin interrupts tail-call the original Cosmos stub with its original
# hardware frame. The user foreground window services its own bounded watchdog.
.altmacro
.macro Z_STUB number
z_stub_\number:
    .if !(\number == 8 || \number == 10 || \number == 11 || \number == 12 || \number == 13 || \number == 14 || \number == 17 || \number == 21 || \number == 29 || \number == 30)
        pushq $0
    .endif
    pushq $\number
    jmp z_bridge
z_forward_\number:
    .if !(\number == 8 || \number == 10 || \number == 11 || \number == 12 || \number == 13 || \number == 14 || \number == 17 || \number == 21 || \number == 29 || \number == 30)
        add $8, %rsp
    .endif
    jmp *z_old_stubs+(\number*8)(%rip)
.endm
.set z_vector,0
.rept 256
    Z_STUB %z_vector
    .set z_vector,z_vector+1
.endr
.section .rodata
.p2align 3
.macro Z_STUB_ADDRESS number
    .quad z_stub_\number
.endm
z_stub_table:
.set z_vector,0
.rept 256
    Z_STUB_ADDRESS %z_vector
    .set z_vector,z_vector+1
.endr
.macro Z_FORWARD_ADDRESS number
    .quad z_forward_\number
.endm
z_forward_table:
.set z_vector,0
.rept 256
    Z_FORWARD_ADDRESS %z_vector
    .set z_vector,z_vector+1
.endr
.noaltmacro

.bss
.p2align 4
z_gdt: .zero 1024
z_idt: .zero 4096
z_tss: .zero 104
z_old_stubs: .zero 2048
z_saved_rsp: .zero 8
z_context: .zero 8
z_old_cr3: .zero 8
z_old_cr4: .zero 8
z_old_efer: .zero 8
z_old_sysenter: .zero 8
z_old_fs: .zero 8
z_old_gs: .zero 8
z_apic_flags: .zero 8
z_apic_address: .zero 8
z_old_gdtr: .zero 10
z_old_idtr: .zero 10
z_gdtr: .zero 10
z_idtr: .zero 10
z_user_cs: .zero 2
z_user_ss: .zero 2
z_own_tr: .zero 2

.section .note.GNU-stack,"",@progbits

.bss
.p2align 4
z_interrupt_stack: .zero 69632
z_initialized: .zero 8
z_owner_cpu: .zero 8
z_kernel_gdt_bytes: .zero 8

.p2align 4
z_old_fp: .zero 512
z_user_fp: .zero 512
z_old_cr0: .zero 8
z_old_ds_selector: .zero 2
z_old_es_selector: .zero 2
z_old_fs_selector: .zero 2
z_old_gs_selector: .zero 2
z_old_ss_selector: .zero 2
