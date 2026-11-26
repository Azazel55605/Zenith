/* Zenith runs one application thread per protected invocation. */
uintptr_t zns_thread_pointer(void);
static inline uintptr_t __get_tp(void) { return zns_thread_pointer(); }
#define MC_PC gregs[REG_RIP]
