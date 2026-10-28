#include "meridian.h"
static unsigned long length(const char *s){unsigned long n=0;while(s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static char lower(char c){return c>='A'&&c<='Z'?(char)(c+32):c;}
static int contains(const char *s,const char *q){
    if(!*q)return 1;
    for(;*s;s++){const char *a=s,*b=q;while(*a&&*b&&lower(*a)==lower(*b)){a++;b++;}if(!*b)return 1;}
    return 0;
}
static void text(struct zui *u,int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(u,x,y,s,length(s),f,c);}
static const char *categories[]={"Quick Settings","Input & Output","Connected Devices","Networking","Appearance & Style","Apps & Windows","Workspace","Security & Privacy","Language & Time","System"};
static const char *groups[]={"Mouse & Touchpad","Keyboard","Sound","Display & Monitor","Colors & Themes","Text & Fonts","Window Management"};
struct zui *meridian_ui(struct meridian *a){return a->page==MERIDIAN_COLORS?&a->colors.ui:&a->navigation;}
static void recent(struct meridian *a,int page){
    int i=0;while(i<a->recent_count&&a->recent[i]!=page)i++;
    if(i==a->recent_count&&i<6)a->recent_count++;
    if(i>5)i=5;
    for(;i>0;i--)a->recent[i]=a->recent[i-1];
    a->recent[0]=page;
}
static void open_page(struct meridian *a,int p){
    a->page=p;a->scroll=0;a->search_focus=0;
    if(p>=0)recent(a,p);
    a->navigation.focus=-1;a->navigation.pressed=-1;a->navigation.dirty=1;
    a->colors.ui.dirty=1;
}
int meridian_open(struct meridian *a,const char *id){
    if(a->colors.edited)return 0;
    for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(same(id,meridian_pages[i].id)){open_page(a,i);return 1;}
    return 0;
}
int meridian_init(struct meridian *a,unsigned int *pixels,struct settings_backend backend,const char *page){
    *a=(struct meridian){0};a->category=-1;a->group=-1;a->page=0;
    if(!settings_init(&a->colors,pixels,backend)||!zui_init(&a->navigation,pixels,600,480)||!zui_input_init(&a->search,a->query,sizeof(a->query)))return 0;
    zui_add(&a->colors.ui,900,ZUI_BUTTON,(struct zui_rect){450,12,126,32},"All settings",0,1);
    if(page&&!meridian_open(a,page))return 0;
    recent(a,a->page);return 1;
}
void meridian_refresh(struct meridian *a){
    unsigned long revision=a->colors.state.revision,resources=a->colors.state.resources;unsigned int flags=a->colors.state.flags;
    settings_refresh(&a->colors);
    struct zui_theme t=a->colors.ui.theme;
    a->navigation.theme=t;
    if(revision!=a->colors.state.revision||resources!=a->colors.state.resources||flags!=a->colors.state.flags)a->navigation.dirty=1;
}
static void controls(struct meridian *a){
    struct zui *u=&a->navigation;int focused=u->focus>=0&&u->focus<u->count?u->controls[u->focus].id:0;
    int pressed=u->pressed>=0&&u->pressed<u->count?u->controls[u->pressed].id:0;
    u->count=0;u->focus=-1;u->pressed=-1;
    if(a->page>=0){
        zui_add(u,1,ZUI_BUTTON,(struct zui_rect){450,12,126,32},"All settings",0,1);
        if(a->page==0){
            zui_add(u,100+MERIDIAN_COLORS,ZUI_NAVIGATION,(struct zui_rect){24,178,266,36},"Colors and theme",0,1);
            zui_add(u,100+15,ZUI_NAVIGATION,(struct zui_rect){306,178,270,36},"Wallpaper",0,1);
            int row=0;
            for(int i=0;i<a->recent_count&&row<4;i++)if(a->recent[i]!=0){
                zui_add(u,200+i,ZUI_NAVIGATION,(struct zui_rect){24,254+row*39,552,32},meridian_pages[a->recent[i]].title,0,1);row++;
            }
        }else{
            zui_add(u,10,ZUI_BUTTON,(struct zui_rect){24,422,100,34},"Scroll up",0,a->scroll>0);
            zui_add(u,11,ZUI_BUTTON,(struct zui_rect){136,422,116,34},"Scroll down",0,a->scroll+10<a->lines);
        }
    }else{
        zui_add(u,2,ZUI_BUTTON,(struct zui_rect){24,58,430,36},a->query,0,1);
        zui_add(u,3,ZUI_BUTTON,(struct zui_rect){466,58,110,36},"Clear",0,!!a->query[0]);
        a->match_count=0;int grouped[7]={0};
        for(int i=0;i<MERIDIAN_PAGE_COUNT;i++){
            const struct meridian_page *p=&meridian_pages[i];
            if(a->category>=0&&!same(categories[a->category],p->category))continue;
            if(a->group>=0&&p->group!=a->group)continue;
            if(!contains(p->title,a->query)&&!contains(p->id,a->query)&&!contains(p->requirements,a->query))continue;
            if(!a->query[0]&&a->category>=0&&a->group<0&&p->group>=0){
                if(!grouped[p->group]){a->matches[a->match_count++]=-1-p->group;grouped[p->group]=1;}
            }else a->matches[a->match_count++]=i;
        }
        int maximum=a->match_count>7?a->match_count-7:0;if(a->offset>maximum)a->offset=maximum;if(a->offset<0)a->offset=0;
        for(int i=0;i<10;i++)zui_add(u,20+i,ZUI_NAVIGATION,(struct zui_rect){24,114+i*30,188,26},categories[i],a->category==i,1);
        for(int i=0;i<7&&a->offset+i<a->match_count;i++){
            int p=a->matches[a->offset+i];zui_add(u,p<0?300+(-1-p):100+p,ZUI_NAVIGATION,(struct zui_rect){228,114+i*42,348,36},p<0?groups[-1-p]:meridian_pages[p].title,0,1);
        }
        zui_add(u,4,ZUI_BUTTON,(struct zui_rect){228,422,98,34},"Previous",0,a->offset>0);
        zui_add(u,5,ZUI_BUTTON,(struct zui_rect){338,422,98,34},"Next",0,a->offset+7<a->match_count);
        zui_add(u,a->group>=0?7:6,ZUI_BUTTON,(struct zui_rect){448,422,128,34},a->group>=0?"Back to groups":"All categories",0,a->category>=0);
    }
    for(int i=0;i<u->count;i++)if(u->controls[i].enabled){if(u->controls[i].id==focused)u->focus=i;if(u->controls[i].id==pressed)u->pressed=i;}
}
void meridian_draw(struct meridian *a){
    if(a->page==MERIDIAN_COLORS){
        settings_draw(&a->colors);struct zui *u=&a->colors.ui;
        zui_surface(u,(struct zui_rect){24,48,552,26},6,ZUI_SURFACE_CONTROL);
        text(u,30,53,"WIP: automatic switching and custom colors are planned.",&zui_caption,u->theme.secondary);
        u->controls[15].enabled=!a->colors.prompt;zui_draw_range(u,15,1);u->dirty=0;return;
    }
    struct zui *u=&a->navigation;if(u->foreground)zui_begin_material_frame(u);zui_clip(u,(struct zui_rect){0,0,600,480});zui_surface(u,u->clip,0,ZUI_SURFACE_CONTENT);
    if(a->page<0){
        text(u,24,14,"Meridian",&zui_heading,u->theme.text);text(u,178,23,"System Settings",&zui_body,u->theme.secondary);
        zui_surface(u,(struct zui_rect){12,104,208,312},8,ZUI_SURFACE_NAVIGATION);
        controls(a);zui_draw_controls(u);zui_input_draw(u,(struct zui_rect){24,58,430,36},&a->search,a->search_focus&&u->window_focused);
        if(!a->query[0]&&!a->search_focus)text(u,34,67,"Search settings",&zui_body,u->theme.secondary);
        if(!a->match_count)text(u,234,132,"No matching settings.",&zui_body,u->theme.secondary);
    }else{
        const struct meridian_page *p=&meridian_pages[a->page];
        text(u,24,14,p->title,&zui_heading,u->theme.text);
        zui_surface(u,(struct zui_rect){24,58,552,56},8,ZUI_SURFACE_CONTROL);
        zui_paragraph(u,(struct zui_rect){34,67,532,46},a->page==0?"WIP: scheduled mode and persistent history are planned. Recent pages are kept only while Meridian is open.":a->page==MERIDIAN_ABOUT?"WIP: showing available runtime information. Hardware inventory and release-version metadata are planned.":"Work in progress. These controls need additional system services. No settings on this page can be changed yet.",&zui_caption,u->theme.secondary,0);
        if(a->page==0){
            const char *status=a->colors.state.mode?"Saved appearance: Light":"Saved appearance: Dark";
            text(u,24,135,status,&zui_body,u->theme.text);text(u,24,228,"Recently visited",&zui_label,u->theme.text);
            controls(a);zui_draw_controls(u);
        }else{
            text(u,24,130,p->category,&zui_label,u->theme.text);
            zui_surface(u,(struct zui_rect){24,154,552,250},8,ZUI_SURFACE_CONTROL);
            a->lines=zui_paragraph(u,(struct zui_rect){36,166,528,228},a->page==MERIDIAN_ABOUT&&a->information[0]?a->information:p->requirements,&zui_body,u->theme.secondary,a->scroll);
            controls(a);zui_draw_controls(u);
        }
    }
    u->dirty=0;
}
void meridian_event(struct meridian *a,const struct zenith_gui_event *e){
    /* Both UIs must see focus transitions even when the other view is active. */
    if(e->type==Z_GUI_FOCUS||e->type==Z_GUI_RESET){zui_event(&a->navigation,e);zui_event(&a->colors.ui,e);return;}
    if(e->type==Z_GUI_KEY&&!meridian_ui(a)->window_focused)return;
    if(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='q'||e->code=='Q')){
        if(a->page==MERIDIAN_COLORS){struct zenith_gui_event close={Z_GUI_CLOSE_INTENT,0,0,0,0,0};settings_event(&a->colors,&close);a->closing=a->colors.closing;}
        else a->closing=1;
        return;
    }
    if(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='f'||e->code=='F')){
        if(a->colors.edited){a->colors.message="Save or discard your changes before searching.";a->colors.ui.dirty=1;return;}
        open_page(a,-1);a->category=-1;a->group=-1;a->search_focus=1;a->navigation.focus=0;return;
    }
    if(a->page==MERIDIAN_COLORS){if(settings_event(&a->colors,e)==900)open_page(a,-1);a->closing=a->colors.closing;return;}
    struct zui *u=&a->navigation;
    if(e->type==Z_GUI_CLOSE||e->type==Z_GUI_CLOSE_INTENT){a->closing=1;return;}
    if(!u->window_focused)return;
    if(e->type==Z_GUI_KEY&&e->extra==1){if(a->search_focus){a->search_focus=0;u->dirty=1;}else if(a->page>=0)open_page(a,-1);else if(a->group>=0){a->group=-1;a->offset=0;u->dirty=1;}else if(a->category>=0){a->category=-1;a->offset=0;u->dirty=1;}else open_page(a,0);return;}
    if(a->search_focus&&e->type==Z_GUI_KEY&&e->extra!=4){
        if(e->extra==2){a->search_focus=0;u->focus=u->count>12?12:0;u->dirty=1;return;}
        if(zui_input_event(&a->search,e)){a->offset=0;a->category=-1;a->group=-1;u->dirty=1;}return;
    }
    if(e->type==Z_GUI_SCROLL){
        if(a->page<0)a->offset+=e->code<0?3:-3;
        else {a->scroll+=e->code<0?3:-3;if(a->scroll<0)a->scroll=0;if(a->scroll>a->lines-10)a->scroll=a->lines>10?a->lines-10:0;}
        u->dirty=1;return;
    }
    int action=zui_event(u,e);
    a->search_focus=a->page<0&&u->focus==0;
    if(action==1)open_page(a,-1);
    else if(action==2){a->search_focus=1;u->dirty=1;}
    else if(action==3){a->query[0]=0;zui_input_init(&a->search,a->query,sizeof(a->query));a->offset=0;u->dirty=1;}
    else if(action==4){a->offset-=7;u->dirty=1;}
    else if(action==5){a->offset+=7;u->dirty=1;}
    else if(action==6){a->category=-1;a->group=-1;a->offset=0;u->dirty=1;}
    else if(action==7){a->group=-1;a->offset=0;u->dirty=1;}
    else if(action==10){a->scroll=a->scroll>8?a->scroll-8:0;u->dirty=1;}
    else if(action==11){a->scroll+=8;u->dirty=1;}
    else if(action>=20&&action<30){a->category=action-20;a->group=-1;a->offset=0;u->dirty=1;}
    else if(action>=300&&action<307){a->group=action-300;a->offset=0;u->dirty=1;}
    else if(action>=100&&action<100+MERIDIAN_PAGE_COUNT)open_page(a,action-100);
    else if(action>=200&&action<200+a->recent_count)open_page(a,a->recent[action-200]);
}
