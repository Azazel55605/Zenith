#include "../ui/ui.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
static unsigned int pixels[1920*1000];
static struct zui ui;
static char information[4096];
static int material;
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){unsigned long n=0;while(s[n])n++;zenith_write(s,n);}
static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static void update(void){
    struct zenith_appearance state={0};
    if(!zenith_appearance_read(&state)&&state.mode<2&&state.accent<6){const unsigned int *p=state.palette[state.mode][state.accent];ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};}
    long r=zenith_call(612,(unsigned long)information,sizeof(information)-1,1);
    if(r>0&&r<(long)sizeof(information))information[r]=0;
    else {const char *s="System information unavailable.";unsigned int i=0;for(;s[i];i++)information[i]=s[i];information[i]=0;}
    struct zenith_material_settings material_state={0};
    if(!zenith_material_settings_read(&material_state))ui.full_background=(material_state.saved&5)==5;
    ui.dirty=1;
}
int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("desktop-tools " SUNBEAM_VERSION "\n");return 0;}
    int about=argc==2&&same(argv[1],"system");
    if(!about&&!(argc==2&&same(argv[1],"welcome"))){print("usage: native run desktop-tools welcome|system\n");return 2;}
    zui_init(&ui,pixels,600,480);update();
    struct zenith_material_caps caps={0};struct zenith_material_settings settings={0};
    material=!zenith_material_settings_read(&settings)&&!zenith_gui_material_caps(&caps)&&caps.version==2;
    long handle=material?zenith_gui_open_material(600,480,about?"System Information":"Welcome",about?18:7):zenith_gui_open(600,480,about?"System Information":"Welcome",about?18:7);
    if(handle<0){print("desktop-tools: graphical session required\n");return 1;}
    ui.foreground=material;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);return 1;}
    print(about?"DESKTOP_SYSTEM_READY\n":"DESKTOP_WELCOME_READY\n");
    int done=0,scroll=0,lines=0,result=0,tab=0;
    while(!done){
        if(ui.dirty){
            struct zui_workspace w=zui_workspace_begin(&ui,156);struct zui_rect c=w.content;
            int focus=ui.focus>=0&&ui.focus<ui.count?ui.controls[ui.focus].id:0;
            int pressed=ui.pressed>=0&&ui.pressed<ui.count?ui.controls[ui.pressed].id:0;
            const char *tabs[]={"Overview","Getting started","Recovery"};ui.count=0;ui.pressed=-1;ui.focus=-1;
            zui_text(&ui,24,28,about?"System":"Welcome",16,&zui_label,ui.theme.text);
            for(int i=0;i<(about?1:3);i++)zui_add(&ui,10+i,ZUI_NAVIGATION,(struct zui_rect){20,86+i*38,140,32},tabs[i],tab==i,1);
            zui_add(&ui,1,ZUI_BUTTON,(struct zui_rect){c.x+16,c.y+c.h-48,100,34},"Refresh",0,about);
            zui_add(&ui,2,ZUI_BUTTON,(struct zui_rect){c.x+c.w-112,c.y+c.h-48,96,34},"Close",0,1);
            const char *title=about?"About this system":tab==0?"Welcome to Zenith":tabs[tab];
            zui_clip(&ui,c);zui_text(&ui,c.x+20,c.y+20,title,length(title),&zui_heading,ui.theme.text);
            const char *body=about?information:tab==0?"Your desktop, built around independent applications.\n\nUse the sidebar for an introduction, everyday tasks and recovery.\n\nMeridian is the system settings app. Colors and materials support Light and Dark modes, six accents and optional transparency/blur. Other planned settings are clearly marked Work in progress.":tab==1?"Open apps from the launcher. Drag their title bars to move them, maximize supported windows, or switch between them from the taskbar.\n\nTerminal is your command line. Type help to explore commands. Editor opens and saves text files. Files provides the existing file browser; its larger redesign is separate.\n\nUse Meridian's category sidebar and search to find settings pages.":"Keep a confirmed previous kernel when updating. Confirm a healthy new boot with boot confirm.\n\nTerminal remains the recovery and command host. Local text login and serial fallback are available where supported.\n\nSunrise installs Zenith from the live image. It erases the selected disk only after explicit confirmation.\n\nGeneral snapshot restore and arbitrary power-loss recovery are not implemented.";
            struct zui_rect area={c.x+20,c.y+78,c.w-40,c.h-150};
            lines=zui_paragraph(&ui,area,body,&zui_body,ui.theme.secondary,scroll);
            for(int i=0;i<ui.count;i++)if(ui.controls[i].enabled){if(ui.controls[i].id==focus)ui.focus=i;if(ui.controls[i].id==pressed)ui.pressed=i;}
            zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});zui_draw_controls(&ui);ui.dirty=0;
            long r=material?zenith_gui_present_material(handle,pixels,(unsigned long)ui.width*ui.height*4,ui.materials,ui.material_count):zenith_gui_present(handle,pixels,(unsigned long)ui.width*ui.height*4);
            if(r<0){result=1;break;}
        }
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_RESIZE){
            struct zenith_viewport v={0};
            if(zenith_gui_viewport(handle,&v)||v.width<600||v.height<480||v.width>1920||v.height>1000){result=1;break;}
            long accepted=zenith_gui_resize_accept(handle,&v);if(accepted==-11)continue;
            if(accepted||!zui_resize(&ui,v.width,v.height,1920*1000)){result=1;break;}continue;
        }
        if(e.type==Z_GUI_CLOSE||e.type==Z_GUI_CLOSE_INTENT||(e.type==Z_GUI_KEY&&e.extra==1&&ui.window_focused)){done=1;continue;}
        if(e.type==Z_GUI_FOCUS&&e.code)print("DESKTOP_FOCUSED\n");
        int action=zui_event(&ui,&e);if(action==2)done=1;if(action==1)update();
        if(action>=10&&action<13){tab=action-10;scroll=0;ui.dirty=1;}
        if(ui.window_focused&&(e.type==Z_GUI_SCROLL||(e.type==Z_GUI_KEY&&(e.extra==7||e.extra==8)))){
            int next=zui_scroll_step(scroll,(e.type==Z_GUI_SCROLL?e.code<0:e.extra==8)?3:-3,lines-(ui.height-174)/(zui_body.height+4));
            if(scroll!=next){scroll=next;ui.dirty=1;}
        }
    }
    zenith_gui_close(handle);return result;
}
