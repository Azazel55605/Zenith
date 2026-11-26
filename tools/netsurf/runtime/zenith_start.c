#include "pthread_impl.h"
#include <stdlib.h>
#include <stdio.h>
extern char **__environ;
extern int main(int argc, char **argv, char **envp);
static struct pthread initial_thread;
uintptr_t zns_thread_pointer(void) { return (uintptr_t)&initial_thread; }
int zns_start(int argc, char **argv, char **envp)
{
    static size_t empty_auxv[2];
    libc.auxv = empty_auxv;
    libc.page_size = 4096;
    __environ = envp;
    initial_thread.tid = 1;
    initial_thread.self = initial_thread.next = initial_thread.prev = &initial_thread;
    initial_thread.locale = &libc.global_locale;
    int status = main(argc, argv, envp);
    fflush(NULL);
    return status;
}
