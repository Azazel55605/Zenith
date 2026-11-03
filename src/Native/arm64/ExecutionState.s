# ARM64 remains uniprocessor; no ARM AP admission is implemented.
.text
.globl zenith_cpu_logical_id
zenith_cpu_logical_id:
    mov w0, #0
    ret
.section .note.GNU-stack,"",@progbits
