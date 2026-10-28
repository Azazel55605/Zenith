#include "app.h"
static unsigned long length(const char *s){unsigned long n=0;while(s[n])n++;return n;}
static void text(struct settings_app *a,int x,int y,const char *s,const struct zui_font *font,unsigned int color){zui_text(&a->ui,a->viewport.x+x-8,a->viewport.y+y,s,length(s),font,color);}
static int valid(const struct zenith_appearance *s){
    if(s->version!=1||s->flags&~15U||!s->revision||s->mode>1||s->active_mode>1||s->accent>5||s->active_accent>5||s->reserved[0]||s->reserved[1])return 0;
    for(int m=0;m<2;m++)for(int c=0;c<6;c++)for(int i=0;i<8;i++)if(s->palette[m][c][i]>0xffffff)return 0;
    return 1;
}
static void palette(struct settings_app *a){
    if(a->have_state){
        const unsigned int *p=a->state.palette[a->mode][a->accent];
        for(int i=0;i<6;i++)a->ui.swatches[i]=a->state.palette[a->mode][i][6];
        a->ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};
    }
    a->ui.full_background=(a->effects&5)==5;
    a->ui.dirty=1;
}
void settings_refresh(struct settings_app *a){
    struct zenith_appearance s={0};unsigned int saved=0,effective=0,available=0,reason=0,level=40;long result=a->backend.read(&s,&saved,&effective,&available,&reason,&level);
    if(result||level>100||!valid(&s)||saved>7||effective>7||available>7||reason>5){a->message="Settings service unavailable. Edits have been kept.";a->ui.dirty=1;return;}
    int changed=effective!=a->effective_effects||available!=a->available_effects||reason!=a->material_reason||!a->have_state||s.revision!=a->state.revision||s.active_revision!=a->state.active_revision||s.resources!=a->state.resources||s.flags!=a->state.flags;
    if(!a->edited){a->translucency=level;a->effects=saved;a->mode=s.mode;a->accent=s.accent;a->base_revision=s.revision;}
    else if(s.revision!=a->base_revision)a->message="Changed elsewhere. Reload before saving; your edits are kept.";
    a->saved_translucency=level;a->saved_effects=saved;a->effective_effects=effective;a->available_effects=available;a->material_reason=reason;a->state=s;a->have_state=1;if(changed)palette(a);
}
int settings_init(struct settings_app *a,unsigned int *pixels,struct settings_backend backend){
    *a=(struct settings_app){0};a->backend=backend;
    if(!backend.read||!backend.save||!backend.dirty||!zui_init(&a->ui,pixels,600,480))return 0;
    a->viewport=(struct zui_rect){8,0,592,480};
    settings_refresh(a);
    if(!a->have_state)return 0;
    zui_add(&a->ui,1,ZUI_SEGMENT,(struct zui_rect){28,108,136,28},"Dark",0,1);
    zui_add(&a->ui,2,ZUI_SEGMENT,(struct zui_rect){164,108,136,28},"Light",0,1);
    const char *names[]={"Blue","Violet","Rose","Amber","Green","Teal"};
    for(int i=0;i<6;i++)zui_add_swatch(&a->ui,10+i,(struct zui_rect){24+i*44,184,32,32},names[i],i,0,1);
    zui_add(&a->ui,30,ZUI_BUTTON,(struct zui_rect){24,420,148,36},"Save changes",0,1);
    zui_add(&a->ui,31,ZUI_BUTTON,(struct zui_rect){188,420,112,36},"Reload",0,1);
    zui_add(&a->ui,40,ZUI_BUTTON,(struct zui_rect){316,420,112,36},"Close",0,1);
    zui_add(&a->ui,90,ZUI_BUTTON,(struct zui_rect){24,420,148,36},"Keep editing",0,0);
    zui_add(&a->ui,91,ZUI_BUTTON,(struct zui_rect){188,420,180,36},"Discard changes",0,0);
    zui_add(&a->ui,50,ZUI_TOGGLE,(struct zui_rect){24,242,552,26},"Transparent surfaces",0,0);
    zui_add(&a->ui,51,ZUI_TOGGLE,(struct zui_rect){24,292,552,26},"Backdrop blur",0,0);
    zui_add(&a->ui,52,ZUI_TOGGLE,(struct zui_rect){24,336,552,26},"Whole app background",0,0);
    zui_add(&a->ui,53,ZUI_BUTTON,(struct zui_rect){1,1,32,26},"-",0,1);
    zui_add(&a->ui,54,ZUI_BUTTON,(struct zui_rect){1,1,32,26},"+",0,1);
    settings_set_viewport(a,a->viewport);return 1;
}
void settings_set_viewport(struct settings_app *a,struct zui_rect r){
    a->viewport=r;int x=r.x+16,y=r.y,bottom=r.y+r.h;
    a->ui.controls[0].rect=(struct zui_rect){x+4,y+96,136,28};
    a->ui.controls[1].rect=(struct zui_rect){x+140,y+96,136,28};
    for(int i=0;i<6;i++)a->ui.controls[2+i].rect=(struct zui_rect){x+i*44,y+164,32,32};
    a->ui.controls[8].rect=(struct zui_rect){x,bottom-48,108,36};
    a->ui.controls[9].rect=(struct zui_rect){x+120,bottom-48,88,36};
    a->ui.controls[10].rect=(struct zui_rect){x+220,bottom-48,88,36};
    a->ui.controls[11].rect=(struct zui_rect){x,bottom-48,124,36};
    a->ui.controls[12].rect=(struct zui_rect){x+136,bottom-48,180,36};
    a->ui.controls[13].rect=(struct zui_rect){x,y+224,r.w-32,26};
    a->ui.controls[14].rect=(struct zui_rect){x,y+264,r.w-32,26};
    a->ui.controls[15].rect=(struct zui_rect){x,y+304,r.w-32,26};
    a->ui.controls[16].rect=(struct zui_rect){x+240,y+346,32,26};
    a->ui.controls[17].rect=(struct zui_rect){x+280,y+346,32,26};
}
void settings_draw(struct settings_app *a){
    struct zui *u=&a->ui;struct zui_rect r=a->viewport;
    if(r.x>8)zui_workspace_begin(u,180);
    else {if(u->foreground)zui_begin_material_frame(u);zui_clip(u,(struct zui_rect){0,0,u->width,u->height});zui_surface(u,u->clip,0,ZUI_SURFACE_CONTENT);}
    zui_clip(u,r);
    text(a,24,16,"Appearance",&zui_heading,u->theme.text);
    text(a,24,75,"Color mode",&zui_label,u->theme.text);
    text(a,24,139,"Accent color",&zui_label,u->theme.text);
    zui_setting_row(u,(struct zui_rect){r.x+16,r.y+226,r.w-32,45},"","Let panels reveal the background.");
    zui_setting_row(u,(struct zui_rect){r.x+16,r.y+266,r.w-32,45},"",!a->available_effects?"Unavailable here; saved choices are kept.":!(a->effects&1)&&(a->effects&2)?"Saved; enable transparency to activate it.":a->material_reason>=3?"Some surfaces fall back for contrast or speed.":"Diffuse the backdrop; controls remain clear.");
    zui_setting_row(u,(struct zui_rect){r.x+16,r.y+306,r.w-32,45},"",!(a->effects&1)&&(a->effects&4)?"Saved; enable transparency to activate it.":"Extend effects to the app canvas.");
    const char *scope=!(a->state.flags&Z_SETTINGS_WRITABLE)?"Read only: sign in as an administrator to save.":
        a->state.flags&Z_SETTINGS_PERSISTENT?"Installed system: saved changes survive reboot.":"Live system: changes will be lost at reboot.";
    text(a,24,374,scope,&zui_caption,u->theme.secondary);
    const char *status=a->edited?"Unsaved changes - showing your preview.":
        a->state.flags&Z_SETTINGS_PENDING?"Saved. Waiting for the desktop or next login to apply.":"Saved appearance is active.";
    text(a,24,390,a->message&&!a->prompt?a->message:status,&zui_caption,u->theme.text);
    if(a->prompt)text(a,24,390,a->prompt==1?"Discard unsaved changes and close?":a->prompt==3?"Discard changes and return to settings?":"Discard unsaved changes and reload?",&zui_label,u->theme.text);
    for(int i=0;i<13;i++){
        struct zui_control *c=&u->controls[i];
        c->enabled=a->prompt?c->id>=90:c->id<90;
        if(c->id<30){c->enabled=c->enabled&&!!(a->state.flags&Z_SETTINGS_WRITABLE);c->value=c->id<10?c->id==(int)a->mode+1:c->id==(int)a->accent+10;}
        if(c->id==30){c->enabled=c->enabled&&a->edited&&!!(a->state.flags&Z_SETTINGS_WRITABLE);c->value=1;}
    }
    for(int i=13;i<16;i++){
        u->controls[i].enabled=!a->prompt&&!!(a->state.flags&Z_SETTINGS_WRITABLE)&&!!(a->available_effects&(1U<<(i-13)));
        u->controls[i].value=!!(a->effects&(1U<<(i-13)));
    }
    char percent[24]="Translucency: 000%";unsigned int n=a->translucency;percent[14]=n<100?' ':(char)('0'+n/100);percent[15]=n<10?' ':(char)('0'+n/10%10);percent[16]=(char)('0'+n%10);
    zui_text(u,r.x+16,r.y+350,percent,18,&zui_label,u->theme.text);
    for(int i=16;i<18;i++)u->controls[i].enabled=!a->prompt&&!!(a->state.flags&Z_SETTINGS_WRITABLE)&&!!a->available_effects;
    if(!a->prompt)zui_draw_range(u,16,2);
    zui_swatch_caption(u,r.x+16,r.y+204);
    zui_segment_track(u,(struct zui_rect){r.x+16,r.y+92,280,36});
    if(!a->prompt)zui_draw_range(u,13,3);
    zui_draw_range(u,a->prompt?11:0,a->prompt?2:11);
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});u->dirty=0;
}
static void close_request(struct settings_app *a,int prompt){
    if(a->edited){a->prompt=prompt;a->ui.focus=11;a->ui.pressed=-1;a->ui.dirty=1;}
    else if(prompt==1)a->closing=1;
    else {a->message=0;settings_refresh(a);}
}
void settings_request_navigation(struct settings_app *a){close_request(a,3);}
int settings_event(struct settings_app *a,const struct zenith_gui_event *e){
    if(e->type==Z_GUI_CLOSE){a->closing=1;return 0;}
    if(e->type==Z_GUI_CLOSE_INTENT){close_request(a,1);return 0;}
    if(e->type==Z_GUI_KEY&&e->extra==1){if(a->prompt){a->prompt=0;a->ui.focus=0;a->ui.dirty=1;}else close_request(a,1);return 0;}
    int action=zui_event(&a->ui,e);
    if(!action)return 0;
    if(action>=1000)return action;
    if(action==90){a->prompt=0;a->ui.focus=0;a->ui.dirty=1;return 0;}
    if(action==900){if(a->edited){close_request(a,3);return 0;}return 900;}
    if(action==91){
        if(a->backend.dirty(0)){a->message="Cannot update window state. Edits have been kept.";return 0;}
        int prompt=a->prompt;a->edited=0;a->prompt=0;
        if(prompt==1)a->closing=1;
        else {a->message=0;settings_refresh(a);a->ui.focus=0;}a->ui.dirty=1;return prompt==3?900:0;
    }
    if(action==40){close_request(a,1);return 0;}
    if(action==31){close_request(a,2);return 0;}
    if(action==30){
        long result=a->backend.save(a->mode,a->accent,a->effects,a->translucency,a->base_revision);
        if(!result){a->edited=0;a->backend.dirty(0);a->message="Appearance saved.";settings_refresh(a);}
        else a->message=result==-11?"Changed elsewhere. Reload before saving; your edits are kept.":result==-13?"Administrator permission required. Edits have been kept.":"Save failed. Check writable storage; edits have been kept.";
        a->ui.dirty=1;return 0;
    }
    unsigned int mode=a->mode,accent=a->accent,effects=a->effects,level=a->translucency;
    if(action<=2)mode=(unsigned int)action-1;
    else if(action>=10&&action<=15)accent=(unsigned int)action-10;
    if(action==50)effects^=1;
    if(action==51)effects^=2;
    if(action==52)effects^=4;
    if(action==53)level=level<10?0:level-10;
    if(action==54)level=level>90?100:level+10;
    int dirty=level!=a->saved_translucency||effects!=a->saved_effects||mode!=a->state.mode||accent!=a->state.accent;
    if(a->backend.dirty(dirty)){a->message="Cannot protect unsaved changes. Selection was not changed.";a->ui.dirty=1;return 0;}
    a->translucency=level;a->mode=mode;a->accent=accent;a->effects=effects;a->edited=dirty;a->message=0;palette(a);return 0;
}
