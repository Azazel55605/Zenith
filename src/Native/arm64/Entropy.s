.text
.globl zenith_rdseed_word
.type zenith_rdseed_word, %function
zenith_rdseed_word:
    str xzr, [x0]
    mov x0, #0
    ret
.size zenith_rdseed_word, .-zenith_rdseed_word
.section .note.GNU-stack,"",@progbits
