#include "meridian.h"
static unsigned int pixels[1920*1000];
static struct meridian shell;
#define app shell.colors
static long handle=-1;
static int material_surface;
static long present(void){
    struct zui *u=meridian_ui(&shell);
    return material_surface?zenith_gui_present_material(handle,pixels,(unsigned long)u->width*u->height*4,u->materials,u->material_count):zenith_gui_present(handle,pixels,(unsigned long)u->width*u->height*4);
}
static void print(const char *s);
static int viewport(void){
    struct zenith_viewport v={0};if(zenith_gui_viewport(handle,&v)||v.version!=1||!(v.flags&1)||v.width<600||v.height<480||v.width>1920||v.height>1000)return 0;
    if(v.width==(unsigned int)app.ui.width&&v.height==(unsigned int)app.ui.height)return 1;
    long r=zenith_gui_resize_accept(handle,&v);if(r==-11)return 1;if(r)return 0;
    if(!meridian_resize(&shell,v.width,v.height,1920*1000))return 0;
    print("MERIDIAN_RESIZED\n");return 1;
}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){unsigned long n=0;while(s[n])n++;zenith_write(s,n);}
static void number(unsigned long value){char text[21];int n=0;do{text[n++]=(char)('0'+value%10);value/=10;}while(value);while(n)zenith_write(&text[--n],1);}
static const char *mode(unsigned int value){return value?"light":"dark";}
static const char *accent(unsigned int value){const char *names[]={"blue","violet","rose","amber","green","teal"};return value<6?names[value]:"unknown";}
static long read_settings(struct zenith_appearance *s,unsigned int *saved,unsigned int *effective,unsigned int *available,unsigned int *reason){
    long r=zenith_appearance_read(s);if(r)return r;
    struct zenith_material_settings m={0};r=zenith_material_settings_read(&m);
    if(r){*saved=*effective=*available=*reason=0;return 0;}
    if((m.version!=1&&m.version!=2)||m.reserved||m.revision!=s->revision)return -11;
    *saved=m.saved;*effective=m.effective;*available=m.available;*reason=m.reason;return 0;
}
static long save_settings(unsigned int m,unsigned int a,unsigned int effects,unsigned long revision){
    struct zenith_material_settings state={0};
    if(zenith_material_settings_read(&state))return effects?-19:zenith_appearance_save(m,a,revision);
    return zenith_material_settings_save(m,a,effects,revision);
}
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
    if(zenith_call(612,0xffff800000000000UL,4096,1)!=-14)return 10;
    if(zenith_call(612,(unsigned long)&after,4097,1)!=-22)return 11;
    if(zenith_call(605,20001,1,0)!=-9)return 12;
    struct zenith_material_settings materials={0};
    if(!zenith_material_settings_read(&materials)){
        if(zenith_call(608,(unsigned long)&materials,47,0)!=-22)return 20;
        if(zenith_call(609,0xffff800000000000UL,32,0)!=-14)return 21;
        struct zenith_material_settings_request request={1,0,before.revision,0,0,0,1};
        if(zenith_call(609,(unsigned long)&request,sizeof(request),0)!=-22)return 22;
        request.reserved=0;request.materials=4;
        if(zenith_call(609,(unsigned long)&request,sizeof(request),0)!=((before.flags&Z_SETTINGS_WRITABLE)?-22:-13))return 23;
    }
    struct zenith_viewport viewport_state={0};
    if(zenith_gui_resize_enable(20001)!=-9)return 24;
    if(zenith_call(613,20001,(unsigned long)&viewport_state,31)!=-22)return 25;
    if(zenith_gui_viewport(20001,&viewport_state)!=-9)return 26;
    if(materials.version==2){
        if(zenith_call(616,(unsigned long)&materials,47,0)!=-22)return 27;
        if(zenith_call(617,0xffff800000000000UL,32,0)!=-14)return 28;
        struct zenith_material_settings_request request={2,0,before.revision,0,0,8,0};
        if(zenith_call(617,(unsigned long)&request,sizeof(request),0)!=((before.flags&Z_SETTINGS_WRITABLE)?-22:-13))return 29;
    }
    if(zenith_appearance_read(&after)||before.revision!=after.revision)return 9;
    print("SETTINGS_BOUNDS_OK\n");return 0;
}
int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("settings " SUNBEAM_VERSION "\n");return 0;}
    if(argc==2&&same(argv[1],"--show"))return show();
    if(argc==2&&same(argv[1],"--system-info")){
        long n=zenith_call(612,(unsigned long)shell.information,sizeof(shell.information)-1,1);
        if(n<=0||n>=(long)sizeof(shell.information)){print("settings: system information unavailable\n");return 1;}
        shell.information[n]=0;print(shell.information);print("\n");return 0;
    }
    if(argc==3&&same(argv[1],"--materials")){
        unsigned int effects=same(argv[2],"solid")?0:same(argv[2],"transparent")?1:same(argv[2],"blur")?3:same(argv[2],"background")?5:same(argv[2],"background-blur")?7:8;
        struct zenith_appearance a={0};if(effects>7||zenith_appearance_read(&a))return 2;
        if(zenith_material_settings_save(a.mode,a.accent,effects,a.revision))return 1;
        print("MATERIAL_SETTINGS_SAVED\n");return 0;
    }
    if(argc==2&&same(argv[1],"--materials-show")){
        struct zenith_material_settings m={0};if(zenith_material_settings_read(&m))return 1;
        print("MATERIAL_SETTINGS saved=");number(m.saved);print(" effective=");number(m.effective);print(" available=");number(m.available);print(" reason=");number(m.reason);print("\n");return 0;
    }
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
    const char *page=0;
    if(argc==3&&same(argv[1],"--page"))page=argv[2];
    else if(argc!=1){print("usage: native run settings [--page ID | --system-info | --show | --set dark|light ACCENT | --materials solid|transparent|blur | --materials-show | --version | --bounds]\n");return 2;}
    struct settings_backend backend={read_settings,save_settings,dirty};
    if(!meridian_init(&shell,pixels,backend,page)){print("settings: service unavailable\n");return 1;}
    long info=zenith_call(612,(unsigned long)shell.information,sizeof(shell.information)-1,1);
    if(info>0&&info<(long)sizeof(shell.information))shell.information[info]=0;
    struct zenith_material_caps caps={0};
    struct zenith_material_settings material_state={0};
    material_surface=zenith_material_settings_read(&material_state)==0&&(material_state.version==1||material_state.version==2)&&!material_state.reserved&&
        zenith_gui_material_caps(&caps)==0&&caps.version==2&&(caps.features&3)==3;
    handle=material_surface?zenith_gui_open_material(600,480,"Meridian",8):zenith_gui_open(600,480,"Meridian",8);
    if(material_surface){app.ui.foreground=1;shell.navigation.foreground=1;}
    if(handle<0){print("settings: graphical session required; use --show or --set\n");return 1;}
    if(zenith_gui_interactive(handle)<0){zenith_gui_close(handle);print("settings: interactive client support required\n");return 1;}
    if(zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);print("settings: viewport service required\n");return 1;}
    meridian_refresh(&shell);meridian_draw(&shell);
    if(present()<0){zenith_gui_close(handle);return 1;}
    print("SETTINGS_READY\nMERIDIAN_PAGE ");print(meridian_pages[shell.page].id);print("\n");int result=0;
    struct zenith_gui_event deferred={0};int have_deferred=0;
    for(unsigned int i=0;!shell.closing;i++){
        struct zenith_gui_event e={0};long r;
        if(have_deferred){e=deferred;have_deferred=0;r=1;}else r=zenith_gui_poll(handle,&e);
        if(r>0&&e.type==Z_GUI_SCROLL){
            for(int n=0;n<15;n++){
                struct zenith_gui_event next={0};long k=zenith_gui_poll(handle,&next);
                if(k<0){r=k;break;}if(!k)break;
                if(next.type!=Z_GUI_SCROLL||((next.x<204)!=(e.x<204))){deferred=next;have_deferred=1;break;}
                e.code+=next.code;
            }
        }
        if((r>0&&e.type==Z_GUI_RESIZE)||(i&31)==0){if(!viewport()){result=1;break;}}
        if(r<0){result=1;break;}
        int previous_page=shell.page;
        int edited=app.edited,prompt=app.prompt;unsigned long rev=app.state.revision;
        if(r){meridian_event(&shell,&e);if(e.type==Z_GUI_FOCUS&&e.code)print("MERIDIAN_FOCUSED\n");}
        if(previous_page!=shell.page){print("MERIDIAN_PAGE ");print(shell.page<0?"navigation":meridian_pages[shell.page].id);print("\n");}
        if((i&31)==0)meridian_refresh(&shell);
        if(!edited&&app.edited)print("SETTINGS_UNSAVED\n");
        if(!prompt&&app.prompt)print("SETTINGS_DISCARD_PROMPT\n");
        if(app.state.revision!=rev)print("SETTINGS_REVISION_CHANGED\n");
        if(meridian_ui(&shell)->dirty&&!shell.closing){meridian_refresh(&shell);meridian_draw(&shell);if(present()<0){result=1;break;}}
    }
    zenith_gui_close(handle);print("SETTINGS_EXIT\n");return result;
}
