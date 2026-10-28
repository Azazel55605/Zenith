#include "meridian.h"
static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static char lower(char c){return c>='A'&&c<='Z'?(char)(c+32):c;}
static int contains(const char *s,const char *q){if(!*q)return 1;for(;*s;s++){const char *a=s,*b=q;while(*a&&*b&&lower(*a)==lower(*b)){a++;b++;}if(!*b)return 1;}return 0;}
static void text(struct zui *u,int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(u,x,y,s,length(s),f,c);}
static const char *categories[]={"Quick Settings","Input & Output","Connected Devices","Networking","Appearance & Style","Apps & Windows","Workspace","Security & Privacy","Language & Time","System"};
static const char *groups[]={"Mouse & Touchpad","Keyboard","Sound","Display & Monitor","Colors & Themes","Text & Fonts","Window Management"};
struct zui *meridian_ui(struct meridian *a){return a->page==MERIDIAN_COLORS?&a->colors.ui:&a->navigation;}
static void recent(struct meridian *a,int page){int i=0;while(i<a->recent_count&&a->recent[i]!=page)i++;if(i==a->recent_count&&i<6)a->recent_count++;if(i>5)i=5;for(;i>0;i--)a->recent[i]=a->recent[i-1];a->recent[0]=page;}
static int category_of(int page){for(int i=0;i<10;i++)if(same(categories[i],meridian_pages[page].category))return i;return 0;}
static void open_page(struct meridian *a,int p){
    a->page=p;a->scroll=0;a->search_focus=0;
    if(p>=0){recent(a,p);a->category=category_of(p);}
    a->navigation.focus=a->navigation.pressed=-1;a->navigation.dirty=1;a->colors.ui.dirty=1;
}
int meridian_open(struct meridian *a,const char *id){if(a->colors.edited)return 0;for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(same(id,meridian_pages[i].id)){open_page(a,i);return 1;}return 0;}
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
    settings_refresh(&a->colors);a->navigation.theme=a->colors.ui.theme;a->navigation.full_background=(a->colors.state.mode<2&&(a->colors.saved_effects&5)==5);
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
        u->count=16;settings_draw(&a->colors);sidebar(a,u);
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
            zui_paragraph(u,(struct zui_rect){c.x+26,c.y+94,c.w-52,40},a->page==0?"WIP: scheduled mode and persistent history. Recent pages are kept while Meridian is open.":a->page==MERIDIAN_ABOUT?"WIP: available runtime information; hardware inventory is planned.":"Work in progress. This page needs additional system services. No settings here can be changed yet.",&zui_caption,u->theme.secondary,0);
            if(a->page==0){
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
    int first=a->page==MERIDIAN_COLORS?16:0,side_count=1+category_rows(u);
    zui_clip(u,(struct zui_rect){12,12,180,u->height-24});zui_draw_range(u,first+1,side_count-1);
    zui_input_draw(u,(struct zui_rect){24,60,156,34},&a->search,a->search_focus&&u->window_focused);
    if(!a->query[0]&&!a->search_focus)text(u,33,68,"Search settings",&zui_caption,u->theme.secondary);
    zui_clip(u,(struct zui_rect){204,12,u->width-216,u->height-24});
    if(a->page==MERIDIAN_COLORS)zui_draw_range(u,first+side_count,1);
    else zui_draw_range(u,side_count,u->count-side_count);
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});u->dirty=0;
}
static void navigate(struct meridian *a,int category){
    if(a->colors.edited){a->pending_category=category;settings_request_navigation(&a->colors);return;}
    a->category=category;a->group=-1;a->offset=0;a->query[0]=0;zui_input_init(&a->search,a->query,sizeof(a->query));
    if(category==0)open_page(a,0);else open_page(a,-1);
}
void meridian_event(struct meridian *a,const struct zenith_gui_event *e){
    if(e->type==Z_GUI_FOCUS||e->type==Z_GUI_RESET){zui_event(&a->navigation,e);zui_event(&a->colors.ui,e);return;}
    struct zui *u=meridian_ui(a);if(!u->window_focused&&e->type!=Z_GUI_CLOSE&&e->type!=Z_GUI_CLOSE_INTENT)return;
    if(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='q'||e->code=='Q')){
        if(a->page==MERIDIAN_COLORS){struct zenith_gui_event c={Z_GUI_CLOSE_INTENT,0,0,0,0,0};settings_event(&a->colors,&c);a->closing=a->colors.closing;}else a->closing=1;return;
    }
    if(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='f'||e->code=='F')){
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
    if(action==900){int cat=a->pending_category;a->pending_category=-2;if(cat==-2)open_page(a,-1);else navigate(a,cat);}
    else if(action==1002){if(a->colors.edited){a->colors.message="Save or discard changes before searching.";u->dirty=1;}else {open_page(a,-1);a->category=a->group=-1;a->search_focus=1;}}
    else if(action>=1100&&action<1110)navigate(a,action-1100);
    else if(action==4||action==5){int next=zui_scroll_step(a->offset,action==4?-result_rows(u):result_rows(u),a->match_count-result_rows(u));if(next!=a->offset){a->offset=next;u->dirty=1;}}
    else if(action==6){if(a->group>=0){a->group=-1;u->dirty=1;}else navigate(a,-1);}
    else if(action==10||action==11){int next=zui_scroll_step(a->scroll,action==10?-8:8,a->lines-8);if(next!=a->scroll){a->scroll=next;u->dirty=1;}}
    else if(action>=300&&action<307){a->group=action-300;a->offset=0;u->dirty=1;}
    else if(action>=100&&action<100+MERIDIAN_PAGE_COUNT)open_page(a,action-100);
    else if(action>=200&&action<200+a->recent_count)open_page(a,a->recent[action-200]);
}
