/* Equinox: disks and partitions. Every disk is drawn twice, as it is and as the plan will leave it;
 * actions only add steps to the plan, and nothing is written until Apply. Apply runs the steps one
 * at a time through zenith-native-disk-abi 1.0 (the same rules as the disk command) and stops at the
 * first that fails. Changes need root; mounted disks and the running system disk are shown, never
 * changed in place (the root grows from the rescue system). */
#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
#include "../include/zenith_disks.h"

static unsigned int pixels[640*480];
static struct zui ui;
static long handle;
static int material;

/* ---------- Text helpers: no libc in native programs. */
static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){zenith_write(s,length(s));}
static char *put(char *o,const char *s){while(*s)*o++=*s++;*o=0;return o;}
static char *num(char *o,unsigned long v){char t[24];int n=0;do{t[n++]=(char)('0'+v%10);v/=10;}while(v);while(n)*o++=t[--n];*o=0;return o;}
static void copy(char *o,const char *s,unsigned long n){unsigned long i=0;for(;i+1<n&&s[i];i++)o[i]=s[i];o[i]=0;}
/* One decimal place: 1.8 GiB, 512 MiB, 2.0 TiB. */
static char *size_text(char *o,unsigned long sectors){
    const unsigned long mib=2048,gib=mib*1024,tib=gib*1024;
    unsigned long unit=sectors>=tib?tib:sectors>=gib?gib:mib;
    unsigned long tenths=(sectors*10+unit/2)/unit;
    o=num(o,tenths/10);
    if(unit!=mib&&tenths<1000){*o++='.';o=num(o,tenths%10);}
    return put(o,unit==tib?" TiB":unit==gib?" GiB":" MiB");
}
static void text(int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(&ui,x,y,s,length(s),f,c);}
static void text_right(int right,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(&ui,right-zui_measure(s,length(s),f),y,s,length(s),f,c);}
static int inside(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x<r.x+r.w&&y<r.y+r.h;}
static int light(void){unsigned int s=ui.theme.surface;return ((s>>16&255)+(s>>8&255)+(s&255))>384;}
static unsigned int warn(void){return light()?0x886010:0xE6C47A;}
static unsigned int bad(void){return light()?0xAE325D:0xF29BAE;}
static unsigned int good(void){return light()?0x277347:0x8BCBA4;}

/* ---------- The kernel's view. */
static struct zenith_disks disks __attribute__((aligned(16)));
static int have_disks,can_edit;
static void refresh(void){
    static unsigned long seen=0,token=0;static unsigned int state=99;
    have_disks=zenith_disks_read(&disks)==0&&disks.version==1&&disks.disk_count<=Z_DISKS;
    can_edit=have_disks&&(disks.flags&Z_DISKS_CAN_EDIT);
    if(have_disks&&(disks.revision!=seen||disks.job_token!=token||disks.job_state!=state)){seen=disks.revision;token=disks.job_token;state=disks.job_state;ui.dirty=1;}
}

/* ---------- A disk model the plan can change: partitions by start sector, sorted. */
#define MAX_PARTS 16
struct part { unsigned long start,sectors; unsigned int type,fs,role,planned; char name[16],label[16],mount[32]; };
struct model { char name[16]; unsigned long sectors; unsigned int flags,count; struct part parts[MAX_PARTS]; };
static const unsigned long ALIGN=2048,RESERVED=34;
static unsigned long last_usable(const struct model *m){return m->sectors-RESERVED-1;}
static void load(struct model *m,const struct zenith_disk *d){
    copy(m->name,d->name,16);m->sectors=d->sectors;m->flags=d->flags;m->count=d->part_count>MAX_PARTS?MAX_PARTS:d->part_count;
    for(unsigned int i=0;i<m->count;i++){
        const struct zenith_disk_part *p=&d->parts[i];struct part *q=&m->parts[i];
        q->start=p->start;q->sectors=p->sectors;q->type=p->type;q->fs=p->filesystem;q->role=p->role;q->planned=0;
        copy(q->name,p->name,16);copy(q->label,p->label,16);copy(q->mount,p->mount,32);
    }
}
static void sort(struct model *m){
    for(unsigned int i=1;i<m->count;i++)for(unsigned int j=i;j>0&&m->parts[j-1].start>m->parts[j].start;j--){struct part t=m->parts[j];m->parts[j]=m->parts[j-1];m->parts[j-1]=t;}
}
/* Free regions exactly as DiskLayout.Free: 1 MiB-aligned starts, whole-MiB lengths, at least 1 MiB. */
struct region { unsigned long start,sectors; };
static int free_regions(const struct model *m,struct region *out,int max){
    int n=0;unsigned long cursor=ALIGN,last=last_usable(m);
    for(unsigned int i=0;i<=m->count;i++){
        unsigned long end=i<m->count?(m->parts[i].start?m->parts[i].start-1:0):last;
        if(end>last)end=last;
        if(end>=cursor){unsigned long len=(end-cursor+1)/ALIGN*ALIGN;if(len>=ALIGN&&n<max)out[n++]=(struct region){cursor,len};}
        if(i<m->count){unsigned long next=(m->parts[i].start+m->parts[i].sectors+ALIGN-1)/ALIGN*ALIGN;if(next>cursor)cursor=next;}
    }
    return n;
}
static int place(const struct model *m,unsigned long sectors,struct region *out){
    struct region r[MAX_PARTS+1];int n=free_regions(m,r,MAX_PARTS+1),best=-1;
    for(int i=0;i<n;i++){
        if(sectors){if(r[i].sectors>=sectors){*out=(struct region){r[i].start,sectors};return 1;}}
        else if(best<0||r[i].sectors>r[best].sectors)best=i;
    }
    if(!sectors&&best>=0){*out=r[best];return 1;}
    return 0;
}
static unsigned long grow_limit(const struct model *m,int index){
    unsigned long end=last_usable(m);const struct part *p=&m->parts[index];
    for(unsigned int i=0;i<m->count;i++)if((int)i!=index&&m->parts[i].start>p->start+p->sectors-1&&m->parts[i].start-1<end)end=m->parts[i].start-1;
    if(end<p->start+p->sectors-1)return p->sectors;
    unsigned long len=(end-p->start+1)/ALIGN*ALIGN;
    return len>p->sectors?len:p->sectors;
}
static int find(const struct model *m,unsigned long start){for(unsigned int i=0;i<m->count;i++)if(m->parts[i].start==start)return (int)i;return -1;}

/* ---------- The plan: steps against one disk each, applied in order. */
#define MAX_STEPS 8
struct step { unsigned int action; char disk[16]; unsigned long start,sectors; unsigned int ext2; char label[16]; char text[112]; };
static struct step plan[MAX_STEPS];
static int steps;
/* Applies one step to a model; 0 when it cannot apply (the plan is then refused here, not in the kernel). */
static int simulate(struct model *m,const struct step *s){
    if(!same(m->name,s->disk))return 1;
    if(s->action==Z_DISK_INIT){m->count=0;return 1;}
    if(s->action==Z_DISK_CREATE){
        struct region r;if(m->count>=MAX_PARTS||!place(m,s->sectors,&r))return 0;
        struct part *p=&m->parts[m->count++];
        *p=(struct part){r.start,r.sectors,Z_PART_LINUX,s->ext2?Z_FS_EXT2:Z_FS_UNKNOWN,0,1,"new","","" };
        copy(p->label,s->label,16);sort(m);return 1;
    }
    int i=find(m,s->start);if(i<0)return 0;
    if(s->action==Z_DISK_DELETE){for(unsigned int j=(unsigned int)i;j+1<m->count;j++)m->parts[j]=m->parts[j+1];m->count--;return 1;}
    if(s->action==Z_DISK_GROW){unsigned long limit=grow_limit(m,i),want=s->sectors?s->sectors:limit;if(want<=m->parts[i].sectors||want>limit)return 0;m->parts[i].sectors=want;m->parts[i].planned=1;return 1;}
    return 0;
}
static int planned_model(int disk,struct model *m){
    load(m,&disks.disks[disk]);
    for(int i=0;i<steps;i++)if(!simulate(m,&plan[i]))return 0;
    return 1;
}
static int disk_steps(const char *name){int n=0;for(int i=0;i<steps;i++)if(same(plan[i].disk,name))n++;return n;}
/* The disk command a step corresponds to: what Plan shows under "the exact commands". */
static void command_text(char *o,const struct step *s,const struct model *before){
    if(s->action==Z_DISK_INIT){o=put(o,"disk init ");o=put(o,s->disk);put(o," --yes");return;}
    if(s->action==Z_DISK_CREATE){
        o=put(o,"disk create ");o=put(o,s->disk);o=put(o," ");
        if(s->sectors){o=num(o,s->sectors/2048);o=put(o,"M");}else o=put(o,"rest");
        o=put(o,s->ext2?" ext2":" none");
        if(s->label[0]){o=put(o," -L ");o=put(o,s->label);}
        put(o," --yes");return;
    }
    int i=find(before,s->start);const char *name=i>=0?before->parts[i].name:"?";
    o=put(o,s->action==Z_DISK_DELETE?"disk delete ":"disk grow ");o=put(o,name);
    if(s->action==Z_DISK_GROW){if(s->sectors){o=put(o," ");o=num(o,s->sectors/2048);o=put(o,"M");}else o=put(o," max");}
    put(o," --yes");
}

/* ---------- Navigation: disks, then Plan and History. */
static int page;            /* 0..disk_count-1: a disk; disk_count: Plan; disk_count+1: History */
static struct zui_list sidebar;
static char nav_labels[Z_DISKS][40],nav_subs[Z_DISKS][40],plan_label[24];
static int nav_rows(void *context,int i,struct zui_list_item *o){
    (void)context;int n=have_disks?(int)disks.disk_count:0;
    if(i<n){
        char *p=put(nav_labels[i],disks.disks[i].name);p=put(p,"  ");size_text(p,disks.disks[i].sectors);
        put(nav_subs[i],disks.disks[i].flags&Z_DISK_SYSTEM?"System disk":disks.disks[i].flags&Z_DISK_GPT?"GPT":"No partition table");
        *o=(struct zui_list_item){"hard-drive",nav_labels[i],nav_subs[i],0,1};return 1;
    }
    if(i==n){char *p=put(plan_label,"Plan");if(steps){p=put(p," (");p=num(p,(unsigned long)steps);put(p,")");}*o=(struct zui_list_item){"list",plan_label,0,0,1};return 1;}
    if(i==n+1){*o=(struct zui_list_item){"clock","History",0,0,1};return 1;}
    return 0;
}

/* ---------- State of the disk page. */
enum { MODE_DISK=0,MODE_CREATE=1,MODE_GROW=2 };
static int mode,selected=-1;          /* selected: partition index in the current (planned) model, or -1-region */
static struct zui_rect now_bar,after_bar,segment_rects[MAX_PARTS*2+2];
static int segment_ids[MAX_PARTS*2+2],segments;
static char status_line[160];
static struct zui_combo size_combo;
static char label_text[16];static struct zui_input label_input;static int label_focus;static struct zui_rect label_rect;
static struct zui_dialog confirm;static char confirm_title[64],confirm_message[200];
static int confirm_kind;      /* 1 delete, 2 new table, 3 apply */
#define HISTORY 16
static char history[HISTORY][176];static int history_count;
static void remember(const char *line){
    if(history_count==HISTORY){for(int i=1;i<HISTORY;i++)copy(history[i-1],history[i],176);history_count--;}
    copy(history[history_count++],line,176);
}
enum { ID_GROW=100,ID_DELETE=101,ID_CREATE=102,ID_TABLE=103,ID_ADD=104,ID_CANCEL=105,ID_APPLY=106,ID_UNDO=107,ID_CLEAR=108,ID_SIZE=109 };

static unsigned int segment_color(const struct part *p){
    if(p->type==Z_PART_EFI)return light()?0x8A6FD0:0xB09BEB;
    if(p->fs==Z_FS_EXT2)return ui.theme.accent;
    return light()?0x6E7A8C:0x8C96A8;
}
/* One bar: partitions and free space in proportion (each at least 18 px, so small ones stay visible). */
static void draw_bar(struct zui_rect r,const struct model *m,int record){
    zui_round(&ui,r,8,ui.theme.subtle);
    struct region fr[MAX_PARTS+1];int nf=free_regions(m,fr,MAX_PARTS+1);
    struct { unsigned long start,sectors; int id; } items[MAX_PARTS*2+2];int n=0;
    for(unsigned int i=0;i<m->count;i++)items[n++]=(typeof(items[0])){m->parts[i].start,m->parts[i].sectors,(int)i};
    for(int i=0;i<nf;i++)items[n++]=(typeof(items[0])){fr[i].start,fr[i].sectors,-1-i};
    for(int i=1;i<n;i++)for(int j=i;j>0&&items[j-1].start>items[j].start;j--){typeof(items[0]) t=items[j];items[j]=items[j-1];items[j-1]=t;}
    int minimum=18,available=r.w-n*minimum-(n>0?n-1:0)*2;if(available<0)available=0;
    unsigned long total=0;for(int i=0;i<n;i++)total+=items[i].sectors;
    int x=r.x;
    if(record)segments=0;
    for(int i=0;i<n;i++){
        int w=minimum+(total?(int)((unsigned long)available*items[i].sectors/total):0);
        if(i==n-1)w=r.x+r.w-x;
        struct zui_rect s={x,r.y,w,r.h};
        int sel=record&&items[i].id==selected;
        if(items[i].id>=0){
            const struct part *p=&m->parts[items[i].id];
            zui_round(&ui,s,8,segment_color(p));
            if(p->planned)zui_round_alpha(&ui,s,8,0xFFFFFF,50);
            char t[40];put(t,p->label[0]?p->label:p->name);
            if(zui_measure(t,length(t),&zui_caption)<w-12)text(s.x+8,s.y+r.h/2-8,t,&zui_caption,ui.theme.on_accent);
        }else if(w>60){char t[24];size_text(t,items[i].sectors);text(s.x+8,s.y+r.h/2-8,t,&zui_caption,ui.theme.secondary);}
        if(sel)zui_round_outline(&ui,(struct zui_rect){s.x-2,s.y-2,s.w+4,s.h+4},10,ui.theme.text);
        if(record&&segments<MAX_PARTS*2+2){segment_rects[segments]=s;segment_ids[segments]=items[i].id;segments++;}
        x+=w+2;
    }
}

static struct model current;          /* the disk with the plan applied: what actions act on */
static int current_ok;
static struct region current_free[MAX_PARTS+1];static int current_free_count;
static const char *filesystem_text(const struct part *p){
    if(p->type==Z_PART_EFI)return p->fs==Z_FS_FAT32?"EFI, FAT32":"EFI";
    return p->fs==Z_FS_EXT2?"ext2":p->fs==Z_FS_FAT32?"FAT32":p->planned&&p->fs==Z_FS_UNKNOWN?"unformatted":"unknown";
}
/* Why this disk cannot be changed in place, or 0. */
static const char *protection(const struct zenith_disk *d){
    if(!(d->flags&Z_DISK_GPT))return "This disk has no GPT partition table. Create one to use it with Equinox.";
    if(d->flags&Z_DISK_SYSTEM)return "This disk runs Zenith. Equinox shows it and may add partitions in free space; grow the root from the rescue entry of the boot menu.";
    if(d->flags&Z_DISK_MOUNTED)return "Partitions on this disk are mounted. Unmount them to change it.";
    return 0;
}
static int size_rows(void *context,int i,struct zui_list_item *o){
    (void)context;static char labels[6][24];
    if(selected>=0||-1-selected>=current_free_count)return 0;
    unsigned long room=current_free[-1-selected].sectors;
    static const unsigned int quarters[5]={4,3,2,1,0};
    if(i>4)return 0;
    unsigned long sectors=quarters[i]?room*quarters[i]/4/ALIGN*ALIGN:0;
    if(i>0&&sectors<ALIGN)return 0;
    char *p=labels[i];
    if(!quarters[i])p=put(p,"64 MiB");
    else{p=size_text(p,sectors);if(i==0)put(p,", all free space");}
    *o=(struct zui_list_item){0,labels[i],0,0,1};return 1;
}
static unsigned long chosen_size(void){
    unsigned long room=current_free[-1-selected].sectors;
    static const unsigned int quarters[5]={4,3,2,1,0};
    unsigned int q=quarters[size_combo.value<0||size_combo.value>4?0:size_combo.value];
    return q==4?0:q?room*q/4/ALIGN*ALIGN:64*2048;
}
static void add_step(unsigned int action,unsigned long start,unsigned long sectors,unsigned int ext2,const char *label,const char *description){
    if(steps>=MAX_STEPS){put(status_line,"The plan holds at most 8 steps. Apply it first.");return;}
    struct step *s=&plan[steps];
    *s=(struct step){action,"",start,sectors,ext2,"",""};copy(s->disk,disks.disks[page].name,16);copy(s->label,label,16);copy(s->text,description,112);
    struct model m;load(&m,&disks.disks[page]);int ok=1;
    for(int i=0;i<=steps&&ok;i++)ok=simulate(&m,&plan[i]);
    if(!ok){put(status_line,"That step does not fit the plan.");return;}
    steps++;status_line[0]=0;mode=MODE_DISK;selected=-1;
}

static void draw_disk(struct zui_rect c){
    const struct zenith_disk *d=&disks.disks[page];
    struct model before;load(&before,d);
    current_ok=planned_model(page,&current);
    current_free_count=free_regions(&current,current_free,MAX_PARTS+1);
    int x=c.x+20,w=c.w-40,y=c.y+20;
    char title[64],*o=put(title,d->name);o=put(o,"  ");size_text(o,d->sectors);
    zui_icon(&ui,"hard-drive",x,y+2,24,ZUI_ICON_NORMAL);
    text(x+34,y,title,&zui_heading,ui.theme.text);
    const char *why=protection(d);
    y+=44;
    text(x,y,"Now",&zui_label,ui.theme.secondary);
    now_bar=(struct zui_rect){x,y+22,w,34};
    draw_bar(now_bar,&before,disk_steps(d->name)==0);
    y+=66;
    if(disk_steps(d->name)){
        text(x,y,current_ok?"After the plan":"After the plan: a step no longer fits",&zui_label,current_ok?ui.theme.secondary:bad());
        after_bar=(struct zui_rect){x,y+22,w,34};
        draw_bar(after_bar,&current,1);
        y+=66;
    }else after_bar=(struct zui_rect){0,0,0,0};
    if(why){zui_paragraph(&ui,(struct zui_rect){x,y,w,40},why,&zui_caption,warn(),0);y+=44;}
    /* The selection: a partition (in the planned model) or a free region. */
    if(mode==MODE_DISK){
        int sel_part=selected>=0&&(unsigned int)selected<current.count,sel_free=selected<0&&-1-selected<current_free_count;
        int bx=x,by=y;
        if(sel_part){
            const struct part *p=&current.parts[selected];
            char line[120],*q=put(line,p->planned&&same(p->name,"new")?"New partition":p->name);q=put(q,"  ");q=size_text(q,p->sectors);q=put(q,"  ");q=put(q,filesystem_text(p));
            if(p->label[0]){q=put(q,"  \"");q=put(q,p->label);q=put(q,"\"");}
            if(p->mount[0]){q=put(q,"  mounted at ");q=put(q,p->mount);}
            if(p->role==Z_ROLE_ROOT)q=put(q,"  (running system)");
            text(x,by,line,&zui_label,ui.theme.text);by+=30;
            int editable=!why||(d->flags&Z_DISK_SYSTEM&&0);
            int growable=editable&&p->fs==Z_FS_EXT2&&grow_limit(&current,selected)>p->sectors;
            zui_add(&ui,ID_GROW,ZUI_BUTTON,(struct zui_rect){bx,by,120,34},"Grow",0,growable);
            zui_add(&ui,ID_DELETE,ZUI_BUTTON,(struct zui_rect){bx+132,by,120,34},"Delete",0,editable&&p->role==Z_ROLE_NONE);
            by+=46;
        }else if(sel_free){
            char line[80],*q=put(line,"Free space  ");size_text(q,current_free[-1-selected].sectors);
            text(x,by,line,&zui_label,ui.theme.text);by+=30;
            int creatable=(d->flags&Z_DISK_GPT)&&(!(d->flags&Z_DISK_MOUNTED)||(d->flags&Z_DISK_SYSTEM));
            zui_add(&ui,ID_CREATE,ZUI_BUTTON,(struct zui_rect){bx,by,180,34},"New partition",0,creatable);
            by+=46;
        }else{
            text(x,by,"Select a partition or free space in the bar.",&zui_body,ui.theme.secondary);by+=30;
        }
        zui_add(&ui,ID_TABLE,ZUI_BUTTON,(struct zui_rect){x,by,220,34},"New partition table",0,!(d->flags&(Z_DISK_SYSTEM|Z_DISK_MOUNTED)));
        by+=46;
        /* Partition table, as it is now. */
        if(by+40<c.y+c.h){
            int cols[5]={x,x+w*20/100,x+w*42/100,x+w*60/100,x+w*80/100};
            static const char *heads[5]={"Partition","Filesystem","Size","Mounted at","Label"};
            for(int i=0;i<5;i++)text(cols[i],by,heads[i],&zui_caption,ui.theme.secondary);
            by+=22;zui_fill(&ui,(struct zui_rect){x,by,w,1},ui.theme.border);by+=6;
            for(unsigned int i=0;i<before.count&&by+22<c.y+c.h;i++){
                const struct part *p=&before.parts[i];char sz[24];size_text(sz,p->sectors);
                text(cols[0],by,p->name,&zui_body,ui.theme.text);text(cols[1],by,filesystem_text(p),&zui_body,ui.theme.text);
                text(cols[2],by,sz,&zui_body,ui.theme.text);text(cols[3],by,p->mount[0]?p->mount:"-",&zui_body,ui.theme.secondary);
                text(cols[4],by,p->role==Z_ROLE_ROOT?"Zenith (root)":p->role==Z_ROLE_BOOT?"boot":p->label[0]?p->label:"-",&zui_body,ui.theme.secondary);
                by+=24;
            }
            if(!before.count)text(x,by,"No partitions.",&zui_body,ui.theme.secondary);
        }
    }else if(mode==MODE_CREATE){
        text(x,y,"New partition",&zui_label,ui.theme.text);y+=30;
        text(x,y+8,"Size",&zui_body,ui.theme.secondary);
        zui_combo_draw(&ui,&size_combo,(struct zui_rect){x+120,y,260,34},5,size_rows,0);y+=46;
        text(x,y+8,"Label",&zui_body,ui.theme.secondary);
        label_rect=(struct zui_rect){x+120,y,260,34};zui_input_draw(&ui,label_rect,&label_input,label_focus);y+=46;
        text(x,y,"Formatted ext2 with room to grow to 2 TiB (1 KiB blocks below 64 MiB).",&zui_caption,ui.theme.secondary);y+=28;
        zui_add(&ui,ID_ADD,ZUI_BUTTON,(struct zui_rect){x,y,140,34},"Add to plan",0,1);
        zui_add(&ui,ID_CANCEL,ZUI_BUTTON,(struct zui_rect){x+152,y,120,34},"Cancel",0,1);
    }else if(mode==MODE_GROW&&selected>=0&&(unsigned int)selected<current.count){
        const struct part *p=&current.parts[selected];unsigned long limit=grow_limit(&current,selected);
        char line[120],*q=put(line,"Grow ");q=put(q,p->name);q=put(q," from ");q=size_text(q,p->sectors);q=put(q," to ");size_text(q,limit);
        text(x,y,line,&zui_label,ui.theme.text);y+=30;
        zui_paragraph(&ui,(struct zui_rect){x,y,w,60},"It takes all the free space after it. The start stays fixed; the ext2 volume grows after the partition, then is checked.",&zui_body,ui.theme.secondary,0);
        y+=64;
        zui_add(&ui,ID_ADD,ZUI_BUTTON,(struct zui_rect){x,y,140,34},"Add to plan",0,1);
        zui_add(&ui,ID_CANCEL,ZUI_BUTTON,(struct zui_rect){x+152,y,120,34},"Cancel",0,1);
    }else mode=MODE_DISK;
    if(status_line[0])text(x,c.y+c.h-28,status_line,&zui_caption,warn());
}

/* ---------- Plan and History. */
static int applying=-1;           /* index of the step running, or -1 */
static unsigned long applying_token;
static void draw_plan(struct zui_rect c){
    int x=c.x+20,w=c.w-40,y=c.y+20;
    text(x,y,"Plan",&zui_heading,ui.theme.text);y+=40;
    if(!steps){text(x,y,"Nothing planned. Choose a disk, select a partition or free space and pick an action.",&zui_body,ui.theme.secondary);return;}
    text(x,y,applying>=0?"Applying. Leave Equinox open until it finishes.":"Nothing is written until Apply. The partition table is written before each filesystem change.",&zui_caption,ui.theme.secondary);y+=28;
    for(int i=0;i<steps&&y+48<c.y+c.h-60;i++){
        struct model before;int d=-1;
        for(unsigned int k=0;k<disks.disk_count;k++)if(same(disks.disks[k].name,plan[i].disk))d=(int)k;
        if(d<0){continue;}
        load(&before,&disks.disks[d]);for(int k=0;k<i;k++)simulate(&before,&plan[k]);
        char n[8],*o=num(n,(unsigned long)i+1);(void)o;
        zui_round(&ui,(struct zui_rect){x,y,26,26},13,i==applying?ui.theme.accent:ui.theme.raised);
        text(x+9-(i>=9?4:0),y+5,n,&zui_caption,i==applying?ui.theme.on_accent:ui.theme.text);
        text(x+38,y+2,plan[i].text,&zui_body,ui.theme.text);
        char cmd[112];command_text(cmd,&plan[i],&before);text(x+38,y+24,cmd,&zui_caption,ui.theme.secondary);
        y+=52;
    }
    int by=c.y+c.h-54;
    zui_add(&ui,ID_APPLY,ZUI_BUTTON,(struct zui_rect){x+w-140,by,140,34},"Apply plan",1,can_edit&&applying<0);
    zui_add(&ui,ID_UNDO,ZUI_BUTTON,(struct zui_rect){x,by,150,34},"Remove last",0,applying<0);
    zui_add(&ui,ID_CLEAR,ZUI_BUTTON,(struct zui_rect){x+162,by,120,34},"Clear",0,applying<0);
    if(!can_edit)text_right(x+w-152,by+9,"Sign in as root to apply changes.",&zui_caption,warn());
    else if(status_line[0])text_right(x+w-152,by+9,status_line,&zui_caption,warn());
}
static void draw_history(struct zui_rect c){
    int x=c.x+20,y=c.y+20;
    text(x,y,"History",&zui_heading,ui.theme.text);y+=40;
    if(!history_count){text(x,y,"Changes applied in this session appear here.",&zui_body,ui.theme.secondary);return;}
    for(int i=history_count-1;i>=0&&y+24<c.y+c.h;i--){
        unsigned int color=history[i][0]=='!'?bad():good();
        zui_paragraph(&ui,(struct zui_rect){x,y,c.w-40,40},history[i]+1,&zui_body,color,0);y+=44;
    }
}

/* ---------- Applying: one step at a time; the next waits for the previous to finish. */
static void start_step(void){
    while(applying>=0&&applying<steps){
        struct step *s=&plan[applying];
        struct zenith_disk_request r={1,s->action,disks.revision,"",s->start,s->sectors,s->ext2?Z_FS_EXT2:Z_FS_UNKNOWN,0,""};
        copy(r.disk,s->disk,16);copy(r.label,s->label,16);
        long token=zenith_disks_request(&r);
        if(token>0){applying_token=(unsigned long)token;return;}
        char line[176],*o=put(line,"!");o=put(o,s->text);o=put(o,": ");
        put(o,token==-13?"administrator permission required":token==-11?"the disks changed meanwhile; review the plan":token==-16?"another change is running":"refused");
        remember(line);copy(status_line,line+1,160);applying=-1;return;
    }
    applying=-1;
}
static void poll_step(void){
    if(applying<0||!have_disks||disks.job_token!=applying_token)return;
    if(disks.job_state==Z_DISK_JOB_RUNNING)return;
    char line[176],*o=put(line,disks.job_state==Z_DISK_JOB_DONE?" ":"!");o=put(o,plan[applying].text);o=put(o,": ");
    put(o,disks.job_message[0]?disks.job_message:disks.job_state==Z_DISK_JOB_DONE?"done":"failed");
    remember(line);
    if(disks.job_state!=Z_DISK_JOB_DONE){copy(status_line,line+1,160);applying=-1;ui.dirty=1;return;}
    /* Done: the step leaves the plan; the rest continues against the new revision. */
    for(int i=applying;i+1<steps;i++)plan[i]=plan[i+1];
    steps--;
    if(applying>=steps){applying=-1;status_line[0]=0;put(status_line,"Plan applied.");}
    else start_step();
    ui.dirty=1;
}

/* ---------- Appearance. */
static void update_appearance(void){
    struct zenith_appearance state={0};struct zenith_scoped_appearance scoped={0};
    int personal=zenith_scoped_appearance_read(1,&scoped)==0;
    if(personal)state=scoped.appearance;
    if((personal||!zenith_appearance_read(&state))&&state.active_mode<2&&state.active_accent<6){const unsigned int *p=state.palette[state.active_mode][state.active_accent];ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};}
    struct zenith_material_settings material_state={0};
    if(personal)ui.full_background=(scoped.materials.saved&5)==5;
    else if(!zenith_material_settings_read(&material_state))ui.full_background=(material_state.saved&5)==5;
    long clock=zui_motion_clock();if(clock>=0)zui_apply_motion(&ui,clock);
    ui.dirty=1;
}

/* ---------- Main loop. */
static void draw(void){
    struct zui_workspace w=zui_workspace_begin(&ui,ui.width<800?176:212);struct zui_rect c=w.content;
    int focus=ui.focus>=0&&ui.focus<ui.count?ui.controls[ui.focus].id:0;
    int pressed=ui.pressed>=0&&ui.pressed<ui.count?ui.controls[ui.pressed].id:0;
    ui.count=0;ui.pressed=-1;ui.focus=-1;
    text(24,24,"Equinox",&zui_label,ui.theme.text);
    int n=have_disks?(int)disks.disk_count:0;
    sidebar.selected=page;
    zui_list_draw(&ui,&sidebar,(struct zui_rect){w.sidebar.x,w.sidebar.y+44,w.sidebar.w,w.sidebar.h-44},n+2,nav_rows,0);
    zui_clip(&ui,c);
    if(!have_disks){text(c.x+20,c.y+20,"This system does not list disks.",&zui_label,ui.theme.text);
        text(c.x+20,c.y+44,"Equinox needs a newer Zenith kernel. Update Zenith, then reopen it.",&zui_body,ui.theme.secondary);}
    else if(page<n)draw_disk(c);
    else if(page==n)draw_plan(c);
    else draw_history(c);
    for(int i=0;i<ui.count;i++)if(ui.controls[i].enabled){if(ui.controls[i].id==focus)ui.focus=i;if(ui.controls[i].id==pressed)ui.pressed=i;}
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});zui_update_hover(&ui);zui_draw_controls(&ui);
    if(mode==MODE_CREATE&&page<n)zui_combo_popup(&ui,&size_combo,5,size_rows,0);
    if(confirm.open)zui_dialog_draw(&ui,&confirm);
    ui.dirty=0;
}
static void ask(int kind,const char *title,const char *message,const char *yes){
    confirm_kind=kind;copy(confirm_title,title,64);copy(confirm_message,message,200);
    confirm=(struct zui_dialog){1,200,confirm_title,confirm_message,{yes,"Cancel",0},2};ui.dirty=1;
}
static void action(int id){
    int n=have_disks?(int)disks.disk_count:0;
    if(page<n){
        const struct zenith_disk *d=&disks.disks[page];
        if(id==ID_CREATE){mode=MODE_CREATE;{struct zui_combo fresh={0};fresh.id=ID_SIZE;size_combo=fresh;}label_text[0]=0;zui_input_init(&label_input,label_text,sizeof(label_text));label_focus=0;}
        if(id==ID_GROW)mode=MODE_GROW;
        if(id==ID_CANCEL)mode=MODE_DISK;
        if(id==ID_DELETE&&selected>=0){
            char m[200],*o=put(m,"Delete ");o=put(o,current.parts[selected].name);put(o,"? Its data becomes unreachable once the plan is applied.");
            ask(1,"Add a deletion to the plan?",m,"Add to plan");
        }
        if(id==ID_TABLE){
            char m[200],*o=put(m,"A new, empty partition table on ");o=put(o,d->name);put(o," removes every partition on it once the plan is applied.");
            ask(2,"Add a new partition table to the plan?",m,"Add to plan");
        }
        if(id==ID_ADD&&mode==MODE_CREATE&&selected<0){
            unsigned long sectors=chosen_size();char t[112],*o=put(t,"Create ");
            if(sectors)o=size_text(o,sectors);else o=put(o,"a partition over the free space");
            o=put(o," on ");o=put(o,d->name);o=put(o,", ext2");if(label_text[0]){o=put(o," \"");o=put(o,label_text);put(o,"\"");}
            /* Placement follows the kernel: the first free region that fits, or the largest. The
             * region the person selected is the largest when they chose all of it. */
            add_step(Z_DISK_CREATE,0,sectors,1,label_text,t);
        }
        if(id==ID_ADD&&mode==MODE_GROW&&selected>=0){
            const struct part *p=&current.parts[selected];char t[112],*o=put(t,"Grow ");o=put(o,p->name);o=put(o," to ");size_text(o,grow_limit(&current,selected));
            add_step(Z_DISK_GROW,p->start,0,0,"",t);
        }
    }else if(page==n){
        if(id==ID_UNDO&&steps)steps--;
        if(id==ID_CLEAR)steps=0;
        if(id==ID_APPLY&&steps){
            char m[200],*o=put(m,"Equinox writes ");o=num(o,(unsigned long)steps);put(o,steps==1?" step now. A failed step stops the plan; earlier steps stay.":" steps now, in order. A failed step stops the plan; earlier steps stay.");
            ask(3,"Apply the plan?",m,"Apply");
        }
    }
    ui.dirty=1;
}
static void confirmed(void){
    if(confirm_kind==1&&selected>=0){
        const struct part *p=&current.parts[selected];char t[112],*o=put(t,"Delete ");put(o,p->name);
        add_step(Z_DISK_DELETE,p->start,0,0,"",t);
    }
    if(confirm_kind==2){char t[112],*o=put(t,"New partition table on ");put(o,disks.disks[page].name);add_step(Z_DISK_INIT,0,0,0,"",t);}
    if(confirm_kind==3){status_line[0]=0;applying=0;start_step();}
}
static void pointer(const struct zenith_gui_event *e){
    if(e->type!=Z_GUI_DOWN)return;
    int n=have_disks?(int)disks.disk_count:0;
    if(page>=n)return;
    if(mode==MODE_CREATE){int was=label_focus;label_focus=inside(label_rect,e->x,e->y);if(was!=label_focus)ui.dirty=1;return;}
    for(int i=0;i<segments;i++)if(inside(segment_rects[i],e->x,e->y)){selected=segment_ids[i];status_line[0]=0;mode=MODE_DISK;ui.dirty=1;return;}
}
/* ---------- Test entry points (no window): the snapshot, and a fixed plan through the real ABI. */
static int probe(void){
    refresh();
    if(!have_disks){print("EQUINOX_NO_DISK_ABI\n");return 1;}
    char line[256],*o=put(line,"EQUINOX_DISKS count=");o=num(o,disks.disk_count);o=put(o," root=");put(o,can_edit?"yes\n":"no\n");print(line);
    for(unsigned int d=0;d<disks.disk_count;d++){
        const struct zenith_disk *k=&disks.disks[d];
        o=put(line,"EQUINOX_DISK ");o=put(o,k->name);o=put(o," ");o=size_text(o,k->sectors);o=put(o," parts=");o=num(o,k->part_count);
        o=put(o," free=");o=num(o,k->free_count);put(o,k->flags&Z_DISK_SYSTEM?" system\n":"\n");print(line);
        for(unsigned int i=0;i<k->part_count&&i<Z_DISK_PARTS;i++){
            const struct zenith_disk_part *p=&k->parts[i];
            o=put(line,"EQUINOX_PART ");o=put(o,p->name);o=put(o," ");o=size_text(o,p->sectors);o=put(o," fs=");o=num(o,p->filesystem);
            o=put(o," label=");o=put(o,p->label);o=put(o," role=");o=num(o,p->role);put(o,"\n");print(line);
        }
    }
    return 0;
}
static int wait_job(long token,const char *what){
    char line[256],*o;
    if(token<0){o=put(line,"EQUINOX_STEP ");o=put(o,what);o=put(o," refused=");num(o,(unsigned long)-token);print(line);print("\n");return (int)-token;}
    for(long polls=0;polls<6000;polls++){
        refresh();
        if(have_disks&&disks.job_token==(unsigned long)token&&disks.job_state!=Z_DISK_JOB_RUNNING){
            o=put(line,"EQUINOX_STEP ");o=put(o,what);o=put(o,disks.job_state==Z_DISK_JOB_DONE?" done: ":" failed: ");o=put(o,disks.job_message);put(o,"\n");print(line);
            return disks.job_state==Z_DISK_JOB_DONE?0:1;
        }
        /* No sleep call in this ABI: pause on the CPU (tens of ms) between snapshot reads, which probe disks. */
        for(volatile unsigned long i=0;i<20000000;i++){}
    }
    print("EQUINOX_STEP timeout\n");return 1;
}
static int request(unsigned int action,const char *disk,unsigned long start,unsigned long sectors,int ext2,const char *label,const char *what){
    refresh();
    struct zenith_disk_request r={1,action,disks.revision,"",start,sectors,ext2?Z_FS_EXT2:Z_FS_UNKNOWN,0,""};
    copy(r.disk,disk,16);copy(r.label,label,16);
    return wait_job(zenith_disks_request(&r),what);
}
static int apply_test(const char *disk){
    refresh();if(!have_disks)return 1;
    if(request(Z_DISK_INIT,disk,0,0,0,"","init"))return 1;
    if(request(Z_DISK_CREATE,disk,0,512*2048,1,"EQX","create-a"))return 1;
    if(request(Z_DISK_CREATE,disk,0,0,0,"","create-b"))return 1;
    refresh();
    int d=-1;for(unsigned int k=0;k<disks.disk_count;k++)if(same(disks.disks[k].name,disk))d=(int)k;
    if(d<0||disks.disks[d].part_count!=2)return 1;
    unsigned long first=disks.disks[d].parts[0].start,second=disks.disks[d].parts[1].start;
    if(request(Z_DISK_DELETE,disk,second,0,0,"","delete-b"))return 1;
    if(request(Z_DISK_GROW,disk,first,0,0,"","grow-a"))return 1;
    print("EQUINOX_APPLIED\n");return 0;
}
int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("equinox " SUNBEAM_VERSION "\n");return 0;}
    if(argc==2&&same(argv[1],"--probe"))return probe();
    if(argc==3&&same(argv[1],"--apply-test"))return apply_test(argv[2]);
    if(argc!=1){print("usage: native run equinox\n");return 2;}
    zui_icons_use_packages();
    zui_init(&ui,pixels,640,480);update_appearance();
    struct zenith_material_caps caps={0};struct zenith_material_settings settings={0};
    material=!zenith_material_settings_read(&settings)&&!zenith_gui_material_caps(&caps)&&caps.version==2;
    handle=material?zenith_gui_open_material(640,480,"Equinox",7):zenith_gui_open(640,480,"Equinox",7);
    if(handle<0){print("equinox: graphical session required\n");return 1;}
    ui.foreground=material;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);return 1;}
    ui.hover_supported=zenith_gui_pointer_motion(handle)==0;
    sidebar.base_id=10;
    refresh();
    print(have_disks?"EQUINOX_READY\n":"EQUINOX_NO_DISK_ABI\n");
    long last=zui_motion_clock();int polls=0,result=0,done=0;
    while(!done){
        long now=zui_motion_clock();
        int interval=applying>=0?150:1000;
        if((now>=0&&now-last>=interval)||(now<0&&++polls>=200)){last=now;polls=0;refresh();poll_step();}
        if(ui.dirty){
            draw();
            long r=material?zenith_gui_present_material(handle,ui.pixels,(unsigned long)ui.width*ui.height*4,ui.materials,ui.material_count):zenith_gui_present(handle,ui.pixels,(unsigned long)ui.width*ui.height*4);
            if(r<0){result=zui_window_closed(handle)?0:1;break;}
        }
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_RESIZE){if(zui_apply_viewport(&ui,handle,640*480)<0){result=zui_window_closed(handle)?0:1;break;}continue;}
        if(e.type==Z_GUI_CLOSE||e.type==Z_GUI_CLOSE_INTENT){done=1;continue;}
        if(e.type==Z_GUI_RESET){update_appearance();continue;}
        if(e.type==Z_GUI_FOCUS&&e.code)print("EQUINOX_FOCUSED\n");
        if(confirm.open){int choice=zui_dialog_event(&ui,&confirm,&e);if(choice==1)confirmed();if(choice)ui.dirty=1;continue;}
        int n=have_disks?(int)disks.disk_count:0;
        if(mode==MODE_CREATE&&page<n){
            int chosen=zui_combo_event(&ui,&size_combo,&e,size_rows,0);
            if(chosen){size_combo.value=chosen-1;ui.dirty=1;continue;}
            if(size_combo.open){ui.dirty=1;continue;}
            if(e.type==Z_GUI_KEY&&label_focus&&e.extra!=1&&zui_input_event(&label_input,&e)){ui.dirty=1;continue;}
        }
        if(e.type==Z_GUI_KEY&&e.extra==1&&ui.window_focused){if(mode!=MODE_DISK){mode=MODE_DISK;ui.dirty=1;continue;}done=1;continue;}
        if(e.type==Z_GUI_DOWN||e.type==Z_GUI_UP)pointer(&e);
        int list=zui_list_event(&ui,&sidebar,&e,nav_rows,0);
        if(list>0&&list-1!=page){page=list-1;mode=MODE_DISK;selected=-1;status_line[0]=0;ui.dirty=1;continue;}
        int id=zui_event(&ui,&e);
        if(id>=10&&id<10+n+2&&id-10!=page){page=id-10;mode=MODE_DISK;selected=-1;status_line[0]=0;ui.dirty=1;continue;}
        if(id)action(id);
    }
    zenith_gui_close(handle);return result;
}
