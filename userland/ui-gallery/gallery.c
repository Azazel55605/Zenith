#include "gallery.h"
static void label(struct gallery *g,int x,int y,const char *s,const struct zui_font *font,unsigned int color){
    unsigned long n=0;while(s[n])n++;zui_text(&g->ui,x,y,s,n,font,color);
}
int gallery_init(struct gallery *g,unsigned int *pixels,int light){
    *g=(struct gallery){0};g->light=!!light;
    if(!zui_init(&g->ui,pixels,600,440))return 0;
    zui_appearance(&g->ui,g->light,0);
    if(!zui_add(&g->ui,1,ZUI_SEGMENT,(struct zui_rect){28,108,136,28},"Dark",!light,1)||
       !zui_add(&g->ui,2,ZUI_SEGMENT,(struct zui_rect){164,108,136,28},"Light",!!light,1))return 0;
    const char *names[]={"Blue","Violet","Rose","Amber","Green","Teal"};
    for(int i=0;i<6;i++)if(!zui_add_swatch(&g->ui,10+i,(struct zui_rect){24+i*44,190,32,32},names[i],i,i==0,1))return 0;
    if(!zui_add(&g->ui,20,ZUI_TOGGLE,(struct zui_rect){24,276,552,36},"Transparent surfaces",0,0)||
       !zui_add(&g->ui,21,ZUI_TOGGLE,(struct zui_rect){24,324,552,36},"Backdrop blur",0,0))return 0;
    return 1;
}
void gallery_draw(struct gallery *g){
    struct zui *u=&g->ui;zui_clip(u,(struct zui_rect){0,0,600,440});
    zui_surface(u,u->clip,0,ZUI_SURFACE_CONTENT);
    label(g,24,16,"Appearance",&zui_heading,u->theme.text);
    label(g,24,51,"Widget gallery - local preview, not saved Settings",&zui_body,u->theme.secondary);
    label(g,24,79,"Color mode",&zui_label,u->theme.text);
    label(g,24,163,"Accent color",&zui_label,u->theme.text);
    zui_setting_row(u,(struct zui_rect){24,286,480,42},"","Let selected surfaces reveal the background.");
    zui_setting_row(u,(struct zui_rect){24,334,480,42},"","Requires transparency and compositor support.");
    label(g,24,387,"Tab / Shift+Tab to focus. Enter or Space to choose.",&zui_caption,u->theme.secondary);
    label(g,24,408,"Escape closes. No system preferences are changed.",&zui_caption,u->theme.secondary);
    zui_swatch_caption(u,24,224);
    zui_segment_track(u,(struct zui_rect){24,104,280,36});
    zui_draw_controls(u);u->dirty=0;
}
int gallery_event(struct gallery *g,const struct zenith_gui_event *e){
    int action=zui_event(&g->ui,e);
    if(action==1||action==2)g->light=action==2;
    if(action>=10&&action<=15)g->accent=action-10;
    if(action){
        zui_appearance(&g->ui,g->light,g->accent);
        g->ui.controls[0].value=!g->light;g->ui.controls[1].value=g->light;
        for(int i=0;i<6;i++)g->ui.controls[i+2].value=i==g->accent;
    }
    return action;
}
