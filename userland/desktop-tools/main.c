#include "../ui/ui.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
static unsigned int pixels[600*440];
static struct zui ui;
static char information[4096];
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){unsigned long n=0;while(s[n])n++;zenith_write(s,n);}
static void update(void){
    struct zenith_appearance state={0};
    if(!zenith_appearance_read(&state)&&state.mode<2&&state.accent<6){const unsigned int *p=state.palette[state.mode][state.accent];ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};}
    long r=zenith_call(612,(unsigned long)information,sizeof(information)-1,1);
    if(r>0&&r<(long)sizeof(information))information[r]=0;
    else {const char *s="System information unavailable.";unsigned int i=0;for(;s[i];i++)information[i]=s[i];information[i]=0;}
    ui.dirty=1;
}
int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("desktop-tools " SUNBEAM_VERSION "\n");return 0;}
    int about=argc==2&&same(argv[1],"system");
    if(!about&&!(argc==2&&same(argv[1],"welcome"))){print("usage: native run desktop-tools welcome|system\n");return 2;}
    zui_init(&ui,pixels,600,440);update();
    long handle=zenith_gui_open(600,440,about?"System Information":"Welcome",about?18:7);
    if(handle<0){print("desktop-tools: graphical session required\n");return 1;}
    if(zenith_gui_interactive(handle)<0){zenith_gui_close(handle);return 1;}
    zui_add(&ui,1,ZUI_BUTTON,(struct zui_rect){24,382,148,36},"Refresh",0,about);
    zui_add(&ui,2,ZUI_BUTTON,(struct zui_rect){428,382,148,36},"Close",0,1);
    print(about?"DESKTOP_SYSTEM_READY\n":"DESKTOP_WELCOME_READY\n");
    int done=0,scroll=0,lines=0,result=0;
    while(!done){
        if(ui.dirty){
            zui_clip(&ui,(struct zui_rect){0,0,600,440});zui_fill(&ui,ui.clip,ui.theme.surface);
            const char *title=about?"About this system":"Welcome to Zenith";
            zui_text(&ui,24,20,title,64,&zui_heading,ui.theme.text);
            const char *body=about?information:"Your desktop, built around independent applications.\n\nUse the launcher to open Terminal, Editor and Files. Drag a window by its title bar; use the taskbar to switch between applications.\n\nMeridian is the system settings app. Colors supports Light and Dark modes and six accent presets. Planned settings are marked Work in progress.\n\nTerminal remains the command line and recovery host. Type help to explore commands.\n\nSunrise installs Zenith from the live image. Installation erases the selected disk only after explicit confirmation.";
            lines=zui_paragraph(&ui,(struct zui_rect){24,72,552,292},body,&zui_body,ui.theme.secondary,scroll);
            zui_draw_controls(&ui);ui.dirty=0;
            if(zenith_gui_present(handle,pixels,sizeof(pixels))<0){result=1;break;}
        }
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_CLOSE||e.type==Z_GUI_CLOSE_INTENT||(e.type==Z_GUI_KEY&&e.extra==1&&ui.window_focused)){done=1;continue;}
        if(e.type==Z_GUI_FOCUS&&e.code)print("DESKTOP_FOCUSED\n");
        int action=zui_event(&ui,&e);if(action==2)done=1;if(action==1)update();
        if(ui.window_focused&&(e.type==Z_GUI_SCROLL||(e.type==Z_GUI_KEY&&(e.extra==7||e.extra==8)))){
            scroll+=(e.type==Z_GUI_SCROLL?e.code<0:e.extra==8)?3:-3;
            if(scroll<0)scroll=0;
            if(scroll>lines-12)scroll=lines>12?lines-12:0;
            ui.dirty=1;
        }
    }
    zenith_gui_close(handle);return result;
}
