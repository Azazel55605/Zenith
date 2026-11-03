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
    u64 start, end, result, managed_phase, managed_release;
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
    u64 timer_ticks, reschedules;
    u32 lock, head, tail, current, raw_task;
    u64 ring[JOBS];
    struct job jobs[JOBS];
    u8 work_stack[262144] __attribute__((aligned(16)));
    u8 irq_stack[8192] __attribute__((aligned(16)));
    u8 gc_stack[8192] __attribute__((aligned(16)));
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
static u64 count, hhdm, stopping, sequence, managed_callback, completion_signals;
static int cancelled(struct job *j);
#ifdef ZENITH_CPU_HOST_TEST
static u64 host_pause, host_seen;
static _Thread_local u64 host_cpu_identity;
#endif
#include "blur.h"
#include "process.h"
#ifndef ZENITH_CPU_HOST_TEST
extern u64 zenith_gc_managed_enter(void);
extern u64 zenith_gc_managed_leave(void);
extern void zenith_gc_fail_stop(void);
extern u64 zenith_execution_test(void);
extern void zenith_cpu_ap_entry(void);
extern void zenith_cpu_wake_entry(void);
extern void zenith_cpu_reschedule_entry(void);
extern void zenith_cpu_timer_entry(void);
extern void zenith_gc_stop_entry(void);
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
    return host_cpu_identity;
#else
    u32 a=1,b,c,d; __asm__ volatile("cpuid":"+a"(a),"=b"(b),"=c"(c),"=d"(d)); return b>>24;
#endif
}
/* Scheduler identity is hardware-derived even before C1 bring-up. Native APs
 * have logical identities but no managed scheduler state/heap admission yet.
 * Unknown CPUs fail closed; never silently alias the BSP scheduler state. */
u32 zenith_cpu_logical_id(void) {
#ifdef ZENITH_CPU_HOST_TEST
    return (u32)host_cpu_identity;
#else
    if (msr(0x1b)&0x100) return 0;
    u64 id=identity();
    for (u32 i=1;i<count;i++) if (cpus[i].apic==id) return i;
    return 0xffffffffU;
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
/* Shared with the signature-checked central InterruptManager adapter. Static
 * native leases apply at boot, not just after the first worker registration.
 * The pinned MSI allocator stops at EE. 256 admits its BSP registry operation;
 * it is never a vector. No AP can mutate global routing/handler ownership. */
u64 zenith_cpu_irq_registration_allowed(u64 vector) {
    if(zenith_cpu_logical_id()!=0)return 0;
    return vector==256||(vector<256&&vector!=0xf1&&vector!=0xf3&&vector!=0xf4&&vector!=0xf5&&vector!=0xf6);
}
static int signal_cpu(struct cpu *c,u32 vector) {
#ifdef ZENITH_CPU_HOST_TEST
    (void)vector; __atomic_fetch_add(&c->wakes,1,__ATOMIC_RELAXED);
    return 1;
#else
    u32 sender=zenith_cpu_logical_id();
    if(sender>=count) return 0;
    struct cpu *b=&cpus[sender];
    /* The ICR belongs to the executing sender, including AP->BSP completion
     * notifications. Never assume the BSP's APIC mode/base describes this CPU.
     * x2APIC WRMSR may pass earlier memory stores. Publish queue/stop state before
     * the doorbell, even on machines where a plain WRMSR is not serializing. */
    __asm__ volatile("mfence":::"memory");
    if (b->x2) {
        /* Intel SDM 11.12.3 requires MFENCE;LFENCE before non-serializing
         * x2APIC WRMSR (MFENCE alone is insufficient on Intel). */
        __asm__ volatile("lfence":::"memory");
        wrmsr(0x830,(c->apic<<32)|vector); return 1;
    }
    for (u32 n=0;n<100000;n++) {
        if (!(apic_read(b,0x300)&0x1000)) {
            apic_write(b,0x310,(u32)c->apic<<24);
            apic_write(b,0x300,vector);
            return 1;
        }
        pause_cpu();
    }
    __atomic_fetch_add(&c->ipi_errors,1,__ATOMIC_RELAXED);
    return 0;
#endif
}
static int wake(struct cpu *c) { return signal_cpu(c,WAKE); }
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
    c->tss.ist[1]=(u64)(c->fault_stack+sizeof(c->fault_stack));
    c->tss.ist[2]=(u64)(c->gc_stack+sizeof(c->gc_stack)); c->tss.iomap=104;
    c->current=JOBS; c->raw_task=PROCESSES;
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
    make_gate(0xf3,(u64)zenith_cpu_reschedule_entry,1);
    make_gate(0xf4,(u64)zenith_cpu_timer_entry,1);
    make_gate(0xf5,(u64)zenith_gc_stop_entry,3);
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
void zenith_cpu_interrupt(struct cpu *c,u64 stack,u64 vector) {
    if (stack<(u64)c->irq_stack||stack>=(u64)(c->irq_stack+sizeof(c->irq_stack))) __atomic_fetch_add(&c->irq_stack_errors,1,__ATOMIC_RELAXED);
    if (vector==WAKE) __atomic_fetch_add(&c->wakes,1,__ATOMIC_RELAXED);
    if (vector==0xf3) __atomic_fetch_add(&c->reschedules,1,__ATOMIC_RELAXED);
    if (vector==0xf4) __atomic_fetch_add(&c->timer_ticks,1,__ATOMIC_RELAXED);
    apic_write(c,0xb0,0);
}
/* A one-shot AP-local timer diagnostic, not scheduler admission or a periodic
 * polling source. Its absolute frequency is deliberately not used as a clock.
 * Stop wakes the parked probe and disarms it, preserving C1 bounded shutdown. */
u64 zenith_cpu_timer_probe(void) {
    u32 id=zenith_cpu_logical_id();
    if (!id||id>=count) return 0;
    struct cpu *c=&cpus[id]; u64 before=LOAD(c->timer_ticks);
    apic_write(c,0x3e0,3); /* Divide by 16. */
    apic_write(c,0x320,0xf4); apic_write(c,0x380,100000);
    while (LOAD(c->timer_ticks)==before&&!LOAD(stopping)) park();
    apic_write(c,0x320,0x10000|0xf2); apic_write(c,0x380,0);
    return LOAD(c->timer_ticks)!=before;
}
__attribute__((noreturn)) void zenith_cpu_fault(struct cpu *c,u64 vector,u64 stack) {
    c->fault=vector;
    c->fault_stack_ok=stack>=(u64)c->fault_stack&&stack<(u64)(c->fault_stack+sizeof(c->fault_stack));
    if (c->current<JOBS) STORE(c->jobs[c->current].state,FAULTED);
    process_fault(c);
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
static u64 enqueue_work(u64 index,u64 kind,u64 n,u64 seed,u64 rounds,u64 held,int blur) {
    u64 f=irq_off(),token=0;
    if (identity()!=cpus[0].actual||LOAD(stopping)||index==0||index>=count||
        kind<1||kind>(blur?6ULL:5ULL)||(kind==5&&!LOAD(managed_callback))||!n||n>(kind==1?100000000ULL:kind==6?BLUR_W*BLUR_H:PIXELS)||!rounds||rounds>128) goto end;
    struct cpu *c=&cpus[index];
    if (!lock_cpu(c)) goto end;
    u64 state=LOAD(c->state);
    if ((state!=2&&state!=3)||c->tail-c->head>=JOBS||LOAD(c->halted)||LOAD(process_reserved[index])) goto unlock;
    u32 i;
    for (i=0;i<JOBS;i++) if (LOAD(c->jobs[i].state)==FREE) break;
    if (i==JOBS||sequence==0x03ffffffffffffffULL) goto unlock;
    struct job *j=&c->jobs[i];
    token=(++sequence<<6)|(index*JOBS+i);
    j->token=token; j->cancel=0; j->released=!held; j->kind=kind; j->count=n;
    j->seed=seed; j->rounds=rounds; j->start=j->end=j->result=j->managed_phase=j->managed_release=0;
    STORE(j->state,QUEUED);
    c->ring[c->tail%JOBS]=token; STORE(c->tail,c->tail+1);
unlock:
    unlock_cpu(c);
    if (token) wake(c);
end:
    irq_restore(f); return token;
}
u64 zenith_cpu_enqueue(u64 index,u64 kind,u64 n,u64 seed,u64 rounds,u64 held) {
    return enqueue_work(index,kind,n,seed,rounds,held,0);
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
        if (field==4) v=LOAD(j->managed_phase);
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
    if (action==3&&j->kind==5&&LOAD(j->state)==RUNNING) { STORE(j->managed_release,1);ok=1; }
    if (action==2&&LOAD(j->state)>=DONE) {
        if(j->kind==6&&blur_work[(token&63)/JOBS].token==token)blur_work[(token&63)/JOBS].token=0;
        STORE(j->state,FREE); ok=1;
    }
    unlock_cpu(c);
    if (ok&&action!=2) wake(c);
end:
    irq_restore(f); return ok;
}
/* Short BSP-owned compute waits keep the current thread until an ordinary
 * completion interrupt (with the existing BSP timer as a fallback), rather than enqueueing a
 * 1ms sleep that only runs again after an entire scheduler quantum. No spin,
 * no AP managed scheduler callback and no change to general sleep semantics. */
u64 zenith_cpu_wait_interrupt(u64 token) {
    u64 flags=irq_off();struct job *j;struct cpu *c=lookup(token,&j);
    if(!c||identity()!=cpus[0].actual) {if(c)unlock_cpu(c);irq_restore(flags);return 0;}
    u64 terminal=LOAD(j->state)>=DONE;unlock_cpu(c);
#ifndef ZENITH_CPU_HOST_TEST
    if(!terminal&&(flags&0x200))__asm__ volatile("sti; hlt; cli":::"memory");
#endif
    irq_restore(flags);return terminal||((flags&0x200)!=0);
}
u64 zenith_cpu_pixel(u64 token,u64 index) {
    u64 f=irq_off(),v=0; struct job *j; struct cpu *c=lookup(token,&j);
    if (c) { if (LOAD(j->state)==DONE&&j->kind==2&&index<j->count) v=j->pixels[index]; unlock_cpu(c); }
    irq_restore(f); return v;
}
/* Trusted C3 frame bridge. A private managed frame is copied only after its
 * computation completes; BSP readers require terminal success and exact length.
 * No pointer/reference is retained in a descriptor or exposed to clients. */
u64 zenith_cpu_managed_pixels(u64 token,u32 *buffer,u64 length,u64 publish) {
    u64 flags=irq_off(),ok=0;struct job *j;struct cpu *c=lookup(token,&j);
    if(c) {
        if(buffer&&length==j->count&&length<=PIXELS&&j->kind==5&&j->rounds==4&&
            ((publish==1&&c->actual==identity()&&LOAD(j->state)==RUNNING)||
             (publish==0&&cpus[0].actual==identity()&&LOAD(j->state)==DONE))) {
            for(u64 i=0;i<length;i++) {if(publish)j->pixels[i]=buffer[i];else buffer[i]=j->pixels[i];}
            ok=1;
        }
        unlock_cpu(c);
    }
    irq_restore(flags);return ok;
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
    } else if (j->kind==6) {
        blur_execute(j);
        if(LOAD(j->state)==FAULTED)return;
        __atomic_fetch_add(cancelled(j)?&blur_cancelled:&blur_completed,1,__ATOMIC_RELAXED);
    } else if (j->kind==5) {
#ifndef ZENITH_CPU_HOST_TEST
        if (!zenith_gc_managed_enter()) { STORE(j->state,FAULTED);return; }
        __asm__ volatile("sti":::"memory");
        result=((u64(*)(u64,u64,u64,u64,u64))LOAD(managed_callback))
            (zenith_cpu_logical_id(),j->count,j->seed,j->rounds,j->token);
        __asm__ volatile("cli":::"memory");
        if (!zenith_gc_managed_leave()) zenith_gc_fail_stop();
        if (result==~0ULL) { STORE(j->end,ticks());STORE(j->result,result);STORE(j->state,FAULTED);return; }
#endif
    } else if (j->kind==4) {
#ifndef ZENITH_CPU_HOST_TEST
        result=zenith_execution_test() | ((u64)zenith_cpu_logical_id()<<32);
#else
        result=1;
#endif
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
    if(c->raw_task<PROCESSES||process_pending(c))return 0;
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
        if(process_step(c))continue;
        struct job *j=claim(c);
        if (!j) {
            STORE(c->state,3);
            /* Producer cannot clear pending IPI: queue state is authoritative. */
            if (LOAD(stopping)||LOAD(c->head)!=LOAD(c->tail)||process_pending(c)) continue;
            sleep_worker(c); continue;
        }
        while (!LOAD(j->released)&&!cancelled(j)) sleep_worker(c);
        /* A terminal publication lets BSP reap/reuse this descriptor. Capture
         * the notification kind while RUNNING still owns it; never reread job
         * fields after execute() publishes terminal state. */
        int notify=j->kind==5||j->kind==6;
        execute(j);
        if(notify)signal_cpu(&cpus[0],0xf6);
        __atomic_fetch_add(&c->completed,1,__ATOMIC_RELAXED); c->current=JOBS;
    }
}
#endif
#ifndef ZENITH_CPU_HOST_TEST
u64 zenith_cpu_gc_notify(u64 mask) {
    u64 sent=0;
    for (u32 i=1;i<count;i++)
        if ((mask&(1ULL<<i))&&!LOAD(cpus[i].halted)&&signal_cpu(&cpus[i],0xf5)) sent|=1ULL<<i;
    return sent;
}
void zenith_cpu_gc_eoi(void) {
    u32 cpu=zenith_cpu_logical_id();
    if (cpu>0&&cpu<count) apic_write(&cpus[cpu],0xb0,0);
}
#endif
#ifndef ZENITH_CPU_HOST_TEST
extern void irq246_stub(void);
extern void zenith_cpu_completion_entry(void);
/* Reserve an otherwise unused BSP vector only when the pinned original gate
 * matches exactly. Never overwrite an unknown handler or introduce a managed
 * callback on an AP. The native doorbell just wakes a halted BSP waiter. */
static int install_completion(void) {
    u64 flags=irq_off();struct descriptor table;u16 selector;
    __asm__ volatile("sidt %0":"=m"(table));
    __asm__ volatile("mov %%cs,%0":"=r"(selector));
    int ok=0;
    if(table.address&&table.limit==4095) {
        struct gate *gate=&((struct gate*)table.address)[0xf6];
        u64 original=(u64)gate->low|((u64)gate->mid<<16)|((u64)gate->high<<32);
        if(original==(u64)irq246_stub&&gate->selector==selector&&gate->flags==0x8e&&gate->ist==0) {
            u64 address=(u64)zenith_cpu_completion_entry;
            gate->low=(u16)address;gate->mid=(u16)(address>>16);gate->high=(u32)(address>>32);
            __asm__ volatile("mfence":::"memory");ok=1;
        } else if(original==(u64)zenith_cpu_completion_entry&&gate->selector==selector&&gate->flags==0x8e&&gate->ist==0)ok=1;
    }
    irq_restore(flags);return ok;
}
void zenith_cpu_completion_eoi(void) {
    __atomic_fetch_add(&completion_signals,1,__ATOMIC_RELAXED);
    apic_write(&cpus[0],0xb0,0);
}
#endif
/* Kernel-private compositor admission: select a healthy idle owner, rotating
 * ties. Never queue latency-sensitive work behind a held managed/service test.
 * Dimensions are bounded before touching source; callers supply validated arrays.
 * Copy a complete immutable halo while IF is masked, then release exactly once. */
u64 zenith_cpu_blur_submit(const u32 *source,u64 width,u64 height,u64 x,u64 y,u64 w,u64 h) {
    u64 flags=irq_off(),token=0;
    if(identity()!=cpus[0].actual||LOAD(stopping)||count<2||!source||!width||!height||
        width>4096||height>4096||!w||w>BLUR_W||!h||h>BLUR_H||x>=width||y>=height||w>width-x||h>height-y)goto end;
#ifndef ZENITH_CPU_HOST_TEST
    if(!install_completion())goto end;
#endif
    for(u32 attempt=0;attempt<count-1;attempt++) {
        u32 cpu=1+(u32)((blur_cursor+attempt)%(count-1));struct cpu *c=&cpus[cpu];
        if(LOAD(c->state)!=3||LOAD(c->halted)||LOAD(c->head)!=LOAD(c->tail)||blur_work[cpu].token)continue;
        struct blur_work *b=&blur_work[cpu];b->width=(u32)w;b->height=(u32)h;
        for(u32 dy=0;dy<h+8;dy++)for(u32 dx=0;dx<w+8;dx++) {
            long long sx=(long long)x+dx-4,sy=(long long)y+dy-4;
            if(sx<0)sx=0;if(sy<0)sy=0;
            if((u64)sx>=width)sx=(long long)width-1;if((u64)sy>=height)sy=(long long)height-1;
            b->source[dy*(w+8)+dx]=source[sy*width+sx];
        }
        token=enqueue_work(cpu,6,w*h,0,1,1,1);
        if(!token)continue;
        b->token=token;blur_cursor=cpu;
        if(!zenith_cpu_control(token,1)) {zenith_cpu_control(token,0);token=0;}
        break;
    }
end:
    irq_restore(flags);return token;
}
u64 zenith_cpu_blur_copy(u64 token,u32 *target,u64 length) {
    u64 flags=irq_off(),ok=0;struct job *j;struct cpu *c=lookup(token,&j);
    if(c) {
        struct blur_work *b=&blur_work[(token&63)/JOBS];
        if(identity()==cpus[0].actual&&target&&b->token==token&&j->kind==6&&
            length==j->count&&LOAD(j->state)==DONE&&!LOAD(stopping)) {
            for(u64 i=0;i<length;i++)target[i]=b->output[i];
            ok=1;
        }
        unlock_cpu(c);
    }
    irq_restore(flags);return ok;
}
/* A single trusted kernel UCO entry, registered once on the BSP after its
 * permanent scheduler/TLS/TLAB owners exist. This is not a client callback API. */
u64 zenith_cpu_managed_callback(u64 address) {
    if (identity()!=cpus[0].actual||LOAD(stopping)||!count||address<0xffff800000000000ULL) return 0;
    u64 existing=LOAD(managed_callback);
    if (existing&&existing!=address) return 0;
#ifndef ZENITH_CPU_HOST_TEST
    if(!install_completion())return 0;
#endif
    STORE(managed_callback,address);return 1;
}
u64 zenith_cpu_managed_trace(u64 token,u64 phase) {
    u64 flags=irq_off(),ok=0;struct job *j;struct cpu *c=lookup(token,&j);
    if(c) {if(c->actual==identity()&&j->kind==5&&LOAD(j->state)==RUNNING) {STORE(j->managed_phase,phase);ok=1;}unlock_cpu(c);}
    irq_restore(flags);return ok;
}
u64 zenith_cpu_managed_checkpoint(u64 token) {
    u64 flags=irq_off();struct job *j;struct cpu *c=lookup(token,&j);
    if (!c) {irq_restore(flags);return 0;}
    int valid=c->actual==identity()&&j->kind==5&&LOAD(j->state)==RUNNING;
    if(valid)STORE(j->managed_phase,100);
    unlock_cpu(c);
    if(!valid){irq_restore(flags);return 0;}
    while(!LOAD(j->managed_release)&&!cancelled(j)) {
#ifndef ZENITH_CPU_HOST_TEST
        park();
#else
        pause_cpu();
#endif
    }
    u64 ok=!cancelled(j);irq_restore(flags);return ok;
}
/* Permanent descriptor storage, borrowed only until this callback returns.
 * The trusted managed loop reads this atomic word; it never retains/publishes it.
 * Stop also sets every job cancel word before waiting for halt acknowledgement. */
u64 zenith_cpu_managed_fault(u64 token) {
    u64 flags=irq_off(),valid=0;struct job *j;struct cpu *c=lookup(token,&j);
    if(c){valid=c->actual==identity()&&j->kind==5&&j->rounds==6&&LOAD(j->state)==RUNNING;unlock_cpu(c);}
    if(valid) {
#ifndef ZENITH_CPU_HOST_TEST
        __asm__ volatile("ud2");
#endif
    }
    irq_restore(flags);return 0;
}
u64 zenith_cpu_managed_cancel_word(u64 token) {
    u64 flags=irq_off(),address=0;struct job *j;struct cpu *c=lookup(token,&j);
    if(c) {if(c->actual==identity()&&j->kind==5&&LOAD(j->state)==RUNNING)address=(u64)&j->cancel;unlock_cpu(c);}
    irq_restore(flags);return address;
}
u64 zenith_cpu_managed_cancelled(u64 token) {
    u64 f=irq_off(),result=1;struct job *j;struct cpu *c=lookup(token,&j);
    if(c) {
        if (c->actual==identity()&&j->kind==5&&LOAD(j->state)==RUNNING) result=cancelled(j);
        unlock_cpu(c);
    }
    irq_restore(f);return result;
}
u64 zenith_cpu_reschedule(u64 index) {
    u64 flags=irq_off(),ok=0;
    if (identity()==cpus[0].actual&&!LOAD(stopping)&&index>0&&index<count&&
        (LOAD(cpus[index].state)==2||LOAD(cpus[index].state)==3)) ok=signal_cpu(&cpus[index],0xf3);
    irq_restore(flags); return ok;
}
void zenith_cpu_stop(void) {
    u64 f=irq_off(); STORE(stopping,1); process_stop();
    for (u32 i=1;i<count;i++) {
        for(u32 j=0;j<JOBS;j++)STORE(cpus[i].jobs[j].cancel,1);
        if(!LOAD(cpus[i].halted))wake(&cpus[i]);
    }
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
        case 19:return LOAD(c->timer_ticks); case 20:return LOAD(c->reschedules);
        case 21:return (u64)c->work_stack;case 22:return sizeof(c->work_stack);
        case 23:return (u64)c->gc_stack;case 25:return LOAD(completion_signals);
        case 24:return sizeof(c->gc_stack);
        case 26:return LOAD(blur_completed);case 27:return LOAD(blur_cancelled);
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
    host_cpu_identity=0;
    count=n<=CPUS?n:CPUS; stopping=sequence=0; host_pause=host_seen=0;
    for(u32 i=0;i<CPUS;i++)blur_work[i].token=0;
    blur_cursor=blur_completed=blur_cancelled=0; process_reset();
    for (u32 i=0;i<count;i++) { configure(&cpus[i],i,0); cpus[i].actual=i; cpus[i].state=3; }
}
void zenith_cpu_host_identity(u64 value) { host_cpu_identity=value; }
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

#include "process.inc"
