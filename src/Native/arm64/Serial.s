.text
.global zenith_serial_read
.type zenith_serial_read, %function
zenith_serial_read:
    mov w0, #-1
    ret
.size zenith_serial_read, .-zenith_serial_read
.section .note.GNU-stack,"",%progbits
.text
.global zenith_has_display
.type zenith_has_display, %function
zenith_has_display:
    mov w0, #0
    ret
.size zenith_has_display, .-zenith_has_display
