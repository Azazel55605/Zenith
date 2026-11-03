/* All metadata changes are native, IRQ-scoped and follow pool -> CPU lock order.
 * Legacy producers take only the CPU lock and cannot enter a leased process CPU.
 * Completion retains the lease/storage until the original generation is reaped. */
static int process_acquire(void) {
    if(LOAD(process_poisoned))return 0;
    u32 empty=0;return __atomic_compare_exchange_n(&process_lock,&empty,zenith_cpu_logical_id()+1,0,__ATOMIC_ACQUIRE,__ATOMIC_RELAXED);
}
static void process_unlock(void) { __atomic_store_n(&process_lock,0,__ATOMIC_RELEASE); }
static int process_healthy(u32 cpu) { return cpu>0&&cpu<count&&!LOAD(cpus[cpu].halted)&&(LOAD(cpus[cpu].state)==2||LOAD(cpus[cpu].state)==3); }
static int process_eligible(u32 cpu) {
    return process_healthy(cpu)&&LOAD(cpus[cpu].head)==LOAD(cpus[cpu].tail)&&
        (cpus[cpu].raw_task<PROCESSES||(cpus[cpu].current==JOBS&&LOAD(cpus[cpu].state)==3));
}
static void process_terminal(struct process_task *p,u64 state) {
    if(p->ticket) {if(!LOAD(process_runnable[p->target])) __builtin_trap();STORE(process_runnable[p->target],LOAD(process_runnable[p->target])-1);p->ticket=0;}
    STORE(p->state,state);
}
u64 zenith_cpu_process_submit(u64 context,u64 affinity) {
    u64 flags=irq_off(),token=0;
    if(zenith_cpu_logical_id()!=0||count<2||LOAD(stopping)||!context||!(affinity&254)||affinity>255)goto done;
#ifndef ZENITH_CPU_HOST_TEST
    if(context<0xffff800000000000ULL||!install_completion())goto done;
#endif
#ifndef ZENITH_CPU_HOST_TEST
    if(!process_period_count) {
        u32 initial=apic_read(&cpus[0],0x380),divide=apic_read(&cpus[0],0x3e0);
        if(initial<1000||initial>0x7fffffff)goto done;
        process_period_divide=divide;STORE(process_period_count,initial);
    }
#endif
    if(!process_acquire())goto done;
    u32 slot;
    for(slot=0;slot<PROCESSES;slot++)if(LOAD(process_tasks[slot].state)==FREE)break;
    if(slot==PROCESSES||sequence==0x03ffffffffffffffULL||process_order==~0ULL)goto unlock;
    u32 best=0;u64 load=~0ULL;
    for(u32 n=0;n<count-1;n++) {
        u32 cpu=1+(u32)((process_cursor+n)%(count-1));
        if(!(affinity&(1ULL<<cpu))||!lock_cpu(&cpus[cpu]))continue;
        if(process_eligible(cpu)&&LOAD(process_runnable[cpu])<load) {best=cpu;load=LOAD(process_runnable[cpu]);}
        unlock_cpu(&cpus[cpu]);
    }
    if(!best||!lock_cpu(&cpus[best]))goto unlock;
    if(!process_eligible(best)) {unlock_cpu(&cpus[best]);goto unlock;}
    struct process_task *p=&process_tasks[slot];
    token=(++sequence<<6)|slot;
    STORE(p->token,token);p->context=context;p->cancel=0;p->affinity=affinity;p->target=best;p->actual=0;
    p->order=++process_order;p->ticket=1;p->result=p->start=p->end=0;
    STORE(process_reserved[best],LOAD(process_reserved[best])+1);STORE(process_runnable[best],LOAD(process_runnable[best])+1);process_cursor=best;
    STORE(p->state,QUEUED);unlock_cpu(&cpus[best]);
    /* Wake every eligible affinity owner: an idle sibling may transfer a queued
     * context from a loaded target. It never moves a Running kernel/user frame. */
    for(u32 cpu=1;cpu<count;cpu++)if((affinity&(1ULL<<cpu))&&process_healthy(cpu))wake(&cpus[cpu]);
unlock:
    process_unlock();
done:
    irq_restore(flags);return token;
}
static struct process_task *process_find(u64 token) {
    u32 slot=(u32)(token&63);
    if(!token||slot>=PROCESSES)return 0;
    struct process_task *p=&process_tasks[slot];
    return LOAD(p->token)==token&&LOAD(p->state)!=FREE?p:0;
}
u64 zenith_cpu_process_field(u64 token,u64 field) {
    if(LOAD(process_poisoned))return field==0?6:0;
    /* A faulted CPU cannot access its context after release-publishing halted.
     * Readers do not need a potentially abandoned target/metadata lock. */
    struct process_task *p=process_find(token);if(!p)return 0;
    if(LOAD(p->state)==FAULTED&&p->target<count&&!LOAD(cpus[p->target].halted))return field==0?RUNNING:0;
    switch(field) {case 0:return LOAD(p->state);case 1:return LOAD(p->result);case 2:return LOAD(p->actual);
        case 3:return LOAD(p->start);case 4:return LOAD(p->end);default:return 0;}
}
u64 zenith_cpu_process_control(u64 token,u64 action) {
    u64 flags=irq_off(),ok=0;
    if(zenith_cpu_logical_id()!=0)goto done;
    struct process_task *p=process_find(token);if(!p)goto done;
    if(action==0) {
        STORE(p->cancel,1);
        if(process_acquire()) {if(LOAD(p->state)==QUEUED)process_terminal(p,CANCELLED);process_unlock();}
        if(process_healthy((u32)p->target))wake(&cpus[p->target]);ok=1;goto done;
    }
    if(action!=2||!process_acquire())goto done;
    if(LOAD(p->state)>=DONE&&LOAD(p->state)!=FAULTED&&
        (process_healthy((u32)p->target)||(LOAD(cpus[p->target].state)==5&&LOAD(cpus[p->target].halted)))&&lock_cpu(&cpus[p->target])) {
        if(!LOAD(process_reserved[p->target])||p->ticket)__builtin_trap();
        STORE(process_reserved[p->target],LOAD(process_reserved[p->target])-1);STORE(p->state,FREE);ok=1;unlock_cpu(&cpus[p->target]);
    }
    /* Faulted descriptors remain reserved. The halted witness still permits
     * freeing invocation memory, never restarting/reusing the failed owner. */
    process_unlock();
done:
    irq_restore(flags);return ok;
}
u64 zenith_cpu_process_mask(void) {
    u64 mask=0;
    for(u32 cpu=1;cpu<count;cpu++)if(process_healthy(cpu))mask|=1ULL<<cpu;
    return mask;
}
u64 zenith_cpu_process_wait(u64 token) {
    u64 flags=irq_off();struct process_task *p=process_find(token);
    if(!p||zenith_cpu_logical_id()!=0) {irq_restore(flags);return 0;}
#ifndef ZENITH_CPU_HOST_TEST
    if(LOAD(p->state)<DONE&&(flags&0x200))__asm__ volatile("sti; hlt; cli":::"memory");
#endif
    irq_restore(flags);return 1;
}
static int process_pending(struct cpu *c) { return LOAD(process_runnable[c-cpus])!=0; }
static struct process_task *process_claim(struct cpu *c) {
    u32 cpu=(u32)(c-cpus);struct process_task *best=0;
    if(!process_acquire())return 0;
    for(u32 i=0;i<PROCESSES;i++) {
        struct process_task *p=&process_tasks[i];
        if(LOAD(p->state)!=QUEUED)continue;
        if(LOAD(p->cancel)||LOAD(stopping)) {process_terminal(p,CANCELLED);continue;}
        if(!(p->affinity&(1ULL<<cpu)))continue;
        if(p->target!=cpu&&LOAD(process_runnable[p->target])<=LOAD(process_runnable[cpu]))continue;
        if(!best||p->order<best->order)best=p;
    }
    if(best&&lock_cpu(c)) {
        if(LOAD(c->head)!=LOAD(c->tail)||c->current!=JOBS||c->raw_task<PROCESSES||LOAD(c->halted))best=0;
        else {
            u32 old=(u32)best->target;
            if(old!=cpu) {
                if(!lock_cpu(&cpus[old]))best=0;
                else {
                    STORE(process_reserved[old],LOAD(process_reserved[old])-1);STORE(process_runnable[old],LOAD(process_runnable[old])-1);
                    STORE(process_reserved[cpu],LOAD(process_reserved[cpu])+1);STORE(process_runnable[cpu],LOAD(process_runnable[cpu])+1);best->target=cpu;
                    process_moves++;unlock_cpu(&cpus[old]);
                }
            }
            if(best) {c->raw_task=(u32)(best-process_tasks);best->actual=cpu;best->start=ticks();STORE(c->state,2);STORE(best->state,RUNNING);}
        }
        unlock_cpu(c);
    } else best=0;
    process_unlock();return best;
}
#ifndef ZENITH_CPU_HOST_TEST
extern u64 zenith_process_ap_dispatch(u64 context);
#endif
static int process_step(struct cpu *c) {
    struct process_task *p=process_claim(c);if(!p)return 0;
    u64 result=130;
    if(!LOAD(p->cancel)&&!LOAD(stopping)) {
#ifndef ZENITH_CPU_HOST_TEST
        result=zenith_process_ap_dispatch(p->context);
#else
        if(LOAD(host_pause)) {
            STORE(host_seen,1);
            while(LOAD(host_pause)&&!LOAD(p->cancel)&&!LOAD(stopping))pause_cpu();
        }
        result=*((u64*)p->context); /* Host queue tests only; never kernel code. */
#endif
    }
#ifndef ZENITH_CPU_HOST_TEST
    apic_write(c,0x320,0x10000|0xf2);apic_write(c,0x380,0);
#endif
    /* Metadata critical sections never contain user execution or managed calls. */
    while(!process_acquire()) {
        if(LOAD(process_poisoned)) {
#ifndef ZENITH_CPU_HOST_TEST
            zenith_gc_fail_stop();
#else
            __builtin_trap();
#endif
        }
        pause_cpu();
    }
    p->result=result;p->end=ticks();process_runs++;
    process_terminal(p,(LOAD(p->cancel)||LOAD(stopping))?CANCELLED:DONE);
    c->raw_task=PROCESSES;STORE(c->state,3);process_unlock();
    signal_cpu(&cpus[0],0xf6);return 1;
}
static void process_fault(struct cpu *c) {
    /* Called only on the target's private fault path; no user/kernel body runs
     * afterward. Mark all of its contexts before publishing the halt witness. */
    /* A kernel fault inside queue metadata cannot be recovered by guessing
     * which writes completed. Fail-stop rather than abandon a locked queue.
     * Ordinary protected user faults are returned by the private bridge. */
    u64 started=ticks();
    while(!process_acquire()) {
        if(LOAD(process_lock)==(u32)(c-cpus)+1||ticks()-started>100000000ULL) {
            STORE(process_poisoned,1);
#ifndef ZENITH_CPU_HOST_TEST
            zenith_gc_fail_stop();
#else
            __builtin_trap();
#endif
        }
        pause_cpu();
    }
    for(u32 i=0;i<PROCESSES;i++) {
        struct process_task *p=&process_tasks[i];
        if(p->target==(u64)(c-cpus)&&LOAD(p->state)!=FREE&&LOAD(p->state)<DONE) {
            p->result=139;p->end=ticks();process_terminal(p,FAULTED);
        }
    }
    process_unlock();
}
static void process_stop(void) {
    while(!process_acquire()) {
        if(LOAD(process_poisoned)) {
#ifndef ZENITH_CPU_HOST_TEST
            zenith_gc_fail_stop();
#else
            __builtin_trap();
#endif
        }
        pause_cpu();
    }
    for(u32 i=0;i<PROCESSES;i++) {
        STORE(process_tasks[i].cancel,1);
        if(LOAD(process_tasks[i].state)==QUEUED)process_terminal(&process_tasks[i],CANCELLED);
    }
    process_unlock();
}
#ifdef ZENITH_CPU_HOST_TEST
static void process_reset(void) {
    u8 *p=(u8*)process_tasks;for(u64 i=0;i<sizeof(process_tasks);i++)p[i]=0;
    for(u32 i=0;i<CPUS;i++)process_reserved[i]=process_runnable[i]=0;
    process_lock=process_poisoned=0;process_order=process_cursor=process_moves=process_runs=0;
}
#endif
/* AP bridge helpers run only with IF clear, after a leased task was claimed. */
u64 zenith_cpu_process_allowed(u64 context) {
    u32 cpu=zenith_cpu_logical_id();if(!cpu||cpu>=count||cpus[cpu].raw_task>=PROCESSES)return 0;
    struct process_task *p=&process_tasks[cpus[cpu].raw_task];
    return LOAD(p->state)==RUNNING&&p->actual==cpu&&p->context==context;
}
u64 zenith_cpu_process_prepare(u64 tss) {
    u32 cpu=zenith_cpu_logical_id();if(!cpu||cpu>=count||cpus[cpu].raw_task>=PROCESSES||!tss)return 0;
    struct cpu *c=&cpus[cpu];struct process_task *p=&process_tasks[c->raw_task];
    u8 *to=(u8*)tss,*from=(u8*)&c->tss;for(u32 i=0;i<104;i++)to[i]=from[i];
#ifndef ZENITH_CPU_HOST_TEST
    u32 initial=(u32)LOAD(process_period_count),divide=(u32)process_period_divide;
    if(initial<1000||initial>0x7fffffff)return 0;
    apic_write(c,0x3e0,divide);apic_write(c,0x320,0xf4);
    /* The asm arms/rearms only after raw IDT/CR3 are installed. */
    p->result=initial;
#endif
    return (u64)&p->cancel;
}
u64 zenith_cpu_process_period(void) {
    u32 cpu=zenith_cpu_logical_id();return cpu&&cpu<count&&cpus[cpu].raw_task<PROCESSES?process_tasks[cpus[cpu].raw_task].result:0;
}
u64 zenith_cpu_process_stats(u64 field,u64 cpu) {
    if(field==0)return LOAD(process_runs);if(field==1)return LOAD(process_moves);
    if(cpu>=count)return 0;
    if(field==2)return LOAD(process_runnable[cpu]);if(field==3)return LOAD(process_reserved[cpu]);
    return 0;
}
#ifdef ZENITH_CPU_HOST_TEST
u64 zenith_cpu_host_process_step(u64 cpu) {
    if(!cpu||cpu>=count)return 0;
    host_cpu_identity=cpu;u64 result=process_step(&cpus[cpu]);host_cpu_identity=0;return result;
}
void zenith_cpu_host_process_fault(u64 cpu) {
    if(cpu>0&&cpu<count) {process_fault(&cpus[cpu]);STORE(cpus[cpu].state,4);STORE(cpus[cpu].halted,1);}
}
#endif

#ifndef ZENITH_CPU_HOST_TEST
__attribute__((noreturn)) void zenith_cpu_process_bad_irq(u64 vector) {
    u32 cpu=zenith_cpu_logical_id();
    if(cpu&&cpu<count)zenith_cpu_fault(&cpus[cpu],vector,(u64)__builtin_frame_address(0));
    zenith_gc_fail_stop();__builtin_unreachable();
}
#endif
