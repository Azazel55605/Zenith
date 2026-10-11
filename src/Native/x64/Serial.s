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
