#include "meridian.h"
static unsigned int pixels[1920*1000];
static struct meridian shell;
static long handle=-1;
static int selected_scope=-1;
static unsigned int scope_info;
static int material_surface;
static long present(void){
    struct zui *u=meridian_ui(&shell);
    return material_surface?zenith_gui_present_material(handle,pixels,(unsigned long)u->width*u->height*4,u->materials,u->material_count):zenith_gui_present(handle,pixels,(unsigned long)u->width*u->height*4);
}
static void print(const char *s);
static int viewport(void){
    struct zenith_viewport v={0};if(zenith_gui_viewport(handle,&v)||v.version!=1||!(v.flags&1)||v.width<600||v.height<480||v.width>1920||v.height>1000)return 0;
    if(v.width==(unsigned int)shell.ui.width&&v.height==(unsigned int)shell.ui.height)return 1;
    long r=zenith_gui_resize_accept(handle,&v);if(r==-11)return 1;if(r)return 0;
    if(!meridian_resize(&shell,v.width,v.height,1920*1000))return 0;
    print("MERIDIAN_RESIZED\n");return 1;
}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){unsigned long n=0;while(s[n])n++;zenith_write(s,n);}
static void number(unsigned long value){char text[21];int n=0;do{text[n++]=(char)('0'+value%10);value/=10;}while(value);while(n)zenith_write(&text[--n],1);}
static const char *mode(unsigned int value){return value?"light":"dark";}
static const char *accent(unsigned int value){const char *names[]={"blue","violet","rose","amber","green","teal"};return value<6?names[value]:"unknown";}
static long read_settings(struct zenith_appearance *s,unsigned int *saved,unsigned int *effective,unsigned int *available,unsigned int *reason,unsigned int *level){
    *level=40;
    long r;
    if(selected_scope<0){r=zenith_appearance_read(s);if(r)return r;selected_scope=(s->flags&2)?0:1;}
    struct zenith_scoped_appearance scoped={0};r=zenith_scoped_appearance_read((unsigned int)selected_scope,&scoped);
    if(r!=-38){
        if(r)return r;
        if(scoped.version!=1||scoped.scope!=(unsigned int)selected_scope||scoped.inherited>1||scoped.materials.version!=3)return -22;
        *s=scoped.appearance;*saved=scoped.materials.saved;*effective=scoped.materials.effective;*available=scoped.materials.available;*reason=scoped.materials.reason;*level=scoped.materials.translucency;
        scope_info=4U|(unsigned int)selected_scope|(scoped.inherited<<1);return 0;
    }
    scope_info=0;r=zenith_appearance_read(s);if(r)return r;
    struct zenith_material_control control={0};
    r=zenith_material_control_read(&control);
    if(!r){if(control.version!=3||control.translucency>100||control.revision!=s->revision)return -11;*saved=control.saved;*effective=control.effective;*available=control.available;*reason=control.reason;*level=control.translucency;return 0;}
    struct zenith_material_settings m={0};r=zenith_material_settings_read(&m);
    if(r){*saved=*effective=*available=*reason=0;return 0;}
    if((m.version!=1&&m.version!=2)||m.reserved||m.revision!=s->revision)return -11;
    *saved=m.saved;*effective=m.effective;*available=m.available;*reason=m.reason;return 0;
}
static long save_settings(unsigned int m,unsigned int a,unsigned int effects,unsigned int level,unsigned long revision){
    if(scope_info&4)return zenith_scoped_appearance_save((unsigned int)selected_scope,m,a,effects,level,revision,0);
    long result=zenith_material_control_save(m,a,effects,level,revision);
    if(result!=-38)return result;
    if(level!=40)return -19;
    struct zenith_material_settings state={0};
    if(zenith_material_settings_read(&state))return effects?-19:zenith_appearance_save(m,a,revision);
    return zenith_material_settings_save(m,a,effects,revision);
}
static long scope_action(unsigned int action,unsigned long revision){
    if(action==3)return scope_info;
    if(action==2)return selected_scope==1?zenith_scoped_appearance_save(1,0,0,0,40,revision,1):-22;
    if(action>1)return -22;
    struct zenith_scoped_appearance state={0};long r=zenith_scoped_appearance_read(action,&state);
    if(!r){selected_scope=(int)action;}return r;
}
static long dirty(int value){return zenith_gui_dirty(handle,value);}
static int show(void){
    struct zenith_appearance s={0};if(zenith_appearance_read(&s)||s.version!=1){print("settings: service unavailable\n");return 1;}
    print("SETTINGS saved=");print(mode(s.mode));print("/");print(accent(s.accent));
    print(" active=");print(mode(s.active_mode));print("/");print(accent(s.active_accent));
    print(" persistent=");number(!!(s.flags&1));print(" writable=");number(!!(s.flags&2));
    print(" pending=");number(!!(s.flags&4));print(" revision=");number(s.revision);print("\n");return 0;
}
static int system_preferences(int argc,char **argv){
    struct zenith_system_settings state={0};
    if(zenith_system_settings_read(&state)||(state.version!=1&&state.version!=2)||state.keyboard_count>8||state.zone_count>48||state.keyboard>=state.keyboard_count){print("settings: shared service unavailable\n");return 1;}
    if(argc==2){print("SHARED_SETTINGS scope=system keyboard=");print(state.keyboards[state.keyboard]);print(" timezone=");print(state.current_zone);print(" writable=");number(!!(state.flags&2));if(state.version==2){print(" session=");print(state.current_session);}print("\n");return 0;}
    unsigned int field=same(argv[2],"keyboard")?1:same(argv[2],"timezone")?2:same(argv[2],"session")?3:0,count=field==1?state.keyboard_count:field==2?state.zone_count:state.session_count;
    if(!field){print("settings: unknown shared field\n");return 2;}
    for(unsigned int i=0;i<count;i++)if(same(argv[3],field==1?state.keyboards[i]:field==2?state.zones[i]:state.sessions[i])){
        long r=zenith_system_settings_save(field,i,state.revision);if(r){print("settings: system default save failed\n");return 1;}print("SYSTEM_DEFAULT_SAVED\n");return 0;
    }
    print("settings: unknown supported choice\n");return 2;
}
static int personal_settings(int argc,char **argv){
    struct zenith_scoped_appearance state={0};if(zenith_scoped_appearance_read(1,&state)){print("settings: personal preferences unavailable\n");return 1;}
    if(argc==2){print("PERSONAL_SETTINGS uid=");number(state.uid);print(" inherited=");number(state.inherited);print(" mode=");print(mode(state.appearance.mode));print(" accent=");print(accent(state.appearance.accent));print("\n");return 0;}
    int inherit=argc==3&&same(argv[2],"inherit");if(argc==3&&!inherit)return 2;unsigned int m=state.appearance.mode,a=state.appearance.accent;
    if(!inherit){m=same(argv[2],"dark")?0:same(argv[2],"light")?1:2;for(a=0;a<6;a++)if(same(argv[3],accent(a)))break;if(m>1||a>5)return 2;}
    long r=zenith_scoped_appearance_save(1,m,a,state.materials.saved,state.materials.translucency,state.appearance.revision,inherit);
    if(r){print("settings: personal save failed\n");return 1;}print("PERSONAL_SETTINGS_SAVED\n");return 0;
}
static int settings_information(const char *name){
    unsigned int kind=same(name,"network")?1:same(name,"accounts")?2:0;if(!kind)return 2;
    struct zenith_settings_information state={0};long r=zenith_settings_information_read(kind,&state);
    if(r||state.version!=1||state.kind!=kind||state.length>=sizeof(state.text))return 1;
    state.text[state.length]=0;print(state.text);print("\nSETTINGS_INFORMATION_OK\n");return 0;
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
    struct zenith_material_control control={0};
    if(zenith_call(618,(unsigned long)&control,47,0)!=-22)return 30;
    if(zenith_call(619,0xffff800000000000UL,40,0)!=-14)return 31;
    struct zenith_material_control_request control_request={3,0,before.revision,0,0,0,101,0};
    if(zenith_call(619,(unsigned long)&control_request,40,0)!=((before.flags&Z_SETTINGS_WRITABLE)?-22:-13))return 32;
    if(zenith_call(620,0xffff800000000000UL,4096,0)!=-14)return 33;
    if(zenith_call(620,(unsigned long)&after,4095,0)!=-22)return 34;
    if(zenith_call(621,0xffff800000000000UL,32,0)!=-14)return 35;
    struct zenith_system_settings_request preference_request={1,1,1,0,1,0};
    if(zenith_call(621,(unsigned long)&preference_request,32,0)!=-22)return 36;
    if(zenith_call(622,2,(unsigned long)&after,512)!=-22)return 40;
    if(zenith_call(622,0,0xffff800000000000UL,512)!=-14)return 41;
    if(zenith_call(623,0xffff800000000000UL,48,0)!=-14)return 42;
    struct zenith_scoped_request scoped_request={1,0,before.revision,0,0,0,40,0,1,0};
    if(zenith_call(623,(unsigned long)&scoped_request,48,0)!=-22)return 43;
    if(zenith_call(624,3,(unsigned long)&after,4096)!=-22)return 44;
    if(zenith_call(624,1,0xffff800000000000UL,4096)!=-14)return 45;
    if(zenith_call(625,0xffff800000000000UL,4096,0)!=-14)return 46;
    if(zenith_call(626,0xffff800000000000UL,32,0)!=-14)return 47;
    if(zenith_gui_viewport(20001,&viewport_state)!=-9)return 26;
    long motion=zenith_gui_pointer_motion(20001);
    if(motion!=-9&&motion!=-38)return 48;
    if(motion==-9&&zenith_call(627,20001,2,0)!=-22)return 49;
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
    if((argc==2&&same(argv[1],"--system-show"))||(argc==4&&same(argv[1],"--system-set")))return system_preferences(argc,argv);
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
    if((argc==2&&same(argv[1],"--personal-show"))||((argc==3||argc==4)&&same(argv[1],"--personal-set")))return personal_settings(argc,argv);
    if(argc==3&&same(argv[1],"--settings-info"))return settings_information(argv[2]);
    if(argc==2&&same(argv[1],"--private-store-probe")){
        struct zenith_scoped_appearance state={0};
        if(zenith_scoped_appearance_read(1,&state)||!state.uid)return 1;
        char path[80]="/etc/zenith/preferences/",digits[10];unsigned int uid=state.uid,n=0,i=sizeof("/etc/zenith/preferences/")-1;
        do{digits[n++]=(char)('0'+uid%10);uid/=10;}while(uid);
        while(n)path[i++]=digits[--n];
        const char suffix[]=".conf";for(n=0;suffix[n];n++)path[i++]=suffix[n];path[i]=0;
        long fd=zenith_call(2,(unsigned long)path,i,0);
        if(fd>=0){zenith_call(3,(unsigned long)fd,0,0);return 1;}
        if(fd!=-13)return 1;
        print("PRIVATE_PREFERENCES_RAW_DENIED\n");return 0;
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
    zui_icons_use_packages();
    struct settings_backend backend={read_settings,save_settings,dirty,scope_action};
    if(!meridian_init(&shell,pixels,backend,page)){print("settings: service unavailable\n");return 1;}
    long info=zenith_call(612,(unsigned long)shell.information,sizeof(shell.information)-1,1);
    if(info>0&&info<(long)sizeof(shell.information))shell.information[info]=0;
    struct zenith_material_caps caps={0};
    struct zenith_material_settings material_state={0};
    material_surface=zenith_material_settings_read(&material_state)==0&&(material_state.version==1||material_state.version==2)&&!material_state.reserved&&
        zenith_gui_material_caps(&caps)==0&&caps.version==2&&(caps.features&3)==3;
    /* 640x480 is the largest surface the host admits at open; maximize goes beyond it. */
    if(!meridian_resize(&shell,640,480,1920*1000))return 1;
    handle=material_surface?zenith_gui_open_material(640,480,"Meridian",8):zenith_gui_open(640,480,"Meridian",8);
    if(material_surface)shell.ui.foreground=1;
    if(handle<0){print("settings: graphical session required; use --show or --set\n");return 1;}
    if(zenith_gui_interactive(handle)<0){zenith_gui_close(handle);print("settings: interactive client support required\n");return 1;}
    if(zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);print("settings: viewport service required\n");return 1;}
    shell.ui.hover_supported=zenith_gui_pointer_motion(handle)==0; /* -38 on older kernels */
    meridian_system_services(&shell,zenith_system_settings_read,zenith_system_settings_save);
    meridian_information_services(&shell,zenith_settings_information_read);
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
                int edge=shell.ui.width>=720?252:212;
                if(next.type!=Z_GUI_SCROLL||((next.x<edge)!=(e.x<edge))){deferred=next;have_deferred=1;break;}
                e.code+=next.code;
            }
        }
        if((r>0&&e.type==Z_GUI_RESIZE)||(i&31)==0){if(!viewport()){result=1;break;}}
        if(r<0){result=1;break;}
        int previous_page=shell.page,previous_mode=shell.nav.mode;
        int edited=binding_any_changed(&shell.bindings),prompt=shell.dialog.open;unsigned long rev=shell.colors.state.revision,preference_revision=shell.bindings.preferences.revision;
        if(r){meridian_event(&shell,&e);if(e.type==Z_GUI_FOCUS&&e.code)print("MERIDIAN_FOCUSED\n");}
        if(previous_page!=shell.page){print("MERIDIAN_PAGE ");print(meridian_pages[shell.page].id);print("\n");}
        if(previous_mode!=NAV_CATEGORIES&&shell.nav.mode==NAV_CATEGORIES)print("MERIDIAN_PAGE navigation\n");
        if((i&31)==0)meridian_refresh(&shell);
        if(!edited&&binding_any_changed(&shell.bindings))print("SETTINGS_UNSAVED\n");
        if(!prompt&&shell.dialog.open)print("SETTINGS_DISCARD_PROMPT\n");
        if(shell.bindings.preferences.revision!=preference_revision)print("SYSTEM_SETTINGS_REVISION_CHANGED\n");
        if(shell.colors.state.revision!=rev)print("SETTINGS_REVISION_CHANGED\n");
        if(meridian_ui(&shell)->dirty&&!shell.closing){meridian_refresh(&shell);meridian_draw(&shell);if(present()<0){result=1;break;}}
    }
    zenith_gui_close(handle);print("SETTINGS_EXIT\n");return result;
}
