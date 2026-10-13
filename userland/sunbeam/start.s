.section .text.start,"ax",@progbits
.globl _start
_start:
    cld
    mov (%rsp), %rdi
    lea 8(%rsp), %rsi
    and $-16, %rsp
    call main
    mov %eax, %edi
    mov $60, %eax
    int $0xfe
    ud2
.section .note.zenith,"a",@note
.p2align 2
.long 7,4,1
.asciz "ZENITH"
.p2align 2
.long 1
.section .note.GNU-stack,"",@progbits
