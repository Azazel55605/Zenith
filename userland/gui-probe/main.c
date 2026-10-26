#include "../include/zenith_graphics.h"
static unsigned int pixels[320*200];
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
int main(int argc,char **argv) {
    const char *mode=argc>1?argv[1]:"--interactive";
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
