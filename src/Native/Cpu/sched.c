/* C5.1 scheduler lock. No allocation, managed calls or I/O.
 *
 * The pinned Cosmos scheduler protects its thread registry, run queues and thread states by masking
 * interrupts. That excludes the local timer, not another CPU. Every such section (and the interrupt-time
 * scheduling paths) also takes this lock once C5 lets more than one CPU dispatch managed threads.
 *
 * Recursive per CPU, held only with local interrupts masked: a CPU that holds it cannot be switched
 * away from, so recursion by CPU identity is recursion by the same thread. A spinner cooperates with a
 * pending GC stop the way the publication gate does, so a collector never waits on a CPU that is
 * spinning here with IF clear.
 */
typedef unsigned long long u64;
typedef unsigned int u32;
#define CPUS 8
#define GET(x) __atomic_load_n(&(x),__ATOMIC_ACQUIRE)
#define PUT(x,v) __atomic_store_n(&(x),(v),__ATOMIC_RELEASE)
static u64 owner, depth[CPUS], acquired, contended, spins, refused, max_depth;
#ifdef ZENITH_SCHED_HOST_TEST
static _Thread_local u32 host_cpu;
static u32 cpu_id(void) { return host_cpu; }
static void poll(void) { }
static u64 stop_pending(void) { return 0; }
#else
extern u32 zenith_cpu_logical_id(void);
extern void zenith_gc_poll(void);
extern unsigned long long zenith_gc_state(unsigned long long field);
static u32 cpu_id(void) { return zenith_cpu_logical_id(); }
static void poll(void) { zenith_gc_poll(); }
/* A stop is pending and this CPU is one of its participants. */
static u64 stop_pending(void) { return zenith_gc_state(0) && (zenith_gc_state(1) & (1ULL<<cpu_id())); }
#endif
static void pause_cpu(void) { __asm__ volatile("pause":::"memory"); }

/* 1 when held (first or nested), 0 when refused (unknown CPU or nesting overflow). The caller has
 * already masked local interrupts. */
u64 zenith_sched_lock(void) {
    u32 cpu=cpu_id();
    if (cpu>=CPUS) { __atomic_fetch_add(&refused,1,__ATOMIC_RELAXED); return 0; }
    if (GET(owner)==cpu+1) {
        if (depth[cpu]==0xffff) { __atomic_fetch_add(&refused,1,__ATOMIC_RELAXED); return 0; }
        if (++depth[cpu]>GET(max_depth)) PUT(max_depth,depth[cpu]);
        return 1;
    }
    u64 waited=0;
    for (;;) {
        u64 empty=0;
        if (__atomic_compare_exchange_n(&owner,&empty,cpu+1,0,__ATOMIC_ACQUIRE,__ATOMIC_RELAXED)) break;
        if (!waited) { waited=1; __atomic_fetch_add(&contended,1,__ATOMIC_RELAXED); }
        __atomic_fetch_add(&spins,1,__ATOMIC_RELAXED);
        if (stop_pending()) poll();
        pause_cpu();
    }
    depth[cpu]=1;
    __atomic_fetch_add(&acquired,1,__ATOMIC_RELAXED);
    return 1;
}

/* 1 when released one level, 0 when this CPU did not hold it (a broken pairing: reported, the lock
 * is left as it is rather than freed under its real owner). */
u64 zenith_sched_unlock(void) {
    u32 cpu=cpu_id();
    if (cpu>=CPUS||GET(owner)!=cpu+1||!depth[cpu]) { __atomic_fetch_add(&refused,1,__ATOMIC_RELAXED); return 0; }
    if (--depth[cpu]==0) PUT(owner,0);
    return 1;
}

/* Diagnostics: 0 acquisitions, 1 contended acquisitions, 2 spin iterations, 3 refusals, 4 deepest
 * nesting, 5 current owner CPU + 1 (0 when free). */
u64 zenith_sched_state(u64 field) {
    switch (field) {
    case 0: return GET(acquired); case 1: return GET(contended); case 2: return GET(spins);
    case 3: return GET(refused); case 4: return GET(max_depth); case 5: return GET(owner);
    default: return 0;
    }
}

#ifdef ZENITH_SCHED_HOST_TEST
void zenith_sched_host_cpu(u64 cpu) { host_cpu=(u32)cpu; }
/* A deliberately non-atomic read-modify-write under the lock, n times, as CPU `cpu`. */
static volatile u64 host_counter;
u64 zenith_sched_host_work(u64 cpu,u64 n) {
    host_cpu=(u32)cpu;
    for (u64 i=0;i<n;i++) {
        if (!zenith_sched_lock()) return 0;
        u64 v=host_counter; for (int k=0;k<20;k++) pause_cpu(); host_counter=v+1;
        if (!zenith_sched_lock()) return 0;   /* nested, like ReadyThread inside the timer path */
        zenith_sched_unlock();
        zenith_sched_unlock();
    }
    return 1;
}
u64 zenith_sched_host_counter(void) { return host_counter; }
void zenith_sched_host_init(void) {
    owner=acquired=contended=spins=refused=max_depth=0;
    for (u32 i=0;i<CPUS;i++) depth[i]=0;
    host_cpu=0; host_counter=0;
    (void)stop_pending;
}
#endif
