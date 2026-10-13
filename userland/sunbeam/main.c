#include "zenith.h"
#ifndef SUNBEAM_VERSION
#define SUNBEAM_VERSION "0.1.0"
#endif
static volatile unsigned long native_abi = 1;
static unsigned long length(const char *text) { unsigned long n=0; while(text[n])n++; return n; }
static int same(const char *a,const char *b) { while(*a && *a==*b){a++;b++;}return *a==*b; }
static void print(const char *text) { if(zenith_write(text,length(text))<0)zenith_exit(1); }
static void number(unsigned long value) { char buffer[24];unsigned int n=0;do{buffer[n++]=(char)('0'+value%10);value/=10;}while(value);for(unsigned int i=0;i<n/2;i++){char c=buffer[i];buffer[i]=buffer[n-i-1];buffer[n-i-1]=c;}if(zenith_write(buffer,n)<0)zenith_exit(1); }
int main(int argc, char **argv) {
    if(native_abi!=1)return 95;
    if(argc==2 && same(argv[1],"id")){print("uid=");number(zenith_identity(102));print(" gid=");number(zenith_identity(104));print(" pid=");number(zenith_identity(39));print("\n");return 0;}
    if(argc==1){print("Sunbeam native tools " SUNBEAM_VERSION " (Zenith ABI 1)\n");return 0;}
    if(same(argv[1],"echo")) {
        int start=2,newline=1;if(argc>2 && same(argv[2],"-n")){start=3;newline=0;}
        for(int i=start;i<argc;i++){if(i>start)print(" ");print(argv[i]);}if(newline)print("\n");return 0;
    }
    if(same(argv[1],"basename") && (argc==3 || argc==4)) {
        const char *value=argv[2];unsigned long end=length(value),start;
        if(!end){print("\n");return 0;}
        while(end>1 && value[end-1]=='/')end--;
        start=end;while(start && value[start-1]!='/')start--;
        if(end==1 && value[0]=='/')start=0;
        if(argc==4){unsigned long suffix=length(argv[3]);if(suffix && suffix<end-start){unsigned long i=0;while(i<suffix && value[end-suffix+i]==argv[3][i])i++;if(i==suffix)end-=suffix;}}
        if(zenith_write(value+start,end-start)<0){return 1;}print("\n");return 0;
    }
    if(same(argv[1],"dirname") && argc==3) {
        const char *value=argv[2];unsigned long end=length(value);
        while(end>1 && value[end-1]=='/')end--;
        while(end && value[end-1]!='/')end--;
        if(!end){print(".\n");return 0;}
        while(end>1 && value[end-1]=='/')end--;
        if(zenith_write(value,end)<0){return 1;}print("\n");return 0;
    }
    if(same(argv[1],"--cpu-state")) {
        unsigned int mxcsr=0x3f80;unsigned short control=0x077f;
        __asm__ volatile("fldcw %0; ldmxcsr %1" : : "m"(control), "m"(mxcsr) : "memory");
        __asm__ volatile("xor %%eax,%%eax; mov %%ax,%%ds; mov %%ax,%%es; mov %%ax,%%fs; mov %%ax,%%gs" : : : "rax", "memory");
        return 0;
    }
    if(same(argv[1],"--memory")){native_abi=2;native_abi=1;return 0;}
    if(same(argv[1],"--nx")){((void (*)(void))&native_abi)();return 1;}
    if(same(argv[1],"--code-write")){*(volatile unsigned char *)(void *)&main=0;return 1;}
    /* Deliberate containment probes; never execute these in kernel mode. */
    if(same(argv[1],"--fault")){*(volatile unsigned long *)0xffff800000000000UL=1;return 1;}
    if(same(argv[1],"--privileged")){__asm__ volatile("cli");return 1;}
    if(same(argv[1],"--loop")){for(;;)__asm__ volatile("pause");}
    if(same(argv[1],"--bad-pointer")){return zenith_write((void *)0xffff800000000000UL,4)==-14?0:1;}
    print("Usage: native run sunbeam [echo [-n] ARGS... | basename PATH [SUFFIX] | dirname PATH | id]\n");return 2;
}
