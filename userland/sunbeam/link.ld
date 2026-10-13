ENTRY(_start)
PHDRS { code PT_LOAD FLAGS(5); data PT_LOAD FLAGS(6); note PT_NOTE FLAGS(4); }
SECTIONS {
  . = 0x400000;
  .text : { *(.text.start) *(.text*) *(.rodata*) } :code
  .note.zenith : { *(.note.zenith) } :code :note
  . = ALIGN(4096);
  .data : { *(.data*) } :data
  .bss : { *(.bss*) *(COMMON) } :data
  /DISCARD/ : { *(.eh_frame*) *(.comment*) }
}
