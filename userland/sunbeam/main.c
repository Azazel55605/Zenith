#include "zenith.h"
#ifndef SUNBEAM_VERSION
#define SUNBEAM_VERSION "0.1.0"
#endif
static volatile unsigned long native_abi = 1;
static unsigned long length(const char *text) { unsigned long n=0; while(text[n])n++; return n; }
static int same(const char *a,const char *b) { while(*a && *a==*b){a++;b++;}return *a==*b; }
static void print(const char *text) { long n=zenith_write(text,length(text));if(n<0)zenith_exit(n==-32?141:1); }
static void number(unsigned long value) { char buffer[24];unsigned int n=0;do{buffer[n++]=(char)('0'+value%10);value/=10;}while(value);for(unsigned int i=0;i<n/2;i++){char c=buffer[i];buffer[i]=buffer[n-i-1];buffer[n-i-1]=c;}if(zenith_write(buffer,n)<0)zenith_exit(1); }
static long open_path(const char *path, unsigned long flags) { return zenith_call(2,(unsigned long)path,length(path),flags); }
static void error_text(const char *text) { zenith_call(1, 2, (unsigned long)text, length(text)); }
static int file_error(const char *path, long code)
{
    if (code == -32)
        return 141;
    error_text("sunbeam: ");
    error_text(path);
    error_text(": ");
    error_text(code == -13   ? "permission denied"
               : code == -2  ? "not found"
               : code == -24 ? "too many open files"
               : code == -14 ? "invalid buffer"
               : code == -28 ? "transfer/storage limit exceeded"
                             : "filesystem operation failed");
    error_text("\n");
    return 1;
}
static int cat_file(const char *path)
{
    char buffer[4096];
    long fd = same(path, "-") ? 0 : open_path(path, 0);
    if (fd < 0)
        return file_error(path, fd);
    for (;;)
    {
        long n = zenith_call(0, fd, (unsigned long)buffer, sizeof(buffer));
        if (n <= 0)
        {
            if (fd >= 3)
                zenith_call(3, fd, 0, 0);
            return n < 0 ? file_error(path, n) : 0;
        }
        long w = zenith_write(buffer, n);
        if (w < 0)
        {
            if (fd >= 3)
                zenith_call(3, fd, 0, 0);
            return file_error(path, w);
        }
    }
}
static int tee_file(const char *path, int append)
{
    char buffer[4096];
    long fd =
        zenith_call(2, (unsigned long)path, length(path),
                    Z_OPEN_WRITE | Z_OPEN_CREATE | (append ? Z_OPEN_APPEND : Z_OPEN_TRUNCATE));
    if (fd < 0)
        return file_error(path, fd);
    for (;;)
    {
        long n = zenith_call(0, 0, (unsigned long)buffer, sizeof(buffer));
        if (n < 0)
        {
            zenith_call(3, fd, 0, 0);
            return file_error("stdin", n);
        }
        if (!n)
            break;
        long off = 0;
        while (off < n)
        {
            long w = zenith_call(1, fd, (unsigned long)(buffer + off), n - off);
            if (w <= 0)
            {
                zenith_call(3, fd, 0, 0);
                return file_error(path, w ? w : -5);
            }
            off += w;
        }
        long w = zenith_write(buffer, n);
        if (w < 0)
        {
            zenith_call(3, fd, 0, 0);
            return file_error("stdout", w);
        }
    }
    long flushed = zenith_call(74, fd, 0, 0), closed = zenith_call(3, fd, 0, 0);
    return flushed < 0 || closed < 0 ? file_error(path, flushed < 0 ? flushed : closed) : 0;
}
static void permissions(unsigned int mode) { char text[11];text[0]=(mode&0170000)==0040000?'d':(mode&0170000)==0120000?'l':(mode&0170000)==0020000?'c':(mode&0170000)==0060000?'b':(mode&0170000)==0010000?'p':(mode&0170000)==0140000?'s':'-';const char *bits="rwxrwxrwx";for(int i=0;i<9;i++)text[i+1]=(mode&(1U<<(8-i)))?bits[i]:'-';if(mode&04000)text[3]=text[3]=='x'?'s':'S';if(mode&02000)text[6]=text[6]=='x'?'s':'S';if(mode&01000)text[9]=text[9]=='x'?'t':'T';text[10]=0;print(text); }
static int list_path(const char *path, int all, int details) {
    struct zenith_dirent entry;long fd=open_path(path,1);if(fd<0)return file_error(path,fd);
    for(;;){long n=zenith_call(217,fd,(unsigned long)&entry,sizeof(entry));if(n<=0){zenith_call(3,fd,0,0);return n<0?file_error(path,n):0;}if(!all&&entry.name[0]=='.')continue;
        if(details){permissions(entry.stat.mode);print(" uid=");number(entry.stat.uid);print(" gid=");number(entry.stat.gid);print(" size=");number(entry.stat.size);print(" ");}print(entry.name);print("\n");}
}
/* Byte counts and ASCII whitespace words, no locale/libc dependency. */
static int whitespace(unsigned char c) { return c == ' ' || (c >= 9 && c <= 13); }
static int count_file(const char *path, int flags)
{
    unsigned char buffer[4096];
    unsigned long lines = 0, words = 0, bytes = 0;
    int word = 0;
    long fd = same(path, "-") ? 0 : open_path(path, 0);
    if (fd < 0)
        return file_error(path, fd);
    for (;;)
    {
        long n = zenith_call(0, fd, (unsigned long)buffer, sizeof(buffer));
        if (n < 0)
        {
            if (fd >= 3)
                zenith_call(3, fd, 0, 0);
            return file_error(path, n);
        }
        if (!n)
            break;
        bytes += n;
        for (long i = 0; i < n; i++)
        {
            if (buffer[i] == '\n')
                lines++;
            int next = !whitespace(buffer[i]);
            if (next && !word)
                words++;
            word = next;
        }
    }
    if (fd >= 3)
        zenith_call(3, fd, 0, 0);
    int printed = 0;
    if (flags & 1)
    {
        number(lines);
        printed = 1;
    }
    if (flags & 2)
    {
        if (printed)
            print(" ");
        number(words);
        printed = 1;
    }
    if (flags & 4)
    {
        if (printed)
            print(" ");
        number(bytes);
    }
    if (!same(path, "-"))
    {
        print(" ");
        print(path);
    }
    print("\n");
    return 0;
}
static int line_tool(const char *path, unsigned long wanted, int tail)
{
    static unsigned char ring[65536];
    unsigned char buffer[4096];
    unsigned long used = 0, start = 0, complete = 0;
    long fd = same(path, "-") ? 0 : open_path(path, 0);
    if (fd < 0)
        return file_error(path, fd);
    if (!wanted)
    {
        if (fd >= 3)
            zenith_call(3, fd, 0, 0);
        return 0;
    }
    for (;;)
    {
        long n = zenith_call(0, fd, (unsigned long)buffer, sizeof(buffer));
        if (n < 0)
        {
            if (fd >= 3)
                zenith_call(3, fd, 0, 0);
            return file_error(path, n);
        }
        if (!n)
            break;
        if (!tail)
        {
            long take = n;
            for (long i = 0; i < n; i++)
                if (buffer[i] == '\n' && --wanted == 0)
                {
                    take = i + 1;
                    break;
                }
            long w = zenith_write(buffer, take);
            if (w < 0)
            {
                if (fd >= 3)
                    zenith_call(3, fd, 0, 0);
                return file_error("stdout", w);
            }
            if (!wanted)
                break;
        }
        else
        {
            for (long i = 0; i < n; i++)
            {
                if (used == sizeof(ring))
                {
                    if (fd >= 3)
                        zenith_call(3, fd, 0, 0);
                    return file_error("tail: retained lines exceed 64 KiB", -28);
                }
                ring[(start + used) % sizeof(ring)] = buffer[i];
                used++;
                /* Keep wanted complete lines plus the current unterminated line. */
                if (buffer[i] == '\n')
                {
                    complete++;
                    if (complete > wanted)
                    {
                        do
                        {
                            unsigned char c = ring[start];
                            start = (start + 1) % sizeof(ring);
                            used--;
                            if (c == '\n')
                            {
                                complete--;
                                break;
                            }
                        } while (used);
                    }
                }
            }
        }
    }
    if (fd >= 3)
        zenith_call(3, fd, 0, 0);
    if (tail)
    {
        unsigned long lines = used && ring[(start + used - 1) % sizeof(ring)] != '\n' ? 1 : 0;
        for (unsigned long i = 0; i < used; i++)
            if (ring[(start + i) % sizeof(ring)] == '\n')
                lines++;
        while (lines > wanted)
        {
            unsigned char c = ring[start];
            start = (start + 1) % sizeof(ring);
            used--;
            if (c == '\n')
                lines--;
        }
        while (used)
        {
            unsigned long n = sizeof(ring) - start;
            if (n > used)
                n = used;
            if (n > 4096)
                n = 4096;
            long w = zenith_write(ring + start, n);
            if (w < 0)
                return file_error("stdout", w);
            start = (start + n) % sizeof(ring);
            used -= n;
        }
    }
    return 0;
}
#include "files.c"

int main(int argc, char **argv, char **envp) {
    if(argc==2 && same(argv[1],"ps"))
    {
        char bytes[4096];unsigned long offset=0;
        for(;;){long n=zenith_call(403,(unsigned long)bytes,sizeof(bytes),offset);if(n<0)return file_error("ps",n);if(!n)return 0;
            if(zenith_write(bytes,n)!=n){return 1;}offset+=(unsigned long)n;}
    }
    if(argc==3 && same(argv[1],"kill"))
    {
        unsigned long pid=0;if(!argv[2][0])return file_error("kill",-22);
        for(int i=0;argv[2][i];i++){if(argv[2][i]<'0'||argv[2][i]>'9'||pid>214748364UL)return file_error("kill",-22);pid=pid*10+(unsigned long)(argv[2][i]-'0');}
        if(!pid||pid>2147483647UL)return file_error("kill",-22);
        long result=zenith_call(404,pid,0,0);return result<0?file_error("kill",result):0;
    }
    if(argc==2 && same(argv[1],"env")){for(char **p=envp; p && *p; p++){print(*p);print("\n");}return 0;}
    if(native_abi!=1)return 95;
    if(argc>1){int result=native_files_command(argc,argv);if(result>=0)return result;}
    if(argc==2 && same(argv[1],"--path-bounds"))
    {
        const char *path="/home/user/cli/MUST_NOT_EXIST";
        struct zenith_path_request request={1,1,(unsigned long)path,length(path),0,0,0755,0};
        if(zenith_call(405,(unsigned long)&request,sizeof(request),0)!=-22)return 1;
        request.flags=0;
        if(zenith_call(405,(unsigned long)&request,sizeof(request)-1,0)!=-22)return 1;
        request.path=0xffff800000000000UL;
        if(zenith_call(405,(unsigned long)&request,sizeof(request),0)!=-14)return 1;
        request.path=(unsigned long)path;request.length=1025;
        if(zenith_call(405,(unsigned long)&request,sizeof(request),0)!=-22)return 1;
        request.length=length(path);request.target=1;
        return zenith_call(405,(unsigned long)&request,sizeof(request),0)==-22?0:1;
    }
    if(argc==3 && same(argv[1],"--seek-probe"))
    {
        char before[4],after[4];long fd=open_path(argv[2],0);if(fd<0)return 1;
        int ok=zenith_call(0,fd,(unsigned long)before,4)==4&&zenith_call(406,fd,0,0)==0
            &&zenith_call(0,fd,(unsigned long)after,4)==4&&zenith_call(406,fd,0,3)==-22
            &&zenith_call(406,9999,0,0)==-9&&zenith_call(406,fd,~0UL,0)==-22;
        zenith_call(3,fd,0,0);for(int i=0;i<4;i++)if(before[i]!=after[i])ok=0;return ok?0:1;
    }
    if(argc==2 && same(argv[1],"--tty-binary"))
    {
        if(zenith_call(402,0,0,0)!=0)return 1;
        print("TTY_BINARY_READY\n");unsigned char bytes[4]={0};unsigned long got=0;
        while(got<sizeof(bytes)){long n=zenith_call(0,0,(unsigned long)(bytes+got),sizeof(bytes)-got);if(n<=0)return 1;got+=(unsigned long)n;}
        return bytes[0]==0xc3&&bytes[1]==0xbc&&bytes[2]==0xff&&bytes[3]==0?0:1;
    }
    if(argc==2 && same(argv[1],"--tty-raw"))
    {
        if(zenith_call(401,0,0,0)!=7||zenith_call(402,0,0,0)!=0)return 1;
        print("TTY_RAW_READY\n");unsigned char bytes[5];unsigned long got=0;
        while(got<sizeof(bytes)){long n=zenith_call(0,0,(unsigned long)(bytes+got),sizeof(bytes)-got);if(n<=0)return 1;got+=(unsigned long)n;}
        return bytes[0]==27&&bytes[1]=='['&&bytes[2]=='A'&&bytes[3]==3&&bytes[4]==4?0:1;
    }
    if(argc==2 && same(argv[1],"--tty-cancel"))
    { if(zenith_call(402,0,4,0)!=0)return 1;print("TTY_CANCEL_READY\n");char c;return zenith_call(0,0,(unsigned long)&c,1)<0?130:1; }
    if(argc==2 && same(argv[1],"--tty-fault"))
    { if(zenith_call(402,0,0,0)!=0)return 1;*(volatile unsigned long *)0xffff800000000000UL=1;return 1; }
    if(argc==2 && same(argv[1],"--tty-bounds"))
    { return zenith_call(401,99,0,0)==-9&&zenith_call(401,1,0,0)==-25&&zenith_call(402,0,8,0)==-22&&zenith_call(401,0,0,0)==7?0:1; }
    if(argc==2 && same(argv[1],"--process-bounds"))
    { char b[64];return zenith_call(403,0xffff800000000000UL,32,0)==-14&&zenith_call(403,(unsigned long)&main,32,0)==-14
      &&zenith_call(403,(unsigned long)b,4097,0)==-22&&zenith_call(404,0,0,0)==-22&&zenith_call(404,1,9,0)==-22?0:1; }
    if(argc==2 && same(argv[1],"--version")){print(argv[0]);print(" " SUNBEAM_VERSION "\n");return 0;}
    if(argc==1){print("Sunbeam native tools " SUNBEAM_VERSION " (Zenith ABI 1)\n");return 0;}
    if(same(argv[1],"--stream-probe")){if(zenith_isatty(0)!=1||zenith_isatty(1)!=1||zenith_isatty(2)!=1)return 1;print("STREAM_READY\n");char c;long n=zenith_call(0,0,(unsigned long)&c,1);error_text("STREAM_STDERR\n");return n==1?0:1;}
    if(same(argv[1],"--stream-utf8")){const unsigned char bytes[]={0xc3,0xbc,10};for(unsigned int i=0;i<sizeof(bytes);i++)if(zenith_write(bytes+i,1)!=1)return 1;return 0;}
    if(same(argv[1],"--stream-fault-in")){char c;if(zenith_call(0,0,(unsigned long)&c,1)!=1)return 1;*(volatile unsigned long *)0xffff800000000000UL=1;return 1;}
    if(same(argv[1],"--stream-fp")){char bytes[4096];unsigned int mx=0x3f80,out;unsigned short cw=0x077f,other;
        __asm__ volatile("fldcw %0; ldmxcsr %1"::"m"(cw),"m"(mx):"memory");
        for(;;){long n=zenith_call(0,0,(unsigned long)bytes,sizeof(bytes));if(n<0)return 1;if(!n)return 0;if(zenith_write(bytes,n)!=n)return 1;
            __asm__ volatile("fnstcw %0; stmxcsr %1":"=m"(other),"=m"(out)::"memory");if(other!=cw||out!=mx)return 1;}}
    if(same(argv[1],"--stream-bytes")){unsigned char b[4096];for(unsigned int i=0;i<sizeof(b);i++)b[i]=(unsigned char)i;for(int j=0;j<40;j++){long w=zenith_write(b,sizeof(b));if(w<0)return file_error("stdout",w);}return 0;}
    if(same(argv[1],"--stream-fds")){char c='X';return zenith_call(400,1,0,0)==0&&zenith_call(400,2,0,0)==1&&zenith_call(3,2,0,0)==0&&zenith_call(1,2,(unsigned long)&c,1)==-9&&zenith_call(400,2,0,0)==-9?0:1;}
    if(same(argv[1],"wc")){int i=2,flags=0;if(i<argc&&argv[i][0]=='-'&&argv[i][1]){for(int j=1;argv[i][j];j++){if(argv[i][j]=='l')flags|=1;else if(argv[i][j]=='w')flags|=2;else if(argv[i][j]=='c')flags|=4;else return file_error("wc: use -lwc",-22);}i++;}if(!flags)flags=7;
        if(i+1<argc){return file_error("wc: expected one file or stdin",-22);}return count_file(i<argc?argv[i]:"-",flags);}
    if(same(argv[1],"head")||same(argv[1],"tail")){int i=2;unsigned long n=10;if(i<argc&&same(argv[i],"-n")){if(++i==argc||!argv[i][0])return file_error("missing line count",-22);n=0;for(int j=0;argv[i][j];j++){if(argv[i][j]<'0'||argv[i][j]>'9'||n>1024)return file_error("line count must be 0..1024",-22);n=n*10+(unsigned long)(argv[i][j]-'0');}i++;if(n>1024)return file_error("line count must be 0..1024",-22);}
        if(i+1<argc){return file_error("expected one file or stdin",-22);}return line_tool(i<argc?argv[i]:"-",n,same(argv[1],"tail"));}
    if(same(argv[1],"pwd") && argc==2){char cwd[1025];long n=zenith_call(79,(unsigned long)cwd,sizeof(cwd),0);if(n<0)return file_error("cwd",n);print(cwd);print("\n");return 0;}
    if(same(argv[1],"cat")){if(argc==2)return cat_file("-");int result=0;for(int i=2;i<argc;i++)result|=cat_file(argv[i]);return result;}
    if(same(argv[1],"tee") && argc==3)return tee_file(argv[2],0);
    if(same(argv[1],"tee") && argc==4 && same(argv[2],"-a"))return tee_file(argv[3],1);
    if(same(argv[1],"--io-input-buffers")){char c=0;long a=zenith_call(0,0,(unsigned long)&main,1),b=zenith_call(0,0,0xffff800000000000UL,1),d=zenith_call(0,0,0x7ffeffffUL,4);long n=zenith_call(0,0,(unsigned long)&c,1);return a==-14&&b==-14&&d==-14&&n==1&&c=='s'?0:1;}
    if(same(argv[1],"--io-write-buffers") && argc==3){long fd=open_path(argv[2],Z_OPEN_WRITE);if(fd<0)return 1;static const char c='Z';
        long a=zenith_call(1,fd,0xffff800000000000UL,1),b=zenith_call(1,fd,0x7ffeffffUL,4),d=zenith_call(1,fd,~0UL-1,4),e=zenith_call(1,fd,(unsigned long)&c,4097);
        long n=zenith_call(1,fd,(unsigned long)&c,1),f=zenith_call(74,fd,0,0),g=zenith_call(3,fd,0,0),stale=zenith_call(1,fd,(unsigned long)&c,1),stale_flush=zenith_call(74,fd,0,0);
        return a==-14&&b==-14&&d==-14&&e==-22&&n==1&&f==0&&g==0&&stale==-9&&stale_flush==-9?0:1;}
    if(same(argv[1],"--io-budget") && argc==3){char data[4096];for(unsigned int i=0;i<sizeof(data);i++)data[i]='A';long fd=open_path(argv[2],Z_OPEN_WRITE|Z_OPEN_CREATE|Z_OPEN_TRUNCATE);if(fd<0)return 1;
        for(int i=0;i<256;i++){if(zenith_call(1,fd,(unsigned long)data,sizeof(data))!=4096){zenith_call(3,fd,0,0);return 1;}}
        long extra=zenith_call(1,fd,(unsigned long)data,1);long closed=zenith_call(3,fd,0,0);return extra==-28&&closed==0?0:1;}
    if(same(argv[1],"--io-directions") && argc==3){char data='X';long rd=open_path(argv[2],0),wr=open_path(argv[2],Z_OPEN_WRITE);if(rd<0||wr<0)return 1;
        long a=zenith_call(1,rd,(unsigned long)&data,1),b=zenith_call(0,wr,(unsigned long)&data,1),c=zenith_call(74,rd,0,0);zenith_call(3,rd,0,0);zenith_call(3,wr,0,0);return a==-9&&b==-9&&c==-9?0:1;}
    if((same(argv[1],"--io-fault")||same(argv[1],"--io-loop")) && argc==3){long fd=open_path(argv[2],Z_OPEN_WRITE|Z_OPEN_CREATE|Z_OPEN_TRUNCATE);if(fd<0)return 1;char c='F';if(zenith_call(1,fd,(unsigned long)&c,1)!=1)return 1;
        if(same(argv[1],"--io-loop")){for(;;)zenith_call(74,fd,0,0);}*(volatile unsigned long *)0xffff800000000000UL=1;return 1;}
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
    print("Usage: native run sunbeam [echo [-n] ARGS... | basename PATH [SUFFIX] | dirname PATH | id | pwd | cat [FILE...] | tee [-a] FILE | wc [-lwc] [FILE] | head|tail [-n 0..1024] [FILE] | ls [-a] [-l] [DIR...] | stat FILE]\n");return 2;
}
