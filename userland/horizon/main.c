/* Horizon: the file manager (docs/components/HORIZON.md, concept docs/design/files-concept.svg). Tabs, each
 * with its own location, history, view, sort and filter; places and devices; a breadcrumb that turns into a
 * typed location; icons and details views; multi-selection; an information panel; and copy, move and delete as
 * kernel transfer jobs (zenith-native-files-abi 1.0) with progress, Cancel and the question when a name exists.
 * Everything runs as the person's own identity; the kernel applies the usual permissions. */
#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
#include "../include/zenith_files.h"
#include "../include/zenith_portal.h"

static unsigned int pixels[640*480];
static struct zui ui;
static long handle;

/* ---------- Text helpers: no libc in native programs. */
static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static int starts(const char *s,const char *prefix){while(*prefix)if(*s++!=*prefix++)return 0;return 1;}
static void print(const char *s){zenith_write(s,length(s));}
static char *put(char *o,const char *s){while(*s)*o++=*s++;*o=0;return o;}
static char *num(char *o,unsigned long v){char t[24];int n=0;do{t[n++]=(char)('0'+v%10);v/=10;}while(v);while(n)*o++=t[--n];*o=0;return o;}
static char *num2(char *o,unsigned long v){*o++=(char)('0'+v/10%10);*o++=(char)('0'+v%10);*o=0;return o;}
static void copy(char *o,const char *s,unsigned long n){unsigned long i=0;for(;i+1<n&&s[i];i++)o[i]=s[i];o[i]=0;}
static char lower(char c){return c>='A'&&c<='Z'?(char)(c+32):c;}
static int inside(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x<r.x+r.w&&y<r.y+r.h;}
static void text(int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(&ui,x,y,s,length(s),f,c);}
static int measure(const char *s,const struct zui_font *f){return zui_measure(s,length(s),f);}
static void text_right(int right,int y,const char *s,const struct zui_font *f,unsigned int c){text(right-measure(s,f),y,s,f,c);}
static unsigned int mix(unsigned int a,unsigned int b,int percent){
    unsigned int r=0;
    for(int shift=0;shift<24;shift+=8){unsigned int x=a>>shift&255,y=b>>shift&255;r|=((x*(unsigned int)(100-percent)+y*(unsigned int)percent)/100)<<shift;}
    return r;
}
static int light(void){unsigned int s=ui.theme.surface;return ((s>>16&255)+(s>>8&255)+(s&255))>384;}
static unsigned int warn(void){return light()?0x886010:0xE6C47A;}
static unsigned int bad(void){return light()?0xAE325D:0xF29BAE;}
/* Text cut to a width with an ellipsis, at a UTF-8 boundary. */
static void fit(int x,int y,const char *s,int width,const struct zui_font *f,unsigned int c){
    char t[300];copy(t,s,sizeof(t));unsigned long n=length(t);
    if(zui_measure(t,n,f)<=width){text(x,y,t,f,c);return;}
    while(n>0&&(zui_measure(t,n,f)+zui_measure("\xE2\x80\xA6",3,f)>width||((unsigned char)t[n]&0xC0)==0x80))n--;
    t[n]=0;put(t+n,"\xE2\x80\xA6");text(x,y,t,f,c);
}
static void fit_center(int cx,int y,const char *s,int width,const struct zui_font *f,unsigned int c){
    int w=measure(s,f);if(w>width)w=width;fit(cx-w/2,y,s,width,f,c);
}
/* One decimal place: 512 B, 12.4 KiB, 2.3 MiB. */
static char *size_text(char *o,unsigned long bytes){
    if(bytes<1024){o=num(o,bytes);return put(o," B");}
    static const char *units[4]={" KiB"," MiB"," GiB"," TiB"};
    unsigned long unit=1024;int u=0;while(u<3&&bytes>=unit*1024){unit*=1024;u++;}
    unsigned long tenths=(bytes*10+unit/2)/unit;
    if(tenths>=10240&&u<3){unit*=1024;u++;tenths=(bytes*10+unit/2)/unit;}
    o=num(o,tenths/10);*o++='.';o=num(o,tenths%10);return put(o,units[u]);
}
/* Civil date from days since 1970 (Howard Hinnant's algorithm), integers only. */
static void civil(long days,long *year,int *month,int *day){
    days+=719468;long era=(days>=0?days:days-146096)/146097;long doe=days-era*146097;
    long yoe=(doe-doe/1460+doe/36524-doe/146096)/365;long y=yoe+era*400;long doy=doe-(365*yoe+yoe/4-yoe/100);
    long mp=(5*doy+2)/153;*day=(int)(doy-(153*mp+2)/5+1);*month=(int)(mp<10?mp+3:mp-9);*year=y+(*month<=2);
}
static const char *month_names[12]={"Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"};
static long now_utc;static int zone_now;
/* "30 Oct 14:02" this year, "30 Oct 2025" before; local time from the kernel's offsets. */
static char *date_text(char *o,long mtime,int zone,int full){
    long local=mtime+zone,days=local>=0?local/86400:(local-86399)/86400,secs=local-days*86400;
    long y,ny;int m,d,nm,nd;civil(days,&y,&m,&d);
    long nl=now_utc+zone_now;civil(nl>=0?nl/86400:(nl-86399)/86400,&ny,&nm,&nd);
    o=num(o,(unsigned long)d);*o++=' ';o=put(o,month_names[m-1]);*o++=' ';
    if(full||y!=ny){o=num(o,(unsigned long)(y<0?0:y));if(!full)return o;*o++=' ';}
    o=num2(o,(unsigned long)(secs/3600));*o++=':';return num2(o,(unsigned long)(secs/60%60));
}

/* ---------- Icons come from the zenith-icons package. Without it, a few drawn shapes stand in, so
 * the buttons that carry only an icon stay visible. */
static unsigned int icon_color(int role){
    return role==ZUI_ICON_NORMAL?ui.theme.accent:role==ZUI_ICON_SELECTED?ui.theme.on_accent:role==ZUI_ICON_DISABLED?mix(ui.theme.surface,ui.theme.secondary,45):ui.theme.secondary;
}
static void span(int x0,int x1,int y,unsigned int c){if(x1>x0)zui_fill(&ui,(struct zui_rect){x0,y,x1-x0,1},c);}
/* A filled triangle pointing left (dir 0), right (1), up (2) or down (3) inside a square of side s. */
static void triangle(int x,int y,int s,int dir,unsigned int c){
    for(int i=0;i<s;i++){
        int w=dir<2?(i<s/2?i:s-1-i):0;
        if(dir==0)span(x+s/2-w,x+s/2+1,y+i,c);
        else if(dir==1)span(x+s/2,x+s/2+w+1,y+i,c);
        else{int h=i/2;if(dir==2)span(x+s/2-h,x+s/2+h+1,y+i/2+s/4,c);else span(x+s/2-h,x+s/2+h+1,y+s-1-i/2-s/4,c);}
    }
}
static void outline(struct zui_rect r,int t,unsigned int c){
    zui_fill(&ui,(struct zui_rect){r.x,r.y,r.w,t},c);zui_fill(&ui,(struct zui_rect){r.x,r.y+r.h-t,r.w,t},c);
    zui_fill(&ui,(struct zui_rect){r.x,r.y,t,r.h},c);zui_fill(&ui,(struct zui_rect){r.x+r.w-t,r.y,t,r.h},c);
}
static int icon(const char *name,int x,int y,int size,int role){
    if(zui_icon(&ui,name,x,y,size,role))return 1;
    unsigned int c=icon_color(role);int t=size>=32?3:2,u=size/8;
    if(starts(name,"arrow-")){
        int dir=same(name,"arrow-left")?0:same(name,"arrow-right")?1:same(name,"arrow-up")?2:3;
        if(dir<2){triangle(x+(dir?size/4:-size/8),y+size/4,size/2,dir,c);zui_fill(&ui,(struct zui_rect){x+(dir?2*u:3*u),y+size/2-t/2,size/2,t},c);}
        else{triangle(x+size/4,y-size/8,size/2+1,2,c);zui_fill(&ui,(struct zui_rect){x+size/2-t/2,y+3*u,t,size/2},c);}
        return 1;
    }
    if(same(name,"chevron-right")){triangle(x+size/4,y+size/4,size/2,1,c);return 1;}
    if(same(name,"chevron-down")){triangle(x+size/4,y+size/4,size/2,3,c);return 1;}
    if(same(name,"folder")||same(name,"folder-plus")){
        zui_round(&ui,(struct zui_rect){x+u,y+2*u,size*3/8,2*u},u,c);
        zui_round(&ui,(struct zui_rect){x+u,y+3*u,size-2*u,size-5*u},u,c);
        if(size>=32)zui_round(&ui,(struct zui_rect){x+u+t,y+3*u+t,size-2*u-2*t,size-5*u-2*t},u,mix(c,ui.theme.surface,70));
        return 1;
    }
    if(starts(name,"file")||same(name,"image")||same(name,"link")){
        struct zui_rect r={x+size/5,y+u,size*3/5,size-2*u};outline(r,t,c);
        if(size>=32)for(int i=0;i<3;i++)zui_fill(&ui,(struct zui_rect){r.x+2*t+u/2,r.y+r.h/3+i*u*3/2,r.w-4*t-u,t},c);
        return 1;
    }
    if(same(name,"layout-grid")){int h=size/2-2*u+1;for(int i=0;i<4;i++)zui_fill(&ui,(struct zui_rect){x+u+(i%2)*(h+u),y+u+(i/2)*(h+u),h,h},c);return 1;}
    if(same(name,"list")){for(int i=0;i<3;i++)zui_fill(&ui,(struct zui_rect){x+u,y+2*u+i*(size/4+1),size-2*u,t},c);return 1;}
    if(same(name,"columns-2")){struct zui_rect r={x+u,y+u,size-2*u,size-2*u};outline(r,t,c);zui_fill(&ui,(struct zui_rect){r.x+r.w/2-t/2,r.y,t,r.h},c);return 1;}
    if(same(name,"panel-right")){struct zui_rect r={x+u,y+u,size-2*u,size-2*u};outline(r,t,c);zui_fill(&ui,(struct zui_rect){r.x+r.w*2/3,r.y,t,r.h},c);return 1;}
    if(same(name,"search")){outline((struct zui_rect){x+u,y+u,size/2+u,size/2+u},t,c);triangle(x+size/2,y+size/2,size/2-u,3,c);return 1;}
    if(same(name,"house")){triangle(x,y-size/8,size,2,c);zui_fill(&ui,(struct zui_rect){x+2*u,y+size/2,size-4*u,size/2-u},c);return 1;}
    if(same(name,"hard-drive")||same(name,"download")){zui_round(&ui,(struct zui_rect){x+u,y+size/3,size-2*u,size/3},u,c);return 1;}
    if(same(name,"x")){for(int i=u;i<size-u;i++){zui_fill(&ui,(struct zui_rect){x+i,y+i,t,t},c);zui_fill(&ui,(struct zui_rect){x+size-1-i,y+i,t,t},c);}return 1;}
    return 0;
}

/* ---------- Paths: absolute, no trailing slash except "/". */
static void join(char *o,const char *folder,const char *name){
    unsigned long a=length(folder),b=length(name);
    if(a+b+2>1024){copy(o,folder,1024);return;}
    char *p=put(o,folder);if(!(a==1&&folder[0]=='/'))*p++='/';put(p,name);
}
static const char *leaf(const char *path){const char *l=path;for(const char *p=path;*p;p++)if(*p=='/'&&p[1])l=p+1;return l;}
static void parent(char *o,const char *path){
    copy(o,path,1024);long n=(long)length(o);while(n>1&&o[n-1]!='/')n--;if(n>1)n--;o[n]=0;if(!o[0])put(o,"/");
}
static char home[1024]="/";
static unsigned int my_uid;

/* ---------- Accounts: names for owners and groups, read once from /etc/passwd and /etc/group. */
#define NAMES 64
static struct { unsigned int id; char name[24]; } users[NAMES],groups[NAMES];
static int user_count,group_count;
static void read_ids(const char *path,int is_group){
    static char buffer[8192];long fd=zenith_call(2,(unsigned long)path,length(path),0);if(fd<0)return;
    long total=0,r;while(total<(long)sizeof(buffer)-1&&(r=zenith_call(0,(unsigned long)fd,(unsigned long)(buffer+total),sizeof(buffer)-1-total>4096?4096:sizeof(buffer)-1-(unsigned long)total))>0)total+=r;
    zenith_call(3,(unsigned long)fd,0,0);buffer[total]=0;
    char *line=buffer;
    while(*line){
        char *end=line;while(*end&&*end!='\n')end++;
        char *field[7];int f=0;char *p=line;field[f++]=p;
        while(p<end&&f<7){if(*p==':')field[f++]=p+1;p++;}
        if(f>=3){
            unsigned long id=0;for(char *q=field[2];q<end&&*q>='0'&&*q<='9';q++)id=id*10+(unsigned long)(*q-'0');
            int n=(int)(field[1]-field[0]-1);if(n>23)n=23;
            if(is_group&&group_count<NAMES){groups[group_count].id=(unsigned int)id;for(int i=0;i<n;i++)groups[group_count].name[i]=field[0][i];groups[group_count].name[n]=0;group_count++;}
            if(!is_group&&user_count<NAMES){users[user_count].id=(unsigned int)id;for(int i=0;i<n;i++)users[user_count].name[i]=field[0][i];users[user_count].name[n]=0;user_count++;
                if(id==my_uid&&f>=6){int h=(int)((f>=7?field[6]-1:end)-field[5]);if(h>0&&h<1000&&field[5][0]=='/'){for(int i=0;i<h;i++)home[i]=field[5][i];home[h]=0;}}}
        }
        line=*end?end+1:end;
    }
}
static char *id_text(char *o,unsigned int id,int is_group){
    int n=is_group?group_count:user_count;
    for(int i=0;i<n;i++){if(is_group?groups[i].id==id:users[i].id==id)return put(o,is_group?groups[i].name:users[i].name);}
    return num(o,id);
}

/* ---------- The listing of the active tab's folder. */
#define MAX_ENTRIES 4096
#define POOL_BYTES (MAX_ENTRIES*72)
struct entry { unsigned int name,mode,uid,gid,target,links; unsigned long size; long mtime; int zone,items; unsigned char selected; };
/* One listing per pane of the active tab (HZ2 split view); L is the active pane's. */
struct listing {
    struct entry entries[MAX_ENTRIES];char pool[POOL_BYTES];
    int entry_count,truncated;unsigned int pool_used;
    int order[MAX_ENTRIES],shown;
    long list_error;int folder_writable;
    struct zenith_filesystem fs;int have_fs;
    int focus_index,anchor;   /* positions in order[] */
    struct zui_scroll content_scroll;struct zui_rect content_rect;int columns_now,rows_visible;
};
static struct listing listings[2];static struct listing *L=&listings[0];
static int scratch[MAX_ENTRIES];
static unsigned char page_buffer[Z_FILES_PAGE] __attribute__((aligned(16)));
static int no_abi;
static const char *name_of(int i){return L->pool+L->entries[i].name;}
static int is_folder(const struct entry *e){return (e->mode&0xf000)==0x4000||((e->mode&0xf000)==0xa000&&(e->target&0xf000)==0x4000);}
static int is_link(const struct entry *e){return (e->mode&0xf000)==0xa000;}

/* ---------- Tabs. */
#define MAX_TABS 8
#define HISTORY 12
enum { SORT_NAME=0,SORT_SIZE=1,SORT_TIME=2,SORT_TYPE=3 };
/* A pane: one folder view with its own history, view, sort, zoom and filter. A tab has one, or two side by side
 * (split view, F3); the active pane takes the toolbar, the keyboard and the information panel. */
struct pane { char path[1024],focus[256],filter[96]; char history[HISTORY][1024]; int hcount,hpos,view,sort,descending,scroll,zoom; };
struct tab { struct pane pane[2]; int split,active; };
static struct tab tabs[MAX_TABS];static int tab_count,current;
static struct zui_tabs strip;
static int show_hidden,info_on=1;
static struct tab *TB(void){return &tabs[current];}
static struct pane *T(void){return &tabs[current].pane[tabs[current].active];}
static void stop_search(void);
static int admin,needs_admin;   /* file administration granted through the portal; offered after a refusal */

/* Natural, case-insensitive: "file 2" before "file 10". */
static int natural(const char *a,const char *b){
    while(*a&&*b){
        if(*a>='0'&&*a<='9'&&*b>='0'&&*b<='9'){
            while(*a=='0'){a++;}
            while(*b=='0'){b++;}
            int la=0,lb=0;while(a[la]>='0'&&a[la]<='9')la++;while(b[lb]>='0'&&b[lb]<='9')lb++;
            if(la!=lb)return la-lb;
            for(int i=0;i<la;i++)if(a[i]!=b[i])return a[i]-b[i];
            a+=la;b+=lb;continue;
        }
        char x=lower(*a),y=lower(*b);if(x!=y)return (unsigned char)x-(unsigned char)y;a++;b++;
    }
    return (unsigned char)*a-(unsigned char)*b;
}
static const char *extension(const char *name){const char *dot=0;for(const char *p=name+1;*p;p++)if(*p=='.')dot=p+1;return dot?dot:"";}
static int compare(int i,int j){
    const struct entry *a=&L->entries[i],*b=&L->entries[j];
    int fa=is_folder(a),fb=is_folder(b);if(fa!=fb)return fb-fa;
    int r=0;struct pane *t=T();
    if(t->sort==SORT_SIZE)r=fa?(a->items>b->items)-(a->items<b->items):(a->size>b->size)-(a->size<b->size);
    else if(t->sort==SORT_TIME)r=(a->mtime>b->mtime)-(a->mtime<b->mtime);
    else if(t->sort==SORT_TYPE)r=natural(extension(name_of(i)),extension(name_of(j)));
    if(!r)r=natural(name_of(i),name_of(j));
    return t->descending?-r:r;
}
static void merge_sort(int *a,int n){
    for(int width=1;width<n;width*=2){
        for(int lo=0;lo<n;lo+=2*width){
            int mid=lo+width<n?lo+width:n,hi=lo+2*width<n?lo+2*width:n,i=lo,j=mid,k=lo;
            while(i<mid&&j<hi)scratch[k++]=compare(a[i],a[j])<=0?a[i++]:a[j++];
            while(i<mid){scratch[k++]=a[i++];}
            while(j<hi){scratch[k++]=a[j++];}
        }
        for(int i=0;i<n;i++)a[i]=scratch[i];
    }
}
static int matches(const char *name,const char *filter){
    if(!filter[0])return 1;
    for(const char *s=name;*s;s++){const char *p=s,*q=filter;while(*q&&lower(*p)==lower(*q)){p++;q++;}if(!*q)return 1;}
    return 0;
}
static void arrange(void){
    L->shown=0;struct pane *t=T();
    for(int i=0;i<L->entry_count;i++){
        const char *n=name_of(i);
        if(n[0]=='.'&&!show_hidden){L->entries[i].selected=0;continue;}
        if(!matches(n,t->filter)){L->entries[i].selected=0;continue;}
        L->order[L->shown++]=i;
    }
    merge_sort(L->order,L->shown);
    L->focus_index=-1;
    for(int k=0;k<L->shown;k++)if(t->focus[0]&&same(name_of(L->order[k]),t->focus))L->focus_index=k;
    if(L->focus_index>=L->shown)L->focus_index=L->shown-1;
    L->anchor=L->focus_index;
    ui.dirty=1;
}
static int selected_count(unsigned long *bytes){
    int n=0;if(bytes)*bytes=0;
    for(int k=0;k<L->shown;k++){const struct entry *e=&L->entries[L->order[k]];if(e->selected){n++;if(bytes&&!is_folder(e))*bytes+=e->size;}}
    return n;
}
static void announce_path(void){
    char line[1200],*o=put(line,"HORIZON_PATH ");o=put(o,T()->path);o=put(o," ");o=num(o,(unsigned long)L->entry_count);
    if(L->list_error){o=put(o," error=");o=num(o,(unsigned long)-L->list_error);}
    put(o,"\n");print(line);
}
static int in_trash(void);static void trash_prune(void);static int make_dirs(const char *p);static char trash_files[1024],trash_info[1024];
static void load(void){
    struct pane *t=T();
    if(in_trash()){make_dirs(trash_files);make_dirs(trash_info);trash_prune();}
    static struct zenith_files_list request;
    L->entry_count=0;L->pool_used=0;L->truncated=0;L->list_error=0;L->folder_writable=0;
    unsigned int offset=0;
    for(int pages=0;pages<64;pages++){
        request=(struct zenith_files_list){1,Z_FILES_COUNT_ITEMS,offset,(unsigned int)length(t->path),{0}};
        copy(request.path,t->path,sizeof(request.path));
        long r=zenith_files_list(&request,page_buffer);
        if(r<0){L->list_error=r;if(r==-38)no_abi=1;if((r==-13||r==-1)&&!admin)needs_admin=1;break;}
        const struct zenith_files_page *h=(const void *)page_buffer;
        L->folder_writable=h->flags&1;now_utc=h->now;zone_now=h->zone;
        unsigned int at=sizeof(*h);
        for(unsigned int k=0;k<h->count&&at+sizeof(struct zenith_file_entry)<=Z_FILES_PAGE;k++){
            const struct zenith_file_entry *f=(const void *)(page_buffer+at);
            if(f->record_bytes<sizeof(*f)||at+f->record_bytes>Z_FILES_PAGE)break;
            at+=f->record_bytes;
            if(L->entry_count>=MAX_ENTRIES||L->pool_used+f->name_bytes+1>POOL_BYTES){L->truncated=1;continue;}
            struct entry *e=&L->entries[L->entry_count++];
            *e=(struct entry){L->pool_used,f->mode,f->uid,f->gid,f->target_mode,f->links,f->size,f->mtime,f->zone,f->items,0};
            for(unsigned int c=0;c<f->name_bytes;c++)L->pool[L->pool_used+c]=f->name[c];
            L->pool[L->pool_used+f->name_bytes]=0;L->pool_used+=f->name_bytes+1;
        }
        if(!h->next)break;
        offset=h->next;
    }
    L->have_fs=zenith_files_filesystem(t->path,length(t->path),&L->fs)==0;
    arrange();announce_path();
}

/* ---------- Window title: the active tab's folder. */
static void pane_label(char *o,const struct pane *t){
    if(same(t->path,home))put(o,"Home");else if(same(t->path,"/"))put(o,"Root");else copy(o,leaf(t->path),64);
}
static void tab_label(char *o,const struct tab *t){
    pane_label(o,&t->pane[t->split?0:t->active]);
    if(t->split){char b[80];pane_label(b,&t->pane[1]);char *e=o+length(o);e=put(e," | ");copy(e,b,40);}
}
static void update_title(void){
    char title[160],label[80];pane_label(label,T());
    char *o=put(title,label);o=put(o," \xE2\x80\x94 Horizon");if(admin)put(o," (administrator)");
    zenith_gui_set_title(handle,title,length(title));
}

/* ---------- Navigation. */
static char status_note[160];
static int reveal_later;
static void remember_focus(void){
    struct pane *t=T();t->focus[0]=0;
    if(L->focus_index>=0&&L->focus_index<L->shown)copy(t->focus,name_of(L->order[L->focus_index]),sizeof(t->focus));
}
static void set_view(int view){T()->view=view;zui_scroll_jump(&L->content_scroll,0);reveal_later=1;print(view?"HORIZON_VIEW details\n":"HORIZON_VIEW icons\n");ui.dirty=1;}
static void go(const char *path,int record){
    struct pane *t=T();
    if(record){
        if(t->hcount==0){copy(t->history[0],t->path,1024);t->hcount=1;t->hpos=0;}
        if(t->hpos<t->hcount-1)t->hcount=t->hpos+1;
        if(t->hcount==HISTORY){for(int i=1;i<HISTORY;i++)copy(t->history[i-1],t->history[i],1024);t->hcount--;t->hpos--;}
        copy(t->history[t->hcount++],path,1024);t->hpos=t->hcount-1;
    }
    /* Coming back up: focus the folder we came from. */
    char from[1024];copy(from,t->path,1024);
    stop_search();
    copy(t->path,path,1024);t->focus[0]=0;t->filter[0]=0;
    char up[1024];parent(up,from);
    if(same(up,path))copy(t->focus,leaf(from),sizeof(t->focus));
    zui_scroll_jump(&L->content_scroll,0);
    status_note[0]=0;
    load();update_title();
}
static void back(void){struct pane *t=T();if(t->hpos>0){t->hpos--;go(t->history[t->hpos],0);}}
static void forward(void){struct pane *t=T();if(t->hpos<t->hcount-1){t->hpos++;go(t->history[t->hpos],0);}}
static void up(void){struct pane *t=T();if(same(t->path,"/"))return;char p[1024];parent(p,t->path);go(p,1);}
static int announce_focus,announce_panes;
/* Panes of the active tab. with_pane only points T() and L at a pane (drawing, loading). */
static struct zui_rect pane_rect[2];
static void with_pane(int p){TB()->active=p;L=&listings[p];}
static void remember_panes(void){
    int a=TB()->active;
    for(int p=0;p<=TB()->split;p++){with_pane(p);remember_focus();T()->scroll=L->content_scroll.offset;}
    with_pane(a);
}
static void load_tab(void){
    int a=TB()->active;
    for(int p=0;p<=TB()->split;p++){with_pane(p);load();zui_scroll_jump(&L->content_scroll,T()->scroll);}
    with_pane(a);
}
static int pane_at(int x,int y){for(int p=0;p<=TB()->split;p++)if(inside(pane_rect[p],x,y))return p;return -1;}
static void activate_pane(int p){
    if(!TB()->split||p==TB()->active||p<0||p>1)return;
    remember_focus();with_pane(p);update_title();
    char line[1100],*o=put(line,"HORIZON_PANE ");o=num(o,(unsigned long)p);o=put(o," ");o=put(o,T()->path);put(o,"\n");print(line);
    ui.dirty=1;
}
/* F3: a second pane beside the first, on the same folder (Dolphin's split); F3 again keeps the active pane. */
static void toggle_split(void){
    struct tab *b=TB();
    remember_focus();T()->scroll=L->content_scroll.offset;
    if(!b->split){
        b->pane[1]=b->pane[b->active];b->split=1;with_pane(1);load();zui_scroll_jump(&L->content_scroll,T()->scroll);
    }else{
        if(b->active==1)b->pane[0]=b->pane[1];
        b->split=0;with_pane(0);load();zui_scroll_jump(&L->content_scroll,T()->scroll);
    }
    print(b->split?"HORIZON_SPLIT on\n":"HORIZON_SPLIT off\n");announce_panes=b->split;update_title();ui.dirty=1;
}
static void open_tab(const char *path,int activate){
    if(tab_count>=MAX_TABS){copy(status_note,"Horizon keeps at most 8 tabs. Close one first.",sizeof(status_note));ui.dirty=1;return;}
    if(tab_count)remember_panes();
    struct pane *from=tab_count?T():0;
    tabs[tab_count]=(struct tab){0};
    struct pane *t=&tabs[tab_count].pane[0];
    *t=(struct pane){{0},{0},{0},{{0}},1,0,from?from->view:0,from?from->sort:0,from?from->descending:0,0,from?from->zoom:1};
    copy(t->path,path,1024);copy(t->history[0],path,1024);
    tab_count++;
    if(activate){current=tab_count-1;with_pane(0);zui_scroll_jump(&L->content_scroll,0);load();update_title();}
    char line[1100],*o=put(line,"HORIZON_TABS ");o=num(o,(unsigned long)tab_count);o=put(o," active=");o=num(o,(unsigned long)current);put(o,"\n");print(line);
    ui.dirty=1;
}
static int done;
static void switch_tab(int i){
    stop_search();
    if(i<0||i>=tab_count||i==current)return;
    remember_panes();
    current=i;load_tab();update_title();
    char line[1100],*o=put(line,"HORIZON_TABS ");o=num(o,(unsigned long)tab_count);o=put(o," active=");o=num(o,(unsigned long)current);o=put(o," ");o=put(o,T()->path);put(o,"\n");print(line);
}
static void close_tab(int i){
    if(i<0||i>=tab_count)return;
    if(tab_count==1){done=1;return;}
    remember_panes();
    for(int k=i;k+1<tab_count;k++)tabs[k]=tabs[k+1];
    tab_count--;
    if(current>i||current>=tab_count)current=current>i?current-1:tab_count-1;
    load_tab();update_title();
    char line[64],*o=put(line,"HORIZON_TABS ");o=num(o,(unsigned long)tab_count);o=put(o," active=");o=num(o,(unsigned long)current);put(o,"\n");print(line);
}

/* ---------- Places and devices. */
#define PLACES 12
static struct { char path[1024],label[40]; const char *icon; int device; unsigned long total,available; } places[PLACES];
static int place_count;
static int is_directory(const char *path){struct zenith_stat st;return zenith_call(4,(unsigned long)path,length(path),(unsigned long)&st)==0&&(st.mode&0xf000)==0x4000;}
static void add_place(const char *path,const char *label,const char *icon,int device){
    if(place_count>=PLACES)return;
    copy(places[place_count].path,path,1024);copy(places[place_count].label,label,40);
    places[place_count].icon=icon;places[place_count].device=device;places[place_count].total=places[place_count].available=0;
    if(device){struct zenith_filesystem f;if(zenith_files_filesystem(path,length(path),&f)==0){places[place_count].total=f.total;places[place_count].available=f.available;}}
    place_count++;
}
static void find_places(void){
    place_count=0;char p[1024];
    add_place(home,"Home","house",0);
    join(p,home,"Documents");if(is_directory(p))add_place(p,"Documents","file-text",0);
    join(p,home,"Downloads");if(is_directory(p))add_place(p,"Downloads","download",0);
    if(trash_files[0])add_place(trash_files,"Trash","trash",0);
    add_place("/","Root","folder",0);
    add_place("/","Zenith","hard-drive",1);
    /* Other mounted filesystems under /mnt. */
    static struct zenith_files_list request;
    request=(struct zenith_files_list){1,0,0,4,{0}};put(request.path,"/mnt");
    static unsigned char mnt[Z_FILES_PAGE] __attribute__((aligned(16)));
    if(zenith_files_list(&request,mnt)>0){
        const struct zenith_files_page *h=(const void *)mnt;unsigned int at=sizeof(*h);
        for(unsigned int k=0;k<h->count&&place_count<PLACES;k++){
            const struct zenith_file_entry *f=(const void *)(mnt+at);if(f->record_bytes<sizeof(*f))break;at+=f->record_bytes;
            if((f->mode&0xf000)!=0x4000)continue;
            join(p,"/mnt",f->name);struct zenith_filesystem m;
            if(zenith_files_filesystem(p,length(p),&m)==0&&same(m.mount,p))add_place(p,f->name,"hard-drive",1);
        }
    }
}

/* ---------- File types: a name, an icon. */
static const char *kind_of(const struct entry *e,const char *name){
    if(is_link(e))return is_folder(e)?"Link to a folder":"Link";
    if(is_folder(e))return "Folder";
    if((e->mode&0xf000)!=0x8000)return "Special file";
    const char *x=extension(name);char l[12];int n=0;while(x[n]&&n<11){l[n]=lower(x[n]);n++;}l[n]=0;
    static const char *table[][2]={{"md","Markdown document"},{"txt","Text document"},{"log","Log"},{"conf","Configuration"},{"cfg","Configuration"},
        {"ini","Configuration"},{"json","JSON document"},{"csv","CSV spreadsheet"},{"c","C source"},{"h","C header"},{"cs","C# source"},{"py","Python script"},
        {"sh","Shell script"},{"html","Web page"},{"svg","SVG image"},{"png","PNG image"},{"jpg","JPEG image"},{"jpeg","JPEG image"},{"gif","GIF image"},
        {"bmp","Bitmap image"},{"zpk","Zenith package"},{"zip","ZIP archive"},{"tar","Tar archive"},{"gz","Compressed file"},{"xz","Compressed file"},
        {"iso","Disk image"},{"img","Disk image"},{"elf","Program"},{"pdf","PDF document"},{"wad","Game data"}};
    for(unsigned int i=0;i<sizeof(table)/sizeof(table[0]);i++)if(same(l,table[i][0]))return table[i][1];
    if(e->mode&0111)return "Program";
    return "File";
}
static const char *icon_of(const struct entry *e,const char *name){
    if(is_folder(e))return "folder";
    if(is_link(e))return "link";
    const char *k=kind_of(e,name);
    if(starts(k,"PNG")||starts(k,"JPEG")||starts(k,"GIF")||starts(k,"Bitmap")||starts(k,"SVG"))return "image";
    if(same(k,"C source")||same(k,"C header")||same(k,"C# source")||same(k,"Python script")||same(k,"Shell script")||same(k,"Web page")||same(k,"JSON document"))return "file-code";
    if(starts(k,"ZIP")||starts(k,"Tar")||same(k,"Compressed file")||same(k,"Zenith package"))return "file-archive";
    if(same(k,"Markdown document")||same(k,"Text document")||same(k,"Log")||same(k,"Configuration")||same(k,"CSV spreadsheet"))return "file-text";
    return "file";
}
static char *permissions(char *o,unsigned int mode){
    static const char bits[]="rwxrwxrwx";
    for(int i=0;i<9;i++)*o++=(mode>>(8-i))&1?bits[i]:'-';
    *o=0;return o;
}
static char *items_text(char *o,int items){
    if(items<0)return put(o,"\xE2\x80\x94");
    o=num(o,(unsigned long)items);return put(o,items==1?" item":" items");
}

/* ---------- Hit areas, recorded while drawing; later ones are on top. */
enum { H_NONE=0,H_BACK=1,H_FORWARD,H_UP,H_ICONS,H_DETAILS,H_INFO,H_FILTER,H_LOCATION,H_CANCEL_JOB,H_CLOSE_JOB,H_CONTENT,H_CONTENT2,H_SPLIT,H_UNLOCK,
       H_CRUMB=100,H_PLACE=200,H_COLUMN=300,H_MENU=400,H_MODAL=500,H_ITEM=1000 };
/* Item and column hits carry their pane: H_ITEM + pane*PANE_ITEMS + k, H_COLUMN + pane*8 + sort. */
#define PANE_ITEMS 5000
static int drawing_pane;
static int item_id(int k){return H_ITEM+drawing_pane*PANE_ITEMS+k;}
static int content_id(void){return drawing_pane?H_CONTENT2:H_CONTENT;}
#define MAX_HITS 640
static struct { struct zui_rect r; int id; } hits[MAX_HITS];static int hit_count;
static void hit(struct zui_rect r,int id){if(hit_count<MAX_HITS){hits[hit_count].r=r;hits[hit_count].id=id;hit_count++;}}
static int hit_at(int x,int y){for(int i=hit_count-1;i>=0;i--)if(inside(hits[i].r,x,y))return hits[i].id;return H_NONE;}
static int hover=H_NONE,pointer_x=-1,pointer_y=-1;

/* ---------- Text fields: the typed location, the filter, and the name prompt. */
static char location_text[1024];static struct zui_input location_input;static int editing_location;
static struct zui_input filter_input;static int filter_focus;
static void start_location(void){
    copy(location_text,T()->path,sizeof(location_text));zui_input_init(&location_input,location_text,sizeof(location_text));
    location_input.length=(unsigned int)length(location_text);location_input.cursor=location_input.length;
    editing_location=1;filter_focus=0;ui.dirty=1;
}
/* ---------- Search below this folder (HZ3): a bounded walk of the folders under the pane's folder, a few per
 * event-loop pass, collecting names that contain the query. Results are relative paths ("Notes/a.txt") in the
 * pane's listing, so opening, copying and deleting them work as in any folder. Indexed search is S5. */
#define SEARCH_FOLDERS 2000
#define SEARCH_QUEUE 512
static int search_mode,search_on,search_done,search_pane,search_folders,search_limited;
static char search_query[96],search_queue[SEARCH_QUEUE][256];static int search_head,search_tail;
static void stop_search(void){search_on=0;search_mode=0;}
static void start_search(void){
    copy(search_query,T()->filter,sizeof(search_query));if(!search_query[0])return;
    search_on=1;search_done=0;search_pane=TB()->active;search_folders=0;search_limited=0;search_head=search_tail=0;
    search_queue[search_tail++][0]=0;
    L->entry_count=0;L->pool_used=0;L->truncated=0;L->list_error=0;L->shown=0;L->focus_index=L->anchor=-1;
    zui_scroll_jump(&L->content_scroll,0);
    char line[160],*o=put(line,"HORIZON_SEARCH start ");o=put(o,search_query);put(o,"\n");print(line);ui.dirty=1;
}
static void search_step(void){
    if(!search_on||search_done)return;
    int keep=TB()->active;if(keep!=search_pane)with_pane(search_pane);
    static struct zenith_files_list request;
    for(int folders=0;folders<6&&search_head!=search_tail;folders++){
        char rel[256];copy(rel,search_queue[search_head],sizeof(rel));search_head=(search_head+1)%SEARCH_QUEUE;search_folders++;
        char folder[1024];if(rel[0])join(folder,T()->path,rel);else copy(folder,T()->path,sizeof(folder));
        unsigned int offset=0;
        for(int pages=0;pages<16;pages++){
            request=(struct zenith_files_list){1,0,offset,(unsigned int)length(folder),{0}};copy(request.path,folder,sizeof(request.path));
            if(zenith_files_list(&request,page_buffer)<0)break;
            const struct zenith_files_page *h=(const void *)page_buffer;unsigned int at=sizeof(*h);
            for(unsigned int k=0;k<h->count&&at+sizeof(struct zenith_file_entry)<=Z_FILES_PAGE;k++){
                const struct zenith_file_entry *f=(const void *)(page_buffer+at);if(f->record_bytes<sizeof(*f))break;at+=f->record_bytes;
                if(f->name[0]=='.'&&!show_hidden)continue;
                char path[256];unsigned long rl=length(rel);
                if(rl+f->name_bytes+2>sizeof(path))continue;
                if(rl){copy(path,rel,sizeof(path));path[rl]='/';copy(path+rl+1,f->name,sizeof(path)-rl-1);}else copy(path,f->name,sizeof(path));
                int folder_entry=(f->mode&0xf000)==0x4000;
                if(matches(f->name,search_query)){
                    unsigned long n=length(path);
                    if(L->entry_count>=MAX_ENTRIES||L->pool_used+n+1>POOL_BYTES){search_limited=1;}
                    else{
                        struct entry *e=&L->entries[L->entry_count++];
                        *e=(struct entry){L->pool_used,f->mode,f->uid,f->gid,f->target_mode,f->links,f->size,f->mtime,f->zone,-1,0};
                        copy(L->pool+L->pool_used,path,n+1);L->pool_used+=(unsigned int)n+1;
                    }
                }
                if(folder_entry){
                    int queued=(search_tail-search_head+SEARCH_QUEUE)%SEARCH_QUEUE;
                    if(queued<SEARCH_QUEUE-1&&search_folders+queued<SEARCH_FOLDERS){copy(search_queue[search_tail],path,256);search_tail=(search_tail+1)%SEARCH_QUEUE;}
                    else search_limited=1;
                }
            }
            if(!h->next)break;
            offset=h->next;
        }
    }
    if(search_head==search_tail||search_folders>=SEARCH_FOLDERS){
        search_done=1;
        char line[96],*o=put(line,"HORIZON_SEARCH done results=");o=num(o,(unsigned long)L->entry_count);o=put(o," folders=");o=num(o,(unsigned long)search_folders);
        put(o,search_limited?" limited\n":"\n");print(line);
    }
    /* Results show without the query as a filter (they already match), sorted as usual. */
    char filter[96];copy(filter,T()->filter,sizeof(filter));T()->filter[0]=0;arrange();copy(T()->filter,filter,sizeof(T()->filter));
    if(keep!=search_pane)with_pane(keep);
    ui.dirty=1;
}
static void focus_filter(void){
    zui_input_init(&filter_input,T()->filter,sizeof(T()->filter));filter_input.length=(unsigned int)length(T()->filter);filter_input.cursor=filter_input.length;
    filter_focus=1;editing_location=0;ui.dirty=1;
}

/* ---------- Transfer jobs: at most two at a time; the strip shows the newest. */
struct job { long id; int op,count,state,shown; char target[64]; struct zenith_job_status status; long finished_at; };
static struct job jobs[2];
static struct job *strip_job(void){
    struct job *best=0;
    for(int i=0;i<2;i++)if(jobs[i].id&&(!best||jobs[i].id>best->id))best=&jobs[i];
    return best;
}
static void announce_job(const struct job *j){
    static const char *states[7]={"?","counting","working","asking","done","failed","cancelled"};
    char line[400],*o=put(line,"HORIZON_JOB ");o=num(o,(unsigned long)j->id);o=put(o," ");o=put(o,states[j->state<7?j->state:0]);o=put(o," ");
    o=num(o,j->status.items_done);o=put(o,"/");o=num(o,j->status.items_total);o=put(o," errors=");o=num(o,j->status.errors);
    if(j->state==Z_JOB_ASKING){o=put(o," question=");o=put(o,j->status.question);}
    else if(j->status.message[0]){o=put(o," ");o=put(o,j->status.message);}
    put(o,"\n");print(line);
}
static long clock_ms(void){long c=zui_motion_clock();return c;}
static int asking_dismissed;
static void poll_jobs(void){
    for(int i=0;i<2;i++){
        struct job *j=&jobs[i];
        if(!j->id||Z_JOB_FINAL(j->state))continue;
        long s=zenith_job_status(j->id,&j->status);
        if(s<0){j->state=Z_JOB_FAILED;copy(j->status.message,"The job was lost.",sizeof(j->status.message));announce_job(j);ui.dirty=1;continue;}
        int changed=(int)s!=j->state;
        static unsigned int last_items[2];
        if(changed||j->status.items_done!=last_items[i]){last_items[i]=j->status.items_done;ui.dirty=1;}
        if(changed){j->state=(int)s;announce_job(j);if(s!=Z_JOB_ASKING)asking_dismissed=0;
            if(Z_JOB_FINAL(s)&&j->status.errors&&!admin){for(const char *q=j->status.message;*q;q++)if(starts(q,"ermission")||starts(q,"denied")){needs_admin=1;break;}}}
        if(Z_JOB_FINAL(s)){
            j->finished_at=clock_ms();zenith_job_forget(j->id);
            remember_focus();load();find_places();
        }
    }
}
static struct job *asking(void){for(int i=0;i<2;i++)if(jobs[i].id&&jobs[i].state==Z_JOB_ASKING)return &jobs[i];return 0;}
static void start_job(int op,const char *destination,int count,const char *const *sources){
    struct job *slot=0;
    for(int i=0;i<2;i++)if(!jobs[i].id||Z_JOB_FINAL(jobs[i].state)){slot=&jobs[i];break;}
    if(!slot){copy(status_note,"Two transfers are running. Wait for one to finish.",sizeof(status_note));ui.dirty=1;return;}
    static struct zenith_job_request r;
    r.version=1;r.operation=(unsigned int)op;r.flags=0;r.count=(unsigned int)count;r.reserved=0;
    unsigned int at=0;
    const char *d=destination;while(*d&&at<sizeof(r.text)-1)r.text[at++]=*d++;r.text[at++]=0;
    for(int i=0;i<count;i++){
        unsigned long n=length(sources[i]);
        if(at+n+1>sizeof(r.text)){copy(status_note,"Too many long names at once. Choose fewer items.",sizeof(status_note));ui.dirty=1;return;}
        for(unsigned long k=0;k<n;k++){r.text[at++]=sources[i][k];}
        r.text[at++]=0;
    }
    r.text_bytes=at;
    long id=zenith_job_start(&r);
    if(id<0){
        copy(status_note,id==-38?"This Zenith kernel cannot run transfers. Update Zenith.":id==-22?"A folder cannot go inside itself.":
            id==-2?"An item no longer exists.":id==-20?"The destination is not a folder.":id==-24||id==-16?"Too many transfers are running.":"The transfer was refused.",sizeof(status_note));
        char line[64],*o=put(line,"HORIZON_JOB refused ");o=num(o,(unsigned long)-id);put(o,"\n");print(line);
        ui.dirty=1;return;
    }
    *slot=(struct job){id,op,count,Z_JOB_COUNTING,1,"",{0},0};
    copy(slot->target,op==Z_JOB_DELETE?"":same(destination,home)?"Home":same(destination,"/")?"Root":leaf(destination),sizeof(slot->target));
    if(op==Z_JOB_EXTRACT)copy(slot->target,"here",sizeof(slot->target));
    char line[64],*o=put(line,"HORIZON_JOB ");o=num(o,(unsigned long)id);put(o," started\n");print(line);
    status_note[0]=0;ui.dirty=1;
}

/* ---------- The clipboard is Horizon's own: paths and whether they were cut. */
#define CLIP 64
static char clip_paths[CLIP][1024];static int clip_count,clip_cut;
static int selection_paths(const char *list[],char storage[][1024],int max){
    int n=0;
    for(int k=0;k<L->shown&&n<max;k++)if(L->entries[L->order[k]].selected){join(storage[n],T()->path,name_of(L->order[k]));list[n]=storage[n];n++;}
    if(!n&&L->focus_index>=0&&L->focus_index<L->shown){join(storage[0],T()->path,name_of(L->order[L->focus_index]));list[0]=storage[0];n=1;}
    return n;
}
static void copy_selection(int cut){
    const char *list[CLIP];int n=selection_paths(list,clip_paths,CLIP);
    if(!n)return;
    clip_count=n;clip_cut=cut;
    char t[96],*o=num(status_note,(unsigned long)n);o=put(o,n==1?" item ":" items ");put(o,cut?"cut: paste to move.":"copied: paste to copy.");(void)t;
    char line[64];o=put(line,cut?"HORIZON_CLIP cut ":"HORIZON_CLIP copy ");o=num(o,(unsigned long)n);put(o,"\n");print(line);
    ui.dirty=1;
}
static void paste(void){
    if(!clip_count){copy(status_note,"Nothing to paste. Copy or cut items first.",sizeof(status_note));ui.dirty=1;return;}
    const char *list[CLIP];for(int i=0;i<clip_count;i++)list[i]=clip_paths[i];
    start_job(clip_cut?Z_JOB_MOVE:Z_JOB_COPY,T()->path,clip_count,list);
    if(clip_cut)clip_count=0;
}

/* ---------- Dialogs: delete (the toolkit's), a name prompt and the transfer question (Horizon's own). */
static struct zui_dialog confirm;static char confirm_title[96],confirm_message[240];
static char delete_paths[CLIP][1024];static int delete_count;
static char delete_note[160];
static void ask_delete(void){
    const char *list[CLIP];delete_count=selection_paths(list,delete_paths,CLIP);
    if(!delete_count)return;
    char *o=put(confirm_title,"Delete ");
    if(delete_count==1){o=put(o,"\"");char l[64];copy(l,leaf(delete_paths[0]),sizeof(l));o=put(o,l);o=put(o,"\"");}
    else{o=num(o,(unsigned long)delete_count);o=put(o," items");}
    put(o," permanently?");
    copy(confirm_message,delete_note[0]?delete_note:"They are removed at once and cannot be restored.",sizeof(confirm_message));delete_note[0]=0;
    confirm=(struct zui_dialog){1,900,confirm_title,confirm_message,{"Delete","Cancel",0},2};
    print("HORIZON_ASK delete\n");ui.dirty=1;
}
/* ---------- Trash (HZ3): ~/.local/share/Trash in the freedesktop layout, files/NAME and info/NAME.trashinfo
 * (Path=, DeletionDate=). Items move there by rename, so only items on the home folder's filesystem can go to
 * the Trash; others are deleted permanently after asking. It keeps at most TRASH_LIMIT items. */
#define TRASH_LIMIT 1000
static void trash_paths(void){char d[1024];join(d,home,".local/share/Trash");join(trash_files,d,"files");join(trash_info,d,"info");}
static int exists(const char *p){struct zenith_stat st;return zenith_call(4,(unsigned long)p,length(p),(unsigned long)&st)==0;}
static int make_dirs(const char *p){
    char t[1024];copy(t,p,sizeof(t));unsigned long n=length(t);
    for(unsigned long i=1;i<=n;i++){
        if(t[i]!='/'&&t[i])continue;
        char c=t[i];t[i]=0;
        if(!exists(t)){long r=zenith_mkdir(t,length(t));if(r<0&&r!=-17)return 0;}
        t[i]=c;
    }
    return 1;
}
static int in_trash(void){return trash_files[0]&&same(T()->path,trash_files);}
static int on_home_filesystem(const char *path){
    struct zenith_filesystem a,b;char f[1024];parent(f,path);
    return zenith_files_filesystem(f,length(f),&a)==0&&zenith_files_filesystem(home,length(home),&b)==0&&same(a.mount,b.mount);
}
static long write_small(const char *path,const char *text){
    long fd=zenith_call(2,(unsigned long)path,length(path),Z_OPEN_WRITE|Z_OPEN_CREATE|Z_OPEN_TRUNCATE);if(fd<0)return fd;
    unsigned long n=length(text),done=0;long w=0;
    while(done<n){w=zenith_call(1,(unsigned long)fd,(unsigned long)(text+done),n-done>4096?4096:n-done);if(w<=0)break;done+=(unsigned long)w;}
    zenith_call(74,(unsigned long)fd,0,0);zenith_call(3,(unsigned long)fd,0,0);
    return w<0?w:done<n?-28:0;
}
static long read_small(const char *path,char *out,unsigned long capacity){
    long fd=zenith_call(2,(unsigned long)path,length(path),0);if(fd<0){out[0]=0;return fd;}
    unsigned long n=0;long r;
    while(n+1<capacity&&(r=zenith_call(0,(unsigned long)fd,(unsigned long)(out+n),capacity-1-n>4096?4096:capacity-1-n))>0)n+=(unsigned long)r;
    zenith_call(3,(unsigned long)fd,0,0);out[n]=0;return (long)n;
}
static char *encode_path(char *o,const char *p,const char *end){
    static const char hex[]="0123456789ABCDEF";
    for(;*p&&o+4<end;p++){unsigned char c=(unsigned char)*p;
        if((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='/'||c=='-'||c=='_'||c=='.'||c=='~')*o++=(char)c;
        else{*o++='%';*o++=hex[c>>4];*o++=hex[c&15];}}
    *o=0;return o;
}
static int hexval(char c){return c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:-1;}
static void decode_path(char *o,const char *s,unsigned long capacity){
    unsigned long n=0;
    while(*s&&*s!='\n'&&n+1<capacity){if(*s=='%'&&hexval(s[1])>=0&&hexval(s[2])>=0){o[n++]=(char)(hexval(s[1])*16+hexval(s[2]));s+=3;}else o[n++]=*s++;}
    o[n]=0;
}
/* The info record of a trashed NAME: original path and deletion date ("" when unreadable). */
static void trash_record(const char *name,char *path,char *date){
    char info[1024],text[2048],file[300];char *o=put(file,name);put(o,".trashinfo");join(info,trash_info,file);
    path[0]=date[0]=0;if(read_small(info,text,sizeof(text))<=0)return;
    for(char *l=text;*l;){
        if(starts(l,"Path="))decode_path(path,l+5,1024);
        if(starts(l,"DeletionDate=")){int i=0;for(char *d=l+13;*d&&*d!='\n'&&i<31;d++)date[i++]=*d=='T'?' ':*d;date[i]=0;}
        while(*l&&*l!='\n')l++;
        if(*l)l++;
    }
}
static char *iso_now(char *o){
    long local=now_utc+zone_now,days=local>=0?local/86400:(local-86399)/86400,secs=local-days*86400,y;int m,d;civil(days,&y,&m,&d);
    o=num(o,(unsigned long)(y<0?0:y));*o++='-';o=num2(o,(unsigned long)m);*o++='-';o=num2(o,(unsigned long)d);*o++='T';
    o=num2(o,(unsigned long)(secs/3600));*o++=':';o=num2(o,(unsigned long)(secs/60%60));*o++=':';return num2(o,(unsigned long)(secs%60));
}
/* Keeps the newest TRASH_LIMIT items: the oldest go for good (one delete job of up to 64). */
static void trash_limit(void){
    static struct zenith_files_list request;static unsigned char page[Z_FILES_PAGE] __attribute__((aligned(16)));
    static struct { long mtime; char name[96]; } oldest[64];int kept=0;unsigned int total=0,offset=0;
    for(int pages=0;pages<64;pages++){
        request=(struct zenith_files_list){1,0,offset,(unsigned int)length(trash_info),{0}};copy(request.path,trash_info,sizeof(request.path));
        if(zenith_files_list(&request,page)<0)return;
        const struct zenith_files_page *h=(const void *)page;unsigned int at=sizeof(*h);total=h->total;
        for(unsigned int k=0;k<h->count&&at+sizeof(struct zenith_file_entry)<=Z_FILES_PAGE;k++){
            const struct zenith_file_entry *f=(const void *)(page+at);if(f->record_bytes<sizeof(*f))break;at+=f->record_bytes;
            if(f->name_bytes>=95)continue;
            /* Keep the 64 oldest seen so far. */
            int slot=kept<64?kept++:-1;
            if(slot<0){int newest=0;for(int i=1;i<64;i++)if(oldest[i].mtime>oldest[newest].mtime)newest=i;if(f->mtime>=oldest[newest].mtime)continue;slot=newest;}
            oldest[slot].mtime=f->mtime;copy(oldest[slot].name,f->name,sizeof(oldest[slot].name));
        }
        if(!h->next)break;
        offset=h->next;
    }
    if(total<=TRASH_LIMIT)return;
    int drop=(int)(total-TRASH_LIMIT);if(drop>kept)drop=kept;
    static char paths[64][1024];const char *list[64];int n=0;
    for(int i=0;i<drop;i++){
        unsigned long len=length(oldest[i].name);if(len<11)continue;oldest[i].name[len-10]=0;   /* drop ".trashinfo" */
        join(paths[n],trash_files,oldest[i].name);list[n]=paths[n];n++;
        char info[1024],file[120];char *o=put(file,oldest[i].name);put(o,".trashinfo");join(info,trash_info,file);zenith_path(2,info,length(info),0,0,0,0);
    }
    if(n){start_job(Z_JOB_DELETE,"",n,list);copy(status_note,"The Trash was full: its oldest items were deleted.",sizeof(status_note));}
}
/* Info records whose item is gone (deleted from the Trash) go too. */
static void trash_prune(void){
    static struct zenith_files_list request;static unsigned char page[Z_FILES_PAGE] __attribute__((aligned(16)));
    request=(struct zenith_files_list){1,0,0,(unsigned int)length(trash_info),{0}};copy(request.path,trash_info,sizeof(request.path));
    if(zenith_files_list(&request,page)<0)return;
    const struct zenith_files_page *h=(const void *)page;unsigned int at=sizeof(*h);
    for(unsigned int k=0;k<h->count&&at+sizeof(struct zenith_file_entry)<=Z_FILES_PAGE;k++){
        const struct zenith_file_entry *f=(const void *)(page+at);if(f->record_bytes<sizeof(*f))break;at+=f->record_bytes;
        char name[300];copy(name,f->name,sizeof(name));unsigned long len=length(name);
        if(len<11||!same(name+len-10,".trashinfo"))continue;
        name[len-10]=0;char item[1024];join(item,trash_files,name);
        if(!exists(item)){char info[1024];join(info,trash_info,f->name);zenith_path(2,info,length(info),0,0,0,0);}
    }
}
static void trash_selection(void){
    const char *list[CLIP];static char paths[CLIP][1024];int n=selection_paths(list,paths,CLIP);
    if(!n)return;
    if(admin)copy(delete_note,"As an administrator, deletions are permanent.",sizeof(delete_note));
    if(admin||in_trash()||!on_home_filesystem(paths[0])){
        if(!in_trash()&&!admin)copy(delete_note,"This drive has no Trash: they are removed at once and cannot be restored.",sizeof(delete_note));
        ask_delete();return;
    }
    if(!make_dirs(trash_files)||!make_dirs(trash_info)){copy(status_note,"The Trash cannot be made in your home folder.",sizeof(status_note));ui.dirty=1;return;}
    int moved=0;long error=0;
    for(int i=0;i<n;i++){
        const char *name=leaf(paths[i]);char unique[300],file[1024],info[1024],record[320];
        const char *x=extension(name);unsigned long stem=x[0]&&x!=name?(unsigned long)(x-name-1):length(name);
        for(int k=1;k<1000;k++){
            if(k==1)copy(unique,name,sizeof(unique));
            else{char *o=unique;for(unsigned long c=0;c<stem&&c<200;c++)*o++=name[c];o=put(o," (");o=num(o,(unsigned long)k);o=put(o,")");if(x[0]&&x!=name){*o++='.';put(o,x);}else *o=0;}
            char *o=put(record,unique);put(o,".trashinfo");join(file,trash_files,unique);join(info,trash_info,record);
            if(!exists(file)&&!exists(info))break;
        }
        static char text[3400];char *o=put(text,"[Trash Info]\nPath=");o=encode_path(o,paths[i],text+3300);o=put(o,"\nDeletionDate=");o=iso_now(o);put(o,"\n");
        long r=write_small(info,text);
        if(!r)r=zenith_rename(paths[i],length(paths[i]),file,length(file));
        if(r<0){zenith_path(2,info,length(info),0,0,0,0);error=r;continue;}
        moved++;
    }
    char line[96],*o=put(line,"HORIZON_TRASHED ");o=num(o,(unsigned long)moved);o=put(o," errors=");o=num(o,(unsigned long)(n-moved));put(o,"\n");print(line);
    if(error){copy(status_note,error==-13||error==-1?"You may not move some items from this folder.":"Some items could not go to the Trash.",sizeof(status_note));}
    else{o=put(status_note,"Moved ");o=num(o,(unsigned long)moved);put(o,moved==1?" item to the Trash.":" items to the Trash.");}
    remember_focus();load();trash_limit();ui.dirty=1;
}
static void restore_selection(void){
    if(!in_trash())return;
    int restored=0,failed=0;char why[160]="";
    for(int k=0;k<L->shown;k++){
        int i=L->order[k];if(!L->entries[i].selected&&!(k==L->focus_index&&!selected_count(0)))continue;
        const char *name=name_of(i);char original[1024],date[32],item[1024];trash_record(name,original,date);join(item,trash_files,name);
        if(!original[0]){failed++;copy(why,"Its original location is unknown.",sizeof(why));continue;}
        if(exists(original)){failed++;char *o=put(why,"\"");char l[48];copy(l,leaf(original),sizeof(l));o=put(o,l);put(o,"\" exists again at its old place.");continue;}
        char folder[1024];parent(folder,original);
        if(!make_dirs(folder)){failed++;copy(why,"Its old folder cannot be made again.",sizeof(why));continue;}
        long r=zenith_rename(item,length(item),original,length(original));
        if(r<0){failed++;copy(why,"Zenith could not move it back.",sizeof(why));continue;}
        char info[1024],file[300];char *o=put(file,name);put(o,".trashinfo");join(info,trash_info,file);zenith_path(2,info,length(info),0,0,0,0);
        restored++;
    }
    char line[96],*o=put(line,"HORIZON_RESTORED ");o=num(o,(unsigned long)restored);o=put(o," failed=");o=num(o,(unsigned long)failed);put(o,"\n");print(line);
    if(failed)copy(status_note,why,sizeof(status_note));
    else{o=put(status_note,"Restored ");o=num(o,(unsigned long)restored);put(o,restored==1?" item.":" items.");}
    load();ui.dirty=1;
}
static void empty_trash(void){
    if(!L->entry_count)return;
    copy(confirm_title,"Empty the Trash?",sizeof(confirm_title));
    copy(confirm_message,"Everything in the Trash is deleted for good.",sizeof(confirm_message));
    delete_count=0;copy(delete_paths[delete_count++],trash_files,1024);copy(delete_paths[delete_count++],trash_info,1024);
    confirm=(struct zui_dialog){1,900,confirm_title,confirm_message,{"Empty Trash","Cancel",0},2};
    print("HORIZON_ASK empty\n");ui.dirty=1;
}

enum { PROMPT_NONE=0,PROMPT_RENAME=1,PROMPT_FOLDER=2 };
static int prompt;static char prompt_text[256],prompt_original[256],prompt_error[120];static struct zui_input prompt_input;
static void ask_name(int kind){
    prompt=kind;prompt_error[0]=0;
    if(kind==PROMPT_RENAME){
        if(L->focus_index<0||L->focus_index>=L->shown){prompt=0;return;}
        copy(prompt_text,name_of(L->order[L->focus_index]),sizeof(prompt_text));copy(prompt_original,prompt_text,sizeof(prompt_original));
    }else{put(prompt_text,"New folder");prompt_original[0]=0;}
    zui_input_init(&prompt_input,prompt_text,sizeof(prompt_text));prompt_input.length=(unsigned int)length(prompt_text);
    /* Rename selects up to the extension's dot: the cursor goes there. */
    unsigned int cursor=prompt_input.length;
    if(kind==PROMPT_RENAME){const char *x=extension(prompt_text);if(x[0]&&x!=prompt_text)cursor=(unsigned int)(x-prompt_text-1);}
    prompt_input.cursor=cursor;
    print(kind==PROMPT_RENAME?"HORIZON_ASK rename\n":"HORIZON_ASK folder\n");ui.dirty=1;
}
static void name_done(void){
    char *n=prompt_text;
    if(!n[0]||same(n,".")||same(n,"..")){copy(prompt_error,"Choose a name.",sizeof(prompt_error));ui.dirty=1;return;}
    for(char *p=n;*p;p++)if(*p=='/'){copy(prompt_error,"A name cannot contain \"/\".",sizeof(prompt_error));ui.dirty=1;return;}
    if(prompt==PROMPT_RENAME&&same(n,prompt_original)){prompt=0;ui.dirty=1;return;}
    for(int i=0;i<L->entry_count;i++)if(same(name_of(i),n)){char *o=put(prompt_error,"\"");char l[64];copy(l,n,sizeof(l));o=put(o,l);put(o,"\" already exists here.");ui.dirty=1;return;}
    char target[1024];join(target,T()->path,n);
    long r;
    if(prompt==PROMPT_RENAME){char from[1024];join(from,T()->path,prompt_original);r=zenith_rename(from,length(from),target,length(target));}
    else r=zenith_mkdir(target,length(target));
    if(r<0){copy(prompt_error,r==-13?"You do not have permission to change this folder.":r==-17?"That name already exists.":"Zenith could not do that here.",sizeof(prompt_error));ui.dirty=1;return;}
    char line[400],*o=put(line,prompt==PROMPT_RENAME?"HORIZON_RENAMED ":"HORIZON_CREATED ");o=put(o,n);put(o,"\n");print(line);
    prompt=0;copy(T()->focus,n,sizeof(T()->focus));
    load();
    for(int k=0;k<L->shown;k++)L->entries[L->order[k]].selected=k==L->focus_index;
    ui.dirty=1;
}
static int conflict_all;

/* ---------- Context menu. */
enum { M_OPEN=1,M_OPEN_TAB,M_CUT,M_COPY,M_PASTE,M_RENAME,M_DELETE,M_FOLDER,M_SELECT_ALL,M_HIDDEN,M_REFRESH,M_OPEN_WITH,M_PROPERTIES,M_DROP_MOVE,M_DROP_COPY,M_DROP_CANCEL,M_SPLIT,M_TRASH,M_RESTORE,M_EMPTY_TRASH,M_EXTRACT,M_COMPRESS };
static int menu_open,menu_x,menu_y,menu_items[12],menu_count,menu_focus;
static const char *menu_label(int m){
    switch(m){case M_OPEN:return "Open";case M_OPEN_TAB:return "Open in new tab";case M_CUT:return "Cut";case M_COPY:return "Copy";case M_PASTE:return "Paste";
    case M_RENAME:return "Rename";case M_DELETE:return "Delete permanently";case M_FOLDER:return "New folder";case M_SELECT_ALL:return "Select all";
    case M_HIDDEN:return show_hidden?"Hide hidden files":"Show hidden files";case M_REFRESH:return "Refresh";
    case M_OPEN_WITH:return "Open with...";case M_PROPERTIES:return "Properties";case M_DROP_MOVE:return "Move here";case M_DROP_COPY:return "Copy here";
    case M_DROP_CANCEL:return "Cancel";case M_SPLIT:return TB()->split?"Close split view":"Split view";
    case M_TRASH:return "Move to Trash";case M_RESTORE:return "Restore";case M_EMPTY_TRASH:return "Empty Trash";
    case M_EXTRACT:return "Extract here";case M_COMPRESS:return "Compress to .zip";}return "";
}
static const char *menu_icon(int m){
    switch(m){case M_OPEN:return "folder";case M_OPEN_TAB:return "folder-plus";case M_CUT:return "scissors";case M_COPY:return "copy";case M_PASTE:return "clipboard-paste";
    case M_RENAME:return "pencil";case M_DELETE:return "trash";case M_FOLDER:return "folder-plus";case M_SELECT_ALL:return "check";case M_HIDDEN:return "eye-off";case M_REFRESH:return "refresh-cw";
    case M_OPEN_WITH:return "app-window";case M_PROPERTIES:return "info";case M_DROP_MOVE:return "arrow-right";case M_DROP_COPY:return "copy";case M_SPLIT:return "columns-2";
    case M_TRASH:return "trash";case M_RESTORE:return "undo-2";case M_EMPTY_TRASH:return "trash-2";case M_EXTRACT:return "package-open";case M_COMPRESS:return "file-archive";}return 0;
}
static const char *menu_keys(int m){
    switch(m){case M_OPEN_TAB:return "Ctrl+Enter";case M_CUT:return "Ctrl+X";case M_COPY:return "Ctrl+C";case M_PASTE:return "Ctrl+V";case M_RENAME:return "F2";
    case M_DELETE:return in_trash()?"Delete":"Shift+Delete";case M_TRASH:return "Delete";case M_FOLDER:return "Ctrl+Shift+N";case M_SELECT_ALL:return "Ctrl+A";case M_HIDDEN:return "Ctrl+H";case M_REFRESH:return "F5";
    case M_OPEN:return "Enter";case M_PROPERTIES:return "Alt+Enter";case M_DROP_MOVE:return "Shift";case M_DROP_COPY:return "Ctrl";case M_SPLIT:return "F3";}return "";
}
static int menu_enabled(int m){
    int n=selected_count(0);
    if(m==M_PASTE||m==M_FOLDER)return L->folder_writable&&(m!=M_PASTE||clip_count);
    if(m==M_RENAME)return n<=1&&L->focus_index>=0&&L->folder_writable;
    if(m==M_DELETE||m==M_CUT||m==M_TRASH)return L->folder_writable;
    if(m==M_EMPTY_TRASH)return L->entry_count>0;
    if(m==M_EXTRACT||m==M_COMPRESS)return L->folder_writable;
    return 1;
}
/* ---------- Archives (HZ4): Extract here and Compress to .zip run as kernel transfer jobs (4 and 5). */
static int archive_name(const char *name){
    char l[300];int n=0;while(name[n]&&n<299){l[n]=lower(name[n]);n++;}l[n]=0;
    const char *const kinds[]={".zip",".tar",".tgz",".tar.gz",".gz",0};
    for(int i=0;kinds[i];i++){unsigned long k=length(kinds[i]);if((unsigned long)n>k&&same(l+n-k,kinds[i]))return 1;}
    return 0;
}
static void archive_action(int extract){
    const char *list[CLIP];static char paths[CLIP][1024];int n=selection_paths(list,paths,CLIP);
    if(!n)return;
    if(extract){
        int k=0;for(int i=0;i<n;i++)if(archive_name(leaf(paths[i])))list[k++]=paths[i];
        if(!k){copy(status_note,"Choose a .zip, .tar, .tar.gz or .gz file to extract.",sizeof(status_note));ui.dirty=1;return;}
        start_job(Z_JOB_EXTRACT,T()->path,k,list);return;
    }
    char name[300],target[1024];
    if(n==1){copy(name,leaf(paths[0]),sizeof(name));const char *x=extension(name);if(x[0]&&x!=name&&!is_directory(paths[0]))name[x-name-1]=0;}
    else copy(name,"Archive",sizeof(name));
    char *o=name+length(name);put(o,".zip");join(target,T()->path,name);
    start_job(Z_JOB_COMPRESS,target,n,list);
}
static void open_menu(int x,int y,int on_item){
    menu_count=0;
    if(on_item){
        int folder=L->focus_index>=0&&is_folder(&L->entries[L->order[L->focus_index]]);
        menu_items[menu_count++]=M_OPEN;
        if(folder)menu_items[menu_count++]=M_OPEN_TAB;else menu_items[menu_count++]=M_OPEN_WITH;
        menu_items[menu_count++]=M_CUT;menu_items[menu_count++]=M_COPY;
        if(folder&&clip_count)menu_items[menu_count++]=M_PASTE;
        if(in_trash()){menu_count=0;menu_items[menu_count++]=M_RESTORE;menu_items[menu_count++]=M_DELETE;menu_items[menu_count++]=M_PROPERTIES;}
        else{
            if(!folder&&L->focus_index>=0&&archive_name(name_of(L->order[L->focus_index])))menu_items[menu_count++]=M_EXTRACT;
            menu_items[menu_count++]=M_COMPRESS;
            menu_items[menu_count++]=M_RENAME;menu_items[menu_count++]=M_TRASH;menu_items[menu_count++]=M_DELETE;menu_items[menu_count++]=M_PROPERTIES;
        }
    }else if(in_trash()){
        menu_items[menu_count++]=M_EMPTY_TRASH;menu_items[menu_count++]=M_SELECT_ALL;menu_items[menu_count++]=M_REFRESH;
    }else{
        menu_items[menu_count++]=M_FOLDER;menu_items[menu_count++]=M_PASTE;menu_items[menu_count++]=M_SELECT_ALL;
        menu_items[menu_count++]=M_HIDDEN;menu_items[menu_count++]=M_SPLIT;menu_items[menu_count++]=M_REFRESH;
    }
    menu_open=1;menu_x=x;menu_y=y;menu_focus=-1;print("HORIZON_MENU open\n");ui.dirty=1;
}

/* ---------- Opening files: the Desktop Portal's OpenWith interface picks (or asks for) the app; Horizon never
 * starts one itself. One request at a time; its answer is polled from the event loop. */
static long open_handle;static char opening[256];
static void open_file(const char *path,int choose){
    if(open_handle>0){zenith_portal_close(open_handle);open_handle=0;}
    long h=zenith_open_with(choose?"Choose":"Open",path,length(path));
    char line[1100],*o=put(line,choose?"HORIZON_OPEN_WITH ":"HORIZON_OPEN ");o=put(o,path);put(o,"\n");print(line);
    if(h<0){copy(status_note,h==-38?"This Zenith cannot open files with apps. Update Zenith.":"The desktop cannot open this file now.",sizeof(status_note));ui.dirty=1;return;}
    open_handle=h;copy(opening,leaf(path),sizeof(opening));
    o=put(status_note,"Opening \"");char l[64];copy(l,opening,sizeof(l));o=put(o,l);put(o,"\"...");ui.dirty=1;
}
static void poll_open(void){
    if(open_handle<=0)return;
    static struct zenith_portal_response r;
    long s=zenith_portal_poll(open_handle,&r);
    if(s<0){open_handle=0;return;}
    if(!Z_PORTAL_FINAL(s))return;
    zenith_portal_close(open_handle);open_handle=0;
    char line[200],*o=put(line,"HORIZON_OPENED ");
    if(s==Z_PORTAL_APPROVED){
        const struct zenith_open_with_result *a=(const void *)r.response;char app[64];copy(app,a->app,sizeof(app));
        o=put(o,app);o=put(status_note,"Opened with ");put(o,app);
    }else{
        o=put(o,s==Z_PORTAL_DENIED?"denied":s==Z_PORTAL_UNAVAILABLE?"unavailable":s==Z_PORTAL_CANCELLED?"cancelled":"failed");
        if(s==Z_PORTAL_DENIED&&!same(r.message,"dismissed")){char m[96];copy(m,r.message,sizeof(m));o=put(status_note,"Cannot open it: ");put(o,m);}
        else if(s==Z_PORTAL_UNAVAILABLE)copy(status_note,"No desktop can open it from here.",sizeof(status_note));
        else if(s==Z_PORTAL_FAILED){char m[96];copy(m,r.message,sizeof(m));copy(status_note,m,sizeof(status_note));}
        else status_note[0]=0;
    }
    o=line+length(line);put(o,"\n");print(line);ui.dirty=1;
}

/* ---------- Opening. */
static void open_item(int k,int new_tab){
    if(k<0||k>=L->shown)return;
    const struct entry *e=&L->entries[L->order[k]];char path[1024];join(path,T()->path,name_of(L->order[k]));
    if(is_folder(e)){if(new_tab){open_tab(path,0);char l[64],*o=put(l,"Opened in a new tab: ");copy(o,name_of(L->order[k]),40);copy(status_note,l,sizeof(status_note));ui.dirty=1;}else go(path,1);return;}
    open_file(path,0);
}

/* ---------- Selection. */
static void select_only(int k){for(int i=0;i<L->shown;i++)L->entries[L->order[i]].selected=i==k;}
static void select_range(int a,int b,int add){
    if(a>b){int t=a;a=b;b=t;}
    for(int i=0;i<L->shown;i++){if(i>=a&&i<=b)L->entries[L->order[i]].selected=1;else if(!add)L->entries[L->order[i]].selected=0;}
}
static void announce_selection(void){
    announce_focus=1;
    unsigned long bytes;int n=selected_count(&bytes);
    char line[96],*o=put(line,"HORIZON_SELECTED ");o=num(o,(unsigned long)n);put(o,"\n");print(line);
}
static int item_top(int k);static int item_height(void);
static void reveal(int k){
    if(k<0)return;
    int top=item_top(k);zui_scroll_reveal(&L->content_scroll,top,item_height());
}
static void move_focus(int k,int modifiers){
    if(!L->shown)return;
    if(k<0){k=0;}
    if(k>=L->shown){k=L->shown-1;}
    if(modifiers&1){if(L->anchor<0)L->anchor=L->focus_index>=0?L->focus_index:k;L->focus_index=k;select_range(L->anchor,k,0);}
    else if(modifiers&2){L->focus_index=k;}
    else{L->focus_index=k;L->anchor=k;select_only(k);}
    reveal(k);announce_selection();ui.dirty=1;
}

/* ---------- Layout. */
enum { TAB_Y=4,TAB_H=40,TOOL_Y=48,TOOL_H=36,BODY_Y=94,STATUS_H=30,STRIP_H=58 };
static int sidebar_w(void){return ui.width<720?0:ui.width<900?172:204;}
static int info_w(void){return info_on&&ui.width>=860?(ui.width<1100?236:268):0;}
/* Zoom 0..3 per pane: icon sizes 32, 48 (default), 64, 96; detail rows 26..48. */
static int zoom_of(void){int z=T()->zoom;return z<0?0:z>3?3:z;}
static int icon_px(void){static const int s[4]={32,48,64,96};return s[zoom_of()];}
static int row_px(void){static const int s[4]={26,32,40,48};return s[zoom_of()];}
static int row_icon(void){static const int s[4]={16,16,24,32};return s[zoom_of()];}
static int item_height(void){return T()->view==0?icon_px()+74:row_px();}
static int cell_w(void){return icon_px()+84;}
static void set_zoom(int z){
    if(z<0){z=0;}
    if(z>3){z=3;}
    if(z==T()->zoom)return;
    T()->zoom=z;reveal_later=1;ui.dirty=1;
    char line[32],*o=put(line,"HORIZON_ZOOM ");o=num(o,(unsigned long)z);put(o,"\n");print(line);
}
static int item_top(int k){return T()->view==0?(k/L->columns_now)*item_height():28+k*item_height();}

/* ---------- Drawing pieces. */
static void icon_button(struct zui_rect r,const char *name,int id,int enabled,int active){
    if(active)zui_round(&ui,r,8,ui.theme.raised);
    if(enabled&&hover==id&&!active)zui_round(&ui,r,8,mix(ui.theme.subtle,ui.theme.text,8));
    if(active)zui_round_outline(&ui,r,8,ui.theme.border);
    icon(name,r.x+(r.w-16)/2,r.y+(r.h-16)/2,16,!enabled?ZUI_ICON_DISABLED:active?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
    if(enabled)hit(r,id);
}
static void button(struct zui_rect r,const char *label,int id,int primary,int enabled){
    unsigned int fill=primary?ui.theme.accent:hover==id&&enabled?mix(ui.theme.raised,ui.theme.text,8):ui.theme.raised;
    zui_round(&ui,r,8,primary?fill:ui.theme.border);
    if(!primary)zui_round(&ui,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},7,fill);
    unsigned int color=!enabled?ui.theme.secondary:primary?ui.theme.on_accent:ui.theme.text;
    fit_center(r.x+r.w/2,r.y+(r.h-zui_label.height)/2,label,r.w-16,primary?&zui_label:&zui_body,color);
    if(enabled)hit(r,id);
}
static void field(struct zui_rect r,int focused){
    zui_round(&ui,r,8,focused?ui.theme.accent:ui.theme.border);
    zui_round(&ui,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},7,ui.theme.surface);
}

static void draw_tabs(void){
    static struct zui_tab items[MAX_TABS];static char labels[MAX_TABS][80];
    for(int i=0;i<tab_count;i++){tab_label(labels[i],&tabs[i]);items[i]=(struct zui_tab){labels[i],0,same(tabs[i].pane[tabs[i].active].path,home)?"house":"folder"};}
    zui_tabs_draw(&ui,&strip,(struct zui_rect){8,TAB_Y,ui.width-16,TAB_H},items,tab_count,current,tab_count<MAX_TABS);
}

/* Breadcrumb segments: Home or Root first, then each folder. */
static void draw_toolbar(void){
    struct pane *t=T();int x=12,y=TOOL_Y;
    icon_button((struct zui_rect){x,y,36,TOOL_H},"arrow-left",H_BACK,t->hpos>0,0);x+=38;
    icon_button((struct zui_rect){x,y,36,TOOL_H},"arrow-right",H_FORWARD,t->hpos<t->hcount-1,0);x+=38;
    icon_button((struct zui_rect){x,y,36,TOOL_H},"arrow-up",H_UP,!same(t->path,"/"),0);x+=44;
    int right=ui.width-12;
    int info_x=right-36;icon_button((struct zui_rect){info_x,y,36,TOOL_H},"panel-right",H_INFO,ui.width>=860,info_on&&ui.width>=860);
    info_x-=40;icon_button((struct zui_rect){info_x,y,36,TOOL_H},"columns-2",H_SPLIT,1,TB()->split);
    int filter_w=ui.width>=1000?240:ui.width>=760?180:0;
    int filter_x=info_x-8-filter_w;
    if(filter_w){
        struct zui_rect f={filter_x,y,filter_w,TOOL_H};field(f,filter_focus);
        icon("search",f.x+10,f.y+10,16,ZUI_ICON_TEXT);
        if(filter_focus)zui_input_draw(&ui,(struct zui_rect){f.x+30,f.y+1,f.w-32,f.h-2},&filter_input,1);
        else fit(f.x+34,f.y+(f.h-zui_body.height)/2,t->filter[0]?t->filter:search_mode?"Search below this folder":"Filter this folder",f.w-44,&zui_body,t->filter[0]?ui.theme.text:ui.theme.secondary);
        hit(f,H_FILTER);
    }
    int views_x=(filter_w?filter_x:info_x)-8-82;
    zui_round(&ui,(struct zui_rect){views_x,y+2,82,TOOL_H-4},8,mix(ui.theme.subtle,ui.theme.text,5));
    icon_button((struct zui_rect){views_x+3,y+5,37,TOOL_H-10},"layout-grid",H_ICONS,1,t->view==0);
    icon_button((struct zui_rect){views_x+42,y+5,37,TOOL_H-10},"list",H_DETAILS,1,t->view==1);
    struct zui_rect crumb={x,y,views_x-8-x,TOOL_H};
    field(crumb,editing_location);
    if(editing_location){zui_input_draw(&ui,(struct zui_rect){crumb.x+6,crumb.y+1,crumb.w-8,crumb.h-2},&location_input,1);hit(crumb,H_LOCATION);return;}
    hit(crumb,H_LOCATION);
    /* Segments from the end backwards until the field is full; earlier ones collapse into "...". */
    const char *segs[64];int seg_count=0;char path[1024];copy(path,t->path,sizeof(path));
    int under_home=!same(home,"/")&&(same(path,home)||(starts(path,home)&&path[length(home)]=='/'));
    char *p=path+(under_home?length(home):0);
    segs[seg_count++]=under_home?"Home":"Root";
    while(*p&&seg_count<64){while(*p=='/')*p++=0;if(!*p)break;segs[seg_count++]=p;while(*p&&*p!='/')p++;}
    int widths[64],total=0;for(int i=0;i<seg_count;i++){widths[i]=measure(segs[i],i==seg_count-1?&zui_label:&zui_body)+(i?22:0);total+=widths[i];}
    int first=0;while(first<seg_count-1&&total>crumb.w-40){total-=widths[first];first++;}
    int cx=crumb.x+12,ty=crumb.y+(crumb.h-zui_body.height)/2;
    if(first){text(cx,ty,"\xE2\x80\xA6",&zui_body,ui.theme.secondary);cx+=measure("\xE2\x80\xA6",&zui_body)+4;}
    for(int i=first;i<seg_count;i++){
        if(i>first||first){icon("chevron-right",cx,crumb.y+(crumb.h-16)/2,16,ZUI_ICON_TEXT);cx+=20;}
        int last=i==seg_count-1,w=measure(segs[i],last?&zui_label:&zui_body);
        struct zui_rect r={cx-4,crumb.y+4,w+8,crumb.h-8};
        if(!last&&hover==H_CRUMB+i)zui_round(&ui,r,6,mix(ui.theme.surface,ui.theme.text,8));
        text(cx,ty,segs[i],last?&zui_label:&zui_body,last?ui.theme.text:ui.theme.secondary);
        if(!last)hit(r,H_CRUMB+i);
        cx+=w+4;
    }
}
/* The crumb index i as a path. */
static void crumb_path(char *o,int i){
    struct pane *t=T();
    int under_home=!same(home,"/")&&(same(t->path,home)||(starts(t->path,home)&&t->path[length(home)]=='/'));
    copy(o,under_home?home:"/",1024);
    const char *p=t->path+(under_home?length(home):0);
    for(int k=0;k<i&&*p;k++){
        while(*p=='/')p++;
        char name[256];int n=0;while(*p&&*p!='/'&&n<255)name[n++]=*p++;name[n]=0;
        char next[1024];join(next,o,name);copy(o,next,1024);
    }
}

static void draw_sidebar(int x,int y,int w,int h){
    (void)h;
    text(x+12,y+4,"Places",&zui_caption,ui.theme.secondary);y+=26;
    int devices=0,current=-1;
    /* Root and the system device are both "/": the first entry for the open folder is the active one. */
    for(int i=0;i<place_count&&current<0;i++)if(same(places[i].path,T()->path))current=i;
    for(int i=0;i<place_count;i++){
        if(places[i].device&&!devices){devices=1;y+=10;text(x+12,y+4,"Devices",&zui_caption,ui.theme.secondary);y+=26;}
        int active=i==current;
        int row_h=places[i].device&&places[i].total?52:34;
        struct zui_rect r={x,y,w,row_h};
        /* The highlight covers the whole row, usage bar and free space included. */
        if(active){zui_round(&ui,r,8,mix(ui.theme.subtle,ui.theme.accent,18));zui_round(&ui,(struct zui_rect){x+1,y+(row_h-18)/2,3,18},1,ui.theme.accent);}
        else if(hover==H_PLACE+i)zui_round(&ui,r,8,mix(ui.theme.subtle,ui.theme.text,7));
        icon(places[i].icon,x+12,y+9,16,ZUI_ICON_NORMAL);
        fit(x+38,y+(34-zui_body.height)/2,places[i].label,w-46,active?&zui_label:&zui_body,ui.theme.text);
        if(places[i].device&&places[i].total){
            int pct=(int)(places[i].total?(places[i].total-places[i].available)*100/places[i].total:0);
            zui_meter(&ui,(struct zui_rect){x+38,y+32,w-50,4},pct,ui.theme.accent);
            char t[64],*o=size_text(t,places[i].available);o=put(o," of ");o=size_text(o,places[i].total);put(o," free");
            fit(x+38,y+38,t,w-46,&zui_caption,ui.theme.secondary);
        }
        hit(r,H_PLACE+i);
        y+=row_h+4;
    }
}

static void item_caption(char *o,const struct entry *e){
    if(is_folder(e))items_text(o,e->items);else size_text(o,e->size);
}
/* ---------- Thumbnails (HZ4): images in the icons view show a preview from call 660 (PNG, BMP, baseline JPEG),
 * one fetched per event-loop pass and kept in a small cache per size. */
#define THUMBS 24
#define THUMB_MAX 96
static struct { char path[1024]; int size,w,h,state; unsigned long used; unsigned int px[THUMB_MAX*THUMB_MAX]; } thumbs[THUMBS];
static unsigned long thumb_clock;static char thumb_want[1024];static int thumb_want_size;
static int is_image(const char *name){
    const char *x=extension(name);char l[8];int n=0;while(x[n]&&n<7){l[n]=lower(x[n]);n++;}l[n]=0;
    return same(l,"png")||same(l,"jpg")||same(l,"jpeg")||same(l,"bmp");
}
static int thumb_find(const char *path,int size){for(int i=0;i<THUMBS;i++)if(thumbs[i].state&&thumbs[i].size==size&&same(thumbs[i].path,path)){thumbs[i].used=++thumb_clock;return i;}return -1;}
static void thumb_fetch(void){
    if(!thumb_want[0])return;
    int slot=0;for(int i=1;i<THUMBS;i++)if(thumbs[i].used<thumbs[slot].used)slot=i;
    copy(thumbs[slot].path,thumb_want,1024);thumbs[slot].size=thumb_want_size;thumbs[slot].used=++thumb_clock;
    long r=zenith_thumbnail(thumb_want,length(thumb_want),(unsigned int)thumb_want_size,thumbs[slot].px,sizeof(thumbs[slot].px));
    if(r>0){thumbs[slot].state=1;thumbs[slot].w=(int)(r>>16);thumbs[slot].h=(int)(r&0xffff);
        char line[1200],*o=put(line,"HORIZON_THUMB ");o=put(o,leaf(thumb_want));*o++=' ';o=num(o,(unsigned long)thumbs[slot].w);*o++=' ';o=num(o,(unsigned long)thumbs[slot].h);put(o,"\n");print(line);}
    else thumbs[slot].state=2;
    thumb_want[0]=0;ui.dirty=1;
}
static void draw_item_icons(int k,int x,int y){
    const struct entry *e=&L->entries[L->order[k]];const char *name=name_of(L->order[k]);
    struct zui_rect r={x+4,y+4,cell_w()-8,item_height()-8};
    if(e->selected){zui_round(&ui,r,10,mix(ui.theme.surface,ui.theme.accent,16));zui_round_outline(&ui,r,10,k==L->focus_index?ui.theme.accent:mix(ui.theme.surface,ui.theme.accent,50));}
    else if(k==L->focus_index&&ui.focus_visible)zui_round_outline(&ui,r,10,ui.theme.accent);
    else if(hover==item_id(k))zui_round(&ui,r,10,mix(ui.theme.surface,ui.theme.text,6));
    int cut=clip_cut&&clip_count;
    int s=icon_px(),drawn=0;
    if(!is_folder(e)&&is_image(name)){
        char path[1024];join(path,T()->path,name);int i=thumb_find(path,s);
        if(i>=0&&thumbs[i].state==1){zui_image(&ui,x+(cell_w()-thumbs[i].w)/2,y+14+(s-thumbs[i].h)/2,thumbs[i].w,thumbs[i].h,thumbs[i].px);drawn=1;}
        else if(i<0&&!thumb_want[0]){copy(thumb_want,path,sizeof(thumb_want));thumb_want_size=s;}
    }
    if(!drawn)icon(icon_of(e,name),x+(cell_w()-s)/2,y+14,s,is_folder(e)?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
    (void)cut;
    fit_center(x+cell_w()/2,y+s+22,name,cell_w()-20,e->selected?&zui_label:&zui_body,ui.theme.text);
    char c[48];item_caption(c,e);fit_center(x+cell_w()/2,y+s+44,c,cell_w()-20,&zui_caption,ui.theme.secondary);
    hit(r,item_id(k));
}
static int col_size,col_time,col_owner,col_perm,col_end;
static void draw_item_details(int k,int x,int y,int w){
    const struct entry *e=&L->entries[L->order[k]];const char *name=name_of(L->order[k]);
    struct zui_rect r={x,y+1,w,item_height()-2};
    if(e->selected){zui_round(&ui,r,8,mix(ui.theme.surface,ui.theme.accent,16));if(k==L->focus_index&&ui.focus_visible)zui_round_outline(&ui,r,8,ui.theme.accent);}
    else if(k==L->focus_index&&ui.focus_visible)zui_round_outline(&ui,r,8,ui.theme.accent);
    else if(hover==item_id(k))zui_round(&ui,r,8,mix(ui.theme.surface,ui.theme.text,6));
    int ty=y+(item_height()-zui_body.height)/2;
    icon(icon_of(e,name),x+10,y+(item_height()-row_icon())/2,row_icon(),is_folder(e)?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
    fit(x+36,ty,name,col_size-x-48,e->selected?&zui_label:&zui_body,ui.theme.text);
    char c[48];item_caption(c,e);text(col_size,ty,c,&zui_body,ui.theme.secondary);
    char d[32];date_text(d,e->mtime,e->zone,0);text(col_time,ty,d,&zui_body,ui.theme.secondary);
    if(col_owner){char o[32];id_text(o,e->uid,0);fit(col_owner,ty,o,(col_perm?col_perm:col_end)-col_owner-8,&zui_body,ui.theme.secondary);}
    if(col_perm){char p[16];permissions(p,e->mode);text(col_perm,ty,p,&zui_body,ui.theme.secondary);}
    hit(r,item_id(k));
}
/* The window's material surface (the desktop's translucent, blurred background), when the desktop offers it. */
static int material;
static int see_through(void){return ui.foreground&&ui.full_background&&!ui.theme_preview;}
/* The content and information panels: opaque normally, a light tint over the window material when the whole
 * app background is transparent (as zui's workspace and Observatory's cards do). */
static void panel(struct zui_rect r){
    if(see_through())zui_round_alpha(&ui,r,12,ui.theme.surface,40);
    else zui_round(&ui,r,12,ui.theme.surface);
}
static void draw_content(struct zui_rect c){
    panel(c);
    hit(c,content_id());
    struct pane *t=T();
    struct zui_rect inner={c.x+8,c.y+8,c.w-16,c.h-16};
    if(no_abi){text(inner.x+16,inner.y+16,"Horizon needs a newer Zenith.",&zui_label,ui.theme.text);
        zui_paragraph(&ui,(struct zui_rect){inner.x+16,inner.y+42,inner.w-32,60},"This kernel does not offer the file service Horizon uses. Update Zenith, then open Horizon again.",&zui_body,ui.theme.secondary,0);return;}
    if(L->list_error){
        text(inner.x+16,inner.y+16,L->list_error==-13?"You do not have permission to open this folder.":L->list_error==-2?"This folder does not exist.":L->list_error==-20?"This is not a folder.":"This folder cannot be read.",&zui_label,ui.theme.text);
        text(inner.x+16,inner.y+42,"Use Back or Up to leave it.",&zui_body,ui.theme.secondary);return;
    }
    if(t->view==1){
        /* Column header: click to sort; the active column shows its direction. */
        int x=inner.x,w=inner.w;
        col_end=x+w;col_perm=w>=760?x+w-100:0;col_owner=w>=640?(col_perm?col_perm:x+w)-100:0;
        col_time=(col_owner?col_owner:col_perm?col_perm:x+w)-140;col_size=col_time-100;
        static const char *heads[5]={"Name","Size","Modified","Owner","Permissions"};
        int xs[5]={x+36,col_size,col_time,col_owner,col_perm};int sorts[5]={SORT_NAME,SORT_SIZE,SORT_TIME,-1,-1};
        for(int i=0;i<5;i++){
            if(!xs[i])continue;
            int active=sorts[i]==t->sort;
            text(xs[i],inner.y+6,heads[i],&zui_caption,active?ui.theme.text:ui.theme.secondary);
            if(active)icon(t->descending?"chevron-down":"chevron-down",xs[i]+measure(heads[i],&zui_caption)+4,inner.y+5,16,ZUI_ICON_TEXT);
            if(sorts[i]>=0)hit((struct zui_rect){xs[i]-6,inner.y,i==0?col_size-xs[i]-8:110,24},H_COLUMN+drawing_pane*8+sorts[i]);
        }
        zui_fill(&ui,(struct zui_rect){x,inner.y+26,w,1},ui.theme.border);
    }
    struct zui_rect list=t->view==1?(struct zui_rect){inner.x,inner.y+28,inner.w,inner.h-28}:inner;
    L->content_rect=list;
    L->columns_now=t->view==0?(list.w/cell_w()>0?list.w/cell_w():1):1;
    L->rows_visible=list.h/item_height();if(L->rows_visible<1)L->rows_visible=1;
    int origin=zui_scroll_begin(&ui,&L->content_scroll,list)-(t->view==1?28:0);
    int left=list.x+(t->view==0?(list.w-L->columns_now*cell_w())/2:0);
    if(!L->shown){
        const char *empty=L->entry_count&&t->filter[0]?"No items match the filter.":L->entry_count?"Only hidden items are here. Ctrl+H shows them.":"This folder is empty.";
        text(list.x+16,origin+(t->view==1?28:0)+16,empty,&zui_body,ui.theme.secondary);
    }
    for(int k=0;k<L->shown;k++){
        int top=origin+item_top(k);
        if(top+item_height()<list.y||top>list.y+list.h)continue;
        if(t->view==0)draw_item_icons(k,left+(k%L->columns_now)*cell_w(),top);
        else draw_item_details(k,list.x,top,list.w);
    }
    int height=t->view==0?((L->shown+L->columns_now-1)/L->columns_now)*item_height()+8:L->shown*item_height()+8;
    zui_scroll_end(&ui,&L->content_scroll,height);
}
static void info_row(int x,int y,int w,const char *label,const char *value){
    text(x,y,label,&zui_caption,ui.theme.secondary);fit(x+w-measure(value,&zui_caption)<x+90?x+90:x+w-measure(value,&zui_caption),y,value,w-90,&zui_caption,ui.theme.text);
}
static void draw_info(struct zui_rect r){
    panel(r);
    int x=r.x+18,w=r.w-36,y=r.y+22;
    unsigned long bytes;int n=selected_count(&bytes);
    const struct entry *e=0;const char *name=0;
    if(n==1)for(int k=0;k<L->shown;k++)if(L->entries[L->order[k]].selected){e=&L->entries[L->order[k]];name=name_of(L->order[k]);}
    if(n>1){
        icon("copy",r.x+(r.w-48)/2,y,48,ZUI_ICON_NORMAL);y+=62;
        char t[64],*o=num(t,(unsigned long)n);put(o," items selected");fit_center(r.x+r.w/2,y,t,w,&zui_label,ui.theme.text);y+=26;
        char s[48];o=size_text(s,bytes);put(o," in files");fit_center(r.x+r.w/2,y,s,w,&zui_caption,ui.theme.secondary);
        return;
    }
    char folder_name[80];
    if(!e){pane_label(folder_name,T());name=folder_name;}
    icon(e?icon_of(e,name):"folder",r.x+(r.w-48)/2,y,48,ZUI_ICON_NORMAL);y+=62;
    fit_center(r.x+r.w/2,y,name,w,&zui_label,ui.theme.text);y+=24;
    fit_center(r.x+r.w/2,y,e?kind_of(e,name):"Folder",w,&zui_caption,ui.theme.secondary);y+=36;
    char v[64];
    if(e){
        if(is_folder(e))items_text(v,e->items);else size_text(v,e->size);
        info_row(x,y,w,is_folder(e)?"Contains":"Size",v);y+=26;
        date_text(v,e->mtime,e->zone,1);info_row(x,y,w,"Modified",v);y+=26;
        id_text(v,e->uid,0);info_row(x,y,w,"Owner",v);y+=26;
        id_text(v,e->gid,1);info_row(x,y,w,"Group",v);y+=26;
        permissions(v,e->mode);info_row(x,y,w,"Permissions",v);y+=26;
        num(v,e->links);info_row(x,y,w,"Links",v);y+=26;
        if(in_trash()){
            static char cached[256],original[1024],deleted[32];
            if(!same(cached,name)){copy(cached,name,sizeof(cached));trash_record(name,original,deleted);}
            info_row(x,y,w,"Deleted",deleted[0]?deleted:"\xE2\x80\x94");y+=26;
            text(x,y,"From",&zui_caption,ui.theme.secondary);y+=18;
            zui_paragraph(&ui,(struct zui_rect){x,y,w,40},original[0]?original:"Unknown",&zui_caption,ui.theme.text,0);y+=44;
        }
    }else{
        char *o=num(v,(unsigned long)L->entry_count);put(o,L->entry_count==1?" item":" items");info_row(x,y,w,"Contains",v);y+=26;
        if(L->have_fs){size_text(v,L->fs.available);info_row(x,y,w,"Free",v);y+=26;size_text(v,L->fs.total);info_row(x,y,w,"Capacity",v);y+=26;
            info_row(x,y,w,"Filesystem",L->fs.type);y+=26;info_row(x,y,w,"Mounted at",L->fs.mount);y+=26;}
        info_row(x,y,w,"You may change it",L->folder_writable?"Yes":"No");y+=26;
    }
}
static void draw_strip(struct zui_rect r,struct job *j){
    zui_round(&ui,r,10,ui.theme.border);zui_round(&ui,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},9,ui.theme.surface);
    const struct zenith_job_status *s=&j->status;
    static const char *verbs[6][2]={{"",""},{"Copying","Copied"},{"Moving","Moved"},{"Deleting","Deleted"},{"Extracting","Extracted"},{"Compressing","Compressed"}};
    int final=Z_JOB_FINAL(j->state);
    char head[160],*o;
    if(j->state==Z_JOB_FAILED)o=put(head,j->op==Z_JOB_COPY?"Copy failed":j->op==Z_JOB_MOVE?"Move failed":"Delete failed");
    else if(j->state==Z_JOB_CANCELLED)o=put(head,"Cancelled");
    else{o=put(head,verbs[j->op][final]);*o++=' ';o=num(o,(unsigned long)j->count);o=put(o,j->count==1?" item":" items");
        if(j->target[0]){o=put(o," to ");o=put(o,j->target);}}
    if(j->state==Z_JOB_COUNTING)put(o,": counting");
    int x=r.x+16,y=r.y+10;
    fit(x,y,head,r.w/2-24,&zui_label,j->state==Z_JOB_FAILED?bad():ui.theme.text);
    char sub[240];o=sub;
    if(final){copy(sub,s->message,sizeof(sub));}
    else{
        o=put(o,s->current);o=put(o," \xC2\xB7 ");o=num(o,s->items_done);o=put(o," of ");o=num(o,s->items_total);
        if(s->bytes_total){o=put(o," \xC2\xB7 ");o=size_text(o,s->bytes_done);o=put(o," of ");size_text(o,s->bytes_total);}
    }
    fit(x,y+22,sub,r.w/2-24,&zui_caption,s->errors&&final?warn():ui.theme.secondary);
    int bx=r.x+r.w/2,bw=r.w/2-170;
    int pct=s->bytes_total?(int)(s->bytes_done*100/s->bytes_total):s->items_total?(int)(s->items_done*100/s->items_total):final?100:0;
    if(pct>100)pct=100;
    if(bw>40){zui_meter(&ui,(struct zui_rect){bx,r.y+r.h/2-3,bw,6},pct,j->state==Z_JOB_FAILED?bad():ui.theme.accent);
        char p[8];o=num(p,(unsigned long)pct);put(o,"%");text(bx+bw+12,r.y+(r.h-zui_caption.height)/2,p,&zui_caption,ui.theme.secondary);}
    struct zui_rect b={r.x+r.w-112,r.y+(r.h-34)/2,100,34};
    if(final)button(b,"Close",H_CLOSE_JOB,0,1);else button(b,"Cancel",H_CANCEL_JOB,0,1);
}
static void draw_status(int y){
    unsigned long bytes;int n=selected_count(&bytes);int folders=0;
    for(int k=0;k<L->shown;k++)if(is_folder(&L->entries[L->order[k]]))folders++;
    char t[200],*o=t;
    if(status_note[0])copy(t,status_note,sizeof(t));
    else{
        if(search_on&&TB()->active==search_pane){o=put(o,search_done?"":"Searching... ");o=num(o,(unsigned long)L->entry_count);o=put(o,L->entry_count==1?" found in ":" found in ");o=num(o,(unsigned long)search_folders);o=put(o,search_folders==1?" folder":" folders");if(search_limited)o=put(o," \xC2\xB7 stopped at the limit");}
        else if(T()->filter[0]&&L->shown!=L->entry_count){o=num(o,(unsigned long)L->shown);o=put(o," of ");o=num(o,(unsigned long)L->entry_count);o=put(o," items match");}
        else{o=num(o,(unsigned long)L->shown);o=put(o,L->shown==1?" item":" items");}
        o=put(o," \xC2\xB7 ");o=num(o,(unsigned long)folders);o=put(o,folders==1?" folder":" folders");
        if(n){o=put(o," \xC2\xB7 ");o=num(o,(unsigned long)n);o=put(o," selected");if(bytes){o=put(o," (");o=size_text(o,bytes);o=put(o,")");}}
        if(L->truncated)put(o," \xC2\xB7 too many items: some are not shown");
    }
    int ty=y+(STATUS_H-zui_caption.height)/2;
    fit(16,ty,t,ui.width/2+120,&zui_caption,status_note[0]?warn():ui.theme.secondary);
    int right=ui.width-16;
    if(admin){text_right(right,ty,"Administrator",&zui_caption,ui.theme.accent);right-=measure("Administrator",&zui_caption)+20;}
    else if(needs_admin){struct zui_rect b={right-84,y+3,84,STATUS_H-6};button(b,"Unlock",H_UNLOCK,0,1);right-=96;}
    if(L->have_fs){char f[48];o=size_text(f,L->fs.available);put(o," free");text_right(right,ty,f,&zui_caption,ui.theme.secondary);}
}
static void draw_menu(void){
    int w=250,h=menu_count*34+12,x=menu_x,y=menu_y;
    if(x+w>ui.width-8){x=ui.width-8-w;}
    if(y+h>ui.height-8){y=ui.height-8-h;}
    if(x<8){x=8;}
    if(y<8){y=8;}
    struct zui_rect r={x,y,w,h};
    zui_round(&ui,(struct zui_rect){r.x-1,r.y-1,r.w+2,r.h+2},11,ui.theme.border);zui_round(&ui,r,10,ui.theme.raised);
    hit(r,H_MENU+99);
    for(int i=0;i<menu_count;i++){
        struct zui_rect row={x+6,y+6+i*34,w-12,32};int m=menu_items[i],on=menu_enabled(m);
        if(on&&(hover==H_MENU+i||menu_focus==i))zui_round(&ui,row,6,mix(ui.theme.raised,ui.theme.accent,18));
        if(menu_icon(m))icon(menu_icon(m),row.x+10,row.y+8,16,on?ZUI_ICON_TEXT:ZUI_ICON_DISABLED);
        text(row.x+36,row.y+(32-zui_body.height)/2,menu_label(m),&zui_body,on?ui.theme.text:ui.theme.secondary);
        text_right(row.x+row.w-10,row.y+(32-zui_caption.height)/2,menu_keys(m),&zui_caption,ui.theme.secondary);
        if(on)hit(row,H_MENU+i);
    }
}
static void dim(void){
    /* Through zui: on a material frame pixels are premultiplied, and a plain RGB mix there produced colour above
     * alpha, which the desktop refuses (the present failed and Horizon ended, cancelling its job). */
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});
    zui_round_alpha(&ui,(struct zui_rect){0,0,ui.width,ui.height},0,0x000000,(light()?30:45)*255/100);
}
static struct zui_rect card_rect(int w,int h){return (struct zui_rect){(ui.width-w)/2,(ui.height-h)/2,w,h};}
static void card(struct zui_rect r){zui_round(&ui,(struct zui_rect){r.x-1,r.y-1,r.w+2,r.h+2},13,ui.theme.border);zui_round(&ui,r,12,ui.theme.surface);hit(r,H_MODAL+99);}
static void draw_prompt(void){
    dim();hit((struct zui_rect){0,0,ui.width,ui.height},H_MODAL+98);
    struct zui_rect r=card_rect(420,prompt_error[0]?212:188);card(r);
    int x=r.x+24,w=r.w-48,y=r.y+22;
    text(x,y,prompt==PROMPT_RENAME?"Rename":"New folder",&zui_heading,ui.theme.text);y+=44;
    struct zui_rect f={x,y,w,36};field(f,1);zui_input_draw(&ui,(struct zui_rect){f.x+6,f.y+1,f.w-8,f.h-2},&prompt_input,1);y+=46;
    if(prompt_error[0]){fit(x,y,prompt_error,w,&zui_caption,bad());y+=24;}
    button((struct zui_rect){x+w-216,y+4,104,36},"Cancel",H_MODAL+2,0,1);
    button((struct zui_rect){x+w-104,y+4,104,36},prompt==PROMPT_RENAME?"Rename":"Create",H_MODAL+1,1,1);
}
static void draw_question(struct job *j){
    dim();hit((struct zui_rect){0,0,ui.width,ui.height},H_MODAL+98);
    int folders=j->status.question_kind==2;
    struct zui_rect r=card_rect(ui.width<560?ui.width-40:520,214);card(r);
    int x=r.x+24,w=r.w-48,y=r.y+22;
    char t[220],*o=put(t,"\"");char l[96];copy(l,j->status.question,sizeof(l));o=put(o,l);o=put(o,"\" already exists");
    fit(x,y,t,w,&zui_heading,ui.theme.text);y+=40;
    o=put(t,folders?"A folder of this name is already in ":"An item of this name is already in ");o=put(o,j->target[0]?j->target:"the destination");
    put(o,folders?". Merge puts the contents together; Replace removes the folder that is there.":". Replace removes the one that is there.");
    zui_paragraph(&ui,(struct zui_rect){x,y,w,44},t,&zui_body,ui.theme.secondary,0);y+=50;
    struct zui_rect toggle={x,y,w,24};
    zui_round(&ui,(struct zui_rect){x,y+2,18,18},5,conflict_all?ui.theme.accent:ui.theme.border);
    if(!conflict_all)zui_round(&ui,(struct zui_rect){x+1,y+3,16,16},4,ui.theme.surface);
    else icon("check",x+1,y+3,16,ZUI_ICON_SELECTED);
    text(x+28,y+3,"Do the same for the rest",&zui_body,ui.theme.text);hit(toggle,H_MODAL+10);
    y+=40;
    int bw=folders?(w-24)/4:(w-16)/3,bx=x;
    button((struct zui_rect){bx,y,bw,36},"Skip",H_MODAL+Z_JOB_SKIP,0,1);bx+=bw+8;
    button((struct zui_rect){bx,y,bw,36},"Keep both",H_MODAL+Z_JOB_KEEP_BOTH,0,1);bx+=bw+8;
    if(folders){button((struct zui_rect){bx,y,bw,36},"Merge",H_MODAL+Z_JOB_MERGE,1,1);bx+=bw+8;}
    button((struct zui_rect){bx,y,bw,36},"Replace",H_MODAL+Z_JOB_REPLACE,!folders,1);
}

/* ---------- Appearance. */
static void update_appearance(void){
    struct zenith_appearance state={0};struct zenith_scoped_appearance scoped={0};
    int personal=zenith_scoped_appearance_read(1,&scoped)==0;
    if(personal)state=scoped.appearance;
    if((personal||!zenith_appearance_read(&state))&&state.active_mode<2&&state.active_accent<6){const unsigned int *p=state.palette[state.active_mode][state.active_accent];ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};}
    /* Whole-app transparency (Meridian > Appearance > Materials), from the person's own scope when they have one. */
    struct zenith_material_settings material_state={0};
    if(personal)ui.full_background=(scoped.materials.saved&5)==5;
    else if(!zenith_material_settings_read(&material_state))ui.full_background=(material_state.saved&5)==5;
    long clock=zui_motion_clock();if(clock>=0)zui_apply_motion(&ui,clock);
    ui.dirty=1;
}

/* ---------- Frame. */
static int props_open;static void draw_properties(void);static void draw_drag(void);
static void draw(void){
    hit_count=0;ui.count=0;ui.focus=-1;ui.pressed=-1;
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});
    struct job *j=strip_job();
    long now=clock_ms();
    if(j&&Z_JOB_FINAL(j->state)&&j->state==Z_JOB_DONE&&!j->status.errors&&now>=0&&now-j->finished_at>4000)j->shown=0;
    int strip_on=j&&j->shown;
    int bottom=ui.height-STATUS_H-(strip_on?STRIP_H+8:0);
    int sw=sidebar_w(),iw=info_w();
    if(ui.foreground){
        /* A material frame starts transparent: the desktop fills what the regions name with its blurred backdrop. */
        zui_begin_material_frame(&ui);
        if(see_through())zui_material_surface(&ui,(struct zui_rect){0,0,ui.width,ui.height},0,ZUI_SURFACE_CONTENT,2);
        else{
            zui_fill(&ui,ui.clip,ui.theme.subtle);
            /* Materials without whole-app transparency: Places is navigation, like a workspace sidebar. */
            struct zui_rect nav={0,BODY_Y,sw+24,bottom-BODY_Y};
            if(sw&&zui_material_surface(&ui,nav,0,ZUI_SURFACE_NAVIGATION,2))
                for(int y=nav.y;y<nav.y+nav.h;y++)for(int x=nav.x;x<nav.x+nav.w;x++)ui.pixels[y*ui.width+x]=0;
        }
    }
    else zui_fill(&ui,ui.clip,ui.theme.subtle);
    draw_tabs();
    draw_toolbar();
    if(sw)draw_sidebar(12,BODY_Y,sw,bottom-BODY_Y);
    struct zui_rect c={sw?sw+24:12,BODY_Y,ui.width-(sw?sw+24:12)-12-(iw?iw+12:0),bottom-BODY_Y-4};
    {
        int a=TB()->active;
        if(TB()->split){
            int half=(c.w-8)/2;pane_rect[0]=(struct zui_rect){c.x,c.y,half,c.h};pane_rect[1]=(struct zui_rect){c.x+half+8,c.y,c.w-half-8,c.h};
        }else pane_rect[0]=c;
        for(int pn=0;pn<=TB()->split;pn++){
            with_pane(pn);drawing_pane=pn;
            zui_clip(&ui,pane_rect[pn]);draw_content(pane_rect[pn]);
            zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});
            /* The active pane of a split carries the accent outline. */
            if(TB()->split&&pn==a)zui_round_outline(&ui,pane_rect[pn],12,ui.theme.accent);
        }
        with_pane(a);drawing_pane=a;
    }
    if(iw)draw_info((struct zui_rect){ui.width-12-iw,BODY_Y,iw,bottom-BODY_Y-4});
    if(strip_on){struct zui_rect s={c.x,bottom+4,ui.width-c.x-12,STRIP_H};draw_strip(s,j);}
    zui_fill(&ui,(struct zui_rect){0,ui.height-STATUS_H,ui.width,1},ui.theme.border);
    draw_status(ui.height-STATUS_H);
    draw_drag();
    if(menu_open)draw_menu();
    struct job *q=asking();
    if(q&&!asking_dismissed)draw_question(q);
    else if(prompt)draw_prompt();
    else if(props_open)draw_properties();
    zui_draw_controls(&ui);
    if(confirm.open)zui_dialog_draw(&ui,&confirm);
    ui.dirty=0;
    /* Window coordinates of the focused item and of split panes, after this frame (gates drag with the mouse). */
    if(announce_focus&&L->focus_index>=0&&L->focus_index<L->shown){
        announce_focus=0;int k=L->focus_index,view=T()->view;struct zui_rect list=L->content_rect;
        int y=list.y-L->content_scroll.visual-(view==1?28:0)+item_top(k)+item_height()/2;
        int x=view==0?list.x+(list.w-L->columns_now*cell_w())/2+(k%L->columns_now)*cell_w()+cell_w()/2:list.x+60;
        char line[400],*o=put(line,"HORIZON_FOCUS ");o=put(o,name_of(L->order[k]));o=put(o," ");o=num(o,(unsigned long)(x<0?0:x));o=put(o," ");
        o=num(o,(unsigned long)(y<0?0:y));put(o,"\n");print(line);
    }
    if(announce_panes&&TB()->split){
        announce_panes=0;char line[160],*o=put(line,"HORIZON_PANES");
        for(int pn=0;pn<2;pn++){struct zui_rect r=pane_rect[pn];int v[4]={r.x,r.y,r.w,r.h};for(int i=0;i<4;i++){*o++=' ';o=num(o,(unsigned long)v[i]);}}
        put(o,"\n");print(line);
    }
}

/* ---------- Properties: what an item is, and its permissions, which its owner (or root) may change. */
static int props_focus,props_writable;static unsigned int props_mode,props_original;
static struct entry props_entry;static char props_name[256],props_path[1024],props_error[120];
static void open_properties(void){
    if(L->focus_index<0||L->focus_index>=L->shown)return;
    props_entry=L->entries[L->order[L->focus_index]];copy(props_name,name_of(L->order[L->focus_index]),sizeof(props_name));
    join(props_path,T()->path,props_name);
    props_mode=props_original=props_entry.mode&07777;props_writable=(my_uid==0||admin||props_entry.uid==my_uid)&&!is_link(&props_entry);
    props_focus=0;props_error[0]=0;props_open=1;
    char line[400],*o=put(line,"HORIZON_PROPERTIES ");o=put(o,props_name);o=put(o," mode=");o=permissions(o,props_mode);
    put(o,props_writable?" changeable\n":" read-only\n");print(line);ui.dirty=1;
}
static unsigned int props_bit(int i){return (0400u>>(i%3))>>(3*(i/3));}
static void props_apply(void){
    if(!props_writable||props_mode==props_original){props_open=0;ui.dirty=1;return;}
    long r=zenith_path(5,props_path,length(props_path),0,0,props_mode&07777,0);
    char line[400],*o=put(line,"HORIZON_CHMOD ");o=put(o,props_name);o=put(o," ");o=permissions(o,props_mode);o=put(o," result=");
    if(r<0){*o++='-';o=num(o,(unsigned long)-r);}else o=num(o,(unsigned long)r);put(o,"\n");print(line);
    if(r<0){copy(props_error,r==-1||r==-13?"Only the owner or root can change permissions.":r==-30?"This disk is read-only.":"Zenith could not change the permissions.",sizeof(props_error));if((r==-1||r==-13)&&!admin)needs_admin=1;ui.dirty=1;return;}
    props_open=0;copy(T()->focus,props_name,sizeof(T()->focus));load();ui.dirty=1;
}
static void draw_properties(void){
    dim();hit((struct zui_rect){0,0,ui.width,ui.height},H_MODAL+98);
    int h=props_error[0]?440:416;
    struct zui_rect r=card_rect(ui.width<500?ui.width-24:460,ui.height<h+24?ui.height-24:h);card(r);
    int x=r.x+24,w=r.w-48,y=r.y+20;
    icon(icon_of(&props_entry,props_name),x,y+2,32,ZUI_ICON_NORMAL);
    fit(x+44,y,props_name,w-44,&zui_heading,ui.theme.text);
    fit(x+44,y+30,kind_of(&props_entry,props_name),w-44,&zui_caption,ui.theme.secondary);y+=58;
    char v[160];
    info_row(x,y,w,"Location",T()->path);y+=24;
    if(is_folder(&props_entry))items_text(v,props_entry.items);else size_text(v,props_entry.size);
    info_row(x,y,w,is_folder(&props_entry)?"Contains":"Size",v);y+=24;
    date_text(v,props_entry.mtime,props_entry.zone,1);info_row(x,y,w,"Modified",v);y+=24;
    id_text(v,props_entry.uid,0);info_row(x,y,w,"Owner",v);y+=24;
    id_text(v,props_entry.gid,1);info_row(x,y,w,"Group",v);y+=32;
    text(x,y,"Permissions",&zui_label,ui.theme.text);y+=26;
    static const char *const cols[3]={"Read","Write","Run"},*const rows[3]={"Owner","Group","Others"};
    int cx=x+110,cw=(w-110)/3;
    for(int c=0;c<3;c++)text(cx+c*cw,y,cols[c],&zui_caption,ui.theme.secondary);
    y+=20;
    for(int row=0;row<3;row++){
        text(x,y+4,rows[row],&zui_body,ui.theme.text);
        for(int c=0;c<3;c++){
            int i=row*3+c;struct zui_rect b={cx+c*cw,y+1,20,20};int on=(props_mode&props_bit(i))!=0;
            if(props_focus==i&&ui.focus_visible&&props_writable)zui_round_outline(&ui,(struct zui_rect){b.x-3,b.y-3,26,26},8,ui.theme.accent);
            zui_round(&ui,b,5,on?(props_writable?ui.theme.accent:ui.theme.secondary):ui.theme.border);
            if(!on)zui_round(&ui,(struct zui_rect){b.x+1,b.y+1,18,18},4,ui.theme.surface);
            else icon("check",b.x+2,b.y+2,16,ZUI_ICON_SELECTED);
            if(props_writable)hit((struct zui_rect){b.x-4,b.y-4,cw-8,28},H_MODAL+20+i);
        }
        y+=30;
    }
    if(!props_writable){fit(x,y+4,is_link(&props_entry)?"Links have no permissions of their own.":"Only the owner or root can change these.",w,&zui_caption,ui.theme.secondary);}
    if(props_error[0])fit(x,y+4,props_error,w,&zui_caption,bad());
    int by=r.y+r.h-56;
    button((struct zui_rect){x+w-216,by,104,36},"Close",H_MODAL+41,0,1);
    button((struct zui_rect){x+w-104,by,104,36},"Apply",H_MODAL+40,1,props_writable&&props_mode!=props_original);
}

/* ---------- Drag and drop: the selection onto a folder, the other pane or another tab; then Move here or Copy
 * here (Shift moves and Ctrl copies without asking), as transfer jobs. */
static int drag_armed,dragging,drag_x0,drag_y0,drag_x,drag_y,drag_count,drop_count;
static char drag_paths[CLIP][1024],drop_target[1024];static struct zui_rect drop_rect;static int drop_valid;
static void drag_start(void){
    const char *list[CLIP];drag_count=selection_paths(list,drag_paths,CLIP);
    if(!drag_count){drag_armed=0;return;}
    dragging=1;drop_valid=0;
    char line[64],*o=put(line,"HORIZON_DRAG ");o=num(o,(unsigned long)drag_count);put(o,"\n");print(line);
}
static int dragged(const char *path){for(int i=0;i<drag_count;i++)if(same(drag_paths[i],path))return 1;return 0;}
/* Where a drop at (x,y) goes, from the last frame's hit areas. */
static void drag_target(int x,int y){
    drop_valid=0;
    int id=hit_at(x,y);
    if(id>=H_ITEM){
        int pn=(id-H_ITEM)/PANE_ITEMS,k=(id-H_ITEM)%PANE_ITEMS;struct listing *l=&listings[pn];
        if(k<l->shown){const struct entry *e=&l->entries[l->order[k]];
            if(is_folder(e)){join(drop_target,tabs[current].pane[pn].path,l->pool+e->name);if(!dragged(drop_target)){drop_valid=1;drop_rect=(struct zui_rect){x-4,y-4,8,8};}}}
    }
    if(!drop_valid&&(id==H_CONTENT||id==H_CONTENT2||id>=H_ITEM)){
        int pn=id>=H_ITEM?(id-H_ITEM)/PANE_ITEMS:id==H_CONTENT2;
        if(pn!=TB()->active){copy(drop_target,tabs[current].pane[pn].path,1024);drop_valid=1;drop_rect=pane_rect[pn];}
    }
    if(!drop_valid)for(int i=0;i<strip.count&&i<tab_count;i++)if(i!=current&&inside(strip.tab[i],x,y)){
        copy(drop_target,tabs[i].pane[tabs[i].active].path,1024);drop_valid=1;drop_rect=strip.tab[i];
    }
    /* Into the folder the items are already in: nothing to do. */
    if(drop_valid){char from[1024];parent(from,drag_paths[0]);if(same(from,drop_target))drop_valid=0;}
}
static void drop(int op){
    if(!drop_count)return;
    const char *list[CLIP];for(int i=0;i<drop_count;i++)list[i]=drag_paths[i];
    start_job(op,drop_target,drop_count,list);drop_count=0;
}
static void drag_end(const struct zenith_gui_event *e){
    dragging=0;drag_armed=0;
    drag_target(e->x,e->y);
    if(!drop_valid){print("HORIZON_DROP none\n");ui.dirty=1;return;}
    drop_count=drag_count;
    char line[1100],*o=put(line,"HORIZON_DROP ");o=put(o,drop_target);put(o,"\n");print(line);
    if(e->modifiers&1)drop(Z_JOB_MOVE);
    else if(e->modifiers&2)drop(Z_JOB_COPY);
    else{
        menu_count=0;menu_items[menu_count++]=M_DROP_MOVE;menu_items[menu_count++]=M_DROP_COPY;menu_items[menu_count++]=M_DROP_CANCEL;
        menu_open=1;menu_x=e->x;menu_y=e->y;menu_focus=0;print("HORIZON_MENU drop\n");
    }
    ui.dirty=1;
}
static void draw_drag(void){
    if(!dragging)return;
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});
    if(drop_valid)zui_round_outline(&ui,drop_rect.w>8?drop_rect:(struct zui_rect){drop_rect.x-40,drop_rect.y-20,80,40},10,ui.theme.accent);
    char t[160],*o=num(t,(unsigned long)drag_count);o=put(o,drag_count==1?" item":" items");
    if(drop_valid){o=put(o," to ");char l[64];copy(l,same(drop_target,home)?"Home":leaf(drop_target),sizeof(l));put(o,l);}
    int w=measure(t,&zui_caption)+24;struct zui_rect b={drag_x+14,drag_y+14,w,28};
    if(b.x+b.w>ui.width-4)b.x=ui.width-4-b.w;
    if(b.y+b.h>ui.height-4)b.y=ui.height-4-b.h;
    zui_round(&ui,(struct zui_rect){b.x-1,b.y-1,b.w+2,b.h+2},9,ui.theme.border);zui_round(&ui,b,8,ui.theme.raised);
    text(b.x+12,b.y+(28-zui_caption.height)/2,t,&zui_caption,ui.theme.text);
}

/* ---------- Administrator (HZ4): the Desktop Portal's Authorization asks for org.zenith.files.manage; while
 * granted, the kernel serves this Horizon's file calls as root until it closes. Offered after a refusal. */
static long unlock_handle;
static void unlock(void){
    if(unlock_handle>0||admin)return;
    long h=zenith_authorize("Authorize",Z_ACTION_FILES);
    if(h<0){copy(status_note,h==-38?"This Zenith cannot unlock file administration. Update Zenith.":"Unlock is not available now.",sizeof(status_note));ui.dirty=1;return;}
    unlock_handle=h;print("HORIZON_UNLOCK asking\n");
}
static void poll_unlock(void){
    if(unlock_handle<=0)return;
    static struct zenith_portal_response r;long s=zenith_portal_poll(unlock_handle,&r);
    if(s<0){unlock_handle=0;return;}
    if(!Z_PORTAL_FINAL(s))return;
    zenith_portal_close(unlock_handle);unlock_handle=0;
    const struct zenith_authorization_result *a=(const void *)r.response;
    if(s==Z_PORTAL_APPROVED&&a->granted){
        admin=1;needs_admin=0;copy(status_note,"Unlocked: Horizon may change any file until it closes.",sizeof(status_note));print("HORIZON_UNLOCKED\n");
        remember_focus();load();find_places();update_title();
    }else{
        char m[96];copy(m,r.message,sizeof(m));char *o=put(status_note,"Not unlocked");if(m[0]){o=put(o,": ");put(o,m);}print("HORIZON_UNLOCK denied\n");
    }
    ui.dirty=1;
}

/* ---------- Tabs at sign-in (HZ4): ~/.config/zenith/horizon-tabs, written when Horizon closes and read when it
 * starts without a folder. Folders that are gone are skipped. */
static void tabs_file(char *o){join(o,home,".config/zenith/horizon-tabs");}
static void save_tabs(void){
    if(!tab_count)return;
    remember_panes();
    static char text[24000];char *o=put(text,"horizon-tabs 1\ncurrent ");o=num(o,(unsigned long)current);*o++='\n';
    for(int i=0;i<tab_count;i++){
        o=put(o,"tab ");o=num(o,(unsigned long)tabs[i].active);*o++=' ';o=num(o,(unsigned long)tabs[i].split);*o++='\n';
        for(int pn=0;pn<=tabs[i].split;pn++){const struct pane *q=&tabs[i].pane[pn];
            o=put(o,"pane ");o=num(o,(unsigned long)q->view);*o++=' ';o=num(o,(unsigned long)q->sort);*o++=' ';o=num(o,(unsigned long)q->descending);*o++=' ';
            o=num(o,(unsigned long)q->zoom);*o++=' ';o=put(o,q->path);*o++='\n';}
    }
    *o=0;
    char folder[1024],path[1024];join(folder,home,".config/zenith");tabs_file(path);
    if(make_dirs(folder)&&!write_small(path,text))print("HORIZON_TABS_SAVED\n");
}
static int restore_tabs(void){
    static char text[24000];char path[1024];tabs_file(path);
    if(read_small(path,text,sizeof(text))<=0||!starts(text,"horizon-tabs 1\n"))return 0;
    int saved_current=0,tab_open=-1;
    for(char *l=text;*l;){
        char *end=l;while(*end&&*end!='\n')end++;
        char c=*end;*end=0;
        if(starts(l,"current ")){saved_current=0;for(char *d=l+8;*d>='0'&&*d<='9';d++)saved_current=saved_current*10+(*d-'0');}
        else if(starts(l,"tab ")){tab_open=tab_count<MAX_TABS?-2:-1;}
        else if(starts(l,"pane ")&&tab_open!=-1){
            int v[4]={0,0,0,1};char *d=l+5;
            for(int i=0;i<4;i++){v[i]=0;while(*d>='0'&&*d<='9')v[i]=v[i]*10+(*d++-'0');if(*d==' ')d++;}
            if(*d=='/'&&is_directory(d)){
                if(tab_open==-2){open_tab(d,0);tab_open=tab_count-1;}
                else if(!tabs[tab_open].split){tabs[tab_open].split=1;tabs[tab_open].pane[1]=tabs[tab_open].pane[0];copy(tabs[tab_open].pane[1].path,d,1024);copy(tabs[tab_open].pane[1].history[0],d,1024);}
                struct pane *q=&tabs[tab_open].pane[tabs[tab_open].split];
                q->view=v[0]&1;q->sort=v[1]&3;q->descending=v[2]&1;q->zoom=v[3]>3?3:v[3];
            }
        }
        *end=c;l=*end?end+1:end;
    }
    if(!tab_count)return 0;
    current=saved_current<tab_count?saved_current:0;with_pane(0);load_tab();update_title();
    char line[64],*o=put(line,"HORIZON_TABS_RESTORED ");o=num(o,(unsigned long)tab_count);put(o,"\n");print(line);
    return tab_count;
}

/* ---------- Actions. */
static void menu_action(int m){
    menu_open=0;ui.dirty=1;
    switch(m){
    case M_OPEN:open_item(L->focus_index,0);break;
    case M_OPEN_TAB:open_item(L->focus_index,1);break;
    case M_CUT:copy_selection(1);break;
    case M_COPY:copy_selection(0);break;
    case M_PASTE:{
        /* On a folder: paste into it. */
        if(L->focus_index>=0&&is_folder(&L->entries[L->order[L->focus_index]])&&selected_count(0)==1&&clip_count){
            char p[1024];join(p,T()->path,name_of(L->order[L->focus_index]));const char *list[CLIP];for(int i=0;i<clip_count;i++)list[i]=clip_paths[i];
            start_job(clip_cut?Z_JOB_MOVE:Z_JOB_COPY,p,clip_count,list);if(clip_cut)clip_count=0;
        }else paste();
        break;}
    case M_RENAME:ask_name(PROMPT_RENAME);break;
    case M_DELETE:ask_delete();break;
    case M_TRASH:trash_selection();break;
    case M_EXTRACT:case M_COMPRESS:archive_action(m==M_EXTRACT);break;
    case M_RESTORE:restore_selection();break;
    case M_EMPTY_TRASH:empty_trash();break;
    case M_FOLDER:ask_name(PROMPT_FOLDER);break;
    case M_SELECT_ALL:for(int k=0;k<L->shown;k++)L->entries[L->order[k]].selected=1;announce_selection();break;
    case M_HIDDEN:show_hidden=!show_hidden;remember_focus();arrange();break;
    case M_REFRESH:remember_focus();load();find_places();break;
    case M_OPEN_WITH:if(L->focus_index>=0){char path[1024];join(path,T()->path,name_of(L->order[L->focus_index]));open_file(path,1);}break;
    case M_PROPERTIES:open_properties();break;
    case M_SPLIT:toggle_split();break;
    case M_DROP_MOVE:case M_DROP_COPY:drop(m==M_DROP_MOVE?Z_JOB_MOVE:Z_JOB_COPY);break;
    case M_DROP_CANCEL:drop_count=0;break;
    }
}
static void answer(int value){
    struct job *j=asking();if(!j)return;
    zenith_job_answer(j->id,(unsigned int)value|(conflict_all?Z_JOB_FOR_ALL:0));
    char line[64],*o=put(line,"HORIZON_ANSWER ");o=num(o,(unsigned long)value);put(o,conflict_all?" all\n":"\n");print(line);
    conflict_all=0;j->state=Z_JOB_WORKING;ui.dirty=1;
}
static long last_click_time;static int last_click_id;
static void pointer_down(const struct zenith_gui_event *e){
    int id=hit_at(e->x,e->y);
    if(id!=H_FILTER&&filter_focus){filter_focus=0;ui.dirty=1;}
    if(id!=H_LOCATION&&editing_location){editing_location=0;ui.dirty=1;}
    if(id>=H_ITEM){
        activate_pane((id-H_ITEM)/PANE_ITEMS);
        int k=(id-H_ITEM)%PANE_ITEMS;long now=clock_ms();
        int twice=last_click_id==id&&now>=0&&now-last_click_time<450&&!(e->modifiers&3);
        last_click_id=id;last_click_time=now;
        if(twice){open_item(k,(e->modifiers&2)!=0);last_click_id=0;return;}
        if(e->modifiers&2){L->entries[L->order[k]].selected^=1;L->focus_index=k;L->anchor=k;}
        else if(e->modifiers&1){if(L->anchor<0)L->anchor=k;L->focus_index=k;select_range(L->anchor,k,0);}
        else{if(!L->entries[L->order[k]].selected)select_only(k);L->focus_index=k;L->anchor=k;}
        ui.focus_visible=0;announce_selection();ui.dirty=1;
        drag_armed=1;drag_x0=e->x;drag_y0=e->y;return;
    }
    if(id==H_CONTENT||id==H_CONTENT2){
        activate_pane(id==H_CONTENT2);
        if(!(e->modifiers&3)){for(int k=0;k<L->shown;k++)L->entries[L->order[k]].selected=0;announce_selection();}
        ui.dirty=1;return;
    }
}
static void pointer_up(const struct zenith_gui_event *e){
    int id=hit_at(e->x,e->y);
    if(id==H_BACK)back();else if(id==H_FORWARD)forward();else if(id==H_UP)up();
    else if(id==H_ICONS)set_view(0);
    else if(id==H_DETAILS)set_view(1);
    else if(id==H_INFO){info_on=!info_on;ui.dirty=1;}
    else if(id==H_SPLIT)toggle_split();
    else if(id==H_UNLOCK)unlock();
    else if(id==H_FILTER){if(!filter_focus)focus_filter();}
    else if(id==H_LOCATION){if(!editing_location)start_location();}
    else if(id>=H_CRUMB&&id<H_CRUMB+64){char p[1024];crumb_path(p,id-H_CRUMB);go(p,1);}
    else if(id>=H_PLACE&&id<H_PLACE+PLACES){go(places[id-H_PLACE].path,1);}
    else if(id>=H_COLUMN&&id<H_COLUMN+16&&(id-H_COLUMN)%8<4){activate_pane((id-H_COLUMN)/8);struct pane *t=T();int s=(id-H_COLUMN)%8;if(t->sort==s)t->descending=!t->descending;else{t->sort=s;t->descending=s==SORT_TIME;}remember_focus();arrange();}
    else if(id==H_CANCEL_JOB){struct job *j=strip_job();if(j){zenith_job_cancel(j->id);print("HORIZON_CANCEL\n");}}
    else if(id==H_CLOSE_JOB){struct job *j=strip_job();if(j)j->shown=0;ui.dirty=1;}
}

/* Keys for the file view. Ctrl+letter arrives as the lowercase letter with modifier 2. */
static int key(const struct zenith_gui_event *e){
    int ctrl=(e->modifiers&6)==2,shift=e->modifiers&1,alt=(e->modifiers&4)!=0,plain=!(e->modifiers&6);
    int c=e->code>='A'&&e->code<='Z'?e->code+32:e->code;
    if(ctrl&&c=='t'){open_tab(T()->path,1);return 1;}
    if(ctrl&&c=='w'){close_tab(current);return 1;}
    if(ctrl&&c=='q'){done=1;return 1;}
    if(ctrl&&c=='u'){unlock();return 1;}
    if(e->extra==4&&(e->modifiers&~1)==2){switch_tab((current+(shift?tab_count-1:1))%tab_count);return 1;}
    if(ctrl&&c=='l'){start_location();return 1;}
    if((e->modifiers&7)==3&&c=='f'){search_mode=1;focus_filter();return 1;}
    if(ctrl&&c=='f'){search_mode=0;focus_filter();return 1;}
    if(ctrl&&c=='h'){menu_action(M_HIDDEN);return 1;}
    if(ctrl&&c=='a'){menu_action(M_SELECT_ALL);return 1;}
    if(ctrl&&c=='c'){copy_selection(0);return 1;}
    if(ctrl&&c=='x'){if(L->folder_writable)copy_selection(1);return 1;}
    if(ctrl&&c=='v'){if(L->folder_writable)paste();return 1;}
    if(ctrl&&c=='r'){menu_action(M_REFRESH);return 1;}
    if(ctrl&&(c=='+'||c=='=')){set_zoom(T()->zoom+1);return 1;}
    if(ctrl&&c=='-'){set_zoom(T()->zoom-1);return 1;}
    if(ctrl&&c=='0'){set_zoom(1);return 1;}
    if(e->extra==16&&plain){toggle_split();return 1;}                       /* F3 */
    if(e->extra==4&&!(e->modifiers&7)&&TB()->split){activate_pane(1-TB()->active);return 1;}
    if(ctrl&&c=='1'){set_view(0);return 1;}
    if(ctrl&&c=='2'){set_view(1);return 1;}
    if((e->modifiers&7)==3&&c=='n'){if(L->folder_writable)ask_name(PROMPT_FOLDER);return 1;}
    if(e->extra==Z_KEY_F2&&plain){if(L->folder_writable&&L->focus_index>=0)ask_name(PROMPT_RENAME);return 1;}
    if(e->extra==Z_KEY_F5&&plain){menu_action(M_REFRESH);return 1;}
    if(e->extra==Z_KEY_F11&&plain){info_on=!info_on;ui.dirty=1;return 1;}
    if(e->extra==23&&shift){
        /* A selection without a focused item (Ctrl+A) still gets the item menu, for its first item. */
        if(L->focus_index<0)for(int k=0;k<L->shown;k++)if(L->entries[L->order[k]].selected){L->focus_index=k;break;}
        open_menu(L->content_rect.x+40,L->content_rect.y+40,L->focus_index>=0&&selected_count(0));return 1;
    }
    if(e->extra==9&&plain&&!shift){if(L->folder_writable)trash_selection();return 1;}
    if(e->extra==9&&shift&&!(e->modifiers&6)){if(L->folder_writable)ask_delete();return 1;}
    if(alt&&e->extra==5){back();return 1;}
    if(alt&&e->extra==6){forward();return 1;}
    if(alt&&e->extra==7){up();return 1;}
    if(e->extra==3&&plain){back();return 1;}
    if(e->extra==2&&alt){open_properties();return 1;}
    if(e->extra==2){if(L->focus_index>=0)open_item(L->focus_index,ctrl);return 1;}
    if(e->extra==1){
        if(search_on){stop_search();T()->filter[0]=0;load();print("HORIZON_SEARCH stopped\n");return 1;}
        if(T()->filter[0]){T()->filter[0]=0;remember_focus();arrange();return 1;}
        if(selected_count(0)){for(int k=0;k<L->shown;k++)L->entries[L->order[k]].selected=0;announce_selection();ui.dirty=1;return 1;}
        return 0;
    }
    if(e->code==' '&&ctrl&&L->focus_index>=0){L->entries[L->order[L->focus_index]].selected^=1;announce_selection();ui.dirty=1;return 1;}
    int step=0,horizontal=T()->view==0;
    if(e->extra==5&&horizontal)step=-1;else if(e->extra==6&&horizontal)step=1;
    else if(e->extra==7)step=horizontal?-L->columns_now:-1;else if(e->extra==8)step=horizontal?L->columns_now:1;
    else if(e->extra==ZUI_KEY_PAGE_UP)step=-(horizontal?L->columns_now*(L->rows_visible>1?L->rows_visible-1:1):(L->rows_visible>1?L->rows_visible-1:1));
    else if(e->extra==ZUI_KEY_PAGE_DOWN)step=horizontal?L->columns_now*(L->rows_visible>1?L->rows_visible-1:1):(L->rows_visible>1?L->rows_visible-1:1);
    if(step||e->extra==10||e->extra==11){
        ui.focus_visible=1;
        int k=e->extra==10?0:e->extra==11?L->shown-1:L->focus_index<0?0:L->focus_index+step;
        if(!step||(k>=0&&k<L->shown)||e->extra>=12)move_focus(k,e->modifiers&3);
        return 1;
    }
    /* Type-ahead: the next item starting with this character. */
    if(plain&&e->code>' '&&e->code<127&&L->shown){
        for(int i=1;i<=L->shown;i++){int k=((L->focus_index<0?-1:L->focus_index)+i)%L->shown;if(lower(name_of(L->order[k])[0])==lower((char)e->code)){ui.focus_visible=1;move_focus(k,0);break;}}
        return 1;
    }
    return 0;
}

/* ---------- Test entry points without a window: list a folder, run a transfer through the real ABI. */
static int list_test(const char *path){
    tab_count=1;current=0;copy(tabs[0].pane[0].path,path,1024);load();
    if(L->list_error)return 1;
    for(int k=0;k<L->shown;k++){
        const struct entry *e=&L->entries[L->order[k]];char line[600],*o=put(line,"HORIZON_ENTRY ");o=put(o,name_of(L->order[k]));
        o=put(o,is_folder(e)?" folder items=":" file size=");
        if(is_folder(e)){if(e->items<0)o=put(o,"-");else o=num(o,(unsigned long)e->items);}else o=num(o,e->size);
        o=put(o," modified=");o=date_text(o,e->mtime,e->zone,1);o=put(o," owner=");o=id_text(o,e->uid,0);o=put(o," mode=");o=permissions(o,e->mode);put(o,"\n");print(line);
    }
    char line[200],*o=put(line,"HORIZON_FS ");o=put(o,L->fs.mount);o=put(o," ");o=put(o,L->fs.type);o=put(o," free=");o=num(o,L->fs.available);put(o,L->folder_writable?" writable\n":" read-only\n");print(line);
    return 0;
}
static int job_test(int op,int answer_with,const char *destination,int count,char **sources){
    start_job(op,destination,count,(const char *const *)sources);
    struct job *j=&jobs[0];if(!j->id)return 1;
    for(long polls=0;polls<200000;polls++){
        long s=zenith_job_status(j->id,&j->status);if(s<0)return 1;
        if((int)s!=j->state){j->state=(int)s;announce_job(j);}
        if(s==Z_JOB_ASKING){if(!answer_with){zenith_job_cancel(j->id);}else zenith_job_answer(j->id,(unsigned int)answer_with);}
        if(Z_JOB_FINAL(s)){zenith_job_forget(j->id);return s==Z_JOB_DONE?0:1;}
        /* Wait in the kernel: a spin spends this program's CPU budget and slows the job's own thread. */
        if(zenith_job_wait(j->id,500)<0)for(volatile unsigned long i=0;i<2000000;i++){}
    }
    return 1;
}
static int op_of(const char *s){return same(s,"copy")?Z_JOB_COPY:same(s,"move")?Z_JOB_MOVE:same(s,"delete")?Z_JOB_DELETE:0;}

int main(int argc,char **argv,char **envp){
    my_uid=(unsigned int)zenith_identity(102);
    for(int i=0;i<2;i++){listings[i].focus_index=listings[i].anchor=-1;listings[i].columns_now=listings[i].rows_visible=1;}
    read_ids("/etc/passwd",0);read_ids("/etc/group",1);
    for(char **e=envp;e&&*e;e++)if(starts(*e,"HOME=")&&(*e)[5]=='/')copy(home,*e+5,sizeof(home));
    trash_paths();
    if(argc==2&&same(argv[1],"--version")){print("horizon " SUNBEAM_VERSION "\n");return 0;}
    if(argc==3&&same(argv[1],"--list"))return list_test(argv[2]);
    /* --job copy|move|delete skip|replace|keep|merge|cancel DESTINATION SOURCE... ("-" for no destination) */
    if(argc>=6&&same(argv[1],"--job")&&op_of(argv[2])){
        const char *a=argv[3];int answer_with=same(a,"skip")?Z_JOB_SKIP:same(a,"replace")?Z_JOB_REPLACE:same(a,"keep")?Z_JOB_KEEP_BOTH:same(a,"merge")?Z_JOB_MERGE:0;
        return job_test(op_of(argv[2]),answer_with,same(argv[4],"-")?"":argv[4],argc-5,argv+5);
    }
    const char *start=argc==2&&argv[1][0]=='/'?argv[1]:home;
    if(argc>2||(argc==2&&argv[1][0]!='/')){print("usage: native run horizon [FOLDER]\n");return 2;}
    zui_icons_use_packages();
    /* New windows open at most 640x480; the sidebar and information panel appear once it is larger. */
    zui_init(&ui,pixels,640,480);update_appearance();
    struct zenith_material_caps caps={0};struct zenith_material_settings settings={0};
    material=!zenith_material_settings_read(&settings)&&!zenith_gui_material_caps(&caps)&&caps.version==2;
    handle=material?zenith_gui_open_material(640,480,"Horizon",7):zenith_gui_open(640,480,"Horizon",7);
    if(handle<0){print("horizon: graphical session required\n");return 1;}
    ui.foreground=material;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);return 1;}
    /* 15 adds middle presses (kernel 0.0.67); older kernels refuse it and get 7. */
    ui.hover_supported=zenith_gui_input(handle,15)==0||zenith_gui_input(handle,7)==0||zenith_gui_pointer_motion(handle)==0;
    if(!(argc==1&&restore_tabs()))open_tab(start,1);
    find_places();
    print(material?(ui.full_background?"HORIZON_MATERIAL window\n":"HORIZON_MATERIAL navigation\n"):"HORIZON_MATERIAL none\n");
    print(no_abi?"HORIZON_NO_FILES_ABI\n":"HORIZON_READY\n");
    long last=clock_ms();int polls=0,result=0;
    while(!done){
        long now=clock_ms();
        search_step();
        int busy=jobs[0].id&&!Z_JOB_FINAL(jobs[0].state);busy|=jobs[1].id&&!Z_JOB_FINAL(jobs[1].state);busy|=open_handle>0||unlock_handle>0;
        if((now>=0&&now-last>=(busy?150:1000))||(now<0&&++polls>=200)){
            last=now;polls=0;poll_jobs();poll_open();poll_unlock();
            struct job *j=strip_job();if(j&&j->shown&&j->state==Z_JOB_DONE&&!j->status.errors&&now-j->finished_at>4000)ui.dirty=1;
        }
        /* One thumbnail per pass, after the frame that asked for it. */
        if(thumb_want[0]&&!ui.dirty)thumb_fetch();
        zui_motion_tick(&ui,now);
        if(ui.dirty){
            draw();
            if(reveal_later){reveal_later=0;if(L->focus_index>=0){reveal(L->focus_index);draw();}}
            long r=material?zenith_gui_present_material(handle,ui.pixels,(unsigned long)ui.width*ui.height*4,ui.materials,ui.material_count)
                :zenith_gui_present(handle,ui.pixels,(unsigned long)ui.width*ui.height*4);
            if(r<0){result=zui_window_closed(handle)?0:1;break;}
        }
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_RESIZE){int r=zui_apply_viewport(&ui,handle,640*480);if(r<0){result=zui_window_closed(handle)?0:1;break;}
            /* The layout this size gets, for the gate (wide windows show Places and the information panel). */
            if(r>0){char line[96],*o=put(line,"HORIZON_LAYOUT ");o=num(o,(unsigned long)ui.width);o=put(o,"x");o=num(o,(unsigned long)ui.height);
                o=put(o," places=");o=num(o,(unsigned long)(sidebar_w()>0));o=put(o," info=");o=num(o,(unsigned long)(info_w()>0));put(o,"\n");print(line);}
            continue;}
        if(e.type==Z_GUI_CLOSE||e.type==Z_GUI_CLOSE_INTENT){done=1;continue;}
        if(e.type==Z_GUI_RESET){update_appearance();continue;}
        if(e.type==Z_GUI_FOCUS){ui.window_focused=e.code;if(e.code){print("HORIZON_FOCUSED\n");find_places();}ui.dirty=1;continue;}
        if(e.type==Z_GUI_MOVE){pointer_x=e.x;pointer_y=e.y;int h=e.x<0?H_NONE:hit_at(e.x,e.y);if(h!=hover){hover=h;ui.dirty=1;}continue;}
        /* Modal first: the delete dialog, the transfer question, the name prompt. */
        if(confirm.open){int choice=zui_dialog_event(&ui,&confirm,&e);
            if(choice==1){const char *list[CLIP];for(int i=0;i<delete_count;i++)list[i]=delete_paths[i];start_job(Z_JOB_DELETE,"",delete_count,list);}
            if(choice){ui.dirty=1;}
            continue;}
        struct job *q=asking();
        if(q&&!asking_dismissed){
            if(e.type==Z_GUI_UP){int id=hit_at(e.x,e.y);if(id==H_MODAL+10){conflict_all=!conflict_all;ui.dirty=1;}else if(id>H_MODAL&&id<=H_MODAL+4)answer(id-H_MODAL);}
            if(e.type==Z_GUI_KEY&&e.extra==1){answer(Z_JOB_SKIP);}
            if(e.type==Z_GUI_KEY&&e.extra==2){answer(q->status.question_kind==2?Z_JOB_MERGE:Z_JOB_REPLACE);}
            continue;
        }
        if(props_open&&!prompt){
            if(e.type==Z_GUI_UP){int id=hit_at(e.x,e.y);
                if(id>=H_MODAL+20&&id<H_MODAL+29){props_focus=id-H_MODAL-20;props_mode^=props_bit(props_focus);props_error[0]=0;ui.dirty=1;}
                else if(id==H_MODAL+40)props_apply();else if(id==H_MODAL+41){props_open=0;ui.dirty=1;}}
            if(e.type==Z_GUI_KEY){
                if(e.extra==1){props_open=0;ui.dirty=1;}
                else if(e.extra==2)props_apply();
                else if(e.code==' '&&!e.extra&&props_writable){props_mode^=props_bit(props_focus);props_error[0]=0;ui.focus_visible=1;ui.dirty=1;}
                else if(e.extra>=5&&e.extra<=8){int d=e.extra==5?-1:e.extra==6?1:e.extra==7?-3:3;int f=props_focus+d;if(f>=0&&f<9)props_focus=f;ui.focus_visible=1;ui.dirty=1;}
            }
            continue;
        }
        if(prompt){
            if(e.type==Z_GUI_UP){int id=hit_at(e.x,e.y);if(id==H_MODAL+1)name_done();else if(id==H_MODAL+2){prompt=0;ui.dirty=1;}}
            if(e.type==Z_GUI_KEY){if(e.extra==1){prompt=0;ui.dirty=1;}else if(e.extra==2)name_done();else if(zui_input_event(&prompt_input,&e)){prompt_error[0]=0;ui.dirty=1;}}
            continue;
        }
        if(menu_open){
            if(e.type==Z_GUI_UP){int id=hit_at(e.x,e.y);if(id>=H_MENU&&id<H_MENU+menu_count){menu_action(menu_items[id-H_MENU]);continue;}}
            if(e.type==Z_GUI_DOWN||e.type==Z_GUI_SECONDARY){int id=hit_at(e.x,e.y);if(id<H_MENU||id>H_MENU+99){menu_open=0;ui.dirty=1;}if(e.type==Z_GUI_DOWN)continue;}
            if(e.type==Z_GUI_KEY){
                if(e.extra==1){menu_open=0;ui.dirty=1;}
                else if(e.extra==7||e.extra==8){int d=e.extra==7?-1:1;for(int i=0;i<menu_count;i++){menu_focus=(menu_focus+d+menu_count)%menu_count;if(menu_enabled(menu_items[menu_focus]))break;}ui.dirty=1;}
                else if(e.extra==2&&menu_focus>=0&&menu_enabled(menu_items[menu_focus]))menu_action(menu_items[menu_focus]);
                continue;
            }
            if(menu_open&&e.type!=Z_GUI_SECONDARY)continue;
        }
        if(e.type==Z_GUI_KEY&&editing_location){
            if(e.extra==1){editing_location=0;ui.dirty=1;continue;}
            if(e.extra==2){
                char target[1024];copy(target,location_text,sizeof(target));
                if(target[0]=='~'){char t[1024];char *o=put(t,home);put(o,target+1);copy(target,t,sizeof(target));}
                long n=(long)length(target);while(n>1&&target[n-1]=='/')target[--n]=0;
                editing_location=0;
                if(target[0]!='/'){copy(status_note,"Type a full path, starting with / or ~.",sizeof(status_note));ui.dirty=1;continue;}
                if(!is_directory(target)){copy(status_note,"No such folder, or you may not open it.",sizeof(status_note));ui.dirty=1;continue;}
                go(target,1);continue;
            }
            if(zui_input_event(&location_input,&e))ui.dirty=1;
            continue;
        }
        if(e.type==Z_GUI_KEY&&filter_focus){
            if(e.extra==2&&search_mode&&T()->filter[0]){filter_focus=0;start_search();continue;}
            if(e.extra==1||e.extra==2||e.extra==8){filter_focus=0;if(e.extra==1){if(search_on){stop_search();T()->filter[0]=0;load();}else{T()->filter[0]=0;remember_focus();arrange();}}ui.dirty=1;continue;}
            if(zui_input_event(&filter_input,&e)){if(!search_mode){remember_focus();arrange();}else ui.dirty=1;}
            continue;
        }
        if(e.type==Z_GUI_SCROLL&&(e.modifiers&6)==2){set_zoom(T()->zoom+(e.code>0?1:-1));continue;}
        if(e.type==Z_GUI_SCROLL){int pn=pane_at(e.x,e.y);struct zui_scroll *s=pn>=0?&listings[pn].content_scroll:&L->content_scroll;if(zui_scroll_event(&ui,s,&e))ui.dirty=1;continue;}
        if(e.type==Z_GUI_SECONDARY&&e.code==3){
            /* Middle button (kernel 0.0.67): a folder opens in a new tab behind this one; a tab closes. */
            if(e.y<TAB_Y+TAB_H){for(int i=0;i<strip.count&&i<tab_count;i++){struct zui_rect r=strip.tab[i];
                if(e.x>=r.x&&e.x<r.x+r.w&&e.y>=r.y&&e.y<r.y+r.h){close_tab(i);break;}}continue;}
            int id=hit_at(e.x,e.y);
            if(id>=H_ITEM){activate_pane((id-H_ITEM)/PANE_ITEMS);int k=(id-H_ITEM)%PANE_ITEMS;if(is_folder(&L->entries[L->order[k]]))open_item(k,1);}
            continue;
        }
        if(e.type==Z_GUI_SECONDARY){
            int id=hit_at(e.x,e.y);
            if(id>=H_ITEM){activate_pane((id-H_ITEM)/PANE_ITEMS);int k=(id-H_ITEM)%PANE_ITEMS;if(!L->entries[L->order[k]].selected){select_only(k);L->anchor=k;}L->focus_index=k;announce_selection();open_menu(e.x,e.y,1);}
            else if(id==H_CONTENT||id==H_CONTENT2){activate_pane(id==H_CONTENT2);for(int k=0;k<L->shown;k++)L->entries[L->order[k]].selected=0;open_menu(e.x,e.y,0);}
            continue;
        }
        if(e.type==Z_GUI_DRAG&&(drag_armed||dragging)){
            int dx=e.x-drag_x0,dy=e.y-drag_y0;
            if(!dragging&&dx*dx+dy*dy>64)drag_start();
            if(dragging){drag_x=e.x;drag_y=e.y;drag_target(e.x,e.y);ui.dirty=1;}
            continue;
        }
        if(e.type==Z_GUI_UP&&dragging){drag_end(&e);continue;}
        if(e.type==Z_GUI_UP)drag_armed=0;
        if(e.type==Z_GUI_DOWN||e.type==Z_GUI_UP||e.type==Z_GUI_DRAG){
            if(e.y<TAB_Y+TAB_H){int a=zui_tabs_event(&strip,&e);
                if(a==ZUI_TABS_ADD)open_tab(T()->path,1);else if(a>0)switch_tab(a-1);else if(a<0)close_tab(-a-1);
                continue;}
            if(e.type==Z_GUI_DOWN){int pn=pane_at(e.x,e.y);if(pn>=0)activate_pane(pn);}
            if(zui_scroll_event(&ui,&L->content_scroll,&e)){ui.dirty=1;continue;}
            if(e.type==Z_GUI_DOWN)pointer_down(&e);
            else if(e.type==Z_GUI_UP)pointer_up(&e);
            continue;
        }
        if(e.type==Z_GUI_KEY&&dragging&&e.extra==1){dragging=0;drag_armed=0;print("HORIZON_DROP none\n");ui.dirty=1;continue;}
        if(e.type==Z_GUI_KEY){if(status_note[0]&&e.extra!=0){status_note[0]=0;ui.dirty=1;}key(&e);}
    }
    save_tabs();
    zenith_gui_close(handle);return result;
}
