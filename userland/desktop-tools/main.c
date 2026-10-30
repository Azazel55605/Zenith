#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
static unsigned int pixels[1920*1000];
static struct zui ui;
static char information[4096];
static int material;
static const char *tab_names[3]={"Overview","Get started","Recovery"};
static const char *tab_icons[3]={"info","zap","rotate-ccw"};
static int tab_rows(void *context,int i,struct zui_list_item *o){
    int about=*(int *)context;if(i<0||i>=(about?1:3))return 0;
    *o=(struct zui_list_item){about?"monitor":tab_icons[i],about?"About":tab_names[i],0,0,1};return 1;
}
static struct zui_list sidebar;
static struct zui_scroll page;
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){unsigned long n=0;while(s[n])n++;zenith_write(s,n);}
static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static void update(void){
    struct zenith_appearance state={0};struct zenith_scoped_appearance scoped={0};
    int personal=zenith_scoped_appearance_read(1,&scoped)==0;
    if(personal)state=scoped.appearance;
    if((personal||!zenith_appearance_read(&state))&&state.active_mode<2&&state.active_accent<6){const unsigned int *p=state.palette[state.active_mode][state.active_accent];ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};}
    long r=zenith_call(612,(unsigned long)information,sizeof(information)-1,1);
    if(r>0&&r<(long)sizeof(information))information[r]=0;
    else {const char *s="System information unavailable.";unsigned int i=0;for(;s[i];i++)information[i]=s[i];information[i]=0;}
    struct zenith_material_settings material_state={0};
    if(personal)ui.full_background=(scoped.materials.saved&5)==5;
    else if(!zenith_material_settings_read(&material_state))ui.full_background=(material_state.saved&5)==5;
    ui.dirty=1;
}
int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("desktop-tools " SUNBEAM_VERSION "\n");return 0;}
    int about=argc==2&&same(argv[1],"system");
    if(!about&&!(argc==2&&same(argv[1],"welcome"))){print("usage: native run desktop-tools welcome|system\n");return 2;}
    zui_icons_use_packages();
    zui_init(&ui,pixels,600,480);update();
    struct zenith_material_caps caps={0};struct zenith_material_settings settings={0};
    material=!zenith_material_settings_read(&settings)&&!zenith_gui_material_caps(&caps)&&caps.version==2;
    long handle=material?zenith_gui_open_material(600,480,about?"System Information":"Welcome",about?18:7):zenith_gui_open(600,480,about?"System Information":"Welcome",about?18:7);
    if(handle<0){print("desktop-tools: graphical session required\n");return 1;}
    ui.foreground=material;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);return 1;}
    print(about?"DESKTOP_SYSTEM_READY\n":"DESKTOP_WELCOME_READY\n");
    ui.hover_supported=zenith_gui_pointer_motion(handle)==0; /* -38 on older kernels */
    sidebar.base_id=10;
    int done=0,lines=0,result=0,tab=0;
    while(!done){
        if(ui.dirty){
            struct zui_workspace w=zui_workspace_begin(&ui,156);struct zui_rect c=w.content;
            int focus=ui.focus>=0&&ui.focus<ui.count?ui.controls[ui.focus].id:0;
            int pressed=ui.pressed>=0&&ui.pressed<ui.count?ui.controls[ui.pressed].id:0;
            ui.count=0;ui.pressed=-1;ui.focus=-1;
            zui_text(&ui,24,28,about?"System":"Welcome",about?6:7,&zui_label,ui.theme.text);
            sidebar.selected=tab;zui_list_draw(&ui,&sidebar,(struct zui_rect){w.sidebar.x,w.sidebar.y+52,w.sidebar.w,w.sidebar.h-52},about?1:3,tab_rows,&about);
            zui_add(&ui,1,ZUI_BUTTON,(struct zui_rect){c.x+16,c.y+c.h-48,100,34},"Refresh",0,about);
            zui_add(&ui,2,ZUI_BUTTON,(struct zui_rect){c.x+c.w-112,c.y+c.h-48,96,34},"Close",0,1);
            const char *title=about?"About this system":tab==0?"Welcome to Zenith":tab_names[tab];
            zui_clip(&ui,c);zui_text(&ui,c.x+24,c.y+14,title,length(title),&zui_heading,ui.theme.text);
            const char *caption=about?"Running system information":"Getting around your desktop";
            zui_text(&ui,c.x+24,c.y+50,caption,length(caption),&zui_caption,ui.theme.secondary);
            const char *body=about?information:tab==0?"Your desktop, built around independent applications.\n\nUse the sidebar for an introduction, everyday tasks and recovery.\n\nMeridian is the system settings app. Colors and materials support Light and Dark modes, six accents and optional transparency/blur. Other planned settings are clearly marked Work in progress.":tab==1?"Open apps from the launcher. Drag their title bars to move them, maximize supported windows, or switch between them from the taskbar.\n\nTerminal is your command line. Type help to explore commands. Editor opens and saves text files. Files provides the existing file browser; its larger redesign is separate.\n\nUse Meridian's category sidebar and search to find settings pages.":"Keep a confirmed previous kernel when updating. Confirm a healthy new boot with boot confirm.\n\nTerminal remains the recovery and command host. Local text login and serial fallback are available where supported.\n\nSunrise installs Zenith from the live image. It erases the selected disk only after explicit confirmation.\n\nGeneral snapshot restore and arbitrary power-loss recovery are not implemented.";
            struct zui_rect area={c.x,c.y+74,c.w,c.h-74-60};
            int top=zui_scroll_begin(&ui,&page,area);
            lines=zui_paragraph(&ui,(struct zui_rect){c.x+24,top+8,c.w-52,4000},body,&zui_body,ui.theme.secondary,0);
            zui_scroll_end(&ui,&page,lines*(zui_body.height+4)+16);
            for(int i=0;i<ui.count;i++)if(ui.controls[i].enabled){if(ui.controls[i].id==focus)ui.focus=i;if(ui.controls[i].id==pressed)ui.pressed=i;}
            zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});zui_update_hover(&ui);zui_draw_controls(&ui);ui.dirty=0;
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
        if(action>=10&&action<13){tab=action-10;page.offset=0;ui.dirty=1;}
        zui_scroll_event(&ui,&page,&e);
        if(ui.window_focused&&e.type==Z_GUI_KEY&&!e.modifiers){
            int max=zui_scroll_maximum(&page),next=page.offset;
            if(e.extra==7)next=zui_scroll_step(next,-40,max);
            else if(e.extra==8)next=zui_scroll_step(next,40,max);
            else if(e.extra==ZUI_KEY_PAGE_UP)next=zui_scroll_step(next,-page.rect.h,max);
            else if(e.extra==ZUI_KEY_PAGE_DOWN)next=zui_scroll_step(next,page.rect.h,max);
            if(next!=page.offset){page.offset=next;ui.dirty=1;}
        }
    }
    zenith_gui_close(handle);return result;
}
