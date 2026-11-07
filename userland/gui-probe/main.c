#include "../include/zenith_graphics.h"
static unsigned int pixels[320*200];
static long zenith_probe_handle;
static unsigned long len(const char *s) { unsigned long n=0;while(s[n])n++;return n; }
static int same(const char *a,const char *b) { while(*a && *a==*b){a++;b++;}return *a==*b; }
static void print(const char *s) { zenith_write(s,len(s)); }
static long open_surface(void) {
    const char *title="Native graphical client";
    struct zenith_surface_request r={1,0,320,200,(unsigned long)title,0,{0,0}};r.length=len(title);
    return zenith_call(600,(unsigned long)&r,sizeof(r),0);
}
static void draw(unsigned int accent) {
    for(int y=0;y<200;y++)for(int x=0;x<320;x++) {
        unsigned int c=0x172234;
        if(x>=20 && x<300 && y>=20 && y<70)c=accent;
        if(x>=20 && x<200 && y>=95 && y<110)c=0xe2e8f0;
        if(x>=20 && x<270 && y>=125 && y<135)c=0x64748b;
        if(x>=20 && x<150 && y>=155 && y<180)c=accent;
        pixels[y*320+x]=c;
    }
}
/* Drives a terminal session as a person would: types into it and reads what comes back. */
static void type_keys(long t,const char *s){for(;*s;s++){struct zenith_terminal_key k={*s,0,*s=='\n'?2:0};zenith_terminal_key(t,&k);}}
static int contains(const char *h,long n,const char *w){unsigned long m=len(w);for(long i=0;i+(long)m<=n;i++){unsigned long j=0;while(j<m&&h[i+(long)j]==w[j])j++;if(j==m)return 1;}return 0;}
static char term_out[16384];static long term_len;
static unsigned int term_state[1024];
static long term_wait(long t,const char *word,int want_flags,int want_set){
    for(int i=0;i<1500;i++){
        long n=zenith_terminal_read(t,term_out+term_len,sizeof(term_out)-(unsigned long)term_len-1);if(n<0)return n;term_len+=n;
        long s=zenith_terminal_state(t,term_state);if(s<24)return -1;
        int set=(term_state[1]&(unsigned)want_flags)!=0;
        if((!word||contains(term_out,term_len,word))&&(!want_flags||set==want_set))return 0;
        if(term_len>=(long)sizeof(term_out)-2)term_len=0;
        struct zenith_gui_event e;zenith_gui_poll(zenith_probe_handle,&e);
    }
    return -2;
}
static int terminal_probe(int hold){
    long h=open_surface();if(h<0)return 10;
    zenith_probe_handle=h;
    if(zenith_gui_interactive(h)<0)return 11;
    draw(0x2563eb);if(zenith_gui_present(h,pixels,sizeof(pixels))<0)return 12;
    if(zenith_terminal_open(5)!=-22)return 13;
    long t=zenith_terminal_open(80);if(t<0)return 14;
    if(zenith_terminal_key(t+1000,(struct zenith_terminal_key*)pixels)!=-9)return 15;
    type_keys(t,"echo TERMINAL_OUT\n");
    if(term_wait(t,"TERMINAL_OUT\n",Z_TERMINAL_BUSY,0))return 16;
    /* Each command is marked: where its prompt starts, then its status and time. The end mark is written as the
     * command finishes, so it can arrive just after the state says the shell is idle: wait for it. */
    if(term_wait(t,"\033]133;D;0;",0,0))return 35;
    if(!contains(term_out,term_len,"\033]133;A\007"))return 38;
    if(zenith_gui_set_title(h,"probe \xe2\x80\x94 title",15)!=0)return 36;
    if(zenith_gui_set_title(h,"bad\ntitle",9)!=-22||zenith_gui_set_title(h,"\xe2\x80\xae",3)!=-22||zenith_gui_set_title(h,"\xc3",1)!=-22)return 37;
    /* A command's own output and its exit both come through; the shell really ran it. */
    term_len=0;type_keys(t,"echo TERMINAL_LOG | logger\n");
    /* A fast command may finish before the next state query. Observe its
     * durable completion mark, never require catching a transient Busy edge. */
    if(term_wait(t,"\033]133;D;0;",Z_TERMINAL_BUSY,0))return 17;
    /* A hidden prompt raises the flag and its text never reaches the client. */
    term_len=0;type_keys(t,"passwd\n");
    if(term_wait(t,0,Z_TERMINAL_SECRET,1))return 18;
    type_keys(t,"HIDDENWORD");
    for(int i=0;i<40;i++){struct zenith_gui_event e;zenith_gui_poll(h,&e);}
    if(term_wait(t,0,Z_TERMINAL_SECRET,1))return 19;
    long sn=zenith_terminal_state(t,term_state);if(sn<24||term_state[4]!=0||contains((char*)term_state,sn,"HIDDENWORD")||contains(term_out,term_len,"HIDDENWORD"))return 20;
    struct zenith_terminal_key c={'c',2,0};zenith_terminal_key(t,&c);
    if(term_wait(t,0,Z_TERMINAL_SECRET,0))return 21;
    if(term_wait(t,0,Z_TERMINAL_BUSY,0))return 22;
    /* Line editing lives in the session: the edit line comes back as state, nothing runs yet. */
    term_len=0;type_keys(t,"abc");
    int ok=0;for(int i=0;i<200&&!ok;i++){long s=zenith_terminal_state(t,term_state);if(s>=24&&term_state[4]==3&&term_state[2]==3)ok=1;struct zenith_gui_event e;zenith_gui_poll(h,&e);}
    if(!ok)return 23;
    struct zenith_terminal_key u={'u',2,0};zenith_terminal_key(t,&u);
    /* Pasting never runs anything: line breaks wait, visibly, for Enter, and control characters are gone. */
    static const char pasted[]="echo PASTE_ONE | logger\n\033echo PASTE_TWO | logger\n";
    if(zenith_terminal_copy(t,pasted,sizeof(pasted)-1))return 28;
    for(int i=0;i<60;i++){struct zenith_gui_event e;zenith_gui_poll(h,&e);}
    term_len=0;if(zenith_terminal_paste(t))return 29;
    ok=0;for(int i=0;i<300&&!ok;i++){long s=zenith_terminal_state(t,term_state);if(s>=24&&term_state[4]>40)ok=1;struct zenith_gui_event e;zenith_gui_poll(h,&e);}
    if(!ok)return 30;
    for(int i=0;i<200;i++){struct zenith_gui_event e;zenith_gui_poll(h,&e);long n=zenith_terminal_read(t,term_out+term_len,sizeof(term_out)-(unsigned long)term_len-1);if(n>0)term_len+=n;}
    long ps=zenith_terminal_state(t,term_state);
    if(ps<24||(term_state[1]&Z_TERMINAL_BUSY)||term_len)return 31;                  /* nothing ran */
    {const char *input=(const char *)term_state+24+term_state[3];
     if(!contains(input,term_state[4],"\xc2\xb6")||contains(input,term_state[4],"\033"))return 32;}
    type_keys(t,"\n");
    if(term_wait(t,"PASTE_TWO",0,0))return 33;
    if(term_wait(t,0,Z_TERMINAL_BUSY,0))return 34;
    if(zenith_terminal_resize(t,100))return 24;
    if(zenith_terminal_resize(t,5)!=-22)return 25;
    print("TERMINAL_PROBE_OK\n");
    if(hold){for(int i=0;i<500;i++){struct zenith_gui_event e={0};long r=zenith_gui_poll(h,&e);if(r>0&&e.type==Z_GUI_CLOSE)break;}}
    if(zenith_terminal_close(t))return 26;
    if(zenith_terminal_close(t)!=-9)return 27;
    zenith_gui_close(h);return 0;
}
int main(int argc,char **argv) {
    const char *mode=argc>1?argv[1]:"--interactive";
    if(same(mode,"--terminal")){
        int failed=terminal_probe(0);
        /* The caller pipes this to a log, where only the last command's status survives: say which check failed. */
        if(failed){char text[40]="TERMINAL_PROBE_FAILED 00\n";text[22]=(char)('0'+failed/10%10);text[23]=(char)('0'+failed%10);print(text);}
        return failed;
    }
    if(same(mode,"--terminal-hold"))return terminal_probe(1);
    if(same(mode,"--version")){print("gui-probe " SUNBEAM_VERSION "\n");return 0;}
    if(same(mode,"--bounds")) {
        struct zenith_surface_request r={1,0,320,200,(unsigned long)"probe",5,{0,0}};
        if(zenith_call(600,(unsigned long)&r,47,0)!=-22)return 1;
        r.flags=1;if(zenith_call(600,(unsigned long)&r,48,0)!=-22)return 2;
        r.flags=0;r.width=0xffffffffU;if(zenith_call(600,(unsigned long)&r,48,0)!=-22)return 3;
        r.width=320;r.title=0xffff800000000000UL;if(zenith_call(600,(unsigned long)&r,48,0)!=-14)return 4;
        if(zenith_call(602,20001,(unsigned long)"readonly",24)!=-14)return 5;
        if(zenith_call(603,20001,0,0)!=-9)return 6;
        print("GUI_BOUNDS_OK\n");return 0;
    }
    long h=open_surface();
    if(h<0){print("GUI_UNAVAILABLE\n");return 1;}
    if(same(mode,"--buffer-probe")) {
        if(zenith_gui_buffer(0)!=-22||zenith_gui_buffer(128UL*1024*1024+1)!=-22)return 32;
        long small=zenith_gui_buffer(sizeof(pixels));if(small<0)return 33;
        unsigned int *p=(unsigned int *)small;p[0]=0xff39bca3;
        const unsigned long area=3840UL*2160;
        long large=zenith_gui_buffer(area*4);if(large!=small)return 34;
        p=(unsigned int *)large;
        if(p[0]!=0xff39bca3||p[area-1]!=0)return 35;
        p[area-1]=0xff6b8cff;
        if(zenith_gui_present(h,p+area-320*200,sizeof(pixels)))return 36;
        if(zenith_gui_resize_refuse(h+1)!=-9)return 37;
        zenith_gui_close(h);print("GUI_BUFFER_PROBE_OK\n");return 0;
    }
    draw(0x6b8cff);
    if(zenith_gui_present(h,pixels,sizeof(pixels))!=0)return 2;
    print("GUI_PRESENTED\n");
    if(same(mode,"--fault")){__asm__ volatile("ud2");return 3;}
    if(same(mode,"--exit"))return 0; /* parent must reclaim the unclosed surface */
    if(same(mode,"--probe")) {
        if(zenith_call(601,h,(unsigned long)pixels,sizeof(pixels)-4)!=-22)return 4;
        if(zenith_call(601,h,0xffff800000000000UL,sizeof(pixels))!=-14)return 5;
        if(zenith_call(602,h,(unsigned long)"readonly",24)!=-14)return 6;
        if(open_surface()!=-24)return 7;
        if(zenith_gui_close(h)!=0)return 8;
        if(zenith_gui_present(h,pixels,sizeof(pixels))!=-9)return 9;
        long next=open_surface();if(next<0 || next==h)return 10;
        zenith_call(603,next,0,0);print("GUI_PROBE_OK\n");return 0;
    }
    print("GUI_READY (click, type, Escape or close to exit)\n");
    for(unsigned int i=0;i<30000;i++) {
        struct zenith_gui_event e={0};long result=zenith_gui_poll(h,&e);
        if(result<0)return 11;
        if(!result)continue;
        if(e.type==1){print("GUI_CLOSE\n");break;}
        if(e.type==2)print(e.code?"GUI_FOCUS_IN\n":"GUI_FOCUS_OUT\n");
        if(e.type==3){print("GUI_KEY\n");if(e.extra==1 || e.code=='q')break;}
        if(e.type==4){print("GUI_POINTER\n");draw(0x39bca3);if(zenith_gui_present(h,pixels,sizeof(pixels))<0)break;}
    }
    zenith_gui_close(h);print("GUI_EXIT\n");return 0;
}
