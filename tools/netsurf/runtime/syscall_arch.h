/* musl's private x86_64 syscall wrappers call the Zenith adapter. */
#define __SYSCALL_LL_E(x) (x)
#define __SYSCALL_LL_O(x) (x)
#define IPC_64 0
long zns_syscall(long, long, long, long, long, long, long);
static inline long __syscall0(long n)
{ return zns_syscall(n, 0, 0, 0, 0, 0, 0); }
static inline long __syscall1(long n, long a)
{ return zns_syscall(n, a, 0, 0, 0, 0, 0); }
static inline long __syscall2(long n, long a, long b)
{ return zns_syscall(n, a, b, 0, 0, 0, 0); }
static inline long __syscall3(long n, long a, long b, long c)
{ return zns_syscall(n, a, b, c, 0, 0, 0); }
static inline long __syscall4(long n, long a, long b, long c, long d)
{ return zns_syscall(n, a, b, c, d, 0, 0); }
static inline long __syscall5(long n, long a, long b, long c, long d, long e)
{ return zns_syscall(n, a, b, c, d, e, 0); }
static inline long __syscall6(long n, long a, long b, long c, long d, long e, long f)
{ return zns_syscall(n, a, b, c, d, e, f); }
