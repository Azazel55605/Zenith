#include "app.h"
static unsigned int pixels[600*480];
static struct settings_app app;
static long handle=-1;
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){unsigned long n=0;while(s[n])n++;zenith_write(s,n);}
static void number(unsigned long value){char text[21];int n=0;do{text[n++]=(char)('0'+value%10);value/=10;}while(value);while(n)zenith_write(&text[--n],1);}
static const char *mode(unsigned int value){return value?"light":"dark";}
static const char *accent(unsigned int value){const char *names[]={"blue","violet","rose","amber","green","teal"};return value<6?names[value]:"unknown";}
static long read_settings(struct zenith_appearance *s){return zenith_appearance_read(s);}
static long save_settings(unsigned int m,unsigned int a,unsigned long revision){return zenith_appearance_save(m,a,revision);}
static long dirty(int value){return zenith_gui_dirty(handle,value);}
static int show(void){
    struct zenith_appearance s={0};if(zenith_appearance_read(&s)||s.version!=1){print("settings: service unavailable\n");return 1;}
    print("SETTINGS saved=");print(mode(s.mode));print("/");print(accent(s.accent));
    print(" active=");print(mode(s.active_mode));print("/");print(accent(s.active_accent));
    print(" persistent=");number(!!(s.flags&1));print(" writable=");number(!!(s.flags&2));
    print(" pending=");number(!!(s.flags&4));print(" revision=");number(s.revision);print("\n");return 0;
}
static int bounds(void){
    struct zenith_appearance before={0},after={0};
    if(zenith_appearance_read(&before))return 1;
    if(zenith_call(610,(unsigned long)&after,sizeof(after)-1,0)!=-22)return 2;
    if(zenith_call(610,(unsigned long)"readonly",sizeof(after),0)!=-14)return 3;
    if(zenith_call(611,0xffff800000000000UL,32,0)!=-14)return 4;
    struct zenith_appearance_request r={2,0,before.revision,0,0,0};
    if(zenith_call(611,(unsigned long)&r,sizeof(r),0)!=-22)return 5;
    r.version=1;r.reserved=1;
    if(zenith_call(611,(unsigned long)&r,sizeof(r),0)!=-22)return 6;
    r.reserved=0;r.mode=2;
    long result=zenith_call(611,(unsigned long)&r,sizeof(r),0);
    if(result!=((before.flags&Z_SETTINGS_WRITABLE)?-22:-13))return 7;
    if(zenith_gui_dirty(20001,1)!=-9)return 8;
    if(zenith_appearance_read(&after)||before.revision!=after.revision)return 9;
    print("SETTINGS_BOUNDS_OK\n");return 0;
}
int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("settings " SUNBEAM_VERSION "\n");return 0;}
    if(argc==2&&same(argv[1],"--show"))return show();
    if(argc==2&&same(argv[1],"--bounds"))return bounds();
    if(argc==4&&same(argv[1],"--set")){
        unsigned int m=2,a=6;if(same(argv[2],"dark"))m=0;if(same(argv[2],"light"))m=1;
        for(unsigned int i=0;i<6;i++)if(same(argv[3],accent(i)))a=i;
        if(m>1||a>5){print("settings: expected dark|light and a preset accent\n");return 2;}
        struct zenith_appearance s={0};if(zenith_appearance_read(&s)){print("settings: service unavailable\n");return 1;}
        long result=zenith_appearance_save(m,a,s.revision);
        if(result){print(result==-13?"settings: administrator permission required\n":result==-11?"settings: changed elsewhere; read settings again\n":"settings: save failed; preferences not applied\n");return 1;}
        print("SETTINGS_SAVED\n");return show();
    }
    if(argc!=1){print("usage: native run settings [--show | --set dark|light ACCENT | --version | --bounds]\n");return 2;}
    struct settings_backend backend={read_settings,save_settings,dirty};
    if(!settings_init(&app,pixels,backend)){print("settings: service unavailable\n");return 1;}
    handle=zenith_gui_open(600,480,"Settings",8);
    if(handle<0){print("settings: graphical session required; use --show or --set\n");return 1;}
    settings_draw(&app);
    if(zenith_gui_present(handle,pixels,sizeof(pixels))<0){zenith_gui_close(handle);return 1;}
    print("SETTINGS_READY\n");int result=0;
    for(int i=0;i<30000&&!app.closing;i++){
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);
        if(r<0){result=1;break;}
        int edited=app.edited,prompt=app.prompt;unsigned long rev=app.state.revision;
        if(r)settings_event(&app,&e);
        if((i&31)==0)settings_refresh(&app);
        if(!edited&&app.edited)print("SETTINGS_UNSAVED\n");
        if(!prompt&&app.prompt)print("SETTINGS_DISCARD_PROMPT\n");
        if(app.state.revision!=rev)print("SETTINGS_REVISION_CHANGED\n");
        if(app.ui.dirty&&!app.closing){settings_draw(&app);if(zenith_gui_present(handle,pixels,sizeof(pixels))<0){result=1;break;}}
    }
    zenith_gui_close(handle);print("SETTINGS_EXIT\n");return result;
}
