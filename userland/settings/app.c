#include "app.h"
static unsigned long length(const char *s){unsigned long n=0;while(s[n])n++;return n;}
static void text(struct settings_app *a,int x,int y,const char *s,const struct zui_font *font,unsigned int color){zui_text(&a->ui,x,y,s,length(s),font,color);}
static int valid(const struct zenith_appearance *s){
    if(s->version!=1||s->flags&~15U||!s->revision||s->mode>1||s->active_mode>1||s->accent>5||s->active_accent>5||s->reserved[0]||s->reserved[1])return 0;
    for(int m=0;m<2;m++)for(int c=0;c<6;c++)for(int i=0;i<8;i++)if(s->palette[m][c][i]>0xffffff)return 0;
    return 1;
}
static void palette(struct settings_app *a){
    if(a->have_state){
        const unsigned int *p=a->state.palette[a->mode][a->accent];
        a->ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};
    }
    a->ui.dirty=1;
}
void settings_refresh(struct settings_app *a){
    struct zenith_appearance s={0};long result=a->backend.read(&s);
    if(result||!valid(&s)){a->message="Settings service unavailable. Edits have been kept.";a->ui.dirty=1;return;}
    int changed=!a->have_state||s.revision!=a->state.revision||s.active_revision!=a->state.active_revision||s.resources!=a->state.resources||s.flags!=a->state.flags;
    if(!a->edited){a->mode=s.mode;a->accent=s.accent;a->base_revision=s.revision;}
    else if(s.revision!=a->base_revision)a->message="Changed elsewhere. Reload before saving; your edits are kept.";
    a->state=s;a->have_state=1;if(changed)palette(a);
}
int settings_init(struct settings_app *a,unsigned int *pixels,struct settings_backend backend){
    *a=(struct settings_app){0};a->backend=backend;
    if(!backend.read||!backend.save||!backend.dirty||!zui_init(&a->ui,pixels,600,480))return 0;
    settings_refresh(a);
    if(!a->have_state)return 0;
    zui_add(&a->ui,1,ZUI_CHOICE,(struct zui_rect){24,104,112,36},"Dark",0,1);
    zui_add(&a->ui,2,ZUI_CHOICE,(struct zui_rect){148,104,112,36},"Light",0,1);
    const char *names[]={"Blue","Violet","Rose","Amber","Green","Teal"};
    for(int i=0;i<6;i++)zui_add(&a->ui,10+i,ZUI_CHOICE,(struct zui_rect){24+i*92,184,80,36},names[i],0,1);
    zui_add(&a->ui,30,ZUI_BUTTON,(struct zui_rect){24,420,148,36},"Save changes",0,1);
    zui_add(&a->ui,31,ZUI_BUTTON,(struct zui_rect){188,420,112,36},"Reload",0,1);
    zui_add(&a->ui,40,ZUI_BUTTON,(struct zui_rect){316,420,112,36},"Close",0,1);
    zui_add(&a->ui,90,ZUI_BUTTON,(struct zui_rect){24,420,148,36},"Keep editing",0,0);
    zui_add(&a->ui,91,ZUI_BUTTON,(struct zui_rect){188,420,180,36},"Discard changes",0,0);
    return 1;
}
void settings_draw(struct settings_app *a){
    struct zui *u=&a->ui;zui_clip(u,(struct zui_rect){0,0,600,480});zui_fill(u,u->clip,u->theme.surface);
    text(a,24,16,"Appearance",&zui_heading,u->theme.text);
    text(a,24,51,"System-wide settings. Preview changes before saving.",&zui_body,u->theme.secondary);
    text(a,24,79,"Color mode",&zui_label,u->theme.text);
    text(a,24,157,"Accent color",&zui_label,u->theme.text);
    text(a,24,244,"Transparency and blur",&zui_label,u->theme.text);
    text(a,24,269,"Unavailable until the compositor supports these effects.",&zui_body,u->theme.secondary);
    const char *scope=!(a->state.flags&Z_SETTINGS_WRITABLE)?"Read only: sign in as an administrator to save.":
        a->state.flags&Z_SETTINGS_PERSISTENT?"Installed system: saved changes survive reboot.":"Live system: changes will be lost at reboot.";
    text(a,24,306,scope,&zui_caption,u->theme.secondary);
    const char *status=a->edited?"Unsaved changes - showing your preview.":
        a->state.flags&Z_SETTINGS_PENDING?"Saved. Waiting for the desktop or next login to apply.":"Saved appearance is active.";
    text(a,24,332,status,&zui_body,u->theme.text);
    if(a->message)text(a,24,360,a->message,&zui_caption,u->theme.secondary);
    if(a->prompt)text(a,24,389,a->prompt==1?"Discard unsaved changes and close?":"Discard unsaved changes and reload?",&zui_label,u->theme.text);
    for(int i=0;i<u->count;i++){
        struct zui_control *c=&u->controls[i];
        c->enabled=a->prompt?c->id>=90:c->id<90;
        if(c->id<30){c->enabled=c->enabled&&!!(a->state.flags&Z_SETTINGS_WRITABLE);c->value=c->id<10?c->id==(int)a->mode+1:c->id==(int)a->accent+10;}
        if(c->id==30){c->enabled=c->enabled&&a->edited&&!!(a->state.flags&Z_SETTINGS_WRITABLE);c->value=1;}
    }
    zui_draw_range(u,a->prompt?11:0,a->prompt?2:11);
    u->dirty=0;
}
static void close_request(struct settings_app *a,int prompt){
    if(a->edited){a->prompt=prompt;a->ui.focus=11;a->ui.pressed=-1;a->ui.dirty=1;}
    else if(prompt==1)a->closing=1;
    else {a->message=0;settings_refresh(a);}
}
void settings_event(struct settings_app *a,const struct zenith_gui_event *e){
    if(e->type==Z_GUI_CLOSE){a->closing=1;return;}
    if(e->type==Z_GUI_CLOSE_INTENT){close_request(a,1);return;}
    if(e->type==Z_GUI_KEY&&e->extra==1){if(a->prompt){a->prompt=0;a->ui.focus=0;a->ui.dirty=1;}else close_request(a,1);return;}
    int action=zui_event(&a->ui,e);
    if(!action)return;
    if(action==90){a->prompt=0;a->ui.focus=0;a->ui.dirty=1;return;}
    if(action==91){
        if(a->backend.dirty(0)){a->message="Cannot update window state. Edits have been kept.";return;}
        int prompt=a->prompt;a->edited=0;a->prompt=0;
        if(prompt==1)a->closing=1;
        else {a->message=0;settings_refresh(a);}a->ui.dirty=1;return;
    }
    if(action==40){close_request(a,1);return;}
    if(action==31){close_request(a,2);return;}
    if(action==30){
        long result=a->backend.save(a->mode,a->accent,a->base_revision);
        if(!result){a->edited=0;a->backend.dirty(0);a->message="Appearance saved.";settings_refresh(a);}
        else a->message=result==-11?"Changed elsewhere. Reload before saving; your edits are kept.":result==-13?"Administrator permission required. Edits have been kept.":"Save failed. Check writable storage; edits have been kept.";
        a->ui.dirty=1;return;
    }
    unsigned int mode=a->mode,accent=a->accent;
    if(action<=2)mode=(unsigned int)action-1;
    else if(action>=10&&action<=15)accent=(unsigned int)action-10;
    int dirty=mode!=a->state.mode||accent!=a->state.accent;
    if(a->backend.dirty(dirty)){a->message="Cannot protect unsaved changes. Selection was not changed.";a->ui.dirty=1;return;}
    a->mode=mode;a->accent=accent;a->edited=dirty;a->message=0;palette(a);
}
