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
struct part { unsigned long start,sectors; unsigned int type,fs,role,planned; char name[16],label[16],mount[32]; int source; };   /* source: index in the snapshot, -1 when planned */
struct model { char name[16]; unsigned long sectors; unsigned int flags,count; struct part parts[MAX_PARTS]; };
static const unsigned long ALIGN=2048,RESERVED=34;
static unsigned long last_usable(const struct model *m){return m->sectors-RESERVED-1;}
static void load(struct model *m,const struct zenith_disk *d){
    copy(m->name,d->name,16);m->sectors=d->sectors;m->flags=d->flags;m->count=d->part_count>MAX_PARTS?MAX_PARTS:d->part_count;
    for(unsigned int i=0;i<m->count;i++){
        const struct zenith_disk_part *p=&d->parts[i];struct part *q=&m->parts[i];
        q->start=p->start;q->sectors=p->sectors;q->type=p->type;q->fs=p->filesystem;q->role=p->role;q->planned=0;
        copy(q->name,p->name,16);copy(q->label,p->label,16);copy(q->mount,p->mount,32);q->source=(int)i;
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
        *p=(struct part){r.start,r.sectors,Z_PART_LINUX,s->ext2?Z_FS_EXT2:Z_FS_UNKNOWN,0,1,"new","","",-1};
        p->source=-1;copy(p->label,s->label,16);sort(m);return 1;
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

/* ---------- Layout (docs/design/equinox-concept.svg): sidebar with Disks and Changes; the disk page
 * with Now/After bars, the partition table, three status cards and a notice; the plan as a panel
 * on the right when the window is wide enough, otherwise as its own page. */
static int page;            /* 0..disk_count-1: a disk; disk_count: Plan; disk_count+1: History */
static int wide;
enum { MODE_DISK=0,MODE_CREATE=1,MODE_GROW=2 };
#define NONE (-1000)
static int mode,selected=NONE;        /* selected: partition index in the current (planned) model, -1-region, or NONE */
static struct zui_rect segment_rects[MAX_PARTS*2+2];static int segment_ids[MAX_PARTS*2+2],segments;
static struct zui_rect row_rects[MAX_PARTS];static int row_ids[MAX_PARTS],rows;
static struct zui_rect nav_rects[Z_DISKS+2];static int nav_count,trailing[Z_DISKS+2];
static struct zui_scroll center_scroll,plan_scroll;
static int show_commands;
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
enum { ID_GROW=100,ID_DELETE=101,ID_CREATE=102,ID_TABLE=103,ID_ADD=104,ID_CANCEL=105,ID_APPLY=106,ID_UNDO=107,ID_CLEAR=108,ID_SIZE=109,ID_COMMANDS=110 };
static int applying=-1;           /* index of the step running, or -1 */
static unsigned long applying_token;

/* ---------- Colours and small drawing helpers. */
static unsigned int mix(unsigned int a,unsigned int b,int percent){
    unsigned int r=0;
    for(int shift=0;shift<24;shift+=8){unsigned int x=a>>shift&255,y=b>>shift&255;r|=((x*(unsigned int)(100-percent)+y*(unsigned int)percent)/100)<<shift;}
    return r;
}
static unsigned int purple(void){return light()?0x7C5CD6:0xB49CF5;}
static unsigned int efi_color(void){return light()?0x4F6FCF:0x93AAF0;}
static int is_new(const struct part *p){return p->planned&&p->source<0;}
static unsigned int part_color(const struct part *p){
    if(is_new(p))return purple();
    if(p->type==Z_PART_EFI)return efi_color();
    if(p->fs==Z_FS_EXT2)return ui.theme.accent;
    return light()?0x6E7A8C:0x8C96A8;
}
static struct zui_rect meet(struct zui_rect a,struct zui_rect b){
    int x0=a.x>b.x?a.x:b.x,y0=a.y>b.y?a.y:b.y,x1=a.x+a.w<b.x+b.w?a.x+a.w:b.x+b.w,y1=a.y+a.h<b.y+b.h?a.y+a.h:b.y+b.h;
    return (struct zui_rect){x0,y0,x1>x0?x1-x0:0,y1>y0?y1-y0:0};
}
/* Free space: diagonal hatching inside a rounded, faintly filled area. */
static void hatch(struct zui_rect r,unsigned int color){
    zui_round(&ui,r,8,mix(ui.theme.surface,ui.theme.subtle,60));
    struct zui_rect saved=ui.clip;ui.clip=meet(saved,(struct zui_rect){r.x+2,r.y+2,r.w-4,r.h-4});
    for(int k=-r.h;k<r.w;k+=9)for(int t=0;t<r.h;t++)zui_fill(&ui,(struct zui_rect){r.x+k+r.h-t,r.y+t,1,1},color);
    ui.clip=saved;
}
/* A planned partition: a dashed outline. */
static void dashed(struct zui_rect r,unsigned int color){
    for(int x=r.x+8;x<r.x+r.w-8;x+=9){int w=x+5>r.x+r.w-8?r.x+r.w-8-x:5;zui_fill(&ui,(struct zui_rect){x,r.y,w,1},color);zui_fill(&ui,(struct zui_rect){x,r.y+r.h-1,w,1},color);}
    for(int y=r.y+8;y<r.y+r.h-8;y+=9){int h=y+5>r.y+r.h-8?r.y+r.h-8-y:5;zui_fill(&ui,(struct zui_rect){r.x,y,1,h},color);zui_fill(&ui,(struct zui_rect){r.x+r.w-1,y,1,h},color);}
}
static void dot(int x,int y,unsigned int color){zui_round(&ui,(struct zui_rect){x,y,8,8},4,color);}
static void pill(int x,int y,const char *s,unsigned int color){
    int w=zui_measure(s,length(s),&zui_caption)+14;
    zui_round(&ui,(struct zui_rect){x,y,w,20},10,mix(ui.theme.surface,color,22));
    text(x+7,y+2,s,&zui_caption,color);
}
/* Text cut to a width with an ellipsis. */
static void fit(int x,int y,const char *s,int width,const struct zui_font *f,unsigned int c){
    char t[96];copy(t,s,sizeof(t));unsigned long n=length(t);
    if(zui_measure(t,n,f)<=width){text(x,y,t,f,c);return;}
    while(n>0&&(zui_measure(t,n,f)+zui_measure("\xE2\x80\xA6",3,f)>width||((unsigned char)t[n]&0xC0)==0x80))n--;
    t[n]=0;put(t+n,"\xE2\x80\xA6");text(x,y,t,f,c);
}

/* ---------- What the kernel knows beyond the 1.0 fields (extras, kernel 0.0.54 and later). */
static int extras(void){return have_disks&&disks.extra.version>=1;}
static unsigned long used_bytes(const struct part *p){
    if(!extras()||p->source<0||p->source>=Z_DISK_PARTS||page>=(int)disks.disk_count)return 0;
    return disks.extra.used[page][p->source];
}
static unsigned int fs_state(const struct part *p){
    if(!extras()||p->source<0||p->source>=Z_DISK_PARTS||page>=(int)disks.disk_count)return Z_FS_STATE_UNKNOWN;
    return disks.extra.state[page][p->source];
}
static char *bytes_text(char *o,unsigned long bytes){return size_text(o,(bytes+511)/512);}

static struct model current;          /* the disk with the plan applied: what actions act on */
static int current_ok;
static struct region current_free[MAX_PARTS+1];static int current_free_count;
static const char *filesystem_text(const struct part *p){
    if(p->type==Z_PART_EFI)return p->fs==Z_FS_FAT32?"FAT32":"EFI";
    return p->fs==Z_FS_EXT2?"ext2":p->fs==Z_FS_FAT32?"FAT32":is_new(p)?"unformatted":"unknown";
}
static const char *part_title(const struct part *p){
    if(p->role==Z_ROLE_ROOT)return "Zenith";
    if(p->type==Z_PART_EFI)return "EFI system";
    if(p->label[0])return p->label;
    return is_new(p)?"New":p->name;
}
static const char *disk_title(const struct zenith_disk *d,char *buffer){
    if(d->flags&Z_DISK_SYSTEM)return "System disk";
    put(put(buffer,"Disk "),d->name);return buffer;
}
/* Why this disk cannot be changed in place, or 0. */
static const char *protection(const struct zenith_disk *d){
    if(!(d->flags&Z_DISK_GPT))return "This disk has no GPT partition table. Create one to use it with Equinox.";
    if(d->flags&Z_DISK_SYSTEM)return "Zenith runs from it. Equinox shows it and may add partitions in free space, but changes the running system and its EFI partition only from the rescue entry of the boot menu.";
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
    steps++;status_line[0]=0;mode=MODE_DISK;selected=NONE;
}

/* ---------- Sidebar: Disks (name and size), then Changes (Plan with its step count, History). */
static void draw_sidebar(struct zui_rect s){
    int x=s.x+4,w=s.w-8,y=s.y+14,n=have_disks?(int)disks.disk_count:0;
    nav_count=0;
    text(x+12,y,"Disks",&zui_caption,ui.theme.secondary);y+=24;
    static char names[Z_DISKS][24],sizes[Z_DISKS][24];
    for(int i=0;i<n;i++){
        const struct zenith_disk *d=&disks.disks[i];
        const char *title=disk_title(d,names[i]);
        size_text(sizes[i],d->sectors);
        trailing[nav_count]=44+zui_measure(title,length(title),&zui_label)+12+zui_measure(sizes[i],length(sizes[i]),&zui_caption)+10<=w;
        nav_rects[nav_count++]=(struct zui_rect){x,y,w,40};
        if(zui_add(&ui,10+i,ZUI_NAVIGATION,(struct zui_rect){x,y,w,40},title,page==i,1)){
            ui.controls[ui.count-1].icon="hard-drive";ui.controls[ui.count-1].no_chevron=1;
            if(!trailing[nav_count-1])ui.controls[ui.count-1].detail=sizes[i];
        }
        y+=44;
    }
    if(!n){text(x+12,y,"No disks",&zui_body,ui.theme.secondary);y+=30;}
    y+=10;text(x+12,y,"Changes",&zui_caption,ui.theme.secondary);y+=24;
    trailing[nav_count]=1;nav_rects[nav_count++]=(struct zui_rect){x,y,w,40};
    if(zui_add(&ui,10+n,ZUI_NAVIGATION,(struct zui_rect){x,y,w,40},"Plan",page==n,1)){ui.controls[ui.count-1].icon="list";ui.controls[ui.count-1].no_chevron=1;}
    y+=44;
    trailing[nav_count]=1;nav_rects[nav_count++]=(struct zui_rect){x,y,w,40};
    if(zui_add(&ui,10+n+1,ZUI_NAVIGATION,(struct zui_rect){x,y,w,40},"History",page==n+1,1)){ui.controls[ui.count-1].icon="clock";ui.controls[ui.count-1].no_chevron=1;}
}
/* Trailing details (disk sizes, the plan's step count), drawn over the navigation rows. */
static void draw_sidebar_details(void){
    int n=have_disks?(int)disks.disk_count:0;
    for(int i=0;i<nav_count;i++){
        struct zui_rect r=nav_rects[i];char t[24];t[0]=0;
        if(!trailing[i])continue;
        if(i<n)size_text(t,disks.disks[i].sectors);
        else if(i==n&&steps)num(t,(unsigned long)steps);
        if(t[0])text_right(r.x+r.w-12,r.y+(r.h-zui_caption.height)/2,t,&zui_caption,ui.theme.secondary);
    }
}

/* ---------- The bars: partitions and free space in proportion (small ones stay visible). */
static void draw_bar(struct zui_rect r,const struct model *m,int record){
    struct region fr[MAX_PARTS+1];int nf=free_regions(m,fr,MAX_PARTS+1);
    struct { unsigned long start,sectors; int id; } items[MAX_PARTS*2+2];int n=0;
    for(unsigned int i=0;i<m->count;i++)items[n++]=(typeof(items[0])){m->parts[i].start,m->parts[i].sectors,(int)i};
    for(int i=0;i<nf;i++)items[n++]=(typeof(items[0])){fr[i].start,fr[i].sectors,-1-i};
    for(int i=1;i<n;i++)for(int j=i;j>0&&items[j-1].start>items[j].start;j--){typeof(items[0]) t=items[j];items[j]=items[j-1];items[j-1]=t;}
    if(!n){hatch(r,mix(ui.theme.surface,ui.theme.secondary,30));text(r.x+12,r.y+r.h/2-8,"No partition table",&zui_caption,ui.theme.secondary);return;}
    /* EFI partitions are a narrow tile; the rest share the width by size, at least 40 px. */
    int gap=4,fixed=0,flex=0;unsigned long total=0;
    for(int i=0;i<n;i++){if(items[i].id>=0&&m->parts[items[i].id].type==Z_PART_EFI)fixed+=34;else{flex++;total+=items[i].sectors;}}
    int available=r.w-fixed-(n-1)*gap-flex*40;if(available<0)available=0;
    if(record)segments=0;
    int x=r.x;
    for(int i=0;i<n;i++){
        const struct part *p=items[i].id>=0?&m->parts[items[i].id]:0;
        int w=p&&p->type==Z_PART_EFI?34:40+(total?(int)((unsigned long)available*items[i].sectors/total):0);
        if(i==n-1)w=r.x+r.w-x;
        struct zui_rect s={x,r.y,w,r.h};
        if(p&&p->type==Z_PART_EFI){
            zui_round(&ui,s,8,mix(ui.theme.surface,efi_color(),30));zui_round_outline(&ui,s,8,mix(ui.theme.surface,efi_color(),70));
            zui_icon(&ui,"shield",s.x+(s.w-16)/2,s.y+(s.h-16)/2,16,ZUI_ICON_TEXT);
        }else if(p){
            unsigned int color=part_color(p);
            zui_round(&ui,s,8,mix(ui.theme.surface,color,is_new(p)?14:22));
            if(is_new(p))dashed(s,color);else zui_round_outline(&ui,s,8,mix(ui.theme.surface,color,p->planned?100:70));
            char line[64],*o=put(line,part_title(p));o=put(o," \xC2\xB7 ");put(o,filesystem_text(p));
            char sub[64];o=size_text(sub,p->sectors);
            unsigned long used=used_bytes(p);
            if(is_new(p))put(o,", new");else if(used){o=put(o,", ");o=bytes_text(o,used);put(o," used");}
            if(w>70){fit(s.x+10,s.y+6,line,s.w-20,&zui_label,ui.theme.text);fit(s.x+10,s.y+25,sub,s.w-20,&zui_caption,ui.theme.secondary);}
            if(used&&!is_new(p)&&w>40){
                int track=s.w-20;unsigned long size=p->sectors*512;int fill=size?(int)((unsigned long)track*(used>size?size:used)/size):0;
                zui_fill(&ui,(struct zui_rect){s.x+10,s.y+s.h-6,track,2},mix(ui.theme.surface,color,35));
                zui_fill(&ui,(struct zui_rect){s.x+10,s.y+s.h-6,fill<2?2:fill,2},color);
            }
        }else{
            hatch(s,mix(ui.theme.surface,ui.theme.secondary,22));
            if(w>70){char sub[24];size_text(sub,items[i].sectors);text(s.x+10,s.y+6,"Free",&zui_label,ui.theme.text);text(s.x+10,s.y+25,sub,&zui_caption,ui.theme.secondary);}
        }
        if(record&&items[i].id==selected)zui_round_outline(&ui,(struct zui_rect){s.x-2,s.y-2,s.w+4,s.h+4},10,ui.theme.text);
        if(record&&segments<MAX_PARTS*2+2){segment_rects[segments]=s;segment_ids[segments]=items[i].id;segments++;}
        x+=w+gap;
    }
}

/* ---------- Status cards: filesystem state, GPT copies, alignment. */
static void card(struct zui_rect r,const char *icon,const char *title,const char *sub,unsigned int color){
    zui_round(&ui,r,8,ui.theme.border);zui_round(&ui,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},7,mix(ui.theme.surface,ui.theme.raised,50));
    zui_icon(&ui,icon,r.x+12,r.y+12,16,ZUI_ICON_TEXT);
    fit(r.x+36,r.y+9,title,r.w-46,&zui_label,color);
    fit(r.x+36,r.y+29,sub,r.w-46,&zui_caption,ui.theme.secondary);
}
static int draw_cards(int x,int y,int w,const struct model *before,const struct zenith_disk *d){
    int columns=w>=520?3:1,cw=columns==3?(w-24)/3:w,h=52;
    char t1[48],s1[48],t2[48],s2[48],t3[48],s3[48];unsigned int c1=good(),c2=good(),c3=good();const char *i1="check",*i2="shield",*i3="layout-grid";
    /* ext2: the first that needs a check, else clean, else in use. */
    int ext2=0,check=-1,inuse=0;
    for(unsigned int i=0;i<before->count;i++)if(before->parts[i].fs==Z_FS_EXT2){ext2++;unsigned int st=fs_state(&before->parts[i]);if(st==Z_FS_STATE_CHECK&&check<0)check=(int)i;if(st==Z_FS_STATE_IN_USE)inuse++;}
    if(!extras()){put(t1,"ext2 state unknown");put(s1,"Update Zenith to see it");c1=ui.theme.secondary;i1="info";}
    else if(!ext2){put(t1,"No ext2 volumes");put(s1,"Nothing to check");c1=ui.theme.secondary;i1="info";}
    else if(check>=0){put(t1,"ext2 check needed");put(put(s1,before->parts[check].name)," was not unmounted cleanly");c1=warn();i1="triangle-alert";}
    else{put(t1,"ext2 check: clean");char *o=num(s1,(unsigned long)ext2);o=put(o,ext2==1?" volume":" volumes");if(inuse)put(o,inuse==ext2?", in use":", some in use");}
    unsigned int gpt=extras()&&page<(int)disks.disk_count?disks.extra.gpt[page]:Z_GPT_UNKNOWN;
    if(!(d->flags&Z_DISK_GPT)){put(t2,"No GPT");put(s2,"No partition table");c2=ui.theme.secondary;i2="info";}
    else if(gpt==Z_GPT_COPIES_MATCH){put(t2,"GPT copies match");put(s2,"Primary and backup");}
    else if(gpt==Z_GPT_COPIES_DIFFER){put(t2,"GPT copies differ");put(s2,"The next write repairs the backup");c2=warn();i2="triangle-alert";}
    else{put(t2,"GPT");put(s2,"Copies not checked");c2=ui.theme.secondary;}
    int misaligned=-1;for(unsigned int i=0;i<before->count;i++)if(before->parts[i].start%ALIGN){misaligned=(int)i;break;}
    if(!before->count){put(t3,"Aligned to 1 MiB");put(s3,"New partitions are");}
    else if(misaligned<0){put(t3,"Aligned to 1 MiB");put(s3,"All partitions");}
    else{put(t3,"Not aligned to 1 MiB");put(s3,before->parts[misaligned].name);c3=warn();i3="triangle-alert";}
    struct zui_rect r={x,y,cw,h};
    card(r,i1,t1,s1,c1);
    if(columns==3){r.x+=cw+12;card(r,i2,t2,s2,c2);r.x+=cw+12;card(r,i3,t3,s3,c3);return h;}
    r.y+=h+8;card(r,i2,t2,s2,c2);r.y+=h+8;card(r,i3,t3,s3,c3);return 3*h+16;
}

/* ---------- The partition table: the disk as the plan leaves it, changes marked. */
static int draw_table(int x,int y,int w,const struct model *before){
    int full=w>=560;
    int cols[6];const char *heads[6]={"Partition","Filesystem","Size","Used","Mounted at","Label"};
    if(full){int p[6]={0,17,32,52,66,84};for(int i=0;i<6;i++)cols[i]=x+w*p[i]/100;}
    else{int p[6]={0,30,52,0,74,0};for(int i=0;i<6;i++)cols[i]=x+w*p[i]/100;}
    for(int i=0;i<6;i++)if(full||i==0||i==1||i==2||i==4)text(cols[i]+(i==0?16:0),y,heads[i],&zui_caption,ui.theme.secondary);
    y+=24;
    rows=0;
    if(!current.count){text(x,y+4,"No partitions.",&zui_body,ui.theme.secondary);return 56;}
    for(unsigned int i=0;i<current.count&&rows<MAX_PARTS;i++){
        const struct part *p=&current.parts[i];
        struct zui_rect r={x-8,y,w+16,36};
        row_rects[rows]=r;row_ids[rows]=(int)i;rows++;
        if((int)i==selected)zui_round(&ui,r,8,mix(ui.theme.surface,ui.theme.accent,14));
        int ty=y+(36-zui_body.height)/2,my=y+(36-zui_mono.height)/2;
        dot(cols[0],y+14,part_color(p));
        if(is_new(p))pill(cols[0]+16,y+8,"New",purple());
        else fit(cols[0]+16,my,p->name,cols[1]-cols[0]-24,&zui_mono,ui.theme.text);
        text(cols[1],ty,filesystem_text(p),&zui_body,ui.theme.text);
        /* Size: a change reads "before -> after". */
        int was=-1;for(unsigned int k=0;k<before->count;k++)if(before->parts[k].start==p->start)was=(int)k;
        char sz[48],*o=sz;
        if(was>=0&&before->parts[was].sectors!=p->sectors){o=size_text(o,before->parts[was].sectors);o=put(o," \xE2\x86\x92 ");size_text(o,p->sectors);}
        else size_text(o,p->sectors);
        fit(cols[2],ty,sz,(full?cols[3]:cols[4])-cols[2]-8,&zui_body,p->planned?(is_new(p)?purple():ui.theme.accent):ui.theme.text);
        if(full){char u[24];unsigned long used=used_bytes(p);if(used&&!is_new(p))bytes_text(u,used);else put(u,"\xE2\x80\x94");text(cols[3],ty,u,&zui_body,ui.theme.secondary);}
        fit(cols[4],p->mount[0]?my:ty,p->mount[0]?p->mount:"Not mounted",(full?cols[5]:x+w)-cols[4]-8,p->mount[0]?&zui_mono:&zui_body,p->mount[0]?ui.theme.text:ui.theme.secondary);
        if(full){fit(cols[5],ty,p->role||p->type==Z_PART_EFI||p->label[0]?part_title(p):"\xE2\x80\x94",x+w-cols[5]-28,&zui_body,p->role||p->type==Z_PART_EFI||p->label[0]?ui.theme.text:ui.theme.secondary);
            if(p->type==Z_PART_EFI||p->role==Z_ROLE_ROOT)zui_icon(&ui,"shield",x+w-18,y+10,16,ZUI_ICON_TEXT);}
        y+=38;
    }
    return 24+rows*38;
}

/* ---------- The disk page. */
static int draw_disk(struct zui_rect area){
    const struct zenith_disk *d=&disks.disks[page];
    struct model before;load(&before,d);
    current_ok=planned_model(page,&current);
    current_free_count=free_regions(&current,current_free,MAX_PARTS+1);
    int x=area.x+24,w=area.w-48,y=area.y+20,top=y;
    char title[32];
    if(mode!=MODE_DISK){
        zui_icon(&ui,"arrow-left",x,y+2,16,ZUI_ICON_TEXT);text(x+22,y,disk_title(d,title),&zui_body,ui.theme.secondary);y+=28;
        const struct part *p=selected>=0&&(unsigned int)selected<current.count?&current.parts[selected]:0;
        char heading[48];
        if(mode==MODE_GROW&&p)put(put(heading,"Grow "),part_title(p));else put(heading,"New partition");
        text(x,y,heading,&zui_heading,ui.theme.text);y+=42;
    }else{
        text(x,y,disk_title(d,title),&zui_heading,ui.theme.text);y+=40;
    }
    char sub[112],*o=put(sub,d->name);o=put(o," \xC2\xB7 ");o=size_text(o,d->sectors);o=put(o," \xC2\xB7 ");o=put(o,d->flags&Z_DISK_GPT?"GPT":"no partition table");
    if(d->flags&Z_DISK_SYSTEM)put(o," \xC2\xB7 the running system");else if(d->flags&Z_DISK_MOUNTED)put(o," \xC2\xB7 mounted");
    text(x,y,sub,&zui_caption,ui.theme.secondary);y+=30;
    int planned=disk_steps(d->name)>0;
    /* In an editor, "After" previews the plan with this step added. */
    struct model preview=current;int previewing=0;
    if(mode==MODE_CREATE&&selected<0&&-1-selected<current_free_count){
        struct step s={Z_DISK_CREATE,"",0,chosen_size(),1,"",""};copy(s.disk,d->name,16);copy(s.label,label_text,16);
        previewing=simulate(&preview,&s);
    }else if(mode==MODE_GROW&&selected>=0&&(unsigned int)selected<current.count){
        struct step s={Z_DISK_GROW,"",current.parts[selected].start,0,0,"",""};copy(s.disk,d->name,16);
        previewing=simulate(&preview,&s);
    }
    text(x,y+14,"Now",&zui_label,ui.theme.secondary);
    draw_bar((struct zui_rect){x+56,y,w-56,46},&before,!planned&&mode==MODE_DISK);y+=56;
    if(planned||previewing){
        text(x,y+14,"After",&zui_label,current_ok?ui.theme.accent:bad());
        draw_bar((struct zui_rect){x+56,y,w-56,46},previewing?&preview:&current,mode==MODE_DISK);y+=56;
        if(!current_ok){text(x+56,y,"A step no longer fits this disk: review the plan.",&zui_caption,bad());y+=22;}
    }
    y+=8;
    if(mode==MODE_CREATE){
        text(x,y+8,"Size",&zui_body,ui.theme.secondary);
        zui_combo_draw(&ui,&size_combo,(struct zui_rect){x+120,y,w-120<300?w-120:300,34},5,size_rows,0);y+=46;
        text(x,y+8,"Label",&zui_body,ui.theme.secondary);
        label_rect=(struct zui_rect){x+120,y,w-120<300?w-120:300,34};zui_input_draw(&ui,label_rect,&label_input,label_focus);y+=50;
        text(x,y,"What this does",&zui_label,ui.theme.text);y+=28;
        static const char *icons[3]={"layout-grid","check","shield"};
        static const char *lines[3][2]={{"Placed in free space","The first free region that fits, starting on a 1 MiB boundary"},
            {"Formatted ext2","With room to grow to 2 TiB (1 KiB blocks below 64 MiB)"},
            {"Other partitions are not changed","Only the partition table and the new area are written"}};
        for(int i=0;i<3;i++){zui_icon(&ui,icons[i],x,y+2,16,ZUI_ICON_TEXT);text(x+26,y,lines[i][0],&zui_label,ui.theme.text);fit(x+26,y+20,lines[i][1],w-26,&zui_caption,ui.theme.secondary);y+=46;}
    }else if(mode==MODE_GROW&&selected>=0&&(unsigned int)selected<current.count){
        const struct part *p=&current.parts[selected];unsigned long limit=grow_limit(&current,selected);
        char a[48],b[48];o=put(a,"Largest: ");size_text(o,limit);o=put(b,"From ");o=size_text(o,p->sectors);put(o,": the free space right after it");
        text(x,y,"What limits this",&zui_label,ui.theme.text);y+=28;
        const char *icons[4]={"check","arrow-left","layout-grid",p->mount[0]?"triangle-alert":"check"};
        const char *heads[4]={a,"Start stays fixed","Snaps to 1 MiB",p->mount[0]?"Mounted":"Not mounted"};
        const char *subs[4]={b,"Moving the start would copy every block; Equinox does not offer it yet","So partitions stay aligned for the disk",
            p->mount[0]?"Unmount it first, or grow the root from the rescue entry":"The ext2 volume grows after the partition, then is checked"};
        for(int i=0;i<4;i++){zui_icon(&ui,icons[i],x,y+2,16,ZUI_ICON_TEXT);text(x+26,y,heads[i],&zui_label,i==3&&p->mount[0]?warn():ui.theme.text);fit(x+26,y+20,subs[i],w-26,&zui_caption,ui.theme.secondary);y+=46;}
    }else{
        mode=MODE_DISK;
        y+=draw_table(x,y,w,&before)+10;
        /* Actions for the selection: they only add steps to the plan. */
        const char *why=protection(d);
        int sel_part=selected>=0&&(unsigned int)selected<current.count,sel_free=selected<0&&-1-selected<current_free_count;
        int bx=x;
        if(sel_part){
            const struct part *p=&current.parts[selected];
            int editable=!why;
            int growable=editable&&p->fs==Z_FS_EXT2&&grow_limit(&current,selected)>p->sectors;
            zui_add(&ui,ID_GROW,ZUI_BUTTON,(struct zui_rect){bx,y,110,34},"Grow",0,growable);bx+=120;
            zui_add(&ui,ID_DELETE,ZUI_BUTTON,(struct zui_rect){bx,y,110,34},"Delete",0,editable&&p->role==Z_ROLE_NONE);bx+=120;
        }else if(sel_free){
            int creatable=(d->flags&Z_DISK_GPT)&&(!(d->flags&Z_DISK_MOUNTED)||(d->flags&Z_DISK_SYSTEM));
            zui_add(&ui,ID_CREATE,ZUI_BUTTON,(struct zui_rect){bx,y,160,34},"New partition",0,creatable);bx+=170;
        }
        zui_add(&ui,ID_TABLE,ZUI_BUTTON,(struct zui_rect){bx,y,190,34},"New partition table",0,!(d->flags&(Z_DISK_SYSTEM|Z_DISK_MOUNTED)));
        static const char hint[]="Select a partition or free space for its actions.";
        if(!sel_part&&!sel_free&&zui_measure(hint,sizeof(hint)-1,&zui_caption)<=x+w-bx-204)text(bx+204,y+9,hint,&zui_caption,ui.theme.secondary);
        y+=50;
        y+=draw_cards(x,y,w,&before,d)+14;
        if(why){
            int h=82;
            zui_round(&ui,(struct zui_rect){x,y,w,h},8,ui.theme.border);zui_round(&ui,(struct zui_rect){x+1,y+1,w-2,h-2},7,mix(ui.theme.surface,ui.theme.raised,50));
            zui_icon(&ui,"shield",x+14,y+14,16,ZUI_ICON_TEXT);
            text(x+40,y+12,d->flags&Z_DISK_SYSTEM?"The system disk is protected":!(d->flags&Z_DISK_GPT)?"No partition table":"Mounted partitions",&zui_label,ui.theme.text);
            zui_paragraph(&ui,(struct zui_rect){x+40,y+34,w-56,44},why,&zui_caption,ui.theme.secondary,0);
            y+=h+10;
        }
        text(x,y,"Every action joins the plan first; nothing is written until Apply.",&zui_caption,ui.theme.secondary);y+=24;
    }
    if(status_line[0]&&mode==MODE_DISK){text(x,y,status_line,&zui_caption,warn());y+=24;}
    return y-top+40;
}

/* ---------- The plan: a panel on the right (wide) or its own page (narrow). */
static const char *step_detail(const struct step *s){
    if(s->action==Z_DISK_INIT)return "Removes every partition on the disk";
    if(s->action==Z_DISK_CREATE){
        for(unsigned int k=0;k<disks.disk_count;k++)if(same(disks.disks[k].name,s->disk)&&(disks.disks[k].flags&Z_DISK_SYSTEM))return "Appears after the next restart";
        return s->ext2?"Then formatted ext2, ready to grow":"Left unformatted";
    }
    if(s->action==Z_DISK_DELETE)return "Its data becomes unreachable";
    return "The table first, then the ext2 volume, then a check";
}
static int draw_plan_body(struct zui_rect p,int full){
    int x=p.x+18,w=p.w-36,y=p.y+16,top=y;
    text(x,y,"Your plan",&zui_heading,ui.theme.text);
    text_right(x+w,y+10,applying>=0?"Applying...":steps?"Nothing written yet":"Empty",&zui_caption,applying>=0?ui.theme.accent:ui.theme.secondary);
    y+=44;zui_fill(&ui,(struct zui_rect){p.x,y,p.w,1},ui.theme.border);y+=16;
    if(!steps){
        y+=zui_paragraph(&ui,(struct zui_rect){x,y,w,80},"Nothing planned. Select a partition or free space on a disk and pick an action: it lands here first.",&zui_body,ui.theme.secondary,0)*(zui_body.height+4)+14;
    }
    for(int i=0;i<steps;i++){
        struct model before;int d=-1;
        for(unsigned int k=0;k<disks.disk_count;k++)if(same(disks.disks[k].name,plan[i].disk))d=(int)k;
        if(d<0)continue;
        load(&before,&disks.disks[d]);for(int k=0;k<i;k++)simulate(&before,&plan[k]);
        char n[8];num(n,(unsigned long)i+1);
        int running=i==applying;
        zui_round(&ui,(struct zui_rect){x,y,24,24},12,running?ui.theme.accent:mix(ui.theme.surface,ui.theme.accent,24));
        text(x+12-zui_measure(n,length(n),&zui_caption)/2,y+4,n,&zui_caption,running?ui.theme.on_accent:ui.theme.text);
        fit(x+36,y+2,plan[i].text,w-36,&zui_label,ui.theme.text);y+=24;
        fit(x+36,y,step_detail(&plan[i]),w-36,&zui_caption,ui.theme.secondary);y+=20;
        if(plan[i].action==Z_DISK_CREATE&&same(step_detail(&plan[i]),"Appears after the next restart")){pill(x+36,y,"Runs at restart",warn());y+=26;}
        if(show_commands){char cmd[112];command_text(cmd,&plan[i],&before);fit(x+36,y,cmd,w-36,&zui_mono,ui.theme.secondary);y+=22;}
        y+=12;
    }
    zui_fill(&ui,(struct zui_rect){p.x,y,p.w,1},ui.theme.border);y+=16;
    static const char *icons[4]={"check","shield","file-text","rotate-ccw"};
    static const char *notes[4]={"Each step is checked first","Mounted partitions stay as they are",
        "The table first, then the filesystem","A failure stops the plan there"};
    for(int i=0;i<4;i++){zui_icon(&ui,icons[i],x,y+1,16,ZUI_ICON_TEXT);fit(x+26,y,notes[i],w-26,&zui_caption,ui.theme.text);y+=26;}
    y+=6;
    zui_round(&ui,(struct zui_rect){x,y,w,58},8,mix(ui.theme.surface,ui.theme.subtle,70));
    text(x+12,y+8,"If it fails",&zui_label,ui.theme.text);
    fit(x+12,y+30,"History says why; nothing after it runs.",w-24,&zui_caption,ui.theme.secondary);y+=72;
    if(!can_edit){fit(x,y,"Sign in as root to apply changes.",w,&zui_caption,warn());y+=22;}
    else if(status_line[0]&&!full){fit(x,y,status_line,w,&zui_caption,warn());y+=22;}
    zui_add(&ui,ID_APPLY,ZUI_BUTTON,(struct zui_rect){x,y,w,36},"Apply plan",1,can_edit&&applying<0&&steps>0);y+=44;
    zui_add(&ui,ID_COMMANDS,ZUI_BUTTON,(struct zui_rect){x,y,w,34},show_commands?"Hide the exact commands":"See the exact commands",0,steps>0);y+=42;
    int half=(w-10)/2;
    zui_add(&ui,ID_UNDO,ZUI_BUTTON,(struct zui_rect){x,y,half,34},"Remove last",0,applying<0&&steps>0);
    zui_add(&ui,ID_CLEAR,ZUI_BUTTON,(struct zui_rect){x+half+10,y,w-half-10,34},"Clear",0,applying<0&&steps>0);y+=42;
    return y-top+24;
}
static void draw_plan_panel(struct zui_rect p,int full){
    if(!full){zui_round(&ui,p,10,ui.theme.border);zui_round(&ui,(struct zui_rect){p.x+1,p.y+1,p.w-2,p.h-2},9,ui.theme.surface);}
    int origin=zui_scroll_begin(&ui,&plan_scroll,p);
    int height=draw_plan_body((struct zui_rect){p.x,origin,p.w,p.h},full);
    zui_scroll_end(&ui,&plan_scroll,height);
}
static void draw_history(struct zui_rect c){
    int x=c.x+24,y=c.y+20,w=c.w-48;
    text(x,y,"History",&zui_heading,ui.theme.text);y+=46;
    if(!history_count){text(x,y,"Changes applied in this session appear here.",&zui_body,ui.theme.secondary);return;}
    for(int i=history_count-1;i>=0&&y+44<c.y+c.h;i--){
        int failed=history[i][0]=='!';
        zui_round(&ui,(struct zui_rect){x,y,w,44},8,mix(ui.theme.surface,ui.theme.raised,50));
        zui_icon(&ui,failed?"x":"check",x+12,y+14,16,ZUI_ICON_TEXT);
        fit(x+38,y+13,history[i]+1,w-50,&zui_body,failed?bad():ui.theme.text);
        y+=52;
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

/* ---------- Frame. */
static void draw(void){
    struct zui_workspace w=zui_workspace_begin(&ui,ui.width<800?176:212);struct zui_rect c=w.content;
    int focus=ui.focus>=0&&ui.focus<ui.count?ui.controls[ui.focus].id:0;
    int pressed=ui.pressed>=0&&ui.pressed<ui.count?ui.controls[ui.pressed].id:0;
    ui.count=0;ui.pressed=-1;ui.focus=-1;
    int n=have_disks?(int)disks.disk_count:0;
    wide=c.w>=880;
    draw_sidebar(w.sidebar);
    zui_clip(&ui,c);
    if(!have_disks){text(c.x+24,c.y+20,"This system does not list disks.",&zui_label,ui.theme.text);
        text(c.x+24,c.y+44,"Equinox needs a newer Zenith kernel. Update Zenith, then reopen it.",&zui_body,ui.theme.secondary);}
    else if(page<n){
        struct zui_rect center=c;
        if(wide){center.w=c.w-332;draw_plan_panel((struct zui_rect){c.x+c.w-320,c.y+12,308,c.h-24},0);zui_clip(&ui,c);}
        int origin=zui_scroll_begin(&ui,&center_scroll,center);
        int height=draw_disk((struct zui_rect){center.x,origin,center.w,center.h});
        if(mode!=MODE_DISK){
            /* The editor's own buttons, below its explanation. */
            int bx=center.x+24,by=origin+height-16;
            zui_add(&ui,ID_ADD,ZUI_BUTTON,(struct zui_rect){bx,by,150,36},"Add to plan",1,1);
            zui_add(&ui,ID_CANCEL,ZUI_BUTTON,(struct zui_rect){bx+162,by,120,36},"Cancel",0,1);
            height+=60;
        }
        zui_scroll_end(&ui,&center_scroll,height);
    }
    else if(page==n)draw_plan_panel(c,1);
    else draw_history(c);
    for(int i=0;i<ui.count;i++)if(ui.controls[i].enabled){if(ui.controls[i].id==focus)ui.focus=i;if(ui.controls[i].id==pressed)ui.pressed=i;}
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});zui_update_hover(&ui);zui_draw_controls(&ui);
    draw_sidebar_details();
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
    if(id==ID_UNDO&&steps&&applying<0)steps--;
    if(id==ID_CLEAR&&applying<0)steps=0;
    if(id==ID_COMMANDS)show_commands=!show_commands;
    if(id==ID_APPLY&&steps){
        char m[200],*o=put(m,"Equinox writes ");o=num(o,(unsigned long)steps);put(o,steps==1?" step now. A failed step stops the plan; earlier steps stay.":" steps now, in order. A failed step stops the plan; earlier steps stay.");
        ask(3,"Apply the plan?",m,"Apply");
    }
    if(page<n){
        const struct zenith_disk *d=&disks.disks[page];
        if(id==ID_CREATE){mode=MODE_CREATE;{struct zui_combo fresh={0};fresh.id=ID_SIZE;size_combo=fresh;}label_text[0]=0;zui_input_init(&label_input,label_text,sizeof(label_text));label_focus=0;center_scroll.offset=0;}
        if(id==ID_GROW){mode=MODE_GROW;center_scroll.offset=0;}
        if(id==ID_CANCEL)mode=MODE_DISK;
        if(id==ID_DELETE&&selected>=0){
            char m[200],*o=put(m,"Delete ");o=put(o,current.parts[selected].name);put(o,"? Its data becomes unreachable once the plan is applied.");
            ask(1,"Add a deletion to the plan?",m,"Add to plan");
        }
        if(id==ID_TABLE){
            char m[200],*o=put(m,"A new, empty partition table on ");o=put(o,d->name);put(o," removes every partition on it once the plan is applied.");
            ask(2,"Add a new partition table to the plan?",m,"Add to plan");
        }
        if(id==ID_ADD&&mode==MODE_CREATE&&selected<0&&-1-selected<current_free_count){
            unsigned long sectors=chosen_size(),shown=sectors;char t[112],*o=put(t,"Create ");
            /* "All free space" takes the largest region, as the kernel places it. */
            if(!shown)for(int i=0;i<current_free_count;i++)if(current_free[i].sectors>shown)shown=current_free[i].sectors;
            o=size_text(o,shown);
            if(label_text[0]){o=put(o," \"");o=put(o,label_text);o=put(o,"\"");}
            o=put(o," on ");o=put(o,d->name);put(o,", ext2");
            /* Placement follows the kernel: the first free region that fits, or the largest. The
             * region the person selected is the largest when they chose all of it. */
            add_step(Z_DISK_CREATE,0,sectors,1,label_text,t);
        }
        if(id==ID_ADD&&mode==MODE_GROW&&selected>=0){
            const struct part *p=&current.parts[selected];char t[112],*o=put(t,"Grow ");o=put(o,p->name);o=put(o," to ");size_text(o,grow_limit(&current,selected));
            add_step(Z_DISK_GROW,p->start,0,0,"",t);
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
    if(mode!=MODE_DISK)return;
    for(int i=0;i<segments;i++)if(inside(segment_rects[i],e->x,e->y)){selected=segment_ids[i];status_line[0]=0;ui.dirty=1;return;}
    for(int i=0;i<rows;i++)if(inside(row_rects[i],e->x,e->y)){selected=row_ids[i];status_line[0]=0;ui.dirty=1;return;}
}
/* ---------- Test entry points (no window): the snapshot, and a fixed plan through the real ABI. */
static int probe(void){
    refresh();
    if(!have_disks){print("EQUINOX_NO_DISK_ABI\n");return 1;}
    char line[256],*o=put(line,"EQUINOX_DISKS count=");o=num(o,disks.disk_count);o=put(o," root=");put(o,can_edit?"yes\n":"no\n");print(line);
    for(unsigned int d=0;d<disks.disk_count;d++){
        const struct zenith_disk *k=&disks.disks[d];
        o=put(line,"EQUINOX_DISK ");o=put(o,k->name);o=put(o," ");o=size_text(o,k->sectors);o=put(o," parts=");o=num(o,k->part_count);
        o=put(o," free=");o=num(o,k->free_count);o=put(o,k->flags&Z_DISK_SYSTEM?" system":"");
        if(extras()){o=put(o," gpt=");o=num(o,disks.extra.gpt[d]);}
        put(o,"\n");print(line);
        for(unsigned int i=0;i<k->part_count&&i<Z_DISK_PARTS;i++){
            const struct zenith_disk_part *p=&k->parts[i];
            o=put(line,"EQUINOX_PART ");o=put(o,p->name);o=put(o," ");o=size_text(o,p->sectors);o=put(o," fs=");o=num(o,p->filesystem);
            o=put(o," label=");o=put(o,p->label);o=put(o," role=");o=num(o,p->role);
            if(extras()){o=put(o," used=");o=num(o,disks.extra.used[d][i]);o=put(o," state=");o=num(o,disks.extra.state[d][i]);}
            put(o,"\n");print(line);
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
        if(page<n&&zui_scroll_event(&ui,&center_scroll,&e)){ui.dirty=1;continue;}
        if((page==n||(page<n&&wide))&&zui_scroll_event(&ui,&plan_scroll,&e)){ui.dirty=1;continue;}
        if(e.type==Z_GUI_DOWN||e.type==Z_GUI_UP)pointer(&e);
        int id=zui_event(&ui,&e);
        if(id>=10&&id<10+n+2&&id-10!=page){page=id-10;mode=MODE_DISK;selected=NONE;status_line[0]=0;center_scroll.offset=0;plan_scroll.offset=0;ui.dirty=1;continue;}
        if(id)action(id);
    }
    zenith_gui_close(handle);return result;
}
