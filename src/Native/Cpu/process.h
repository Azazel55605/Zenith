/* Protected user contexts have stable descriptors across ready-queue transfers.
 * Managed service actors remain on BSP; no managed pointer enters AP execution. */
#define PROCESSES 8
struct process_task {
    u64 token,state,context,cancel,affinity,target,actual,order,ticket,result,start,end;
};
static struct process_task process_tasks[PROCESSES];
static u64 process_reserved[CPUS],process_runnable[CPUS],process_order,process_cursor,process_moves,process_runs;
static u32 process_lock;
static u32 process_poisoned;
#ifndef ZENITH_CPU_HOST_TEST
static u64 process_period_count,process_period_divide;
#endif
static int process_step(struct cpu *c);
static int process_pending(struct cpu *c);
static void process_fault(struct cpu *c);
static void process_stop(void);
#ifdef ZENITH_CPU_HOST_TEST
static void process_reset(void);
#endif
