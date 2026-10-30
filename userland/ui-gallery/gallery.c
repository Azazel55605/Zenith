#include "gallery.h"
static void label(struct gallery *g,int x,int y,const char *s,const struct zui_font *font,unsigned int color){
    unsigned long n=0;while(s[n])n++;zui_text(&g->ui,x,y,s,n,font,color);
}
static int page0_controls(struct gallery *g){
    g->ui.count=0;g->ui.focus=-1;g->ui.pressed=-1;
    if(!zui_add(&g->ui,1,ZUI_SEGMENT,(struct zui_rect){28,108,136,28},"Dark",!g->light,1)||
       !zui_add(&g->ui,2,ZUI_SEGMENT,(struct zui_rect){164,108,136,28},"Light",!!g->light,1))return 0;
    static const char *names[]={"Blue","Violet","Rose","Amber","Green","Teal"};
    for(int i=0;i<6;i++)if(!zui_add_swatch(&g->ui,10+i,(struct zui_rect){24+i*44,190,32,32},names[i],i,i==g->accent,1))return 0;
    if(!zui_add(&g->ui,20,ZUI_TOGGLE,(struct zui_rect){24,276,552,36},"Transparent surfaces",0,0)||
       !zui_add(&g->ui,21,ZUI_TOGGLE,(struct zui_rect){24,324,552,36},"Backdrop blur",0,0))return 0;
    return 1;
}
int gallery_init(struct gallery *g,unsigned int *pixels,int light){
    *g=(struct gallery){0};g->light=!!light;
    if(!zui_init(&g->ui,pixels,600,440))return 0;
    zui_appearance(&g->ui,g->light,0);
    g->list.base_id=100;g->combo.id=50;g->combo.popup.base_id=200;
    g->dialog=(struct zui_dialog){0,GALLERY_DIALOG,"Apply changes","The gallery only previews these controls.",{"Apply","Discard","Cancel"},3};
    return page0_controls(g);
}
static const char *categories[]={"Quick Settings","Input & Output","Devices","Networking","Appearance",
    "Apps & Windows","Workspace","Security","Language & Time","System"};
static const char *category_icons[]={"zap","keyboard","usb","network","palette","app-window","layout-grid",
    "shield","languages","monitor-cog"};
static int category_rows(void *context,int i,struct zui_list_item *out){
    (void)context;if(i<0||i>=10)return 0;
    *out=(struct zui_list_item){category_icons[i],categories[i],0,i>0,1};return 1;
}
static const char *layouts[]={"English (US)","German","English (UK)","French"};
static int layout_rows(void *context,int i,struct zui_list_item *out){
    (void)context;if(i<0||i>=4)return 0;
    *out=(struct zui_list_item){0,layouts[i],0,0,1};return 1;
}
static void info(struct zui *u,struct zui_form *f,const char *name,const char *value){
    struct zui_rect r=zui_form_row(u,f,name,0,f->w,20);
    unsigned long n=0;while(value[n])n++;
    zui_text(u,r.x,r.y,value,n,&zui_body,u->theme.secondary);
}
static void draw_widgets(struct gallery *g){
    struct zui *u=&g->ui;u->count=0;
    struct zui_workspace w=zui_workspace_begin(u,200);
    zui_list_draw(u,&g->list,w.sidebar,10,category_rows,0);
    struct zui_rect body={w.content.x,w.content.y,w.content.w,w.content.h-56};
    int top=zui_scroll_begin(u,&g->scroll,body);
    struct zui_form f;zui_form_begin(&f,(struct zui_rect){body.x+16,body.y,body.w-32,body.h},top+16,u->width);
    zui_form_section(u,&f,"Keyboard");
    zui_combo_draw(u,&g->combo,zui_form_row(u,&f,"Layout","Preview only; nothing is saved.",220,36),4,layout_rows,0);
    zui_form_section(u,&f,"Effects");
    int cw=f.narrow?f.w:f.w-f.label_w-16;
    zui_add(u,GALLERY_TOGGLE,ZUI_TOGGLE,zui_form_row(u,&f,"Surfaces",0,cw,36),"Transparent",g->toggles[0],1);
    zui_add(u,GALLERY_TOGGLE+1,ZUI_TOGGLE,zui_form_row(u,&f,"Backdrop",0,cw,36),"Blur",g->toggles[1],1);
    zui_form_section(u,&f,"About");
    info(u,&f,"Toolkit","Client library v2, stage 1");
    info(u,&f,"Icons","Lucide 1.64.0 (ISC)");
    info(u,&f,"Hover","Waiting for host pointer motion");
    info(u,&f,"Navigation","Ctrl+Left returns to appearance");
    zui_scroll_end(u,&g->scroll,zui_form_height(&f,top)+16);
    int changed=g->toggles[0]!=g->saved_toggles[0]||g->toggles[1]!=g->saved_toggles[1]||g->combo.value!=g->saved_layout;
    int defaults=!g->saved_toggles[0]&&!g->saved_toggles[1]&&!g->saved_layout;
    zui_footer(u,(struct zui_rect){w.content.x,w.content.y+w.content.h-56,w.content.w,56},GALLERY_FOOTER,0,changed,defaults);
    zui_draw_controls(u);
    zui_combo_popup(u,&g->combo,4,layout_rows,0);
    zui_dialog_draw(u,&g->dialog);
    u->dirty=0;
}
static void revert(struct gallery *g){
    g->toggles[0]=g->saved_toggles[0];g->toggles[1]=g->saved_toggles[1];g->combo.value=g->saved_layout;g->ui.dirty=1;
}
static int widgets_event(struct gallery *g,const struct zenith_gui_event *e){
    struct zui *u=&g->ui;
    if(g->dialog.open){
        int button=zui_dialog_event(u,&g->dialog,e);
        if(button==1){g->saved_toggles[0]=g->toggles[0];g->saved_toggles[1]=g->toggles[1];g->saved_layout=g->combo.value;}
        if(button==2)revert(g);
        return 0;
    }
    int was_open=g->combo.open;
    zui_combo_event(u,&g->combo,e,layout_rows,0);
    if(was_open||g->combo.open)return 0;
    int list_focus=u->focus>=0&&u->focus<u->count&&u->controls[u->focus].id>=100&&u->controls[u->focus].id<110;
    if(e->type!=Z_GUI_KEY||list_focus)zui_list_event(u,&g->list,e,category_rows,0);
    zui_scroll_event(u,&g->scroll,e);
    int id=zui_event(u,e);
    if(id==GALLERY_TOGGLE||id==GALLERY_TOGGLE+1){g->toggles[id-GALLERY_TOGGLE]^=1;u->dirty=1;}
    if(id==GALLERY_FOOTER+ZUI_FOOTER_APPLY){g->dialog.open=1;u->dirty=1;}
    if(id==GALLERY_FOOTER+ZUI_FOOTER_RESET)revert(g);
    if(id==GALLERY_FOOTER+ZUI_FOOTER_DEFAULTS){g->toggles[0]=g->toggles[1]=0;g->combo.value=0;u->dirty=1;}
    return id;
}
void gallery_draw(struct gallery *g){
    if(g->page){draw_widgets(g);return;}
    struct zui *u=&g->ui;zui_clip(u,(struct zui_rect){0,0,600,440});
    zui_surface(u,u->clip,0,ZUI_SURFACE_CONTENT);
    label(g,24,16,"Appearance",&zui_heading,u->theme.text);
    label(g,24,51,"Widget gallery - local preview, not saved Settings",&zui_body,u->theme.secondary);
    label(g,24,79,"Color mode",&zui_label,u->theme.text);
    label(g,24,163,"Accent color",&zui_label,u->theme.text);
    zui_setting_row(u,(struct zui_rect){24,286,480,42},"","Let selected surfaces reveal the background.");
    zui_setting_row(u,(struct zui_rect){24,334,480,42},"","System material controls are work in progress.");
    label(g,24,387,"Tab / Shift+Tab to focus. Enter or Space to choose.",&zui_caption,u->theme.secondary);
    label(g,24,408,"Escape closes. No system preferences are changed.",&zui_caption,u->theme.secondary);
    zui_swatch_caption(u,24,224);
    zui_segment_track(u,(struct zui_rect){24,104,280,36});
    zui_draw_controls(u);u->dirty=0;
}
int gallery_event(struct gallery *g,const struct zenith_gui_event *e){
    if(e->type==Z_GUI_KEY&&(e->modifiers&2)&&(e->extra==5||e->extra==6)&&!g->dialog.open){
        int page=e->extra==6;
        if(page!=g->page){
            g->page=page;g->combo.open=0;
            if(!page)page0_controls(g);else{g->ui.count=0;g->ui.focus=-1;g->ui.pressed=-1;}
            g->ui.dirty=1;
        }
        return 0;
    }
    if(g->page)return widgets_event(g,e);
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
