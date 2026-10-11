# Limine protocol request layouts: limine-protocol/include/limine.h (0BSD).
# Limine scans request magic in loaded data; no managed static initializer owns these.
.data
.balign 8
.Lexecutable_request:
    .quad 0xc7b1dd30df4c8b88, 0x0a82e883a194f07b
    .quad 0xad97e90e83f1ed67, 0x31eb5d1c5ff23b69
    .quad 0, 0
.Lmodule_request:
    .quad 0xc7b1dd30df4c8b88, 0x0a82e883a194f07b
    .quad 0x3e7e279702be32af, 0xca1c4f3bd1280cee
    .quad 1, 0, 1, .Lmodule_list
.Lmodule_list:
    .quad .Lefi_module
.Lefi_module:
    .quad .Lefi_path, .Lefi_name, 0
.section .rodata
.Lefi_path: .asciz "/EFI/BOOT/BOOTX64.EFI"
.Lefi_name: .asciz "zenith-installer-efi"
.text
.globl zenith_boot_executable
.type zenith_boot_executable, @function
zenith_boot_executable:
    mov .Lexecutable_request+40(%rip), %rax
    ret
.globl zenith_boot_modules
.type zenith_boot_modules, @function
zenith_boot_modules:
    mov .Lmodule_request+40(%rip), %rax
    ret
.section .note.GNU-stack,"",@progbits
