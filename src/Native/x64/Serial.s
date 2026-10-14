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
