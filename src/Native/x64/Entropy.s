.text
.globl zenith_rdseed_word
.type zenith_rdseed_word, @function
zenith_rdseed_word:
    push %rbx
    mov %rdi, %r8
    movq $0, (%r8)
    xor %eax, %eax
    cpuid
    cmp $7, %eax
    jb .Lno_seed
    mov $7, %eax
    xor %ecx, %ecx
    cpuid
    bt $18, %ebx
    jnc .Lno_seed
    rdseed %rax
    jnc .Lno_seed
    mov %rax, (%r8)
    mov $1, %eax
    pop %rbx
    ret
.Lno_seed:
    xor %eax, %eax
    pop %rbx
    ret
.size zenith_rdseed_word, .-zenith_rdseed_word
.section .note.GNU-stack,"",@progbits
