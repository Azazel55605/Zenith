.text
.globl zenith_serial_read
.type zenith_serial_read, @function
zenith_serial_read:
    mov $0x3fd, %dx
    xor %eax, %eax
    inb %dx, %al
    test $1, %al
    jz .Lserial_empty
    test $0x1e, %al
    jnz .Lserial_error
    mov $0x3f8, %dx
    inb %dx, %al
    ret
.Lserial_error:
    mov $0x3f8, %dx
    inb %dx, %al
    mov $-2, %eax
    ret
.Lserial_empty:
    mov $-1, %eax
    ret
.size zenith_serial_read, .-zenith_serial_read
.section .note.GNU-stack,"",@progbits
# Probe the same supported PCI display IDs as Cosmos without allocating a canvas.
.text
.globl zenith_has_display
.type zenith_has_display, @function
zenith_has_display:
    mov $0x80000000, %ecx
.Ldisplay_probe:
    mov %ecx, %eax
    mov $0xcf8, %dx
    outl %eax, %dx
    mov $0xcfc, %dx
    inl %dx, %eax
    cmp $0x10501af4, %eax
    je .Ldisplay_found
    cmp $0x040515ad, %eax
    je .Ldisplay_found
    add $0x100, %ecx
    cmp $0x81000000, %ecx
    jb .Ldisplay_probe
    xor %eax, %eax
    ret
.Ldisplay_found:
    mov $1, %eax
    ret
.size zenith_has_display, .-zenith_has_display

# BSP-only, bounded UTF-16 ASCII transmit. Interrupt masking prevents upstream
# debug writers from splitting a Zenith record; no managed callbacks/locks here.
.text
.globl zenith_serial_write
.type zenith_serial_write, @function
zenith_serial_write:
    pushfq
    cli
    xor %r8d, %r8d
    mov $1000000, %r9d
.Lserial_write_next:
    test %rsi, %rsi
    jz .Lserial_write_done
    mov $0x3fd, %dx
.Lserial_write_poll:
    inb %dx, %al
    test $0x20, %al
    jnz .Lserial_write_byte
    dec %r9d
    jnz .Lserial_write_poll
    jmp .Lserial_write_done
.Lserial_write_byte:
    mov $0x3f8, %dx
    mov (%rdi), %al
    outb %al, %dx
    add $2, %rdi
    inc %r8d
    dec %rsi
    jmp .Lserial_write_next
.Lserial_write_done:
    mov %r8d, %eax
    popfq
    ret
.size zenith_serial_write, .-zenith_serial_write
.section .note.GNU-stack,"",@progbits

.text
# Loading an IDT is not permission to enable interrupts during platform setup.
# Preserve IF for both early masked setup and any later enabled caller.
.globl zenith_load_idt_preserve_irq
.type zenith_load_idt_preserve_irq, @function
zenith_load_idt_preserve_irq:
    pushfq
    cmpq $-1, .Linitial_idt_flags(%rip)
    jne 1f
    mov (%rsp), %rax
    mov %rax, .Linitial_idt_flags(%rip)
1:  cli
    lidt (%rdi)
    popfq
    ret
.size zenith_load_idt_preserve_irq, .-zenith_load_idt_preserve_irq
.section .note.GNU-stack,"",@progbits

.data
.balign 8
.Linitial_idt_flags: .quad -1
.text
.globl zenith_initial_idt_flags
.type zenith_initial_idt_flags, @function
zenith_initial_idt_flags:
    mov .Linitial_idt_flags(%rip), %rax
    ret
.size zenith_initial_idt_flags, .-zenith_initial_idt_flags

# Runtime diagnostic: reload the current valid IDT with IF=0 and IF=1, then
# restore the entry flags. Return bit 0/1 if either state was not preserved.
.globl zenith_idt_irq_test
.type zenith_idt_irq_test, @function
zenith_idt_irq_test:
    push %rbx
    push %r12
    pushfq
    pop %r12
    sub $24, %rsp
    sidt (%rsp)
    xor %ebx, %ebx
    cli
    mov %rsp, %rdi
    call zenith_load_idt_preserve_irq
    pushfq
    pop %rax
    test $0x200, %rax
    jz 1f
    or $1, %ebx
1:  sti
    mov %rsp, %rdi
    call zenith_load_idt_preserve_irq
    pushfq
    pop %rax
    test $0x200, %rax
    jnz 2f
    or $2, %ebx
2:  push %r12
    popfq
    add $24, %rsp
    mov %ebx, %eax
    pop %r12
    pop %rbx
    ret
.size zenith_idt_irq_test, .-zenith_idt_irq_test
.section .note.GNU-stack,"",@progbits

# Port access for the checked power-off path.
.text
.globl zenith_port_inw
.type zenith_port_inw, @function
zenith_port_inw:
    mov %edi, %edx
    inw %dx, %ax
    movzwl %ax, %eax
    ret
.globl zenith_port_outw
.type zenith_port_outw, @function
zenith_port_outw:
    mov %edi, %edx
    mov %esi, %eax
    outw %ax, %dx
    ret
.globl zenith_port_outb
.type zenith_port_outb, @function
zenith_port_outb:
    mov %edi, %edx
    mov %esi, %eax
    outb %al, %dx
    ret
.globl zenith_interrupts_off
.type zenith_interrupts_off, @function
zenith_interrupts_off:
    cli
    ret
.section .note.GNU-stack,"",@progbits
