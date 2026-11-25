#include "desktop_settings.h"

enum { CHOICE=10000, COMBO_ROW=10032, ENTRY=10100, ADD=10200, REFRESH_APPS=10201, STEP=10216, TOGGLE=10232, PICK_APP=8 };
static const char *const modes[]={"Dock when maximized","Floating","Docked","Hide when maximized","Auto-hide"};
static const char *const positions[]={"Bottom","Top"};
static const char *const focus[]={"Click to focus","Focus follows pointer"};
static const char *const title_actions[]={"Maximize or restore","Minimize","Toggle fullscreen","Do nothing"};
static const char *const layouts[]={"Cards","Compact list"};
static const char *const sorts[]={"Recently focused","Opening order","Window title"};
static const char *const workspace_scope[]={"Current desktop","All desktops"};

static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static int equal_bytes(const void *a,const void *b,unsigned long n){
    const unsigned char *x=a,*y=b;
    for(unsigned long i=0;i<n;i++)if(x[i]!=y[i])return 0;
    return 1;
}
static void copy(char *out,const char *in,unsigned long size){
    unsigned long i=0;for(;i+1<size&&in[i];i++)out[i]=in[i];
    for(;i<size;i++)out[i]=0;
}
static unsigned long length(const char *s){unsigned long n=0;while(s[n])n++;return n;}
static void text(struct zui *ui,int x,int y,const char *s,const struct zui_font *font,unsigned int color){zui_text(ui,x,y,s,length(s),font,color);}

unsigned int desktop_page(const char *id){
    if(same(id,"windows.taskbar"))return Z_DESKTOP_TASKBAR;
    if(same(id,"windows.behavior"))return Z_DESKTOP_WINDOWS;
    if(same(id,"windows.switcher"))return Z_DESKTOP_SWITCHER;
    if(same(id,"windows.desktops"))return Z_DESKTOP_WORKSPACES;
    return 0;
}
static struct zenith_desktop_preferences defaults(unsigned int page){
    struct zenith_desktop_preferences p={.version=1,.page=page};
    if(page==Z_DESKTOP_WINDOWS){p.values[1]=250;p.values[2]=p.values[6]=1;}
    if(page==Z_DESKTOP_WORKSPACES){p.values[0]=1;p.count=1;copy(p.entries[0].name,"Desktop 1",65);}
    return p;
}
static int same_values(const struct zenith_desktop_preferences *a,const struct zenith_desktop_preferences *b){
    if(a->count!=b->count||!equal_bytes(a->values,b->values,sizeof(a->values)))return 0;
    for(unsigned int i=0;i<a->count;i++)if(a->entries[i].id!=b->entries[i].id||!same(a->entries[i].name,b->entries[i].name))return 0;
    return 1;
}
int desktop_changed(const struct meridian_desktop *d){return d->ready&&!same_values(&d->draft,&d->saved);}
int desktop_default(const struct meridian_desktop *d){struct zenith_desktop_preferences p=defaults(d->saved.page);return !d->ready||!(d->saved.flags&1)||same_values(&p,&d->draft);}
void desktop_cancel_input(struct meridian_desktop *d){d->field=d->naming=-1;d->choice.open=0;}

static void refresh_catalog(struct meridian_desktop *d){
    d->catalog.count=0;
    if(!d->apps)return;
    long bytes=d->apps(&d->catalog);
    if(bytes<8||d->catalog.version!=1||d->catalog.count>Z_DESKTOP_APPS||bytes!=(long)(8+d->catalog.count*sizeof(d->catalog.entries[0])))d->catalog.count=0;
    for(unsigned int i=0;i<d->catalog.count;i++)if(d->catalog.entries[i].id[64]||d->catalog.entries[i].label[64]){d->catalog.count=0;break;}
}
int desktop_refresh(struct meridian_desktop *d,unsigned int page){
    if(!page||!d->read)return 0;
    struct zenith_desktop_preferences next;
    if(d->read(page,&next)||next.version!=1||next.page!=page||next.count>Z_DESKTOP_MAX)return 0;
    for(unsigned int i=0;i<next.count;i++)if(next.entries[i].name[64])return 0;
    int update=!d->ready||d->saved.revision!=next.revision||d->saved.flags!=next.flags;
    if(!update)return 0;
    if(!desktop_changed(d))d->draft=next;
    d->saved=next;d->ready=1;
    return 1;
}
void desktop_enter(struct meridian_desktop *d,unsigned int page){
    d->ready=0;d->message=0;desktop_cancel_input(d);
    d->choice=(struct zui_combo){.popup={.base_id=COMBO_ROW}};
    desktop_refresh(d,page);
    if(page==Z_DESKTOP_TASKBAR&&d->ready)refresh_catalog(d);
}

static int protect(struct meridian_desktop *d,struct zenith_desktop_preferences old){
    if(d->dirty&&d->dirty(desktop_changed(d))){d->draft=old;d->message="Could not protect the edits.";return 0;}
    d->message=0;return 1;
}
void desktop_reset(struct meridian_desktop *d,int reset_defaults){
    if(!d->ready)return;
    struct zenith_desktop_preferences old=d->draft;
    desktop_cancel_input(d);
    if(reset_defaults){
        d->draft=defaults(d->saved.page);
        d->draft.revision=d->saved.revision;
    }else d->draft=d->saved;
    protect(d,old);
}
long desktop_apply(struct meridian_desktop *d){
    if(!desktop_changed(d))return 0;
    struct zenith_desktop_preferences request=d->draft;
    request.flags=0;
    for(unsigned int i=0;i<request.count;i++){
        char *s=request.entries[i].name;unsigned long n=length(s);
        for(unsigned long j=n+1;j<sizeof(request.entries[i].name);j++)s[j]=0;
    }
    for(unsigned int i=request.count;i<Z_DESKTOP_MAX;i++)request.entries[i]=(struct zenith_desktop_entry){0};
    long result=d->save?d->save(&request):-38;
    if(result){
        d->message=result==-11?"Settings changed elsewhere. Reset to reload.":result==-13?"Administrator permission required.":result==-22?"Enter a valid name with no leading or trailing spaces.":"Could not save the settings.";
        return result;
    }
    desktop_cancel_input(d);
    d->saved=d->draft;d->ready=0;desktop_refresh(d,request.page);
    d->message="Saved and applied.";
    if(d->dirty)d->dirty(0);
    return 0;
}

static int pinned(const struct meridian_desktop *d,const char *id){
    for(unsigned int i=0;i<d->draft.count;i++)if(same(d->draft.entries[i].name,id))return 1;
    return 0;
}
static const char *app_label(const struct meridian_desktop *d,const char *id){
    for(unsigned int i=0;i<d->catalog.count;i++)if(same(d->catalog.entries[i].id,id))return d->catalog.entries[i].label;
    return id;
}
static const char *const *options(const struct meridian_desktop *d,int field,int *count){
    unsigned int page=d->draft.page;
    if(page==Z_DESKTOP_TASKBAR){
        if(field==0){*count=5;return modes;}
        if(field==1){*count=2;return positions;}
        *count=(int)d->catalog.count;return 0;
    }
    if(page==Z_DESKTOP_WINDOWS){
        if(field==0){*count=2;return focus;}
        *count=4;return title_actions;
    }
    if(field==0){*count=2;return layouts;}
    if(field==1){*count=3;return sorts;}
    *count=2;return workspace_scope;
}
static int option_row(void *context,int index,struct zui_list_item *out){
    struct meridian_desktop *d=context;int count;
    const char *const *names=options(d,d->field,&count);
    if(index<0||index>=count)return 0;
    if(d->field==PICK_APP){
        const struct zenith_desktop_app *app=&d->catalog.entries[index];
        *out=(struct zui_list_item){0,app->label,0,0,!pinned(d,app->id)};return 1;
    }
    *out=(struct zui_list_item){0,names[index],0,0,1};return 1;
}
static struct zui_rect row(struct zui *u,struct zui_form *f,const char *label,const char *help,int height){
    return zui_form_row(u,f,label,help,f->narrow?f->w:270,height);
}
static void message(struct zui *u,struct zui_form *f,const char *s){
    int height=24+18*(zui_measure(s,length(s),&zui_caption)/(f->w>20?f->w-12:20));
    zui_paragraph(u,(struct zui_rect){f->x,f->y,f->w,height},s,&zui_caption,u->theme.secondary,0);f->y+=height+8;
}
static int combo(struct meridian_desktop *d,struct zui *u,struct zui_form *f,int field,const char *label,const char *help,int enabled){
    int count;
    const char *const *names=options(d,field,&count);
    const char *shown=field==PICK_APP?"Choose an application":names[d->draft.values[field]];
    struct zui_rect r=row(u,f,label,help,34);
    int added=zui_add(u,CHOICE+field,ZUI_BUTTON,r,shown,0,enabled&&count);
    if(added)u->controls[u->count-1].icon="chevron-down";
    if(added&&d->field==field)d->choice.field=r;
    return added;
}
static void toggle(struct meridian_desktop *d,struct zui *u,struct zui_form *f,int field,const char *label,const char *help,int enabled){
    zui_add(u,TOGGLE+field,ZUI_TOGGLE,row(u,f,label,help,32),"",d->draft.values[field]!=0,enabled);
}
static void stepper(struct meridian_desktop *d,struct zui *u,struct zui_form *f,int field,const char *label,const char *help,const char *unit,unsigned int max,int enabled){
    char shown[24],digits[12];unsigned int value=d->draft.values[field],n=value,i=0,at=0;
    do{digits[i++]=(char)('0'+n%10);n/=10;}while(n);
    while(i)shown[at++]=digits[--i];
    copy(shown+at,unit,sizeof(shown)-at);
    struct zui_rect r=row(u,f,label,help,34);
    zui_add(u,STEP+field*2,ZUI_BUTTON,(struct zui_rect){r.x,r.y,40,34},"-",0,enabled&&value);
    text(u,r.x+52,r.y+8,shown,&zui_body,u->theme.text);
    zui_add(u,STEP+field*2+1,ZUI_BUTTON,(struct zui_rect){r.x+r.w-40,r.y,40,34},"+",0,enabled&&value<max);
}
static void entries_form(struct meridian_desktop *d,struct zui *u,struct zui_form *f,int workspaces,int enabled){
    unsigned int count=d->draft.count;
    if(!count)message(u,f,"No pinned applications.");
    for(unsigned int i=0;i<count;i++){
        struct zenith_desktop_entry *entry=&d->draft.entries[i];
        int y=f->y,base=ENTRY+(int)i*4,buttons=164,w=f->w-buttons-12;
        struct zui_rect field={f->x,y,w,34};
        if(workspaces&&d->naming==(int)i)zui_input_draw(u,field,&d->rename,u->window_focused);
        else if(workspaces)zui_add(u,base,ZUI_BUTTON,field,entry->name[0]?entry->name:"Name this desktop",0,enabled);
        else zui_paragraph(u,field,app_label(d,entry->name),&zui_body,u->theme.text,0);
        int x=f->x+w+8;
        zui_add(u,base+1,ZUI_BUTTON,(struct zui_rect){x,y,38,34},"Up",0,enabled&&i>0);
        zui_add(u,base+2,ZUI_BUTTON,(struct zui_rect){x+44,y,56,34},"Down",0,enabled&&i+1<count);
        zui_add(u,base+3,ZUI_BUTTON,(struct zui_rect){x+106,y,62,34},"Remove",0,enabled&&(!workspaces||count>1));
        f->y+=42;
    }
}
void desktop_form(struct meridian_desktop *d,struct zui *u,struct zui_form *f){
    int enabled=!!(d->saved.flags&1);unsigned int page=d->draft.page;
    if(page==Z_DESKTOP_TASKBAR){
        zui_form_section(u,f,"Panel");
        combo(d,u,f,0,"Visibility",0,enabled);
        combo(d,u,f,1,"Screen edge",0,enabled);
        zui_form_section(u,f,"Pinned applications");
        entries_form(d,u,f,0,enabled);
        combo(d,u,f,PICK_APP,"Add a pin",0,enabled&&d->draft.count<Z_DESKTOP_PINS);
        if(!d->catalog.count)message(u,f,"The application list is unavailable.");
        zui_add(u,REFRESH_APPS,ZUI_BUTTON,(struct zui_rect){f->x,f->y,158,34},"Refresh app list",0,1);f->y+=42;
    }else if(page==Z_DESKTOP_WINDOWS){
        int hover=enabled&&d->draft.values[0]!=0;
        zui_form_section(u,f,"Focus");
        combo(d,u,f,0,"Focus policy",0,enabled);
        stepper(d,u,f,1,"Focus delay","Applies when focus follows the pointer."," ms",1000,hover);
        toggle(d,u,f,2,"Raise on click","Bring a clicked window to the front.",enabled);
        toggle(d,u,f,3,"Raise on hover focus",0,hover);
        zui_form_section(u,f,"Titlebar");
        combo(d,u,f,4,"Double-click",0,enabled);
        combo(d,u,f,5,"Maximize button",0,enabled);
        zui_form_section(u,f,"Snapping");
        toggle(d,u,f,6,"Screen edge zones","Drag to an edge or corner to maximize or tile.",enabled);
        stepper(d,u,f,7,"Snap to other windows","Set the distance to 0 to turn it off."," px",32,enabled);
    }else if(page==Z_DESKTOP_SWITCHER){
        zui_form_section(u,f,"Appearance and order");
        combo(d,u,f,0,"Layout",0,enabled);
        combo(d,u,f,1,"Sort order",0,enabled);
        toggle(d,u,f,2,"One window per app","Use the most recently focused window of each app.",enabled);
        zui_form_section(u,f,"Window scope");
        combo(d,u,f,3,"Virtual desktops",0,enabled);
        message(u,f,"Work in progress: monitor filtering requires multi-monitor support.");
    }else if(page==Z_DESKTOP_WORKSPACES){
        zui_form_section(u,f,"Desktops");
        message(u,f,"Select a name to rename it. Removing a desktop moves its windows to the first desktop.");
        entries_form(d,u,f,1,enabled);
        zui_add(u,ADD,ZUI_BUTTON,(struct zui_rect){f->x,f->y,146,34},"Add desktop",0,enabled&&d->draft.count<Z_DESKTOP_MAX);f->y+=42;
        zui_form_section(u,f,"Switching");
        toggle(d,u,f,0,"Animate switching","Follows the system animation speed.",enabled);
        message(u,f,"Use the taskbar pager or Ctrl+Alt+Left/Right. Add Shift to move the focused window with you.");
    }
    if(d->message)message(u,f,d->message);
}
void desktop_popup(struct meridian_desktop *d,struct zui *u){
    if(!d->choice.open)return;
    int count;options(d,d->field,&count);
    zui_combo_popup(u,&d->choice,count,option_row,d);
}
int desktop_event(struct meridian_desktop *d,struct zui *u,const struct zenith_gui_event *e){
    if(!d->ready)return 0;
    if(d->choice.open){
        struct zenith_desktop_preferences old=d->draft;
        int choice=zui_combo_event(u,&d->choice,e,option_row,d);
        if(choice){
            int field=d->field;
            if(field==PICK_APP&&d->draft.count<Z_DESKTOP_PINS){
                const char *id=d->catalog.entries[choice-1].id;
                if(!pinned(d,id))copy(d->draft.entries[d->draft.count++].name,id,65);
            }else if(field!=PICK_APP)d->draft.values[field]=(unsigned int)choice-1;
            protect(d,old);
        }
        if(!d->choice.open){d->field=-1;u->pressed=-1;}
        u->dirty=1;return 1;
    }
    if(d->naming>=0){
        if(e->type==Z_GUI_DOWN||(e->type==Z_GUI_KEY&&(e->extra==1||e->extra==2||e->extra==4))){
            d->naming=-1;u->dirty=1;
            return e->type==Z_GUI_KEY&&e->extra!=4;
        }
        if(e->type==Z_GUI_KEY&&!(e->modifiers&6)){
            struct zenith_desktop_preferences old=d->draft;
            if(zui_input_event(&d->rename,e)&&!protect(d,old)){zui_input_init(&d->rename,d->draft.entries[d->naming].name,49);}
            u->dirty=1;return 1;
        }
    }
    return 0;
}
int desktop_action(struct meridian_desktop *d,struct zui *u,int id){
    if(!d->ready)return 0;
    if(id==REFRESH_APPS&&d->draft.page==Z_DESKTOP_TASKBAR){refresh_catalog(d);return 1;}
    if(!(d->saved.flags&1))return 0;
    if(id>=CHOICE&&id<=CHOICE+PICK_APP){
        int field=id-CHOICE;
        for(int i=0;i<u->count;i++)if(u->controls[i].id==id&&u->controls[i].enabled){
            d->field=field;d->choice.id=id;d->choice.field=u->controls[i].rect;
            d->choice.value=field==PICK_APP?-1:(int)d->draft.values[field];
            d->choice.open=1;d->choice.popup.selected=d->choice.value;
            zui_scroll_jump(&d->choice.popup.scroll,d->choice.value>3?(d->choice.value-3)*40:0);
            u->pressed=-1;
            return 1;
        }
        return 0;
    }
    struct zenith_desktop_preferences old=d->draft;
    if(id>=TOGGLE&&id<TOGGLE+8)d->draft.values[id-TOGGLE]^=1;
    else if(id>=STEP&&id<STEP+16){
        int field=(id-STEP)/2,delta=(id-STEP)%2?1:-1,step=field==1?50:4,max=field==1?1000:32;
        int next=(int)d->draft.values[field]+delta*step;
        d->draft.values[field]=(unsigned int)(next<0?0:next>max?max:next);
    }else if(id>=ENTRY&&id<ENTRY+Z_DESKTOP_MAX*4){
        unsigned int at=(unsigned int)(id-ENTRY)/4,action=(unsigned int)(id-ENTRY)%4;
        if(at>=d->draft.count)return 0;
        if(!action&&d->draft.page==Z_DESKTOP_WORKSPACES){
            d->naming=(int)at;zui_input_init(&d->rename,d->draft.entries[at].name,49);return 1;
        }
        if(action==3){
            if(d->draft.page==Z_DESKTOP_WORKSPACES&&d->draft.count==1)return 0;
            for(unsigned int i=at+1;i<d->draft.count;i++)d->draft.entries[i-1]=d->draft.entries[i];
            d->draft.entries[--d->draft.count]=(struct zenith_desktop_entry){0};
        }else if((action==1&&at>0)||(action==2&&at+1<d->draft.count)){
            unsigned int target=action==1?at-1:at+1;
            struct zenith_desktop_entry item=d->draft.entries[at];
            d->draft.entries[at]=d->draft.entries[target];d->draft.entries[target]=item;
        }else return 0;
    }else if(id==ADD&&d->draft.page==Z_DESKTOP_WORKSPACES&&d->draft.count<Z_DESKTOP_MAX){
        unsigned int next=0;
        for(unsigned int i=0;i<d->draft.count;i++)if(d->draft.entries[i].id>=next)next=d->draft.entries[i].id+1;
        for(unsigned int i=0;i<d->saved.count;i++)if(d->saved.entries[i].id>=next)next=d->saved.entries[i].id+1;
        if(next>1000000000u){d->message="No more desktop IDs are available.";return 1;}
        struct zenith_desktop_entry item={.id=next,.name="Desktop 1"};
        item.name[8]=(char)('1'+d->draft.count);d->draft.entries[d->draft.count++]=item;
    }else return 0;
    protect(d,old);
    return 1;
}
