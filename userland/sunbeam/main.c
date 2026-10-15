#include "zenith.h"
#ifndef SUNBEAM_VERSION
#define SUNBEAM_VERSION "0.1.0"
#endif
static volatile unsigned long native_abi = 1;
static unsigned long length(const char *text) { unsigned long n=0; while(text[n])n++; return n; }
static int same(const char *a,const char *b) { while(*a && *a==*b){a++;b++;}return *a==*b; }
static void print(const char *text) { if(zenith_write(text,length(text))<0)zenith_exit(1); }
static void number(unsigned long value) { char buffer[24];unsigned int n=0;do{buffer[n++]=(char)('0'+value%10);value/=10;}while(value);for(unsigned int i=0;i<n/2;i++){char c=buffer[i];buffer[i]=buffer[n-i-1];buffer[n-i-1]=c;}if(zenith_write(buffer,n)<0)zenith_exit(1); }
static long open_path(const char *path, int directory) { return zenith_call(2,(unsigned long)path,length(path),(unsigned long)directory); }
static int file_error(const char *path, long code) { print("sunbeam: ");print(path);print(": ");print(code==-13?"permission denied":code==-2?"not found":code==-24?"too many open files":code==-14?"invalid buffer":"filesystem operation failed");print("\n");return 1; }
static int cat_file(const char *path) {
    char buffer[4096];long fd=open_path(path,0);if(fd<0)return file_error(path,fd);
    for(;;){long n=zenith_call(0,fd,(unsigned long)buffer,sizeof(buffer));if(n<=0){zenith_call(3,fd,0,0);return n<0?file_error(path,n):0;}if(zenith_write(buffer,n)<0){zenith_call(3,fd,0,0);return file_error(path,-28);}}
}
static void permissions(unsigned int mode) { char text[11];text[0]=(mode&0170000)==0040000?'d':(mode&0170000)==0120000?'l':(mode&0170000)==0020000?'c':(mode&0170000)==0060000?'b':(mode&0170000)==0010000?'p':(mode&0170000)==0140000?'s':'-';const char *bits="rwxrwxrwx";for(int i=0;i<9;i++)text[i+1]=(mode&(1U<<(8-i)))?bits[i]:'-';if(mode&04000)text[3]=text[3]=='x'?'s':'S';if(mode&02000)text[6]=text[6]=='x'?'s':'S';if(mode&01000)text[9]=text[9]=='x'?'t':'T';text[10]=0;print(text); }
static int list_path(const char *path, int all, int details) {
    struct zenith_dirent entry;long fd=open_path(path,1);if(fd<0)return file_error(path,fd);
    for(;;){long n=zenith_call(217,fd,(unsigned long)&entry,sizeof(entry));if(n<=0){zenith_call(3,fd,0,0);return n<0?file_error(path,n):0;}if(!all&&entry.name[0]=='.')continue;
        if(details){permissions(entry.stat.mode);print(" uid=");number(entry.stat.uid);print(" gid=");number(entry.stat.gid);print(" size=");number(entry.stat.size);print(" ");}print(entry.name);print("\n");}
}
int main(int argc, char **argv) {
    if(native_abi!=1)return 95;
    if(argc==1){print("Sunbeam native tools " SUNBEAM_VERSION " (Zenith ABI 1)\n");return 0;}
    if(same(argv[1],"pwd") && argc==2){char cwd[1025];long n=zenith_call(79,(unsigned long)cwd,sizeof(cwd),0);if(n<0)return file_error("cwd",n);print(cwd);print("\n");return 0;}
    if(same(argv[1],"cat") && argc>2){int result=0;for(int i=2;i<argc;i++)result|=cat_file(argv[i]);return result;}
    if(same(argv[1],"ls")){int i=2,all=0,details=0,result=0;for(;i<argc;i++){if(same(argv[i],"-1")){}else if(same(argv[i],"-a"))all=1;else if(same(argv[i],"-l"))details=1;else if(same(argv[i],"-la")||same(argv[i],"-al")){all=1;details=1;}else break;}if(i==argc)return list_path(".",all,details);for(;i<argc;i++)result|=list_path(argv[i],all,details);return result;}
    if(same(argv[1],"--fs-buffers")){long fd=open_path(argc>2?argv[2]:"/etc/hostname",0);if(fd<0)return 1;
        long a=zenith_call(0,fd,(unsigned long)&main,4),b=zenith_call(0,fd,0xffff800000000000UL,4),c=zenith_call(0,fd,0x7ffeffffUL,4);char first=0,other=1;long n=zenith_call(0,fd,(unsigned long)&first,1);zenith_call(3,fd,0,0);long fresh=open_path(argc>2?argv[2]:"/etc/hostname",0);if(fresh<0)return 1;long m=zenith_call(0,fresh,(unsigned long)&other,1);zenith_call(3,fresh,0,0);return a==-14&&b==-14&&c==-14&&n==1&&m==1&&first==other?0:1;}
    if(same(argv[1],"stat") && argc==3){struct zenith_stat info;long result=zenith_call(4,(unsigned long)argv[2],length(argv[2]),(unsigned long)&info);if(result<0)return file_error(argv[2],result);permissions(info.mode);print(" uid=");number(info.uid);print(" gid=");number(info.gid);print(" size=");number(info.size);print("\n");return 0;}
    if(same(argv[1],"--bad-stack-timer")){__asm__ volatile("movabs $0x800000000000,%%rsp; 1: pause; jmp 1b":::"memory");__builtin_unreachable();}
    if(same(argv[1],"--bad-stack")){__asm__ volatile("movabs $0x800000000000,%%rsp; mov $39,%%eax; int $0xfe; ud2":::"memory");__builtin_unreachable();}
    if(same(argv[1],"--fs-requests")){char cwd[1025];for(;;)zenith_call(79,(unsigned long)cwd,sizeof(cwd),0);}
    if(same(argv[1],"--fs-stale")){long fd=open_path(argc>2?argv[2]:"/etc/hostname",0);if(fd<0)return 1;char byte;long a=zenith_call(3,fd,0,0),b=zenith_call(0,fd,(unsigned long)&byte,1),c=zenith_call(3,fd,0,0);return a==0&&b==-9&&c==-9?0:1;}
    if(same(argv[1],"--fs-fault")||same(argv[1],"--fs-loop")){for(int i=0;i<16;i++)if(open_path(argc>2?argv[2]:"/etc/hostname",0)<0)return 1;if(same(argv[1],"--fs-loop")){for(;;)__asm__ volatile("pause");}*(volatile unsigned long *)0xffff800000000000UL=1;return 1;}
    if(same(argv[1],"--fs-cpu-state")){unsigned int mx=0x3f80,out;unsigned short cw=0x077f,other;__asm__ volatile("fldcw %0; ldmxcsr %1"::"m"(cw),"m"(mx):"memory");long fd=open_path(argc>2?argv[2]:"/etc/hostname",0);if(fd<0)return 1;zenith_call(3,fd,0,0);__asm__ volatile("fnstcw %0; stmxcsr %1":"=m"(other),"=m"(out)::"memory");return other==cw&&out==mx?0:1;}
    if(same(argv[1],"--fs-limit")){int fds[16];for(int i=0;i<16;i++){fds[i]=open_path(argc>2?argv[2]:"/etc/hostname",0);if(fds[i]<0)return 1;}long extra=open_path(argc>2?argv[2]:"/etc/hostname",0);for(int i=0;i<16;i++)zenith_call(3,fds[i],0,0);return extra==-24?0:1;}
    if(argc==2 && same(argv[1],"id")){print("uid=");number(zenith_identity(102));print(" gid=");number(zenith_identity(104));print(" pid=");number(zenith_identity(39));print("\n");return 0;}
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
    print("Usage: native run sunbeam [echo [-n] ARGS... | basename PATH [SUFFIX] | dirname PATH | id | pwd | cat FILE... | ls [-a] [-l] [DIR...] | stat FILE]\n");return 2;
}
