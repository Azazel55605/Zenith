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
static struct entry entries[MAX_ENTRIES];static char pool[POOL_BYTES];
static int entry_count,truncated;static unsigned int pool_used;
static int order[MAX_ENTRIES],scratch[MAX_ENTRIES],shown;
static unsigned char page_buffer[Z_FILES_PAGE] __attribute__((aligned(16)));
static long list_error;static int folder_writable;
static struct zenith_filesystem fs;static int have_fs,no_abi;
static const char *name_of(int i){return pool+entries[i].name;}
static int is_folder(const struct entry *e){return (e->mode&0xf000)==0x4000||((e->mode&0xf000)==0xa000&&(e->target&0xf000)==0x4000);}
static int is_link(const struct entry *e){return (e->mode&0xf000)==0xa000;}

/* ---------- Tabs. */
#define MAX_TABS 8
#define HISTORY 12
enum { SORT_NAME=0,SORT_SIZE=1,SORT_TIME=2,SORT_TYPE=3 };
struct tab { char path[1024],focus[256],filter[96]; char history[HISTORY][1024]; int hcount,hpos,view,sort,descending,scroll; };
static struct tab tabs[MAX_TABS];static int tab_count,current;
static struct zui_tabs strip;
static int show_hidden,info_on=1;
static struct tab *T(void){return &tabs[current];}

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
    const struct entry *a=&entries[i],*b=&entries[j];
    int fa=is_folder(a),fb=is_folder(b);if(fa!=fb)return fb-fa;
    int r=0;struct tab *t=T();
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
static int focus_index=-1,anchor=-1;   /* positions in order[] */
static void arrange(void){
    shown=0;struct tab *t=T();
    for(int i=0;i<entry_count;i++){
        const char *n=name_of(i);
        if(n[0]=='.'&&!show_hidden){entries[i].selected=0;continue;}
        if(!matches(n,t->filter)){entries[i].selected=0;continue;}
        order[shown++]=i;
    }
    merge_sort(order,shown);
    focus_index=-1;
    for(int k=0;k<shown;k++)if(t->focus[0]&&same(name_of(order[k]),t->focus))focus_index=k;
    if(focus_index>=shown)focus_index=shown-1;
    anchor=focus_index;
    ui.dirty=1;
}
static int selected_count(unsigned long *bytes){
    int n=0;if(bytes)*bytes=0;
    for(int k=0;k<shown;k++){const struct entry *e=&entries[order[k]];if(e->selected){n++;if(bytes&&!is_folder(e))*bytes+=e->size;}}
    return n;
}
static void announce_path(void){
    char line[1200],*o=put(line,"HORIZON_PATH ");o=put(o,T()->path);o=put(o," ");o=num(o,(unsigned long)entry_count);
    if(list_error){o=put(o," error=");o=num(o,(unsigned long)-list_error);}
    put(o,"\n");print(line);
}
static void load(void){
    struct tab *t=T();
    static struct zenith_files_list request;
    entry_count=0;pool_used=0;truncated=0;list_error=0;folder_writable=0;
    unsigned int offset=0;
    for(int pages=0;pages<64;pages++){
        request=(struct zenith_files_list){1,Z_FILES_COUNT_ITEMS,offset,(unsigned int)length(t->path),{0}};
        copy(request.path,t->path,sizeof(request.path));
        long r=zenith_files_list(&request,page_buffer);
        if(r<0){list_error=r;if(r==-38)no_abi=1;break;}
        const struct zenith_files_page *h=(const void *)page_buffer;
        folder_writable=h->flags&1;now_utc=h->now;zone_now=h->zone;
        unsigned int at=sizeof(*h);
        for(unsigned int k=0;k<h->count&&at+sizeof(struct zenith_file_entry)<=Z_FILES_PAGE;k++){
            const struct zenith_file_entry *f=(const void *)(page_buffer+at);
            if(f->record_bytes<sizeof(*f)||at+f->record_bytes>Z_FILES_PAGE)break;
            at+=f->record_bytes;
            if(entry_count>=MAX_ENTRIES||pool_used+f->name_bytes+1>POOL_BYTES){truncated=1;continue;}
            struct entry *e=&entries[entry_count++];
            *e=(struct entry){pool_used,f->mode,f->uid,f->gid,f->target_mode,f->links,f->size,f->mtime,f->zone,f->items,0};
            for(unsigned int c=0;c<f->name_bytes;c++)pool[pool_used+c]=f->name[c];
            pool[pool_used+f->name_bytes]=0;pool_used+=f->name_bytes+1;
        }
        if(!h->next)break;
        offset=h->next;
    }
    have_fs=zenith_files_filesystem(t->path,length(t->path),&fs)==0;
    arrange();announce_path();
}

/* ---------- Window title: the active tab's folder. */
static void tab_label(char *o,const struct tab *t){
    if(same(t->path,home))put(o,"Home");else if(same(t->path,"/"))put(o,"Root");else copy(o,leaf(t->path),64);
}
static void update_title(void){
    char title[160],label[80];tab_label(label,T());
    char *o=put(title,label);put(o," \xE2\x80\x94 Horizon");
    zenith_gui_set_title(handle,title,length(title));
}

/* ---------- Navigation. */
static char status_note[160];
static int reveal_later;
static void remember_focus(void){
    struct tab *t=T();t->focus[0]=0;
    if(focus_index>=0&&focus_index<shown)copy(t->focus,name_of(order[focus_index]),sizeof(t->focus));
}
static struct zui_scroll content_scroll;
static void set_view(int view){T()->view=view;zui_scroll_jump(&content_scroll,0);reveal_later=1;print(view?"HORIZON_VIEW details\n":"HORIZON_VIEW icons\n");ui.dirty=1;}
static void go(const char *path,int record){
    struct tab *t=T();
    if(record){
        if(t->hcount==0){copy(t->history[0],t->path,1024);t->hcount=1;t->hpos=0;}
        if(t->hpos<t->hcount-1)t->hcount=t->hpos+1;
        if(t->hcount==HISTORY){for(int i=1;i<HISTORY;i++)copy(t->history[i-1],t->history[i],1024);t->hcount--;t->hpos--;}
        copy(t->history[t->hcount++],path,1024);t->hpos=t->hcount-1;
    }
    /* Coming back up: focus the folder we came from. */
    char from[1024];copy(from,t->path,1024);
    copy(t->path,path,1024);t->focus[0]=0;t->filter[0]=0;
    char up[1024];parent(up,from);
    if(same(up,path))copy(t->focus,leaf(from),sizeof(t->focus));
    zui_scroll_jump(&content_scroll,0);
    status_note[0]=0;
    load();update_title();
}
static void back(void){struct tab *t=T();if(t->hpos>0){t->hpos--;go(t->history[t->hpos],0);}}
static void forward(void){struct tab *t=T();if(t->hpos<t->hcount-1){t->hpos++;go(t->history[t->hpos],0);}}
static void up(void){struct tab *t=T();if(same(t->path,"/"))return;char p[1024];parent(p,t->path);go(p,1);}
static void open_tab(const char *path,int activate){
    if(tab_count>=MAX_TABS){copy(status_note,"Horizon keeps at most 8 tabs. Close one first.",sizeof(status_note));ui.dirty=1;return;}
    remember_focus();T()->scroll=content_scroll.offset;
    struct tab *t=&tabs[tab_count];
    struct tab *from=tab_count?T():0;
    *t=(struct tab){{0},{0},{0},{{0}},1,0,from?from->view:0,from?from->sort:0,from?from->descending:0,0};
    copy(t->path,path,1024);copy(t->history[0],path,1024);
    tab_count++;
    if(activate){current=tab_count-1;zui_scroll_jump(&content_scroll,0);load();update_title();}
    char line[1100],*o=put(line,"HORIZON_TABS ");o=num(o,(unsigned long)tab_count);o=put(o," active=");o=num(o,(unsigned long)current);put(o,"\n");print(line);
    ui.dirty=1;
}
static int done;
static void switch_tab(int i){
    if(i<0||i>=tab_count||i==current)return;
    remember_focus();T()->scroll=content_scroll.offset;
    current=i;load();zui_scroll_jump(&content_scroll,T()->scroll);update_title();
    char line[1100],*o=put(line,"HORIZON_TABS ");o=num(o,(unsigned long)tab_count);o=put(o," active=");o=num(o,(unsigned long)current);o=put(o," ");o=put(o,T()->path);put(o,"\n");print(line);
}
static void close_tab(int i){
    if(i<0||i>=tab_count)return;
    if(tab_count==1){done=1;return;}
    remember_focus();T()->scroll=content_scroll.offset;
    for(int k=i;k+1<tab_count;k++)tabs[k]=tabs[k+1];
    tab_count--;
    if(current>i||current>=tab_count)current=current>i?current-1:tab_count-1;
    load();zui_scroll_jump(&content_scroll,T()->scroll);update_title();
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
enum { H_NONE=0,H_BACK=1,H_FORWARD,H_UP,H_ICONS,H_DETAILS,H_INFO,H_FILTER,H_LOCATION,H_CANCEL_JOB,H_CLOSE_JOB,H_CONTENT,
       H_CRUMB=100,H_PLACE=200,H_COLUMN=300,H_MENU=400,H_MODAL=500,H_ITEM=1000 };
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
        if(changed){j->state=(int)s;announce_job(j);if(s!=Z_JOB_ASKING)asking_dismissed=0;}
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
    char line[64],*o=put(line,"HORIZON_JOB ");o=num(o,(unsigned long)id);put(o," started\n");print(line);
    status_note[0]=0;ui.dirty=1;
}

/* ---------- The clipboard is Horizon's own: paths and whether they were cut. */
#define CLIP 64
static char clip_paths[CLIP][1024];static int clip_count,clip_cut;
static int selection_paths(const char *list[],char storage[][1024],int max){
    int n=0;
    for(int k=0;k<shown&&n<max;k++)if(entries[order[k]].selected){join(storage[n],T()->path,name_of(order[k]));list[n]=storage[n];n++;}
    if(!n&&focus_index>=0&&focus_index<shown){join(storage[0],T()->path,name_of(order[focus_index]));list[0]=storage[0];n=1;}
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
static void ask_delete(void){
    const char *list[CLIP];delete_count=selection_paths(list,delete_paths,CLIP);
    if(!delete_count)return;
    char *o=put(confirm_title,"Delete ");
    if(delete_count==1){o=put(o,"\"");char l[64];copy(l,leaf(delete_paths[0]),sizeof(l));o=put(o,l);o=put(o,"\"");}
    else{o=num(o,(unsigned long)delete_count);o=put(o," items");}
    put(o," permanently?");
    copy(confirm_message,"They are removed at once. Zenith has no Trash yet, so this cannot be undone.",sizeof(confirm_message));
    confirm=(struct zui_dialog){1,900,confirm_title,confirm_message,{"Delete","Cancel",0},2};
    print("HORIZON_ASK delete\n");ui.dirty=1;
}
enum { PROMPT_NONE=0,PROMPT_RENAME=1,PROMPT_FOLDER=2 };
static int prompt;static char prompt_text[256],prompt_original[256],prompt_error[120];static struct zui_input prompt_input;
static void ask_name(int kind){
    prompt=kind;prompt_error[0]=0;
    if(kind==PROMPT_RENAME){
        if(focus_index<0||focus_index>=shown){prompt=0;return;}
        copy(prompt_text,name_of(order[focus_index]),sizeof(prompt_text));copy(prompt_original,prompt_text,sizeof(prompt_original));
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
    for(int i=0;i<entry_count;i++)if(same(name_of(i),n)){char *o=put(prompt_error,"\"");char l[64];copy(l,n,sizeof(l));o=put(o,l);put(o,"\" already exists here.");ui.dirty=1;return;}
    char target[1024];join(target,T()->path,n);
    long r;
    if(prompt==PROMPT_RENAME){char from[1024];join(from,T()->path,prompt_original);r=zenith_rename(from,length(from),target,length(target));}
    else r=zenith_mkdir(target,length(target));
    if(r<0){copy(prompt_error,r==-13?"You do not have permission to change this folder.":r==-17?"That name already exists.":"Zenith could not do that here.",sizeof(prompt_error));ui.dirty=1;return;}
    char line[400],*o=put(line,prompt==PROMPT_RENAME?"HORIZON_RENAMED ":"HORIZON_CREATED ");o=put(o,n);put(o,"\n");print(line);
    prompt=0;copy(T()->focus,n,sizeof(T()->focus));
    load();
    for(int k=0;k<shown;k++)entries[order[k]].selected=k==focus_index;
    ui.dirty=1;
}
static int conflict_all;

/* ---------- Context menu. */
enum { M_OPEN=1,M_OPEN_TAB,M_CUT,M_COPY,M_PASTE,M_RENAME,M_DELETE,M_FOLDER,M_SELECT_ALL,M_HIDDEN,M_REFRESH };
static int menu_open,menu_x,menu_y,menu_items[12],menu_count,menu_focus;
static const char *menu_label(int m){
    switch(m){case M_OPEN:return "Open";case M_OPEN_TAB:return "Open in new tab";case M_CUT:return "Cut";case M_COPY:return "Copy";case M_PASTE:return "Paste";
    case M_RENAME:return "Rename";case M_DELETE:return "Delete";case M_FOLDER:return "New folder";case M_SELECT_ALL:return "Select all";
    case M_HIDDEN:return show_hidden?"Hide hidden files":"Show hidden files";case M_REFRESH:return "Refresh";}return "";
}
static const char *menu_icon(int m){
    switch(m){case M_OPEN:return "folder";case M_OPEN_TAB:return "folder-plus";case M_CUT:return "scissors";case M_COPY:return "copy";case M_PASTE:return "clipboard-paste";
    case M_RENAME:return "pencil";case M_DELETE:return "trash";case M_FOLDER:return "folder-plus";case M_SELECT_ALL:return "check";case M_HIDDEN:return "eye-off";case M_REFRESH:return "refresh-cw";}return 0;
}
static const char *menu_keys(int m){
    switch(m){case M_OPEN_TAB:return "Ctrl+Enter";case M_CUT:return "Ctrl+X";case M_COPY:return "Ctrl+C";case M_PASTE:return "Ctrl+V";case M_RENAME:return "F2";
    case M_DELETE:return "Delete";case M_FOLDER:return "Ctrl+Shift+N";case M_SELECT_ALL:return "Ctrl+A";case M_HIDDEN:return "Ctrl+H";case M_REFRESH:return "F5";}return "";
}
static int menu_enabled(int m){
    int n=selected_count(0);
    if(m==M_PASTE||m==M_FOLDER)return folder_writable&&(m!=M_PASTE||clip_count);
    if(m==M_RENAME)return n<=1&&focus_index>=0&&folder_writable;
    if(m==M_DELETE||m==M_CUT)return folder_writable;
    return 1;
}
static void open_menu(int x,int y,int on_item){
    menu_count=0;
    if(on_item){
        int folder=focus_index>=0&&is_folder(&entries[order[focus_index]]);
        if(folder){menu_items[menu_count++]=M_OPEN;menu_items[menu_count++]=M_OPEN_TAB;}
        menu_items[menu_count++]=M_CUT;menu_items[menu_count++]=M_COPY;
        if(folder&&clip_count)menu_items[menu_count++]=M_PASTE;
        menu_items[menu_count++]=M_RENAME;menu_items[menu_count++]=M_DELETE;
    }else{
        menu_items[menu_count++]=M_FOLDER;menu_items[menu_count++]=M_PASTE;menu_items[menu_count++]=M_SELECT_ALL;
        menu_items[menu_count++]=M_HIDDEN;menu_items[menu_count++]=M_REFRESH;
    }
    menu_open=1;menu_x=x;menu_y=y;menu_focus=-1;print("HORIZON_MENU open\n");ui.dirty=1;
}

/* ---------- Opening. */
static void open_item(int k,int new_tab){
    if(k<0||k>=shown)return;
    const struct entry *e=&entries[order[k]];char path[1024];join(path,T()->path,name_of(order[k]));
    if(is_folder(e)){if(new_tab){open_tab(path,0);char l[64],*o=put(l,"Opened in a new tab: ");copy(o,name_of(order[k]),40);copy(status_note,l,sizeof(status_note));ui.dirty=1;}else go(path,1);return;}
    char line[1100],*o=put(line,"HORIZON_OPEN ");o=put(o,path);put(o,"\n");print(line);
    copy(status_note,"Horizon cannot open files with an app yet. Use the Editor or a terminal for now.",sizeof(status_note));ui.dirty=1;
}

/* ---------- Selection. */
static void select_only(int k){for(int i=0;i<shown;i++)entries[order[i]].selected=i==k;}
static void select_range(int a,int b,int add){
    if(a>b){int t=a;a=b;b=t;}
    for(int i=0;i<shown;i++){if(i>=a&&i<=b)entries[order[i]].selected=1;else if(!add)entries[order[i]].selected=0;}
}
static void announce_selection(void){
    unsigned long bytes;int n=selected_count(&bytes);
    char line[96],*o=put(line,"HORIZON_SELECTED ");o=num(o,(unsigned long)n);put(o,"\n");print(line);
}
static int columns_now=1,rows_visible=1;
static int item_top(int k);static int item_height(void);
static struct zui_rect content_rect;
static void reveal(int k){
    if(k<0)return;
    int top=item_top(k);zui_scroll_reveal(&content_scroll,top,item_height());
}
static void move_focus(int k,int modifiers){
    if(!shown)return;
    if(k<0){k=0;}
    if(k>=shown){k=shown-1;}
    if(modifiers&1){if(anchor<0)anchor=focus_index>=0?focus_index:k;focus_index=k;select_range(anchor,k,0);}
    else if(modifiers&2){focus_index=k;}
    else{focus_index=k;anchor=k;select_only(k);}
    reveal(k);announce_selection();ui.dirty=1;
}

/* ---------- Layout. */
enum { TAB_Y=4,TAB_H=40,TOOL_Y=48,TOOL_H=36,BODY_Y=94,STATUS_H=30,STRIP_H=58 };
static int sidebar_w(void){return ui.width<720?0:ui.width<900?172:204;}
static int info_w(void){return info_on&&ui.width>=860?(ui.width<1100?236:268):0;}
static int item_height(void){return T()->view==0?122:32;}
static int cell_w(void){return 132;}
static int item_top(int k){return T()->view==0?(k/columns_now)*item_height():28+k*item_height();}

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
    for(int i=0;i<tab_count;i++){tab_label(labels[i],&tabs[i]);items[i]=(struct zui_tab){labels[i],0,same(tabs[i].path,home)?"house":"folder"};}
    zui_tabs_draw(&ui,&strip,(struct zui_rect){8,TAB_Y,ui.width-16,TAB_H},items,tab_count,current,tab_count<MAX_TABS);
}

/* Breadcrumb segments: Home or Root first, then each folder. */
static void draw_toolbar(void){
    struct tab *t=T();int x=12,y=TOOL_Y;
    icon_button((struct zui_rect){x,y,36,TOOL_H},"arrow-left",H_BACK,t->hpos>0,0);x+=38;
    icon_button((struct zui_rect){x,y,36,TOOL_H},"arrow-right",H_FORWARD,t->hpos<t->hcount-1,0);x+=38;
    icon_button((struct zui_rect){x,y,36,TOOL_H},"arrow-up",H_UP,!same(t->path,"/"),0);x+=44;
    int right=ui.width-12;
    int info_x=right-36;icon_button((struct zui_rect){info_x,y,36,TOOL_H},"panel-right",H_INFO,ui.width>=860,info_on&&ui.width>=860);
    int filter_w=ui.width>=1000?240:ui.width>=760?180:0;
    int filter_x=info_x-8-filter_w;
    if(filter_w){
        struct zui_rect f={filter_x,y,filter_w,TOOL_H};field(f,filter_focus);
        icon("search",f.x+10,f.y+10,16,ZUI_ICON_TEXT);
        if(filter_focus)zui_input_draw(&ui,(struct zui_rect){f.x+30,f.y+1,f.w-32,f.h-2},&filter_input,1);
        else fit(f.x+34,f.y+(f.h-zui_body.height)/2,t->filter[0]?t->filter:"Filter this folder",f.w-44,&zui_body,t->filter[0]?ui.theme.text:ui.theme.secondary);
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
    struct tab *t=T();
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
    int devices=0;
    for(int i=0;i<place_count;i++){
        if(places[i].device&&!devices){devices=1;y+=10;text(x+12,y+4,"Devices",&zui_caption,ui.theme.secondary);y+=26;}
        int active=!places[i].device&&same(places[i].path,T()->path);
        int row_h=places[i].device&&places[i].total?52:34;
        struct zui_rect r={x,y,w,row_h};
        if(active){zui_round(&ui,(struct zui_rect){x,y,w,34},8,mix(ui.theme.subtle,ui.theme.accent,18));zui_round(&ui,(struct zui_rect){x+1,y+8,3,18},1,ui.theme.accent);}
        else if(hover==H_PLACE+i)zui_round(&ui,(struct zui_rect){x,y,w,34},8,mix(ui.theme.subtle,ui.theme.text,7));
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
static void draw_item_icons(int k,int x,int y){
    const struct entry *e=&entries[order[k]];const char *name=name_of(order[k]);
    struct zui_rect r={x+4,y+4,cell_w()-8,item_height()-8};
    if(e->selected){zui_round(&ui,r,10,mix(ui.theme.surface,ui.theme.accent,16));zui_round_outline(&ui,r,10,k==focus_index?ui.theme.accent:mix(ui.theme.surface,ui.theme.accent,50));}
    else if(k==focus_index&&ui.focus_visible)zui_round_outline(&ui,r,10,ui.theme.accent);
    else if(hover==H_ITEM+k)zui_round(&ui,r,10,mix(ui.theme.surface,ui.theme.text,6));
    int cut=clip_cut&&clip_count;
    icon(icon_of(e,name),x+(cell_w()-48)/2,y+14,48,is_folder(e)?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
    (void)cut;
    fit_center(x+cell_w()/2,y+70,name,cell_w()-20,e->selected?&zui_label:&zui_body,ui.theme.text);
    char c[48];item_caption(c,e);fit_center(x+cell_w()/2,y+92,c,cell_w()-20,&zui_caption,ui.theme.secondary);
    hit(r,H_ITEM+k);
}
static int col_size,col_time,col_owner,col_perm,col_end;
static void draw_item_details(int k,int x,int y,int w){
    const struct entry *e=&entries[order[k]];const char *name=name_of(order[k]);
    struct zui_rect r={x,y+1,w,item_height()-2};
    if(e->selected){zui_round(&ui,r,8,mix(ui.theme.surface,ui.theme.accent,16));if(k==focus_index&&ui.focus_visible)zui_round_outline(&ui,r,8,ui.theme.accent);}
    else if(k==focus_index&&ui.focus_visible)zui_round_outline(&ui,r,8,ui.theme.accent);
    else if(hover==H_ITEM+k)zui_round(&ui,r,8,mix(ui.theme.surface,ui.theme.text,6));
    int ty=y+(item_height()-zui_body.height)/2;
    icon(icon_of(e,name),x+10,y+8,16,is_folder(e)?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
    fit(x+36,ty,name,col_size-x-48,e->selected?&zui_label:&zui_body,ui.theme.text);
    char c[48];item_caption(c,e);text(col_size,ty,c,&zui_body,ui.theme.secondary);
    char d[32];date_text(d,e->mtime,e->zone,0);text(col_time,ty,d,&zui_body,ui.theme.secondary);
    if(col_owner){char o[32];id_text(o,e->uid,0);fit(col_owner,ty,o,(col_perm?col_perm:col_end)-col_owner-8,&zui_body,ui.theme.secondary);}
    if(col_perm){char p[16];permissions(p,e->mode);text(col_perm,ty,p,&zui_body,ui.theme.secondary);}
    hit(r,H_ITEM+k);
}
static void draw_content(struct zui_rect c){
    zui_round(&ui,c,12,ui.theme.surface);
    hit(c,H_CONTENT);
    struct tab *t=T();
    struct zui_rect inner={c.x+8,c.y+8,c.w-16,c.h-16};
    if(no_abi){text(inner.x+16,inner.y+16,"Horizon needs a newer Zenith.",&zui_label,ui.theme.text);
        zui_paragraph(&ui,(struct zui_rect){inner.x+16,inner.y+42,inner.w-32,60},"This kernel does not offer the file service Horizon uses. Update Zenith, then open Horizon again.",&zui_body,ui.theme.secondary,0);return;}
    if(list_error){
        text(inner.x+16,inner.y+16,list_error==-13?"You do not have permission to open this folder.":list_error==-2?"This folder does not exist.":list_error==-20?"This is not a folder.":"This folder cannot be read.",&zui_label,ui.theme.text);
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
            if(sorts[i]>=0)hit((struct zui_rect){xs[i]-6,inner.y,i==0?col_size-xs[i]-8:110,24},H_COLUMN+sorts[i]);
        }
        zui_fill(&ui,(struct zui_rect){x,inner.y+26,w,1},ui.theme.border);
    }
    struct zui_rect list=t->view==1?(struct zui_rect){inner.x,inner.y+28,inner.w,inner.h-28}:inner;
    content_rect=list;
    columns_now=t->view==0?(list.w/cell_w()>0?list.w/cell_w():1):1;
    rows_visible=list.h/item_height();if(rows_visible<1)rows_visible=1;
    int origin=zui_scroll_begin(&ui,&content_scroll,list)-(t->view==1?28:0);
    int left=list.x+(t->view==0?(list.w-columns_now*cell_w())/2:0);
    if(!shown){
        const char *empty=entry_count&&t->filter[0]?"No items match the filter.":entry_count?"Only hidden items are here. Ctrl+H shows them.":"This folder is empty.";
        text(list.x+16,origin+(t->view==1?28:0)+16,empty,&zui_body,ui.theme.secondary);
    }
    for(int k=0;k<shown;k++){
        int top=origin+item_top(k);
        if(top+item_height()<list.y||top>list.y+list.h)continue;
        if(t->view==0)draw_item_icons(k,left+(k%columns_now)*cell_w(),top);
        else draw_item_details(k,list.x,top,list.w);
    }
    int height=t->view==0?((shown+columns_now-1)/columns_now)*item_height()+8:shown*item_height()+8;
    zui_scroll_end(&ui,&content_scroll,height);
}
static void info_row(int x,int y,int w,const char *label,const char *value){
    text(x,y,label,&zui_caption,ui.theme.secondary);fit(x+w-measure(value,&zui_caption)<x+90?x+90:x+w-measure(value,&zui_caption),y,value,w-90,&zui_caption,ui.theme.text);
}
static void draw_info(struct zui_rect r){
    zui_round(&ui,r,12,ui.theme.surface);
    int x=r.x+18,w=r.w-36,y=r.y+22;
    unsigned long bytes;int n=selected_count(&bytes);
    const struct entry *e=0;const char *name=0;
    if(n==1)for(int k=0;k<shown;k++)if(entries[order[k]].selected){e=&entries[order[k]];name=name_of(order[k]);}
    if(n>1){
        icon("copy",r.x+(r.w-48)/2,y,48,ZUI_ICON_NORMAL);y+=62;
        char t[64],*o=num(t,(unsigned long)n);put(o," items selected");fit_center(r.x+r.w/2,y,t,w,&zui_label,ui.theme.text);y+=26;
        char s[48];o=size_text(s,bytes);put(o," in files");fit_center(r.x+r.w/2,y,s,w,&zui_caption,ui.theme.secondary);
        return;
    }
    char folder_name[80];
    if(!e){tab_label(folder_name,T());name=folder_name;}
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
    }else{
        char *o=num(v,(unsigned long)entry_count);put(o,entry_count==1?" item":" items");info_row(x,y,w,"Contains",v);y+=26;
        if(have_fs){size_text(v,fs.available);info_row(x,y,w,"Free",v);y+=26;size_text(v,fs.total);info_row(x,y,w,"Capacity",v);y+=26;
            info_row(x,y,w,"Filesystem",fs.type);y+=26;info_row(x,y,w,"Mounted at",fs.mount);y+=26;}
        info_row(x,y,w,"You may change it",folder_writable?"Yes":"No");y+=26;
    }
}
static void draw_strip(struct zui_rect r,struct job *j){
    zui_round(&ui,r,10,ui.theme.border);zui_round(&ui,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},9,ui.theme.surface);
    const struct zenith_job_status *s=&j->status;
    static const char *verbs[4][2]={{"",""},{"Copying","Copied"},{"Moving","Moved"},{"Deleting","Deleted"}};
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
    for(int k=0;k<shown;k++)if(is_folder(&entries[order[k]]))folders++;
    char t[200],*o=t;
    if(status_note[0])copy(t,status_note,sizeof(t));
    else{
        if(T()->filter[0]&&shown!=entry_count){o=num(o,(unsigned long)shown);o=put(o," of ");o=num(o,(unsigned long)entry_count);o=put(o," items match");}
        else{o=num(o,(unsigned long)shown);o=put(o,shown==1?" item":" items");}
        o=put(o," \xC2\xB7 ");o=num(o,(unsigned long)folders);o=put(o,folders==1?" folder":" folders");
        if(n){o=put(o," \xC2\xB7 ");o=num(o,(unsigned long)n);o=put(o," selected");if(bytes){o=put(o," (");o=size_text(o,bytes);o=put(o,")");}}
        if(truncated)put(o," \xC2\xB7 too many items: some are not shown");
    }
    int ty=y+(STATUS_H-zui_caption.height)/2;
    fit(16,ty,t,ui.width/2+120,&zui_caption,status_note[0]?warn():ui.theme.secondary);
    if(have_fs){char f[48];o=size_text(f,fs.available);put(o," free");text_right(ui.width-16,ty,f,&zui_caption,ui.theme.secondary);}
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
    for(int y=0;y<ui.height;y++)for(int x=0;x<ui.width;x++){unsigned int *p=&ui.pixels[y*ui.width+x];*p=mix(*p,0x000000,light()?30:45);}
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
    long clock=zui_motion_clock();if(clock>=0)zui_apply_motion(&ui,clock);
    ui.dirty=1;
}

/* ---------- Frame. */
static void draw(void){
    hit_count=0;ui.count=0;ui.focus=-1;ui.pressed=-1;
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});
    zui_fill(&ui,ui.clip,ui.theme.subtle);
    draw_tabs();
    draw_toolbar();
    struct job *j=strip_job();
    long now=clock_ms();
    if(j&&Z_JOB_FINAL(j->state)&&j->state==Z_JOB_DONE&&!j->status.errors&&now>=0&&now-j->finished_at>4000)j->shown=0;
    int strip_on=j&&j->shown;
    int bottom=ui.height-STATUS_H-(strip_on?STRIP_H+8:0);
    int sw=sidebar_w(),iw=info_w();
    if(sw)draw_sidebar(12,BODY_Y,sw,bottom-BODY_Y);
    struct zui_rect c={sw?sw+24:12,BODY_Y,ui.width-(sw?sw+24:12)-12-(iw?iw+12:0),bottom-BODY_Y-4};
    zui_clip(&ui,c);draw_content(c);zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});
    if(iw)draw_info((struct zui_rect){ui.width-12-iw,BODY_Y,iw,bottom-BODY_Y-4});
    if(strip_on){struct zui_rect s={c.x,bottom+4,ui.width-c.x-12,STRIP_H};draw_strip(s,j);}
    zui_fill(&ui,(struct zui_rect){0,ui.height-STATUS_H,ui.width,1},ui.theme.border);
    draw_status(ui.height-STATUS_H);
    if(menu_open)draw_menu();
    struct job *q=asking();
    if(q&&!asking_dismissed)draw_question(q);
    else if(prompt)draw_prompt();
    zui_draw_controls(&ui);
    if(confirm.open)zui_dialog_draw(&ui,&confirm);
    ui.dirty=0;
}

/* ---------- Actions. */
static void menu_action(int m){
    menu_open=0;ui.dirty=1;
    switch(m){
    case M_OPEN:open_item(focus_index,0);break;
    case M_OPEN_TAB:open_item(focus_index,1);break;
    case M_CUT:copy_selection(1);break;
    case M_COPY:copy_selection(0);break;
    case M_PASTE:{
        /* On a folder: paste into it. */
        if(focus_index>=0&&is_folder(&entries[order[focus_index]])&&selected_count(0)==1&&clip_count){
            char p[1024];join(p,T()->path,name_of(order[focus_index]));const char *list[CLIP];for(int i=0;i<clip_count;i++)list[i]=clip_paths[i];
            start_job(clip_cut?Z_JOB_MOVE:Z_JOB_COPY,p,clip_count,list);if(clip_cut)clip_count=0;
        }else paste();
        break;}
    case M_RENAME:ask_name(PROMPT_RENAME);break;
    case M_DELETE:ask_delete();break;
    case M_FOLDER:ask_name(PROMPT_FOLDER);break;
    case M_SELECT_ALL:for(int k=0;k<shown;k++)entries[order[k]].selected=1;announce_selection();break;
    case M_HIDDEN:show_hidden=!show_hidden;remember_focus();arrange();break;
    case M_REFRESH:remember_focus();load();find_places();break;
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
        int k=id-H_ITEM;long now=clock_ms();
        int twice=last_click_id==id&&now>=0&&now-last_click_time<450&&!(e->modifiers&3);
        last_click_id=id;last_click_time=now;
        if(twice){open_item(k,(e->modifiers&2)!=0);last_click_id=0;return;}
        if(e->modifiers&2){entries[order[k]].selected^=1;focus_index=k;anchor=k;}
        else if(e->modifiers&1){if(anchor<0)anchor=k;focus_index=k;select_range(anchor,k,0);}
        else{if(!entries[order[k]].selected)select_only(k);focus_index=k;anchor=k;}
        ui.focus_visible=0;announce_selection();ui.dirty=1;return;
    }
    if(id==H_CONTENT){
        if(!(e->modifiers&3)){for(int k=0;k<shown;k++)entries[order[k]].selected=0;announce_selection();}
        ui.dirty=1;return;
    }
}
static void pointer_up(const struct zenith_gui_event *e){
    int id=hit_at(e->x,e->y);
    if(id==H_BACK)back();else if(id==H_FORWARD)forward();else if(id==H_UP)up();
    else if(id==H_ICONS)set_view(0);
    else if(id==H_DETAILS)set_view(1);
    else if(id==H_INFO){info_on=!info_on;ui.dirty=1;}
    else if(id==H_FILTER){if(!filter_focus)focus_filter();}
    else if(id==H_LOCATION){if(!editing_location)start_location();}
    else if(id>=H_CRUMB&&id<H_CRUMB+64){char p[1024];crumb_path(p,id-H_CRUMB);go(p,1);}
    else if(id>=H_PLACE&&id<H_PLACE+PLACES){go(places[id-H_PLACE].path,1);}
    else if(id>=H_COLUMN&&id<H_COLUMN+4){struct tab *t=T();int s=id-H_COLUMN;if(t->sort==s)t->descending=!t->descending;else{t->sort=s;t->descending=s==SORT_TIME;}remember_focus();arrange();}
    else if(id==H_CANCEL_JOB){struct job *j=strip_job();if(j){zenith_job_cancel(j->id);print("HORIZON_CANCEL\n");}}
    else if(id==H_CLOSE_JOB){struct job *j=strip_job();if(j)j->shown=0;ui.dirty=1;}
}

/* Keys for the file view. Ctrl+letter arrives as the lowercase letter with modifier 2. */
static int key(const struct zenith_gui_event *e){
    int ctrl=(e->modifiers&6)==2,shift=e->modifiers&1,alt=(e->modifiers&4)!=0,plain=!(e->modifiers&6);
    int c=e->code>='A'&&e->code<='Z'?e->code+32:e->code;
    if(ctrl&&c=='t'){open_tab(T()->path,1);return 1;}
    if(ctrl&&c=='w'){close_tab(current);return 1;}
    if(e->extra==4&&(e->modifiers&~1)==2){switch_tab((current+(shift?tab_count-1:1))%tab_count);return 1;}
    if(ctrl&&c=='l'){start_location();return 1;}
    if(ctrl&&c=='f'){focus_filter();return 1;}
    if(ctrl&&c=='h'){menu_action(M_HIDDEN);return 1;}
    if(ctrl&&c=='a'){menu_action(M_SELECT_ALL);return 1;}
    if(ctrl&&c=='c'){copy_selection(0);return 1;}
    if(ctrl&&c=='x'){if(folder_writable)copy_selection(1);return 1;}
    if(ctrl&&c=='v'){if(folder_writable)paste();return 1;}
    if(ctrl&&c=='r'){menu_action(M_REFRESH);return 1;}
    if(ctrl&&c=='1'){set_view(0);return 1;}
    if(ctrl&&c=='2'){set_view(1);return 1;}
    if((e->modifiers&7)==3&&c=='n'){if(folder_writable)ask_name(PROMPT_FOLDER);return 1;}
    if(e->extra==Z_KEY_F2&&plain){if(folder_writable&&focus_index>=0)ask_name(PROMPT_RENAME);return 1;}
    if(e->extra==Z_KEY_F5&&plain){menu_action(M_REFRESH);return 1;}
    if(e->extra==Z_KEY_F11&&plain){info_on=!info_on;ui.dirty=1;return 1;}
    if(e->extra==23&&shift){open_menu(content_rect.x+40,content_rect.y+40,focus_index>=0&&selected_count(0));return 1;}
    if(e->extra==9&&plain){if(folder_writable)ask_delete();return 1;}
    if(alt&&e->extra==5){back();return 1;}
    if(alt&&e->extra==6){forward();return 1;}
    if(alt&&e->extra==7){up();return 1;}
    if(e->extra==3&&plain){back();return 1;}
    if(e->extra==2){if(focus_index>=0)open_item(focus_index,ctrl);return 1;}
    if(e->extra==1){
        if(T()->filter[0]){T()->filter[0]=0;remember_focus();arrange();return 1;}
        if(selected_count(0)){for(int k=0;k<shown;k++)entries[order[k]].selected=0;announce_selection();ui.dirty=1;return 1;}
        return 0;
    }
    if(e->code==' '&&ctrl&&focus_index>=0){entries[order[focus_index]].selected^=1;announce_selection();ui.dirty=1;return 1;}
    int step=0,horizontal=T()->view==0;
    if(e->extra==5&&horizontal)step=-1;else if(e->extra==6&&horizontal)step=1;
    else if(e->extra==7)step=horizontal?-columns_now:-1;else if(e->extra==8)step=horizontal?columns_now:1;
    else if(e->extra==ZUI_KEY_PAGE_UP)step=-(horizontal?columns_now*(rows_visible>1?rows_visible-1:1):(rows_visible>1?rows_visible-1:1));
    else if(e->extra==ZUI_KEY_PAGE_DOWN)step=horizontal?columns_now*(rows_visible>1?rows_visible-1:1):(rows_visible>1?rows_visible-1:1);
    if(step||e->extra==10||e->extra==11){
        ui.focus_visible=1;
        int k=e->extra==10?0:e->extra==11?shown-1:focus_index<0?0:focus_index+step;
        if(!step||(k>=0&&k<shown)||e->extra>=12)move_focus(k,e->modifiers&3);
        return 1;
    }
    /* Type-ahead: the next item starting with this character. */
    if(plain&&e->code>' '&&e->code<127&&shown){
        for(int i=1;i<=shown;i++){int k=((focus_index<0?-1:focus_index)+i)%shown;if(lower(name_of(order[k])[0])==lower((char)e->code)){ui.focus_visible=1;move_focus(k,0);break;}}
        return 1;
    }
    return 0;
}

/* ---------- Test entry points without a window: list a folder, run a transfer through the real ABI. */
static int list_test(const char *path){
    tab_count=1;current=0;copy(tabs[0].path,path,1024);load();
    if(list_error)return 1;
    for(int k=0;k<shown;k++){
        const struct entry *e=&entries[order[k]];char line[600],*o=put(line,"HORIZON_ENTRY ");o=put(o,name_of(order[k]));
        o=put(o,is_folder(e)?" folder items=":" file size=");
        if(is_folder(e)){if(e->items<0)o=put(o,"-");else o=num(o,(unsigned long)e->items);}else o=num(o,e->size);
        o=put(o," modified=");o=date_text(o,e->mtime,e->zone,1);o=put(o," owner=");o=id_text(o,e->uid,0);o=put(o," mode=");o=permissions(o,e->mode);put(o,"\n");print(line);
    }
    char line[200],*o=put(line,"HORIZON_FS ");o=put(o,fs.mount);o=put(o," ");o=put(o,fs.type);o=put(o," free=");o=num(o,fs.available);put(o,folder_writable?" writable\n":" read-only\n");print(line);
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
    read_ids("/etc/passwd",0);read_ids("/etc/group",1);
    for(char **e=envp;e&&*e;e++)if(starts(*e,"HOME=")&&(*e)[5]=='/')copy(home,*e+5,sizeof(home));
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
    handle=zenith_gui_open(640,480,"Horizon",7);
    if(handle<0){print("horizon: graphical session required\n");return 1;}
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);return 1;}
    ui.hover_supported=zenith_gui_input(handle,7)==0||zenith_gui_pointer_motion(handle)==0;
    open_tab(start,1);find_places();
    print(no_abi?"HORIZON_NO_FILES_ABI\n":"HORIZON_READY\n");
    long last=clock_ms();int polls=0,result=0;
    while(!done){
        long now=clock_ms();
        int busy=jobs[0].id&&!Z_JOB_FINAL(jobs[0].state);busy|=jobs[1].id&&!Z_JOB_FINAL(jobs[1].state);
        if((now>=0&&now-last>=(busy?150:1000))||(now<0&&++polls>=200)){
            last=now;polls=0;poll_jobs();
            struct job *j=strip_job();if(j&&j->shown&&j->state==Z_JOB_DONE&&!j->status.errors&&now-j->finished_at>4000)ui.dirty=1;
        }
        if(ui.dirty){
            draw();
            if(reveal_later){reveal_later=0;if(focus_index>=0){reveal(focus_index);draw();}}
            long r=zenith_gui_present(handle,ui.pixels,(unsigned long)ui.width*ui.height*4);
            if(r<0){result=zui_window_closed(handle)?0:1;break;}
        }
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_RESIZE){if(zui_apply_viewport(&ui,handle,640*480)<0){result=zui_window_closed(handle)?0:1;break;}continue;}
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
            if(e.extra==1||e.extra==2||e.extra==8){filter_focus=0;if(e.extra==1){T()->filter[0]=0;remember_focus();arrange();}ui.dirty=1;continue;}
            if(zui_input_event(&filter_input,&e)){remember_focus();arrange();}
            continue;
        }
        if(e.type==Z_GUI_SCROLL){if(zui_scroll_event(&ui,&content_scroll,&e))ui.dirty=1;continue;}
        if(e.type==Z_GUI_SECONDARY){
            int id=hit_at(e.x,e.y);
            if(id>=H_ITEM){int k=id-H_ITEM;if(!entries[order[k]].selected){select_only(k);anchor=k;}focus_index=k;announce_selection();open_menu(e.x,e.y,1);}
            else if(id==H_CONTENT){for(int k=0;k<shown;k++)entries[order[k]].selected=0;open_menu(e.x,e.y,0);}
            continue;
        }
        if(e.type==Z_GUI_DOWN||e.type==Z_GUI_UP||e.type==Z_GUI_DRAG){
            if(e.y<TAB_Y+TAB_H){int a=zui_tabs_event(&strip,&e);
                if(a==ZUI_TABS_ADD)open_tab(T()->path,1);else if(a>0)switch_tab(a-1);else if(a<0)close_tab(-a-1);
                continue;}
            if(zui_scroll_event(&ui,&content_scroll,&e)){ui.dirty=1;continue;}
            if(e.type==Z_GUI_DOWN)pointer_down(&e);
            else if(e.type==Z_GUI_UP)pointer_up(&e);
            continue;
        }
        if(e.type==Z_GUI_KEY){if(status_note[0]&&e.extra!=0){status_note[0]=0;ui.dirty=1;}key(&e);}
    }
    zenith_gui_close(handle);return result;
}
