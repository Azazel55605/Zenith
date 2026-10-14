#!/usr/bin/env python3
"""Small valid higher-half ELF that proves entry then intentionally never boots."""
from pathlib import Path
import subprocess

MARKER = 'ZENITH_BROKEN_KERNEL_ENTRY'


def build(output):
    output = Path(output)
    source = output.with_suffix('.s'); obj = output.with_suffix('.o'); script = output.with_suffix('.ld')
    lines = ['.text', '.globl zenith_failure_entry', 'zenith_failure_entry:', '    cli']
    for index, value in enumerate((MARKER+'\n').encode('ascii')):
        lines += ['    mov $0x3fd, %dx', '.Lready'+str(index)+':', '    inb %dx, %al',
                  '    test $0x20, %al', '    jz .Lready'+str(index), '    mov $0x3f8, %dx',
                  '    mov $'+str(value)+', %al', '    outb %al, %dx']
    lines += ['.Lloop:', '    pause', '    jmp .Lloop', '.section .note.GNU-stack,"",@progbits']
    source.write_text('\n'.join(lines)+'\n')
    script.write_text('ENTRY(zenith_failure_entry)\nPHDRS { code PT_LOAD FLAGS(5); }\n'
                      'SECTIONS { . = 0xffffffff80000000; .text : { *(.text) } :code\n'
                      '/DISCARD/ : { *(.note*) *(.comment*) } }\n')
    subprocess.run(['clang','--target=x86_64-none-elf','-c',str(source),'-o',str(obj)],check=True)
    subprocess.run(['ld','-m','elf_x86_64','--build-id=none','-T',str(script),str(obj),'-o',str(output)],check=True)
    return output
