#include "meridian.h"
enum { PENDING_NONE=0,PENDING_CLOSE=1,PENDING_ROW=2,PENDING_PAGE=3 };
enum { ID_ROWS=100,ID_FOOTER=900,ID_DIALOG=950,ID_SEARCH=1002,ID_COMBO=2004,ID_COMBO_ROWS=2100,ID_LINKS=3000 };
static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void text(struct zui *u,int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(u,x,y,s,length(s),f,c);}
static int sidebar_width(const struct meridian *a){return a->ui.width>=720?240:200;}
static int page_kind(const struct meridian *a){return binding_of(meridian_pages[a->page].id);}
static unsigned int information_kind(const struct meridian *a){
    const char *id=meridian_pages[a->page].id;
    return same(id,"network.connections")?1:same(id,"system.users")?2:0;
}
static void refresh_information(struct meridian *a){
    unsigned int kind=information_kind(a);if(!kind||!a->information_read)return;
    struct zenith_settings_information state;long r=a->information_read(kind,&state);
    if(r||state.version!=1||state.kind!=kind||state.length>=sizeof(state.text)||state.truncated>1){a->public_error=1;return;}
    int changed=a->public_kind!=kind||a->public_error;
    for(unsigned int i=0;i<state.length;i++){if(a->public_information[i]!=state.text[i])changed=1;a->public_information[i]=state.text[i];}
    if(a->public_information[state.length])changed=1;
    a->public_information[state.length]=0;a->public_kind=kind;a->public_error=0;if(changed)a->ui.dirty=1;
}
/* The draft palette follows the colors model; repaint only when it really changed. */
static void sync_theme(struct meridian *a){
    struct zui *u=&a->ui;struct zui_theme theme=u->theme;unsigned int swatches[6];int background=u->full_background,dirty=u->dirty;
    for(int i=0;i<6;i++)swatches[i]=u->swatches[i];
    settings_theme(&a->colors,u);
    int same_theme=background==u->full_background;
    const unsigned int *x=(const unsigned int *)&theme,*y=(const unsigned int *)&u->theme;
    for(unsigned int i=0;i<sizeof(theme)/sizeof(unsigned int);i++)if(x[i]!=y[i])same_theme=0;
    for(int i=0;i<6;i++)if(swatches[i]!=u->swatches[i])same_theme=0;
    u->dirty=dirty||!same_theme||a->colors.ui.dirty;a->colors.ui.dirty=0;
}
void meridian_information_services(struct meridian *a,long (*read)(unsigned int,struct zenith_settings_information *)){a->information_read=read;refresh_information(a);}
void meridian_system_services(struct meridian *a,long (*read)(struct zenith_system_settings *),long (*save)(unsigned int,unsigned int,unsigned long)){
    a->bindings.read=read;a->bindings.save=save;binding_refresh(&a->bindings);
}
struct zui *meridian_ui(struct meridian *a){return &a->ui;}
static void remember(struct meridian *a,int page){
    int i=0;while(i<a->recent_count&&a->recent[i]!=page)i++;
    if(i==a->recent_count&&i<6)a->recent_count++;
    if(i>5)i=5;
    for(;i>0;i--)a->recent[i]=a->recent[i-1];
    a->recent[0]=page;
}
/* Adopt a navigation state; a new page starts at its top with fresh transient state. */
static void adopt(struct meridian *a,const struct meridian_nav *next){
    int changed=next->page!=a->page;
    a->nav=*next;a->page=next->page;
    if(a->nav.mode!=NAV_SEARCH&&a->query[0]){a->query[0]=0;zui_input_init(&a->search,a->query,sizeof(a->query));}
    if(changed){
        a->page_scroll.offset=0;a->combo.open=0;a->ui.focus=a->ui.pressed=-1;
        remember(a,a->page);refresh_information(a);
    }
    a->ui.dirty=1;
}
static void clear_search(struct meridian *a){
    a->query[0]=0;zui_input_init(&a->search,a->query,sizeof(a->query));nav_search(&a->nav,"");
    a->search_focus=0;a->sidebar.scroll.offset=0;a->ui.dirty=1;
}
int meridian_open(struct meridian *a,const char *id){
    if(binding_any_changed(&a->bindings))return 0;
    struct meridian_nav next=a->nav;if(!nav_open(&next,id))return 0;
    adopt(a,&next);a->sidebar.scroll.offset=0;return 1;
}
int meridian_resize(struct meridian *a,int w,int h,unsigned long capacity){
    if(w<600||h<480||!zui_resize(&a->ui,w,h,capacity))return 0;
    a->combo.open=0;return 1;
}
int meridian_init(struct meridian *a,unsigned int *pixels,struct settings_backend backend,const char *page){
    *a=(struct meridian){0};
    if(!settings_init(&a->colors,pixels,backend)||!zui_init(&a->ui,pixels,600,480)||!zui_input_init(&a->search,a->query,sizeof(a->query)))return 0;
    binding_init(&a->bindings,&a->colors,0,0,backend.dirty);
    nav_init(&a->nav,0);a->sidebar.base_id=ID_ROWS;a->combo.id=ID_COMBO;a->combo.popup.base_id=ID_COMBO_ROWS;
    remember(a,0);
    if(page&&!meridian_open(a,page))return 0;
    sync_theme(a);return 1;
}
void meridian_refresh(struct meridian *a){
    unsigned long rev=a->colors.state.revision,res=a->colors.state.resources,pref=a->bindings.preferences.revision;unsigned int flags=a->colors.state.flags;
    int ready=a->bindings.ready;
    refresh_information(a);binding_refresh(&a->bindings);settings_refresh(&a->colors);sync_theme(a);
    if(rev!=a->colors.state.revision||res!=a->colors.state.resources||flags!=a->colors.state.flags||pref!=a->bindings.preferences.revision||ready!=a->bindings.ready)a->ui.dirty=1;
}
static int sidebar_rows(void *c,int i,struct zui_list_item *o){return nav_rows(&((struct meridian *)c)->nav,i,o);}
static int choice_rows(void *c,int i,struct zui_list_item *o){
    struct meridian *a=c;int kind=page_kind(a);
    if(i<0||i>=binding_choice_count(&a->bindings,kind))return 0;
    *o=(struct zui_list_item){0,binding_choice_name(&a->bindings,kind,(unsigned int)i),0,0,1};return 1;
}
static struct zui_rect search_rect(struct zui_rect s){return (struct zui_rect){s.x+12,s.y+8,s.w-24,34};}
static void draw_sidebar(struct meridian *a,struct zui *u,struct zui_rect s){
    zui_add(u,ID_SEARCH,ZUI_BUTTON,search_rect(s),"",0,1);
    a->sidebar.selected=a->nav.category;
    if(a->nav.mode!=NAV_CATEGORIES)for(int i=1;i<nav_row_count(&a->nav);i++)if(nav_row_page(&a->nav,i)==a->page)a->sidebar.selected=i;
    zui_list_draw(u,&a->sidebar,(struct zui_rect){s.x,s.y+52,s.w,s.h-52},nav_row_count(&a->nav),sidebar_rows,a);
}
static const char *wip_note(const struct meridian *a,int kind){
    if(kind==BIND_COLORS)return "Work in progress: automatic switching and custom colors.";
    if(kind==BIND_KEYBOARD)return "Work in progress: model, repeat, Num Lock and shortcuts.";
    if(kind==BIND_TIMEZONE)return "Work in progress: clock editing and sync policy.";
    if(kind==BIND_SESSION)return "Work in progress: session restore and confirmation policy.";
    if(a->page==0)return "Work in progress: scheduled mode and persistent history.";
    if(a->page==MERIDIAN_ABOUT)return "Work in progress: hardware inventory is planned.";
    if(information_kind(a)==1)return "Work in progress: connection editing and profiles.";
    if(information_kind(a)==2)return "Work in progress: account creation, deletion and passwords.";
    return "Work in progress: this page needs additional system services. Nothing here can be changed yet.";
}
static void note(struct zui *u,struct zui_form *f,const char *s){
    int inner=f->w-24,lines=inner>0?zui_measure(s,length(s),&zui_caption)/inner+1:1,h=lines*19+16;
    struct zui_rect r={f->x,f->y,f->w,h};
    zui_surface(u,r,8,ZUI_SURFACE_CONTROL);
    zui_paragraph(u,(struct zui_rect){r.x+12,r.y+8,inner,h-12},s,&zui_caption,u->theme.secondary,0);
    f->y+=h+12;
}
static void status(struct zui *u,struct zui_form *f,const char *s){
    if(!s)return;
    zui_paragraph(u,(struct zui_rect){f->x,f->y,f->w,36},s,&zui_caption,u->theme.text,0);f->y+=40;
}
static void colors_form(struct meridian *a,struct zui *u,struct zui_form *f){
    struct settings_app *s=&a->colors;int writable=binding_writable(&a->bindings,BIND_COLORS),cw=f->narrow?f->w:f->w-f->label_w-16;
    zui_form_section(u,f,"Appearance");
    struct zui_rect r=zui_form_row(u,f,"Color mode",0,272,32);
    zui_segment_track(u,(struct zui_rect){r.x-4,r.y-4,r.w+8,r.h+8});
    zui_add(u,1,ZUI_SEGMENT,(struct zui_rect){r.x,r.y,r.w/2,32},"Dark",s->mode==0,writable);
    zui_add(u,2,ZUI_SEGMENT,(struct zui_rect){r.x+r.w/2,r.y,r.w/2,32},"Light",s->mode==1,writable);
    r=zui_form_row(u,f,"Accent color",0,264,54);
    static const char *const names[6]={"Blue","Violet","Rose","Amber","Green","Teal"};
    for(int i=0;i<6;i++)zui_add_swatch(u,10+i,(struct zui_rect){r.x+i*44,r.y,32,32},names[i],i,s->accent==(unsigned int)i,writable);
    zui_swatch_caption(u,r.x,r.y+38);
    zui_form_section(u,f,"Effects");
    static const char *const effect_names[3]={"Transparency","Backdrop blur","Whole app"};
    const char *help[3]={"Let panels reveal the background.",
        !s->available_effects?"Unavailable here; saved choices are kept.":!(s->effects&1)&&(s->effects&2)?"Saved; enable transparency to activate it.":s->material_reason>=3?"Some surfaces fall back for contrast or speed.":"Diffuse the backdrop; controls remain clear.",
        !(s->effects&1)&&(s->effects&4)?"Saved; enable transparency to activate it.":"Extend effects to the app canvas."};
    for(int i=0;i<3;i++){
        r=zui_form_row(u,f,effect_names[i],help[i],cw<300?cw:300,28);
        zui_add(u,50+i,ZUI_TOGGLE,r,"",!!(s->effects&(1U<<i)),writable&&!!(s->available_effects&(1U<<i)));
    }
    r=zui_form_row(u,f,"Translucency",0,152,32);
    int level_enabled=writable&&!!s->available_effects;
    zui_add(u,53,ZUI_BUTTON,(struct zui_rect){r.x,r.y,32,32},"-",0,level_enabled);
    zui_add(u,54,ZUI_BUTTON,(struct zui_rect){r.x+r.w-32,r.y,32,32},"+",0,level_enabled);
    char percent[5];unsigned int n=s->translucency>100?100:s->translucency;int k=0;
    if(n>=100)percent[k++]='1';
    if(n>=10)percent[k++]=(char)('0'+n/10%10);
    percent[k++]=(char)('0'+n%10);percent[k++]='%';percent[k]=0;
    text(u,r.x+60,r.y+8,percent,&zui_label,u->theme.text);
    if(s->has_scopes){
        zui_form_section(u,f,"Scope");
        r=zui_form_row(u,f,"Applies to",0,272,32);
        zui_segment_track(u,(struct zui_rect){r.x-4,r.y-4,r.w+8,r.h+8});
        zui_add(u,60,ZUI_SEGMENT,(struct zui_rect){r.x,r.y,r.w/2,32},"Personal",s->scope==1,!s->edited);
        zui_add(u,61,ZUI_SEGMENT,(struct zui_rect){r.x+r.w/2,r.y,r.w/2,32},"System",s->scope==0,!s->edited);
    }
    const char *where=s->has_scopes&&s->scope?(s->inherited?"Personal: following system defaults.":"Personal override for this account."):
        !(s->state.flags&Z_SETTINGS_WRITABLE)?"Read only: sign in as an administrator to save.":
        s->state.flags&Z_SETTINGS_PERSISTENT?"Installed system: applied changes survive reboot.":"Live system: changes will be lost at reboot.";
    const char *state=s->edited?"Unapplied changes - showing your preview.":s->state.flags&Z_SETTINGS_PENDING?"Applied. Waiting for the desktop or next login.":"Applied appearance is active.";
    if(s->has_scopes&&!s->scope&&!s->inherited)state="System defaults applied; personal overrides are active.";
    f->y+=8;
    text(u,f->x,f->y,where,&zui_caption,u->theme.secondary);f->y+=20;
    text(u,f->x,f->y,s->message?s->message:state,&zui_caption,u->theme.text);f->y+=20;
}
static void preference_form(struct meridian *a,struct zui *u,struct zui_form *f,int kind){
    const struct meridian_bindings *b=&a->bindings;int writable=binding_writable(b,kind),count=binding_choice_count(b,kind);
    zui_form_section(u,f,"System default");
    struct zui_rect r=zui_form_row(u,f,kind==BIND_KEYBOARD?"Keyboard layout":kind==BIND_TIMEZONE?"Time zone":"Login session",
        writable?"Administrators change this for all users.":"Read only: sign in as an administrator.",280,36);
    if(count){
        if(!a->combo.open)a->combo.value=(int)binding_choice(b,kind);
        zui_combo_draw(u,&a->combo,r,count,choice_rows,a);
        int field=-1;for(int i=0;i<u->count;i++)if(u->controls[i].id==ID_COMBO)field=i;
        if(field>=0){
            u->controls[field].enabled=writable;
            if(binding_choice(b,kind)>=(unsigned int)count)u->controls[field].label=kind==BIND_TIMEZONE?b->preferences.current_zone:b->preferences.current_session;
        }
    }else text(u,r.x,r.y+10,"Unavailable from the settings service.",&zui_body,u->theme.secondary);
    status(u,f,b->message?b->message:binding_changed(b,kind)?"Unapplied change.":kind==BIND_KEYBOARD?"Applies to keyboard input across sessions immediately.":
        kind==BIND_TIMEZONE?"Changes local time display; the UTC clock is unchanged.":"Applies on next boot; the running session is unchanged.");
}
static void lines_form(struct zui *u,struct zui_form *f,const char *s){
    while(*s){
        unsigned long n=0;while(s[n]&&s[n]!='\n'&&n<4096)n++;
        if(n)zui_text(u,f->x,f->y,s,n,&zui_body,u->theme.secondary);
        f->y+=22;s+=n;if(*s)s++;
    }
}
static void link_row(struct zui *u,struct zui_form *f,int page,const char *label){
    zui_add(u,ID_LINKS+page,ZUI_NAVIGATION,(struct zui_rect){f->x,f->y,f->w,36},label,0,1);f->y+=42;
}
static void overview_form(struct meridian *a,struct zui *u,struct zui_form *f){
    zui_form_section(u,f,"Appearance");
    link_row(u,f,MERIDIAN_COLORS,"Colors and materials");
    for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(same(meridian_pages[i].id,"style.wallpaper"))link_row(u,f,i,"Wallpaper");
    int shown=0;
    for(int i=0;i<a->recent_count&&shown<3;i++)if(a->recent[i]!=0&&a->recent[i]!=MERIDIAN_COLORS&&!same(meridian_pages[a->recent[i]].id,"style.wallpaper")){
        if(!shown)zui_form_section(u,f,"Recently visited");
        link_row(u,f,a->recent[i],meridian_pages[a->recent[i]].title);shown++;
    }
}
static void planned_form(struct meridian *a,struct zui *u,struct zui_form *f){
    zui_form_section(u,f,"Planned options");
    const char *s=meridian_pages[a->page].requirements;
    while(*s){
        unsigned long n=0;while(s[n]&&s[n]!='\n')n++;
        if(n>2&&s[0]=='-'&&s[1]==' '){
            zui_text(u,f->x,f->y,s+2,n-2,&zui_body,u->theme.text);f->y+=20;
            text(u,f->x,f->y,"Not available yet",&zui_caption,u->theme.secondary);f->y+=26;
        }
        s+=n;if(*s)s++;
    }
}
static void draw_page(struct meridian *a,struct zui *u,struct zui_rect c){
    const struct meridian_page *p=&meridian_pages[a->page];int kind=page_kind(a),footer=kind!=BIND_NONE?56:0,x=c.x+24;
    text(u,x,c.y+14,p->title,&zui_heading,u->theme.text);
    text(u,x,c.y+50,meridian_categories[nav_category_of(a->page)],&zui_caption,u->theme.secondary);
    struct zui_rect body={c.x,c.y+74,c.w,c.h-74-footer};
    int top=zui_scroll_begin(u,&a->page_scroll,body);
    struct zui_form f;zui_form_begin(&f,(struct zui_rect){body.x+16,body.y,body.w-32,body.h},top+8,u->width);
    note(u,&f,wip_note(a,kind));
    if(kind==BIND_COLORS)colors_form(a,u,&f);
    else if(kind!=BIND_NONE)preference_form(a,u,&f,kind);
    else if(a->page==0)overview_form(a,u,&f);
    else if(information_kind(a))lines_form(u,&f,a->information_read&&a->public_kind==information_kind(a)&&!a->public_error?a->public_information:"Information service unavailable.");
    else if(a->page==MERIDIAN_ABOUT&&a->information[0])lines_form(u,&f,a->information);
    else planned_form(a,u,&f);
    zui_scroll_end(u,&a->page_scroll,zui_form_height(&f,top)+8);
    if(footer)zui_footer(u,(struct zui_rect){c.x,c.y+c.h-56,c.w,56},ID_FOOTER,0,binding_changed(&a->bindings,kind),binding_at_defaults(&a->bindings,kind));
}
/* Indices change every frame: restore focus and press by control id. Dialog buttons
 * are registered last in reverse order, so their index follows from the page count. */
static int index_of(const struct zui *u,int id){
    for(int i=0;i<u->count;i++)if(u->controls[i].id==id&&u->controls[i].enabled)return i;
    return -1;
}
void meridian_draw(struct meridian *a){
    struct zui *u=&a->ui;
    int focus=u->focus>=0&&u->focus<u->count?u->controls[u->focus].id:0,pressed=u->pressed>=0&&u->pressed<u->count?u->controls[u->pressed].id:0;
    u->count=0;
    struct zui_workspace w=zui_workspace_begin(u,sidebar_width(a));
    draw_sidebar(a,u,w.sidebar);
    draw_page(a,u,w.content);
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});
    u->focus=focus?index_of(u,focus):-1;u->pressed=pressed?index_of(u,pressed):-1;
    zui_draw_controls(u);
    struct zui_rect r=search_rect(w.sidebar);
    zui_input_draw(u,r,&a->search,a->search_focus&&u->window_focused);
    if(!a->query[0]&&!a->search_focus)text(u,r.x+10,r.y+9,"Search settings",&zui_caption,u->theme.secondary);
    int kind=page_kind(a);
    if(kind>=BIND_KEYBOARD&&a->combo.open)zui_combo_popup(u,&a->combo,binding_choice_count(&a->bindings,kind),choice_rows,a);
    if(a->dialog.open){
        int base=u->count,top=ID_DIALOG+a->dialog.count;
        if(focus>ID_DIALOG&&focus<=top)u->focus=base+top-focus;
        if(pressed>ID_DIALOG&&pressed<=top)u->pressed=base+top-pressed;
        zui_dialog_draw(u,&a->dialog);
    }
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});u->dirty=0;
}
static void confirm(struct meridian *a,int pending){
    a->pending=pending;a->combo.open=0;
    a->dialog=(struct zui_dialog){1,ID_DIALOG,"Apply changes?","This page has unapplied changes.",{"Apply","Discard","Cancel"},3};
    a->ui.pressed=-1;a->ui.dirty=1;
}
static void request_row(struct meridian *a,int row){
    struct meridian_nav next=a->nav;nav_activate(&next,row);
    if(next.page!=a->page&&binding_changed(&a->bindings,page_kind(a))){a->pending_row=row;confirm(a,PENDING_ROW);return;}
    if(next.mode!=a->nav.mode||next.category!=a->nav.category)a->sidebar.scroll.offset=0;
    adopt(a,&next);
}
static void request_page(struct meridian *a,int page){
    if(page!=a->page&&binding_changed(&a->bindings,page_kind(a))){a->pending_page=page;confirm(a,PENDING_PAGE);return;}
    struct meridian_nav next=a->nav;nav_open(&next,meridian_pages[page].id);
    a->sidebar.scroll.offset=0;adopt(a,&next);
}
static void close_request(struct meridian *a){
    if(binding_any_changed(&a->bindings))confirm(a,PENDING_CLOSE);else a->closing=1;
}
static void resolve(struct meridian *a,int button){
    int pending=a->pending,kind=page_kind(a);a->pending=PENDING_NONE;a->ui.dirty=1;
    if(button==3)return;
    if(button==1&&binding_apply(&a->bindings,kind))return;
    if(button==2)binding_reset(&a->bindings,kind);
    if(pending==PENDING_CLOSE)a->closing=1;
    else if(pending==PENDING_ROW)request_row(a,a->pending_row);
    else if(pending==PENDING_PAGE)request_page(a,a->pending_page);
}
static void back(struct meridian *a){
    if(a->nav.mode==NAV_SEARCH)clear_search(a);
    else if(a->nav.mode==NAV_PAGES){nav_back(&a->nav);a->sidebar.scroll.offset=0;}
    a->ui.dirty=1;
}
static void colors_action(struct meridian *a,int id){
    struct settings_app *s=&a->colors;unsigned int mode=s->mode,accent=s->accent,effects=s->effects,level=s->translucency;
    if(id==60||id==61){settings_set_scope(s,id==60);return;}
    if(id==1||id==2)mode=(unsigned int)id-1;
    else if(id>=10&&id<=15)accent=(unsigned int)id-10;
    else if(id>=50&&id<=52)effects^=1U<<(id-50);
    else if(id==53)level=level<10?0:level-10;
    else if(id==54)level=level>90?100:level+10;
    else return;
    settings_preview(s,mode,accent,effects,level);
}
static void page_keys(struct meridian *a,const struct zenith_gui_event *e){
    int max=zui_scroll_maximum(&a->page_scroll),next=a->page_scroll.offset;
    if(e->extra==7)next=zui_scroll_step(next,-40,max);
    else if(e->extra==8)next=zui_scroll_step(next,40,max);
    else if(e->extra==10)next=0;
    else if(e->extra==11)next=max;
    if(next!=a->page_scroll.offset){a->page_scroll.offset=next;a->ui.dirty=1;}
}
void meridian_event(struct meridian *a,const struct zenith_gui_event *e){
    struct zui *u=&a->ui;
    if(e->type==Z_GUI_FOCUS||e->type==Z_GUI_RESET){zui_event(u,e);a->combo.open=0;a->sidebar.scroll.dragging=a->page_scroll.dragging=0;return;}
    if(e->type==Z_GUI_CLOSE){a->closing=1;return;}
    if(!u->window_focused&&e->type!=Z_GUI_CLOSE_INTENT)return;
    if(a->dialog.open){int button=zui_dialog_event(u,&a->dialog,e);if(button)resolve(a,button);sync_theme(a);return;}
    if(e->type==Z_GUI_CLOSE_INTENT||(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='q'||e->code=='Q'))){close_request(a);return;}
    int kind=page_kind(a);
    if(kind>=BIND_KEYBOARD&&binding_writable(&a->bindings,kind)){
        int was=a->combo.open,chosen=zui_combo_event(u,&a->combo,e,choice_rows,a);
        if(chosen)binding_choose(&a->bindings,kind,(unsigned int)chosen-1);
        if(was||a->combo.open){if(a->combo.open!=was)u->pressed=-1;u->dirty=1;return;}
    }
    if(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='f'||e->code=='F')){
        a->search_focus=1;u->dirty=1;return;
    }
    if(a->search_focus&&e->type==Z_GUI_KEY&&e->extra!=4){
        if(e->extra==1)clear_search(a);
        else if(e->extra==2){a->search_focus=0;if(a->nav.mode==NAV_SEARCH&&a->nav.result_count)request_row(a,1);}
        else if(zui_input_event(&a->search,e)){nav_search(&a->nav,a->query);a->sidebar.scroll.offset=0;}
        u->dirty=1;sync_theme(a);return;
    }
    if(e->type==Z_GUI_KEY&&e->extra==4)a->search_focus=0;
    if(e->type==Z_GUI_DOWN)a->search_focus=0;
    if(e->type==Z_GUI_KEY&&(e->extra==1||(e->modifiers==4&&e->extra==5)||(!e->modifiers&&e->extra==3))){back(a);return;}
    int focused=u->focus>=0&&u->focus<u->count?u->controls[u->focus].id:0,on_rows=focused>=ID_ROWS&&focused<ID_ROWS+MERIDIAN_PAGE_COUNT+1;
    if(e->type!=Z_GUI_KEY||on_rows){
        int row=zui_list_event(u,&a->sidebar,e,sidebar_rows,a);
        if(row){u->pressed=-1;request_row(a,row-1);sync_theme(a);return;}
        if(on_rows&&e->type==Z_GUI_KEY&&(e->extra==7||e->extra==8||e->extra==10||e->extra==11)){u->dirty=1;return;}
    }
    if(zui_scroll_event(u,&a->page_scroll,e))return;
    if(e->type==Z_GUI_KEY&&!e->modifiers&&!on_rows)page_keys(a,e);
    int id=zui_event(u,e);
    if(id==ID_SEARCH){a->search_focus=1;u->dirty=1;}
    else if(id==ID_FOOTER+ZUI_FOOTER_APPLY)binding_apply(&a->bindings,kind);
    else if(id==ID_FOOTER+ZUI_FOOTER_RESET)binding_reset(&a->bindings,kind);
    else if(id==ID_FOOTER+ZUI_FOOTER_DEFAULTS)binding_defaults(&a->bindings,kind);
    else if(id>=ID_LINKS&&id<ID_LINKS+MERIDIAN_PAGE_COUNT)request_page(a,id-ID_LINKS);
    else if(id&&kind==BIND_COLORS)colors_action(a,id);
    if(id)u->dirty=1;
    sync_theme(a);
}
