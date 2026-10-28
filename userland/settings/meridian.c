#include "meridian.h"
static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static char lower(char c){return c>='A'&&c<='Z'?(char)(c+32):c;}
static int contains(const char *s,const char *q){if(!*q)return 1;for(;*s;s++){const char *a=s,*b=q;while(*a&&*b&&lower(*a)==lower(*b)){a++;b++;}if(!*b)return 1;}return 0;}
static void text(struct zui *u,int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(u,x,y,s,length(s),f,c);}
static const char *categories[]={"Quick Settings","Input & Output","Connected Devices","Networking","Appearance & Style","Apps & Windows","Workspace","Security & Privacy","Language & Time","System"};
static const char *groups[]={"Mouse & Touchpad","Keyboard","Sound","Display & Monitor","Colors & Themes","Text & Fonts","Window Management"};
static unsigned int preference_field(struct meridian *a){
    if(a->page<0)return 0;
    return same(meridian_pages[a->page].id,"input.keyboard")?1:same(meridian_pages[a->page].id,"locale.time")?2:0;
}
static int bounded_name(const char *s,int capacity){for(int i=0;i<capacity;i++){if(!s[i])return i>0;if((unsigned char)s[i]<32||(unsigned char)s[i]>126)return 0;}return 0;}
static int preferences_valid(const struct zenith_system_settings *s){
    if(s->version!=1||s->flags&~3U||!s->revision||!s->keyboard_count||s->keyboard_count>8||!s->zone_count||s->zone_count>48||s->keyboard>=s->keyboard_count||
       (s->zone>=s->zone_count&&s->zone!=~0U)||!bounded_name(s->current_zone,64))return 0;
    for(unsigned int i=0;i<s->keyboard_count;i++)if(!bounded_name(s->keyboards[i],32))return 0;
    for(unsigned int i=0;i<s->zone_count;i++)if(!bounded_name(s->zones[i],64))return 0;
    return 1;
}
static void refresh_preferences(struct meridian *a){
    if(!a->preferences_read||!preference_field(a))return;
    struct zenith_system_settings s;long result=a->preferences_read(&s);
    if(result||!preferences_valid(&s)){a->preference_message="Settings service unavailable; edits kept.";a->preferences_ready=0;return;}
    unsigned int field=preference_field(a);
    if(!a->preference_edited){
        if(field!=a->preference_field)a->preference_message=0;
        if(field!=a->preference_field||s.revision!=a->preferences.revision)a->navigation.dirty=1;
        a->preference_field=field;a->preference_choice=field==1?s.keyboard:s.zone;a->preference_revision=s.revision;
    }else if(s.revision!=a->preference_revision)a->preference_message="Changed elsewhere. Discard edits to reload.";
    a->preferences=s;a->preferences_ready=1;
}
void meridian_system_services(struct meridian *a,long (*read)(struct zenith_system_settings *),long (*save)(unsigned int,unsigned int,unsigned long)){
    a->preferences_read=read;a->preferences_save=save;refresh_preferences(a);
}
static void preferences_draw(struct meridian *a,struct zui *u,struct zui_rect c){
    unsigned int field=preference_field(a);int writable=a->preferences_ready&&!!(a->preferences.flags&2);
    const char *choice=field==1?a->preferences.keyboards[a->preference_choice]:a->preference_choice<a->preferences.zone_count?a->preferences.zones[a->preference_choice]:a->preferences.current_zone;
    text(u,c.x+20,c.y+166,field==1?"System keyboard layout":"System time zone",&zui_label,u->theme.text);
    struct zui_rect value={c.x+20,c.y+196,c.w-40,42};zui_surface(u,value,8,ZUI_SURFACE_CONTROL);
    text(u,value.x+10,value.y+12,choice,&zui_body,u->theme.text);
    text(u,c.x+20,c.y+306,writable?"System default - administrator changes":"System default - read only",&zui_caption,u->theme.secondary);
    zui_paragraph(u,(struct zui_rect){c.x+20,c.y+338,c.w-40,66},a->preference_message?a->preference_message:a->preference_edited?"Unsaved changes. Save or discard before navigating.":field==1?"Applies to keyboard input across sessions immediately.":"Changes local time display; the UTC clock is unchanged.",&zui_caption,u->theme.secondary,0);
    if(a->preference_prompt){
        zui_add(u,2010,ZUI_BUTTON,(struct zui_rect){c.x+20,c.y+c.h-48,126,34},"Keep editing",0,1);
        zui_add(u,2011,ZUI_BUTTON,(struct zui_rect){c.x+156,c.y+c.h-48,182,34},"Discard changes",0,1);
    }else{
        zui_add(u,2000,ZUI_BUTTON,(struct zui_rect){c.x+20,c.y+250,140,34},"Previous",0,writable);
        zui_add(u,2001,ZUI_BUTTON,(struct zui_rect){c.x+172,c.y+250,140,34},"Next",0,writable);
        zui_add(u,2002,ZUI_BUTTON,(struct zui_rect){c.x+20,c.y+c.h-48,126,34},"Save changes",1,writable&&a->preference_edited);
        zui_add(u,2003,ZUI_BUTTON,(struct zui_rect){c.x+156,c.y+c.h-48,182,34},"Discard changes",0,a->preference_edited);
    }
}
static int preferences_action(struct meridian *a,int action){
    if(action<2000||action>2011)return 0;
    struct zui *u=&a->navigation;u->dirty=1;
    if(action==2010){a->preference_prompt=0;return 1;}
    if(action==2003||action==2011){
        if(a->colors.backend.dirty(0)){a->preference_message="Cannot release unsaved state; edits kept.";return 1;}
        int prompt=a->preference_prompt;a->preference_edited=a->preference_prompt=0;a->preference_message=0;refresh_preferences(a);
        if(prompt==1)a->closing=1;
        if(prompt==2)a->page=-1;
        return 1;
    }
    if(!a->preferences_ready||!a->preferences_save||!(a->preferences.flags&2)||a->preference_prompt)return 1;
    if(action==2002){
        long result=a->preferences_save(a->preference_field,a->preference_choice,a->preference_revision);
        if(!result){a->preference_edited=0;a->colors.backend.dirty(0);a->preference_message="System default saved and applied.";refresh_preferences(a);}
        else a->preference_message=result==-11?"Changed elsewhere. Discard edits to reload.":result==-13?"Administrator permission required.":"Save failed; edits kept.";
        return 1;
    }
    if(action==2000||action==2001){
        unsigned int count=a->preference_field==1?a->preferences.keyboard_count:a->preferences.zone_count;
        unsigned int next=a->preference_choice>=count?0:action==2001?(a->preference_choice+1)%count:(a->preference_choice+count-1)%count;
        unsigned int saved=a->preference_field==1?a->preferences.keyboard:a->preferences.zone;
        int dirty=next!=saved;
        if(a->colors.backend.dirty(dirty)){a->preference_message="Cannot protect edits; selection kept.";return 1;}
        a->preference_choice=next;a->preference_edited=dirty;a->preference_message=0;
    }
    return 1;
}
struct zui *meridian_ui(struct meridian *a){return a->page==MERIDIAN_COLORS?&a->colors.ui:&a->navigation;}
static void recent(struct meridian *a,int page){int i=0;while(i<a->recent_count&&a->recent[i]!=page)i++;if(i==a->recent_count&&i<6)a->recent_count++;if(i>5)i=5;for(;i>0;i--)a->recent[i]=a->recent[i-1];a->recent[0]=page;}
static int category_of(int page){for(int i=0;i<10;i++)if(same(categories[i],meridian_pages[page].category))return i;return 0;}
static void open_page(struct meridian *a,int p){
    a->page=p;a->scroll=0;a->search_focus=0;
    if(p>=0){recent(a,p);a->category=category_of(p);}
    a->navigation.focus=a->navigation.pressed=-1;a->navigation.dirty=1;a->colors.ui.dirty=1;
}
int meridian_open(struct meridian *a,const char *id){if(a->colors.edited||a->preference_edited)return 0;for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(same(id,meridian_pages[i].id)){open_page(a,i);return 1;}return 0;}
int meridian_resize(struct meridian *a,int w,int h,unsigned long capacity){
    if(w<600||h<480||!zui_resize(&a->navigation,w,h,capacity)||!zui_resize(&a->colors.ui,w,h,capacity))return 0;
    settings_set_viewport(&a->colors,(struct zui_rect){204,12,w-216,h-24});return 1;
}
int meridian_init(struct meridian *a,unsigned int *pixels,struct settings_backend backend,const char *page){
    *a=(struct meridian){0};a->category=0;a->group=-1;a->page=0;a->pending_category=-2;
    if(!settings_init(&a->colors,pixels,backend)||!zui_init(&a->navigation,pixels,600,480)||!zui_input_init(&a->search,a->query,sizeof(a->query)))return 0;
    meridian_resize(a,600,480,600*480);
    if(page&&!meridian_open(a,page))return 0;
    recent(a,a->page);return 1;
}
void meridian_refresh(struct meridian *a){
    unsigned long rev=a->colors.state.revision,res=a->colors.state.resources;unsigned int flags=a->colors.state.flags;
    refresh_preferences(a);settings_refresh(&a->colors);a->navigation.theme=a->colors.ui.theme;a->navigation.full_background=(a->colors.state.mode<2&&(a->colors.saved_effects&5)==5);
    if(rev!=a->colors.state.revision||res!=a->colors.state.resources||flags!=a->colors.state.flags)a->navigation.dirty=1;
}
static int result_rows(struct zui *u){int n=(u->height-168)/42;return n>18?18:n<1?1:n;}
static int category_rows(struct zui *u){int n=(u->height-130)/30;return n>10?10:n<1?1:n;}
static void sidebar(struct meridian *a,struct zui *u){
    text(u,24,25,"Meridian",&zui_label,u->theme.text);
    zui_add(u,1002,ZUI_BUTTON,(struct zui_rect){24,60,156,34},a->query,0,!a->colors.prompt);
    int rows=category_rows(u);a->category_offset=zui_scroll_step(a->category_offset,0,10-rows);
    for(int i=0;i<rows;i++){int cat=a->category_offset+i;zui_add(u,1100+cat,ZUI_NAVIGATION,(struct zui_rect){20,110+i*30,164,26},(const char*[]){"Quick Settings","Input & Output","Devices","Networking","Appearance","Apps & Windows","Workspace","Security","Language & Time","System"}[cat],a->category==cat&&!a->query[0],!a->colors.prompt);}
}
static void results(struct meridian *a,struct zui *u,struct zui_rect c){
    text(u,c.x+20,c.y+20,a->query[0]?"Search results":a->group>=0?groups[a->group]:a->category>=0?categories[a->category]:"All Settings",&zui_heading,u->theme.text);
    text(u,c.x+20,c.y+58,a->query[0]?"Matching settings pages":"Choose a settings page",&zui_caption,u->theme.secondary);
    a->match_count=0;int grouped[7]={0};
    for(int i=0;i<MERIDIAN_PAGE_COUNT;i++){
        const struct meridian_page *p=&meridian_pages[i];
        if(a->category>=0&&!same(categories[a->category],p->category))continue;
        if(a->group>=0&&p->group!=a->group)continue;
        if(!contains(p->title,a->query)&&!contains(p->id,a->query)&&!contains(p->requirements,a->query))continue;
        if(!a->query[0]&&a->category>=0&&a->group<0&&p->group>=0){if(!grouped[p->group]){a->matches[a->match_count++]=-1-p->group;grouped[p->group]=1;}}
        else a->matches[a->match_count++]=i;
    }
    int rows=result_rows(u);a->offset=zui_scroll_step(a->offset,0,a->match_count-rows);
    for(int i=0;i<rows&&a->offset+i<a->match_count;i++){
        int p=a->matches[a->offset+i];zui_add(u,p<0?300+(-1-p):100+p,ZUI_NAVIGATION,(struct zui_rect){c.x+16,c.y+92+i*42,c.w-32,36},p<0?groups[-1-p]:meridian_pages[p].title,0,1);
    }
    if(!a->match_count)text(u,c.x+20,c.y+100,"No matching settings.",&zui_body,u->theme.secondary);
    zui_add(u,4,ZUI_BUTTON,(struct zui_rect){c.x+16,c.y+c.h-48,94,34},"Previous",0,a->offset>0);
    zui_add(u,5,ZUI_BUTTON,(struct zui_rect){c.x+120,c.y+c.h-48,82,34},"Next",0,a->offset+rows<a->match_count);
    zui_add(u,6,ZUI_BUTTON,(struct zui_rect){c.x+c.w-138,c.y+c.h-48,122,34},a->group>=0?"Back to category":"All categories",0,a->category>=0);
}
void meridian_draw(struct meridian *a){
    struct zui *u=meridian_ui(a);int focus=u->focus>=0&&u->focus<u->count?u->controls[u->focus].id:0;
    int pressed=u->pressed>=0&&u->pressed<u->count?u->controls[u->pressed].id:0;
    if(a->page==MERIDIAN_COLORS){
        u->count=18;settings_draw(&a->colors);sidebar(a,u);
        zui_add(u,900,ZUI_BUTTON,(struct zui_rect){a->colors.viewport.x+a->colors.viewport.w-88,28,72,28},"Back",0,!a->colors.prompt);
        struct zui_rect r=a->colors.viewport;zui_clip(u,r);
        zui_surface(u,(struct zui_rect){r.x+16,r.y+46,r.w-32,28},6,ZUI_SURFACE_CONTROL);
        zui_paragraph(u,(struct zui_rect){r.x+22,r.y+49,r.w-44,25},"WIP: automatic switching and custom colors.",&zui_caption,u->theme.secondary,0);
    }else{
        struct zui_workspace w=zui_workspace_begin(u,180);struct zui_rect c=w.content;u->count=0;u->focus=u->pressed=-1;
        sidebar(a,u);zui_clip(u,c);
        if(a->page<0)results(a,u,c);
        else {
            const struct meridian_page *p=&meridian_pages[a->page];
            text(u,c.x+20,c.y+18,p->title,&zui_heading,u->theme.text);
            text(u,c.x+20,c.y+56,p->category,&zui_caption,u->theme.secondary);
            zui_surface(u,(struct zui_rect){c.x+16,c.y+84,c.w-32,58},8,ZUI_SURFACE_CONTROL);
            zui_paragraph(u,(struct zui_rect){c.x+26,c.y+94,c.w-52,40},a->preferences.version==1&&preference_field(a)?(preference_field(a)==1?"WIP: model, repeat, Num Lock and shortcuts. Layout selection works.":"WIP: clock editing and sync policy. Time zone selection works."):a->page==0?"WIP: scheduled mode and persistent history. Recent pages are kept while Meridian is open.":a->page==MERIDIAN_ABOUT?"WIP: available runtime information; hardware inventory is planned.":"Work in progress. This page needs additional system services. No settings here can be changed yet.",&zui_caption,u->theme.secondary,0);
            if(a->preferences.version==1&&preference_field(a))preferences_draw(a,u,c);
            else if(a->page==0){
                zui_add(u,100+MERIDIAN_COLORS,ZUI_NAVIGATION,(struct zui_rect){c.x+16,c.y+164,c.w-32,36},"Colors and materials",0,1);
                zui_add(u,115,ZUI_NAVIGATION,(struct zui_rect){c.x+16,c.y+206,c.w-32,36},"Wallpaper",0,1);
                text(u,c.x+20,c.y+266,"Recently visited",&zui_label,u->theme.text);
                int row=0;for(int i=0;i<a->recent_count&&row<3;i++)if(a->recent[i]!=0){zui_add(u,200+i,ZUI_NAVIGATION,(struct zui_rect){c.x+16,c.y+296+row*34,c.w-32,30},meridian_pages[a->recent[i]].title,0,1);row++;}
            }else{
                struct zui_rect body={c.x+20,c.y+164,c.w-40,c.h-232};
                a->lines=zui_paragraph(u,body,a->page==MERIDIAN_ABOUT&&a->information[0]?a->information:p->requirements,&zui_body,u->theme.secondary,a->scroll);
                zui_add(u,10,ZUI_BUTTON,(struct zui_rect){c.x+16,c.y+c.h-48,98,34},"Scroll up",0,a->scroll>0);
                zui_add(u,11,ZUI_BUTTON,(struct zui_rect){c.x+126,c.y+c.h-48,110,34},"Scroll down",0,a->scroll+8<a->lines);
            }
        }
    }
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});
    for(int i=0;i<u->count;i++)if(u->controls[i].enabled){if(u->controls[i].id==focus)u->focus=i;if(u->controls[i].id==pressed)u->pressed=i;}
    int first=a->page==MERIDIAN_COLORS?18:0,side_count=1+category_rows(u);
    zui_clip(u,(struct zui_rect){12,12,180,u->height-24});zui_draw_range(u,first+1,side_count-1);
    zui_input_draw(u,(struct zui_rect){24,60,156,34},&a->search,a->search_focus&&u->window_focused);
    if(!a->query[0]&&!a->search_focus)text(u,33,68,"Search settings",&zui_caption,u->theme.secondary);
    zui_clip(u,(struct zui_rect){204,12,u->width-216,u->height-24});
    if(a->page==MERIDIAN_COLORS)zui_draw_range(u,first+side_count,1);
    else zui_draw_range(u,side_count,u->count-side_count);
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});u->dirty=0;
}
static void navigate(struct meridian *a,int category){
    if(a->preference_edited){a->preference_message="Save or discard changes before navigating.";a->navigation.dirty=1;return;}
    if(a->colors.edited){a->pending_category=category;settings_request_navigation(&a->colors);return;}
    a->category=category;a->group=-1;a->offset=0;a->query[0]=0;zui_input_init(&a->search,a->query,sizeof(a->query));
    if(category==0)open_page(a,0);else open_page(a,-1);
}
void meridian_event(struct meridian *a,const struct zenith_gui_event *e){
    if(e->type==Z_GUI_FOCUS||e->type==Z_GUI_RESET){zui_event(&a->navigation,e);zui_event(&a->colors.ui,e);return;}
    struct zui *u=meridian_ui(a);if(!u->window_focused&&e->type!=Z_GUI_CLOSE&&e->type!=Z_GUI_CLOSE_INTENT)return;
    if(a->preference_edited&&(e->type==Z_GUI_CLOSE_INTENT||(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='q'||e->code=='Q')))){a->preference_prompt=1;u->dirty=1;return;}
    if(a->preference_edited&&e->type==Z_GUI_KEY&&e->extra==1){a->preference_prompt=a->preference_prompt?0:2;u->dirty=1;return;}
    if(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='q'||e->code=='Q')){
        if(a->page==MERIDIAN_COLORS){struct zenith_gui_event c={Z_GUI_CLOSE_INTENT,0,0,0,0,0};settings_event(&a->colors,&c);a->closing=a->colors.closing;}else a->closing=1;return;
    }
    if(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='f'||e->code=='F')){
        if(a->preference_edited){a->preference_message="Save or discard changes before searching.";u->dirty=1;return;}
        if(a->colors.edited){a->colors.message="Save or discard changes before searching.";u->dirty=1;return;}
        a->query[0]=0;zui_input_init(&a->search,a->query,sizeof(a->query));open_page(a,-1);a->category=a->group=-1;a->search_focus=1;return;
    }
    if(a->search_focus&&e->type==Z_GUI_KEY&&e->extra!=4){
        if(e->extra==1){a->search_focus=0;u->dirty=1;return;}
        if(e->extra==2){a->search_focus=0;u->focus=1+category_rows(u);u->dirty=1;return;}
        if(zui_input_event(&a->search,e)){a->page=-1;a->offset=0;a->category=a->group=-1;a->navigation.dirty=1;}return;
    }
    if(e->type==Z_GUI_SCROLL){
        if(!e->code)return;
        long movement=-(long)e->code*3;int delta=movement>24?24:movement< -24?-24:(int)movement,old;
        if(e->x<204){old=a->category_offset;a->category_offset=zui_scroll_step(old,delta,10-category_rows(u));if(old!=a->category_offset)u->dirty=1;}
        else if(a->page<0){old=a->offset;a->offset=zui_scroll_step(old,delta,a->match_count-result_rows(u));if(old!=a->offset)u->dirty=1;}
        else if(a->page!=MERIDIAN_COLORS&&a->page!=0){old=a->scroll;a->scroll=zui_scroll_step(old,delta,a->lines-8);if(old!=a->scroll)u->dirty=1;}
        return;
    }
    if(a->page!=MERIDIAN_COLORS&&(e->type==Z_GUI_CLOSE||e->type==Z_GUI_CLOSE_INTENT)){a->closing=1;return;}
    if(a->page!=MERIDIAN_COLORS&&e->type==Z_GUI_KEY&&e->extra==1){if(a->page>=0)open_page(a,-1);else if(a->group>=0){a->group=-1;u->dirty=1;}else navigate(a,0);return;}
    int prompt=a->colors.prompt;
    int action=a->page==MERIDIAN_COLORS?settings_event(&a->colors,e):zui_event(u,e);a->closing=a->colors.closing;
    if(prompt==3&&!a->colors.prompt&&action!=900)a->pending_category=-2;
    if(preferences_action(a,action))return;
    if(action==900){int cat=a->pending_category;a->pending_category=-2;if(cat==-2)open_page(a,-1);else navigate(a,cat);}
    else if(action==1002){if(a->preference_edited){a->preference_message="Save or discard changes before searching.";u->dirty=1;}else if(a->colors.edited){a->colors.message="Save or discard changes before searching.";u->dirty=1;}else {open_page(a,-1);a->category=a->group=-1;a->search_focus=1;}}
    else if(action>=1100&&action<1110)navigate(a,action-1100);
    else if(action==4||action==5){int next=zui_scroll_step(a->offset,action==4?-result_rows(u):result_rows(u),a->match_count-result_rows(u));if(next!=a->offset){a->offset=next;u->dirty=1;}}
    else if(action==6){if(a->group>=0){a->group=-1;u->dirty=1;}else navigate(a,-1);}
    else if(action==10||action==11){int next=zui_scroll_step(a->scroll,action==10?-8:8,a->lines-8);if(next!=a->scroll){a->scroll=next;u->dirty=1;}}
    else if(action>=300&&action<307){a->group=action-300;a->offset=0;u->dirty=1;}
    else if(action>=100&&action<100+MERIDIAN_PAGE_COUNT)open_page(a,action-100);
    else if(action>=200&&action<200+a->recent_count)open_page(a,a->recent[action-200]);
}
