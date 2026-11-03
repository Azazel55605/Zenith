/* C3 publication and stop/root epochs. No allocation, managed calls or I/O.
 * The publication gate is recursive per CPU only while local IRQs are masked.
 * Only the BSP may collect. Managed participants are admitted explicitly; the
 * C1 native queues are not implicitly registered as managed mutators.
 */
typedef unsigned long long u64;
typedef unsigned int u32;
#define CPUS 8
#define GET(x) __atomic_load_n(&(x),__ATOMIC_ACQUIRE)
#define PUT(x,v) __atomic_store_n(&(x),(v),__ATOMIC_RELEASE)
struct roots { u64 low,high,irq_low,irq_high,active,ack,resumed,frame,sp,errors; };
static struct roots roots[CPUS];
static u64 owner, depth[CPUS], request, sequence, participants, stopped_epochs, refused, deferred, failed;
#ifdef ZENITH_GC_HOST_TEST
static _Thread_local u32 host_cpu;
static u64 host_notify,host_budget=30000000000ULL;
static u32 cpu_id(void) { return host_cpu; }
static void poll(void) { }
static u64 notify(u64 mask) { return mask&host_notify; }
#else
extern u32 zenith_cpu_logical_id(void);
extern void zenith_gc_poll(void);
extern void zenith_gc_fail_stop(void);
extern void zenith_cpu_ap_fail_stop(void);
extern u64 zenith_cpu_gc_notify(u64 mask);
static u32 cpu_id(void) { return zenith_cpu_logical_id(); }
static void poll(void) { zenith_gc_poll(); }
static u64 notify(u64 mask) { return zenith_cpu_gc_notify(mask); }
#endif
static u64 budget(void) {
#ifdef ZENITH_GC_HOST_TEST
    return host_budget;
#else
    return 30000000000ULL;
#endif
}
static void pause_cpu(void) { __asm__ volatile("pause":::"memory"); }
static u64 ticks(void) { u32 a,d; __asm__ volatile("lfence; rdtsc":"=a"(a),"=d"(d)::"memory");return (u64)d<<32|a; }
/* Called inside the outer IRQ scope, before heap reservation. A waiter must
 * cooperate with a pending stop even with IF clear, instead of deadlocking the
 * collector while it owns the publication gate. */
u64 zenith_gc_enter(void) {
    u32 cpu=cpu_id(); if (cpu>=CPUS||GET(failed)) return 0;
    if (GET(owner)==cpu+1) { if (depth[cpu]==0xffff) return 0; depth[cpu]++;return 1; }
    u64 start=ticks();
    for (;;) {
        if (GET(request)&&(GET(participants)&(1ULL<<cpu))) poll();
        u64 empty=0;
        if (__atomic_compare_exchange_n(&owner,&empty,cpu+1,0,__ATOMIC_ACQUIRE,__ATOMIC_RELAXED)) {
            depth[cpu]=1;return 1;
        }
        if (ticks()-start>budget()) return 0;
        pause_cpu();
    }
}
u64 zenith_gc_exit(void) {
    u32 cpu=cpu_id();
    if (cpu>=CPUS||GET(owner)!=cpu+1||!depth[cpu]) return 0;
    if (--depth[cpu]==0) PUT(owner,0);
    return 1;
}
u64 zenith_gc_configure(u64 cpu,u64 low,u64 size,u64 irq_low,u64 irq_size) {
    if (cpu_id()!=0||cpu==0||cpu>=CPUS||!low||size<16384||size>1048576||low+size<low||
        !irq_low||irq_size<4096||irq_size>65536||irq_low+irq_size<irq_low||
        ((low|size|irq_low|irq_size)&15)||(low<irq_low+irq_size&&irq_low<low+size)) return 0;
    if (!zenith_gc_enter()) return 0;
    if (GET(request)||GET(roots[cpu].active)) { zenith_gc_exit();return 0; }
    roots[cpu].low=low;roots[cpu].high=low+size;
    roots[cpu].irq_low=irq_low;roots[cpu].irq_high=irq_low+irq_size;
    zenith_gc_exit();return 1;
}
/* Entry/exit are serialized against collector participant selection. No AP may
 * clear active until its managed call has returned and its roots are dead. */
u64 zenith_gc_managed_enter(void) {
    u32 cpu=cpu_id();if (!cpu||cpu>=CPUS||!zenith_gc_enter()) return 0;
    u64 ok=roots[cpu].low&&!GET(roots[cpu].active);
    if (ok) PUT(roots[cpu].active,1);
    zenith_gc_exit();return ok;
}
u64 zenith_gc_managed_leave(void) {
    u32 cpu=cpu_id();if (!cpu||cpu>=CPUS||!zenith_gc_enter()) return 0;
    u64 ok=GET(roots[cpu].active);
    if (ok) PUT(roots[cpu].active,0);
    zenith_gc_exit();return ok;
}
/* Native IRQ/poll frame: 512-byte FP state + 15 GPRs, with the original work
 * stack bound supplied separately. Ack is published last and is epoch-specific.
 * Never acknowledge invalid bounds or a participant interrupted in publication. */
void zenith_gc_park(u64 frame,u64 sp) {
    u32 cpu=cpu_id();u64 epoch=GET(request);
    if (!epoch||!cpu||cpu>=CPUS||!(GET(participants)&(1ULL<<cpu))) return;
    struct roots *r=&roots[cpu];
    int frame_ok=(frame>=r->low&&frame<=r->high-632)||(frame>=r->irq_low&&frame<=r->irq_high-632);
    if (!frame_ok||sp<r->low||sp>r->high||GET(owner)==cpu+1||!GET(r->active)) {
        __atomic_fetch_add(&r->errors,1,__ATOMIC_RELAXED);return;
    }
    r->frame=frame;r->sp=sp;PUT(r->ack,epoch);
    while (GET(request)==epoch&&!GET(failed)) pause_cpu();
    if(GET(failed)) {
#ifndef ZENITH_GC_HOST_TEST
        zenith_cpu_ap_fail_stop();
#endif
        return;
    }
    PUT(r->resumed,epoch);
}
/* Return 0 without entering the old collector unless every active participant
 * has acknowledged this epoch. A refusal never marks, sweeps or frees storage.
 * AP collection requests are deferred to BSP policy, never collected locally. */
u64 zenith_gc_begin(void) {
    if (cpu_id()!=0) { __atomic_fetch_add(&deferred,1,__ATOMIC_RELAXED);return 0; }
    if (GET(failed)) { __atomic_fetch_add(&refused,1,__ATOMIC_RELAXED);return 0; }
    if (!zenith_gc_enter()) { __atomic_fetch_add(&refused,1,__ATOMIC_RELAXED);return 0; }
    if (GET(request)||sequence==~0ULL) { zenith_gc_exit();return 0; }
    u64 mask=0;
    for (u32 i=1;i<CPUS;i++) if (GET(roots[i].active)) mask|=1ULL<<i;
    u64 epoch=++sequence;PUT(participants,mask);PUT(request,epoch);
    u64 delivered=notify(mask),start=ticks();
    int ready=delivered==mask;
    while (ready) {
        u64 ack=0;
        for (u32 i=1;i<CPUS;i++) if ((mask&(1ULL<<i))&&GET(roots[i].ack)==epoch) ack|=1ULL<<i;
        if (ack==mask) { __atomic_fetch_add(&stopped_epochs,1,__ATOMIC_RELAXED);return epoch; }
        if (ticks()-start>budget()) break;
        pause_cpu();
    }
    __atomic_fetch_add(&refused,1,__ATOMIC_RELAXED);
    PUT(request,0);zenith_gc_exit();return 0;
}
u64 zenith_gc_end(u64 epoch) {
    if (cpu_id()!=0||!epoch||GET(request)!=epoch||GET(owner)!=1||GET(failed)) return 0;
    PUT(request,0);return zenith_gc_exit();
}
/* A collector that throws after admission may already have changed mark/free
 * metadata. Keep the world stopped; never advertise a successful resume or
 * unlock that heap. Native APs trap onto their fault IST, and BSP fail-stops. */
void zenith_gc_abort(u64 epoch) {
    if(cpu_id()==0&&epoch&&GET(request)==epoch&&GET(owner)==1)PUT(failed,1);
#ifndef ZENITH_GC_HOST_TEST
    zenith_gc_fail_stop();
#endif
}
/* Scanner metadata is visible only to the owning collector in a fully stopped
 * current epoch. This is a kernel-private interface, not a client syscall. */
u64 zenith_gc_root(u64 cpu,u64 field) {
    if (cpu_id()!=0||cpu==0||cpu>=CPUS||GET(owner)!=1||!GET(request)||GET(failed)||
        !(GET(participants)&(1ULL<<cpu))||GET(roots[cpu].ack)!=GET(request)) return 0;
    struct roots *r=&roots[cpu];
    switch(field) {case 0:return r->frame;case 1:return 79;case 2:return r->sp;case 3:return r->high;default:return 0;}
}
u64 zenith_gc_state(u64 field) {
    switch(field) {case 0:return GET(request);case 1:return GET(participants);case 2:return GET(stopped_epochs);
        case 3:return GET(refused);case 4:return GET(deferred);case 5:return GET(owner);
        case 7:return GET(failed);
        case 6:{u64 mask=0;for(u32 i=1;i<CPUS;i++)if(GET(roots[i].active))mask|=1ULL<<i;return mask;}
        default:return 0;}
}
#ifdef ZENITH_GC_HOST_TEST
void zenith_gc_host_cpu(u64 cpu) { host_cpu=(u32)cpu; }
void zenith_gc_host_init(void) {
    u64 *p=(u64*)roots;for(u64 i=0;i<sizeof(roots)/8;i++)p[i]=0;
    for(u32 i=0;i<CPUS;i++)depth[i]=0;
    owner=request=sequence=participants=stopped_epochs=refused=deferred=failed=0;host_cpu=0;host_notify=~0ULL;host_budget=30000000000ULL;
}
void zenith_gc_host_budget(u64 cycles) { host_budget=cycles; }
void zenith_gc_host_notify(u64 mask) { host_notify=mask; }
#endif
