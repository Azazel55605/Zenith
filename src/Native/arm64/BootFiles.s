// Self-contained installation initially targets x64 UEFI.
.text
.global zenith_boot_executable
zenith_boot_executable:
    mov x0, #0
    ret
.global zenith_boot_modules
zenith_boot_modules:
    mov x0, #0
    ret
.section .note.GNU-stack,"",@progbits
