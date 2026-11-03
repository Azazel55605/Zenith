/* C1 isolated x64 workers. No libc, managed references, allocation, UART or I/O.
 * One BSP producer per queue (IRQ-scoped), one AP consumer. Static storage stays
 * reserved even after a fault. Intel SDM vol.3: APIC, IST and interrupt shadow.
 */
typedef unsigned long long u64;
typedef unsigned int u32;
typedef unsigned short u16;
typedef unsigned char u8;
#define CPUS 8
#define JOBS 8
#define PIXELS 4096
#define WAKE 0xf1
#define FREE 0
#define QUEUED 1
#define RUNNING 2
#define DONE 3
#define CANCELLED 4
#define FAULTED 5
#define LOAD(x) __atomic_load_n(&(x), __ATOMIC_ACQUIRE)
#define STORE(x,v) __atomic_store_n(&(x),(v),__ATOMIC_RELEASE)
struct descriptor { u16 limit; u64 address; } __attribute__((packed));
struct task_state { u32 reserved; u64 rsp[3]; u64 reserved2; u64 ist[7]; u64 reserved3; u16 reserved4, iomap; } __attribute__((packed));
struct gate { u16 low, selector; u8 ist, flags; u16 mid; u32 high, reserved; } __attribute__((packed));
struct job {
    u64 token, state, cancel, released, kind, count, seed, rounds;
    u64 start, end, result;
    u32 pixels[PIXELS];
};
struct cpu {
    /* Assembly ABI: these offsets are checked below. */
    u64 state, stack, apic, start, end, result, actual, completion;
    u64 root, self;
    struct descriptor gdtr, idtr;
    u64 gdt[5];
    struct task_state tss;
    u64 apic_base, x2;
    u64 parks, wakes, completed, fault, halted, parked, irq_stack_errors, fault_stack_ok, ipi_errors;
    u32 lock, head, tail, current;
    u64 ring[JOBS];
    struct job jobs[JOBS];
    u8 work_stack[16384] __attribute__((aligned(16)));
    u8 irq_stack[8192] __attribute__((aligned(16)));
    u8 fault_stack[8192] __attribute__((aligned(16)));
} __attribute__((aligned(64)));
_Static_assert(__builtin_offsetof(struct cpu, stack)==8, "AP stack ABI");
_Static_assert(__builtin_offsetof(struct cpu, root)==64, "AP root ABI");
_Static_assert(__builtin_offsetof(struct cpu, self)==72, "AP GS ABI");
_Static_assert(__builtin_offsetof(struct cpu, gdtr)==80, "AP GDT ABI");
_Static_assert(__builtin_offsetof(struct cpu, idtr)==90, "AP IDT ABI");
_Static_assert(sizeof(struct task_state)==104, "x64 TSS layout");
static struct cpu cpus[CPUS];
static struct gate idt[256] __attribute__((aligned(16)));
static u64 count, hhdm, stopping, sequence;
#ifdef ZENITH_CPU_HOST_TEST
static u64 host_pause, host_seen;
#endif
#ifndef ZENITH_CPU_HOST_TEST
extern void zenith_cpu_ap_entry(void);
extern void zenith_cpu_wake_entry(void);
extern void zenith_cpu_spurious_entry(void);
extern u64 zenith_cpu_fault_entries[256];
extern u64 zenith_cpu_mp_response(void);
static inline u64 msr(u32 reg) { u32 lo,hi; __asm__ volatile("rdmsr":"=a"(lo),"=d"(hi):"c"(reg)); return (u64)hi<<32|lo; }
static inline void wrmsr(u32 reg,u64 v) { __asm__ volatile("wrmsr"::"c"(reg),"a"((u32)v),"d"((u32)(v>>32)):"memory"); }
static inline u64 irq_off(void) { u64 f; __asm__ volatile("pushfq; pop %0; cli":"=r"(f)::"memory"); return f; }
static inline void irq_restore(u64 f) { __asm__ volatile("push %0; popfq"::"r"(f):"memory","cc"); }
static inline void park(void) { __asm__ volatile("sti; hlt; cli":::"memory"); }
__attribute__((noreturn)) static inline void halt(void) { for (;;) __asm__ volatile("cli; hlt":::"memory"); }
static inline u64 root(void) { u64 r; __asm__ volatile("mov %%cr3,%0":"=r"(r)); return r; }
#else
static inline u64 irq_off(void) { return 0; }
static inline void irq_restore(u64 f) { (void)f; }
#endif
static inline void pause_cpu(void) { __asm__ volatile("pause":::"memory"); }
static inline u64 ticks(void) { u32 lo,hi; __asm__ volatile("lfence; rdtsc":"=a"(lo),"=d"(hi)::"memory"); return (u64)hi<<32|lo; }
static u64 identity(void) {
#ifdef ZENITH_CPU_HOST_TEST
    return 0;
#else
    u32 a=1,b,c,d; __asm__ volatile("cpuid":"+a"(a),"=b"(b),"=c"(c),"=d"(d)); return b>>24;
#endif
}
/* A failed AP may have faulted inside a critical section: never wait forever or
 * recover its partially updated queue. Only its own fault handler proves halt. */
static int lock_cpu(struct cpu *c) {
    for (u32 n=0;n<1000000;n++) {
        if (LOAD(c->state)==4) return 0;
        if (!__atomic_exchange_n(&c->lock,1,__ATOMIC_ACQUIRE)) return 1;
        pause_cpu();
    }
    return 0;
}
static void unlock_cpu(struct cpu *c) { __atomic_store_n(&c->lock,0,__ATOMIC_RELEASE); }
#ifndef ZENITH_CPU_HOST_TEST
static u32 apic_read(struct cpu *c,u32 offset) { return c->x2?(u32)msr(0x800+offset/16):*(volatile u32*)(c->apic_base+offset); }
static void apic_write(struct cpu *c,u32 offset,u32 value) {
    if (c->x2) wrmsr(0x800+offset/16,value); else *(volatile u32*)(c->apic_base+offset)=value;
}
#endif
static int wake(struct cpu *c) {
#ifdef ZENITH_CPU_HOST_TEST
    __atomic_fetch_add(&c->wakes,1,__ATOMIC_RELAXED);
    return 1;
#else
    struct cpu *b=&cpus[0];
    /* x2APIC WRMSR may pass earlier memory stores. Publish queue/stop state before
     * the doorbell, even on machines where a plain WRMSR is not serializing. */
    __asm__ volatile("mfence":::"memory");
    if (b->x2) {
        /* Intel SDM 11.12.3 requires MFENCE;LFENCE before non-serializing
         * x2APIC WRMSR (MFENCE alone is insufficient on Intel). */
        __asm__ volatile("lfence":::"memory");
        wrmsr(0x830,(c->apic<<32)|WAKE); return 1;
    }
    for (u32 n=0;n<100000;n++) {
        if (!(apic_read(b,0x300)&0x1000)) {
            apic_write(b,0x310,(u32)c->apic<<24);
            apic_write(b,0x300,WAKE);
            return 1;
        }
        pause_cpu();
    }
    __atomic_fetch_add(&c->ipi_errors,1,__ATOMIC_RELAXED);
    return 0;
#endif
}
static void make_gate(u32 vector,u64 address,u8 ist) {
    idt[vector]=(struct gate){(u16)address,8,ist,0x8e,(u16)(address>>16),(u32)(address>>32),0};
}
static void configure(struct cpu *c,u64 id,u64 cr3) {
    c->apic=id; c->root=cr3; c->self=(u64)c;
    c->stack=(u64)(c->work_stack+sizeof(c->work_stack));
    c->gdt[1]=0x00af9a000000ffffULL; c->gdt[2]=0x00cf92000000ffffULL;
    u64 t=(u64)&c->tss;
    c->gdt[3]=103|((t&0xffffff)<<16)|(0x89ULL<<40)|(((t>>24)&255)<<56);
    c->gdt[4]=t>>32;
    c->gdtr=(struct descriptor){39,(u64)c->gdt};
    c->idtr=(struct descriptor){4095,(u64)idt};
    c->tss.rsp[0]=c->stack; c->tss.ist[0]=(u64)(c->irq_stack+sizeof(c->irq_stack));
    c->tss.ist[1]=(u64)(c->fault_stack+sizeof(c->fault_stack)); c->tss.iomap=104;
    c->current=JOBS;
}
u64 zenith_cpu_detected(void) {
#ifdef ZENITH_CPU_HOST_TEST
    return count;
#else
    u64 *r=(u64*)zenith_cpu_mp_response(); return r?r[2]:0;
#endif
}
u64 zenith_cpu_initialize(u64 offset) {
    if (count) return count;
    hhdm=offset;
#ifndef ZENITH_CPU_HOST_TEST
    struct response { u64 rev; u32 flags,bsp; u64 count; struct info **cpus; };
    struct info { u32 processor,lapic; u64 reserved,entry,argument; };
    struct response *r=(void*)zenith_cpu_mp_response();
    if (!r||!r->count||!hhdm||(msr(0x1b)&0x800)==0||r->bsp>255) return 0;
    for (u32 i=0;i<256;i++) make_gate(i,zenith_cpu_fault_entries[i],2);
    make_gate(WAKE,(u64)zenith_cpu_wake_entry,1);
    make_gate(255,(u64)zenith_cpu_spurious_entry,1);
    configure(&cpus[0],r->bsp,root());
    u64 base=msr(0x1b); cpus[0].x2=(base>>10)&1; cpus[0].apic_base=hhdm+(base&0xfffff000ULL);
    cpus[0].actual=identity(); cpus[0].state=3; count=1;
    for (u64 i=0;i<r->count&&count<CPUS;i++) {
        struct info *p=r->cpus[i];
        if (p->lapic==r->bsp||p->lapic>255) continue;
        struct cpu *c=&cpus[count]; configure(c,p->lapic,cpus[0].root);
        p->argument=(u64)c;
        /* Publish count/context before release of Limine's goto address. */
        count++;
        __atomic_store_n(&p->entry,(u64)zenith_cpu_ap_entry,__ATOMIC_RELEASE);
    }
#endif
    return count;
}
/* Called after AP assembly has loaded its private GDT/TSS/GS/IDT. */
#ifndef ZENITH_CPU_HOST_TEST
void zenith_cpu_ap_configure(struct cpu *c) {
    u64 base=msr(0x1b);
    if (!(base&0x800)) { wrmsr(0x1b,base|0x800); base=msr(0x1b); }
    c->x2=(base>>10)&1; c->apic_base=hhdm+(base&0xfffff000ULL);
    /* Mask error delivery first, with a valid vector: masking another zero-
     * vector LVT can itself set APIC error state on real hardware. */
    u32 max=(apic_read(c,0x30)>>16)&255;
    if (max>=3) apic_write(c,0x370,0x10000|0xf2);
    /* No standard AP device/timer delivery. Only wake and spurious handlers. */
    apic_write(c,0x320,0x10000|0xf2); apic_write(c,0x380,0);
    apic_write(c,0x350,0x10000|0xf2); apic_write(c,0x360,0x10000|0xf2);
    if (max>=4) apic_write(c,0x340,0x10000|0xf2);
    if (max>=5) apic_write(c,0x330,0x10000|0xf2);
    if (max>=6) apic_write(c,0x2f0,0x10000|0xf2);
    apic_write(c,0x80,0); apic_write(c,0xf0,0x100|255);
    STORE(c->actual,identity()); STORE(c->state,3);
}
void zenith_cpu_interrupt(struct cpu *c,u64 stack) {
    if (stack<(u64)c->irq_stack||stack>=(u64)(c->irq_stack+sizeof(c->irq_stack))) __atomic_fetch_add(&c->irq_stack_errors,1,__ATOMIC_RELAXED);
    __atomic_fetch_add(&c->wakes,1,__ATOMIC_RELAXED); apic_write(c,0xb0,0);
}
__attribute__((noreturn)) void zenith_cpu_fault(struct cpu *c,u64 vector,u64 stack) {
    c->fault=vector;
    c->fault_stack_ok=stack>=(u64)c->fault_stack&&stack<(u64)(c->fault_stack+sizeof(c->fault_stack));
    if (c->current<JOBS) STORE(c->jobs[c->current].state,FAULTED);
    STORE(c->state,4);
    STORE(c->halted,1);
    halt();
}
#endif
static struct cpu *lookup(u64 token,struct job **out) {
    u32 ci=(u32)(token&63)/JOBS,ji=(u32)(token&63)%JOBS;
    if (!token||ci==0||ci>=count) return 0;
    struct cpu *c=&cpus[ci];
    if (!lock_cpu(c)) return 0;
    struct job *j=&c->jobs[ji];
    if (j->token!=token||!LOAD(j->state)) { unlock_cpu(c); return 0; }
    *out=j; return c;
}
/* All exported queue mutations are BSP-only and retain incoming IF. A caller
 * cannot submit code pointers or managed memory. Scalar arguments are copied now. */
u64 zenith_cpu_enqueue(u64 index,u64 kind,u64 n,u64 seed,u64 rounds,u64 held) {
    u64 f=irq_off(),token=0;
    if (identity()!=cpus[0].actual||LOAD(stopping)||index==0||index>=count||
        kind<1||kind>3||!n||n>(kind==1?100000000ULL:PIXELS)||!rounds||rounds>128) goto end;
    struct cpu *c=&cpus[index];
    if (!lock_cpu(c)) goto end;
    u64 state=LOAD(c->state);
    if ((state!=2&&state!=3)||c->tail-c->head>=JOBS||LOAD(c->halted)) goto unlock;
    u32 i;
    for (i=0;i<JOBS;i++) if (LOAD(c->jobs[i].state)==FREE) break;
    if (i==JOBS||sequence==0x03ffffffffffffffULL) goto unlock;
    struct job *j=&c->jobs[i];
    token=(++sequence<<6)|(index*JOBS+i);
    j->token=token; j->cancel=0; j->released=!held; j->kind=kind; j->count=n;
    j->seed=seed; j->rounds=rounds; j->start=j->end=j->result=0;
    STORE(j->state,QUEUED);
    c->ring[c->tail%JOBS]=token; STORE(c->tail,c->tail+1);
unlock:
    unlock_cpu(c);
    if (token) wake(c);
end:
    irq_restore(f); return token;
}
u64 zenith_cpu_job(u64 token,u64 field) {
    u64 f=irq_off(),v=0; struct job *j;
    u32 ci=(u32)(token&63)/JOBS, ji=(u32)(token&63)%JOBS;
    if (token&&ci>0&&ci<count&&LOAD(cpus[ci].state)==4&&LOAD(cpus[ci].halted)) {
        // Failed queues stay reserved. Their valid outstanding tokens report
        // fault, never a partial result, even if the fault interrupted the lock.
        v=cpus[ci].jobs[ji].token==token&&LOAD(cpus[ci].jobs[ji].state)!=FREE&&field==0?FAULTED:0;
        irq_restore(f); return v;
    }
    struct cpu *c=lookup(token,&j);
    if (c) {
        if (field==0) v=LOAD(j->state);
        if (field==1) v=LOAD(j->result);
        if (field==2) v=LOAD(j->start);
        if (field==3) v=LOAD(j->end);
        unlock_cpu(c);
    }
    irq_restore(f); return v;
}
/* action 0=cancel, 1=start held job, 2=reap terminal. A generation token must
 * remain valid through completion; completed jobs retain capacity until reaped. */
u64 zenith_cpu_control(u64 token,u64 action) {
    u64 f=irq_off(),ok=0; struct job *j;
    if (identity()!=cpus[0].actual) goto end;
    struct cpu *c=lookup(token,&j);
    if (!c) goto end;
    if (action==0&&LOAD(j->state)<=RUNNING) { STORE(j->cancel,1); ok=1; }
    if (action==1&&LOAD(j->state)<=RUNNING) { STORE(j->released,1); ok=1; }
    if (action==2&&LOAD(j->state)>=DONE) { STORE(j->state,FREE); ok=1; }
    unlock_cpu(c);
    if (ok&&action<2) wake(c);
end:
    irq_restore(f); return ok;
}
u64 zenith_cpu_pixel(u64 token,u64 index) {
    u64 f=irq_off(),v=0; struct job *j; struct cpu *c=lookup(token,&j);
    if (c) { if (LOAD(j->state)==DONE&&j->kind==2&&index<j->count) v=j->pixels[index]; unlock_cpu(c); }
    irq_restore(f); return v;
}
static int cancelled(struct job *j) { return LOAD(stopping)||LOAD(j->cancel); }
static u32 blend(u32 p,u32 round) {
    u32 tint=0x203040+(round&15)*0x010101;
    u32 r=(((p>>16)&255)*191+((tint>>16)&255)*64+127)/255;
    u32 g=(((p>>8)&255)*191+((tint>>8)&255)*64+127)/255;
    u32 b=((p&255)*191+(tint&255)*64+127)/255;
    return 0xff000000|(r<<16)|(g<<8)|b;
}
static void execute(struct job *j) {
    STORE(j->start,ticks());
    u64 result=0xcbf29ce484222325ULL;
    if (cancelled(j)) goto complete;
    if (j->kind==1) {
        for (u64 n=j->count;n;n--) {
            if (((j->count-n)&255)==0) {
#ifdef ZENITH_CPU_HOST_TEST
                // Deterministic host scheduling at a real mid-loop checkpoint;
                // absent from the kernel binary, with no callback/GC reference.
                if (j->count-n==256&&LOAD(host_pause)) {
                    STORE(host_seen,1);
                    while (LOAD(host_pause)&&!cancelled(j)) pause_cpu();
                }
#endif
                if (cancelled(j)) break;
            }
            result=(result^n)*0x100000001b3ULL;
        }
    } else if (j->kind==2) {
        for (u32 p=0;p<j->count;p++) {
            if ((p&255)==0&&cancelled(j)) goto complete;
            j->pixels[p]=0xff000000U|((u32)(j->seed*33+p*17)&0xffffff);
        }
        for (u32 r=0;r<j->rounds;r++) {
            for (u32 p=0;p<j->count;p++) {
                if ((p&255)==0&&cancelled(j)) goto complete;
                j->pixels[p]=blend(j->pixels[p],r+(u32)j->seed);
            }
        }
        for (u32 p=0;p<j->count;p++) {
            if ((p&255)==0&&cancelled(j)) goto complete;
            result=(result^j->pixels[p])*0x100000001b3ULL;
        }
    } else {
#ifndef ZENITH_CPU_HOST_TEST
        __asm__ volatile("ud2");
#else
        STORE(j->state,FAULTED); return;
#endif
    }
complete:
    STORE(j->result,result); STORE(j->end,ticks());
    STORE(j->state,cancelled(j)?CANCELLED:DONE);
}
static struct job *claim(struct cpu *c) {
    if (!lock_cpu(c)) return 0;
    struct job *j=0;
    while (c->head!=c->tail) {
        u64 token=c->ring[c->head%JOBS]; STORE(c->head,c->head+1);
        u32 i=(u32)(token&63)%JOBS;
        j=&c->jobs[i];
        if (j->token!=token||LOAD(j->state)!=QUEUED) { j=0; continue; }
        if (cancelled(j)) { STORE(j->state,CANCELLED); j=0; continue; }
        c->current=i; STORE(j->state,RUNNING); STORE(c->state,2); break;
    }
    unlock_cpu(c); return j;
}
/* Terminal job publication is part of the stop acknowledgement. A contended
 * lock must not turn into a false halt acknowledgement with queued jobs active. */
static int quiesce_cpu(struct cpu *c) {
    if (!lock_cpu(c)) return 0;
    for (u32 i=0;i<JOBS;i++)
        if (LOAD(c->jobs[i].state)==QUEUED||LOAD(c->jobs[i].state)==RUNNING) STORE(c->jobs[i].state,CANCELLED);
    STORE(c->head,LOAD(c->tail)); unlock_cpu(c);
    STORE(c->state,5); STORE(c->halted,1);
    return 1;
}
#ifndef ZENITH_CPU_HOST_TEST
static void sleep_worker(struct cpu *c) {
    /* IF is clear throughout observation. A targeted IPI after the recheck stays
     * pending until STI; its interrupt shadow includes HLT. No periodic polling. */
    __atomic_fetch_add(&c->parks,1,__ATOMIC_RELAXED); STORE(c->parked,1); park(); STORE(c->parked,0);
}
__attribute__((noreturn)) void zenith_cpu_worker(struct cpu *c) {
    zenith_cpu_ap_configure(c);
    for (;;) {
        if (LOAD(stopping)) {
            if (quiesce_cpu(c)) halt();
            continue;
        }
        struct job *j=claim(c);
        if (!j) {
            STORE(c->state,3);
            /* Producer cannot clear pending IPI: queue state is authoritative. */
            if (LOAD(stopping)||LOAD(c->head)!=LOAD(c->tail)) continue;
            sleep_worker(c); continue;
        }
        while (!LOAD(j->released)&&!cancelled(j)) sleep_worker(c);
        execute(j); __atomic_fetch_add(&c->completed,1,__ATOMIC_RELAXED); c->current=JOBS;
    }
}
#endif
void zenith_cpu_stop(void) {
    u64 f=irq_off(); STORE(stopping,1);
    for (u32 i=1;i<count;i++) if (!LOAD(cpus[i].halted)) wake(&cpus[i]);
    irq_restore(f);
}
u64 zenith_cpu_field(u64 index,u64 field) {
    if (index>=count) return 0;
    struct cpu *c=&cpus[index];
    switch (field) {
        case 0:return LOAD(c->state); case 2:return c->apic; case 3:return LOAD(c->start);
        case 4:return LOAD(c->end); case 5:return LOAD(c->result); case 6:return LOAD(c->actual);
        case 7:return LOAD(c->completion); case 8:return LOAD(c->parks); case 9:return LOAD(c->wakes);
        case 10:return LOAD(c->completed); case 11:return LOAD(c->fault); case 12:return LOAD(c->halted);
        case 13:return LOAD(c->tail)-LOAD(c->head); case 14:return LOAD(c->parked);
        case 15:return LOAD(c->fault_stack_ok); case 16:return LOAD(c->irq_stack_errors);
        case 17:return LOAD(c->ipi_errors); case 18:return c->x2;
        default:return 0;
    }
}
u64 zenith_cpu_bsp(u64 kind,u64 n,u64 seed,u64 rounds) {
    /* BSP reference work uses a separate static slot, never an AP's storage. */
    if (!n||n>(kind==1?100000000ULL:PIXELS)||(kind!=1&&kind!=2)||!rounds||rounds>128) return 0;
    struct job *j=&cpus[0].jobs[0]; j->kind=kind; j->count=n; j->seed=seed; j->rounds=rounds; j->cancel=0;
    execute(j);
    STORE(cpus[0].start,LOAD(j->start)); STORE(cpus[0].end,LOAD(j->end)); STORE(cpus[0].result,LOAD(j->result)); STORE(cpus[0].completion,LOAD(j->state)==CANCELLED);
    return LOAD(j->result);
}
#ifdef ZENITH_CPU_HOST_TEST
void zenith_cpu_host_init(u64 n) {
    /* Each test loads a fresh library or resets it with no active host workers. */
    u8 *p=(u8*)cpus; for (u64 i=0;i<sizeof(cpus);i++) p[i]=0;
    count=n<=CPUS?n:CPUS; stopping=sequence=0; host_pause=host_seen=0;
    for (u32 i=0;i<count;i++) { configure(&cpus[i],i,0); cpus[i].actual=i; cpus[i].state=3; }
}
void zenith_cpu_host_pause(u64 value) { STORE(host_seen,0); STORE(host_pause,value); }
u64 zenith_cpu_host_seen(void) { return LOAD(host_seen); }
void zenith_cpu_host_state(u64 index,u64 state,u64 halted) {
    if (index>0&&index<count) { STORE(cpus[index].state,state); STORE(cpus[index].halted,halted); }
}
u64 zenith_cpu_host_quiesce(u64 index) { return index>0&&index<count?quiesce_cpu(&cpus[index]):0; }
void zenith_cpu_host_lock(u64 index,u64 value) { if (index<count) STORE(cpus[index].lock,(u32)value); }
u64 zenith_cpu_host_step(u64 index) {
    if (index==0||index>=count) return 0;
    struct cpu *c=&cpus[index]; struct job *j=claim(c);
    if (!j) return 0;
    if (!LOAD(j->released)) { STORE(j->state,QUEUED); STORE(c->head,c->head-1); return 0; }
    execute(j); __atomic_fetch_add(&c->completed,1,__ATOMIC_RELAXED); c->current=JOBS; STORE(c->state,3); return 1;
}
#endif
