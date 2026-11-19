/* Orbit: software, as a plan (docs/components/ORBIT.md, concept docs/design/orbit-concept.svg). A front end to pkg
 * through call 668 (zenith-native-package-center-abi 1.0, kernel 0.0.70): the same signatures, planner, transactions
 * and kernel staging as the shell, and no second database. You add and remove changes; the plan shows what else is
 * needed, sizes, signatures and whether a restart is needed, and Apply runs it as one transaction. Packages that
 * cannot be installed are listed with the reason. Every plan shows the exact pkg command, so the GUI teaches the CLI.
 * Reading needs no permission; applying asks for org.zenith.packages.manage. */
#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
#include "../include/zenith_files.h"
#include "../include/zenith_portal.h"
#include "../include/zenith_packages.h"

static unsigned int pixels[640*480];
static struct zui ui;
static long handle;
static int material;

/* ---------- Text helpers: no libc in native programs. */
static unsigned long length(const char *s){unsigned long n=0;while(n<65536&&s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static int starts(const char *s,const char *p){while(*p){if(*s++!=*p++)return 0;}return 1;}
static void print(const char *s){zenith_write(s,length(s));}
static char *put(char *o,const char *s){while(*s)*o++=*s++;*o=0;return o;}
static char *num(char *o,unsigned long v){char t[24];int n=0;do{t[n++]=(char)('0'+v%10);v/=10;}while(v);while(n)*o++=t[--n];*o=0;return o;}
static char *snum(char *o,long v){if(v<0){*o++='-';v=-v;}return num(o,(unsigned long)v);}
static void copy(char *o,const char *s,unsigned long n){unsigned long i=0;if(same(s,"-"))s="";for(;i+1<n&&s[i];i++)o[i]=s[i];o[i]=0;}
static long to_long(const char *s){long v=0;int neg=*s=='-';if(neg)s++;while(*s>='0'&&*s<='9')v=v*10+(*s++-'0');return neg?-v:v;}
static char lower(char c){return c>='A'&&c<='Z'?(char)(c+32):c;}
static int contains(const char *hay,const char *needle){
    if(!*needle)return 1;
    for(;*hay;hay++){const char *h=hay,*n=needle;while(*h&&*n&&lower(*h)==lower(*n)){h++;n++;}if(!*n)return 1;}
    return 0;
}
static char *size_text(char *o,long b){
    int neg=b<0;if(neg){*o++='-';b=-b;}
    if(b<1024){o=num(o,(unsigned long)b);return put(o," B");}
    if(b<1048576){o=num(o,(unsigned long)((b+512)/1024));return put(o," KiB");}
    long tenths=b*10/1048576;
    if(b<1073741824L){o=num(o,(unsigned long)(tenths/10));*o++='.';o=num(o,(unsigned long)(tenths%10));return put(o," MiB");}
    tenths=b*10/1073741824L;o=num(o,(unsigned long)(tenths/10));*o++='.';o=num(o,(unsigned long)(tenths%10));return put(o," GiB");
}
/* Splits a record in place at tabs. */
static int split(char *line,char **f,int max){int n=0;f[n++]=line;for(char *p=line;*p&&n<max;p++)if(*p=='\t'){*p=0;f[n++]=p+1;}return n;}
/* Calls one read operation and walks its lines: fn(fields,count) per record after the header. */
static char reply[65536];
static long read_op(unsigned int op,unsigned int value,const void *input,unsigned int bytes,void (*fn)(char **,int)){
    long r=zenith_packages(op,value,reply,sizeof(reply)-1,input,bytes);
    if(r<0)return r;
    reply[r<(long)sizeof(reply)?r:(long)sizeof(reply)-1]=0;
    char *line=reply;int first=1;
    while(*line){
        char *end=line;while(*end&&*end!='\n')end++;
        int more=*end=='\n';*end=0;
        if(!first&&fn){char *f[20];int n=split(line,f,20);fn(f,n);}
        first=0;line=more?end+1:end;
    }
    return r;
}

static void text(int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(&ui,x,y,s,length(s),f,c);}
static int measure(const char *s,const struct zui_font *f){return zui_measure(s,length(s),f);}
static void text_right(int right,int y,const char *s,const struct zui_font *f,unsigned int c){text(right-measure(s,f),y,s,f,c);}
static void fit(int x,int y,const char *s,int width,const struct zui_font *f,unsigned int c){
    if(width<=0)return;
    if(measure(s,f)<=width){text(x,y,s,f,c);return;}
    char t[256];unsigned long n=length(s);if(n>250)n=250;
    for(unsigned long i=0;i<n;i++)t[i]=s[i];
    while(n>0){t[n]=0;char e[260];unsigned long k=0;for(;k<n;k++)e[k]=t[k];e[k++]='.';e[k++]='.';e[k++]='.';e[k]=0;if(measure(e,f)<=width){text(x,y,e,f,c);return;}n--;}
}
static int inside(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x<r.x+r.w&&y<r.y+r.h;}
static int light(void){unsigned int s=ui.theme.surface;return ((s>>16&255)+(s>>8&255)+(s&255))>384;}
static unsigned int good(void){return light()?0x277347:0x8BCBA4;}
static unsigned int warn(void){return light()?0x886010:0xE6C47A;}
static unsigned int bad(void){return light()?0xAE325D:0xF29BAE;}
static void panel(struct zui_rect r){
    if(ui.foreground&&ui.full_background&&!ui.theme_preview)zui_round_alpha(&ui,r,12,ui.theme.raised,90);
    else zui_round(&ui,r,12,ui.theme.raised);
    zui_round_outline(&ui,r,12,ui.theme.border);
}
static int chip(int x,int y,const char *label,unsigned int color){
    int w=measure(label,&zui_caption)+16;
    zui_round(&ui,(struct zui_rect){x,y,w,20},10,ui.theme.subtle);
    text(x+8,y+2,label,&zui_caption,color);return w;
}

/* ---------- What the kernel says. */
struct pkg { char name[48],kind[8],repo[8],installed[16],candidate[16],latest[16],title[40],summary[96],category[16],icon[24],reason[160];
             int state,programs,mark; long bytes,payload,files; };
static struct pkg pkgs[72];static int npkgs;
static char list_error[200];
static struct { char running[16],high[16]; int pending; long installed,updates,available,not_offered;
                char key[72]; long free_bytes; int mutable_root,busy,may_apply; long offset,now; char error[200]; int have; } ov;
struct source { char name[8],mirror[160]; long priority,generation,issued,expires,entries,checked; };
static struct source sources[2];static int nsources,cache_auto;
static void on_overview(char **f,int n){
    if(same(f[0],"kernel")&&n>=4){copy(ov.running,f[1],16);copy(ov.high,f[2],16);ov.pending=same(f[3],"1");}
    else if(same(f[0],"counts")&&n>=5){ov.installed=to_long(f[1]);ov.updates=to_long(f[2]);ov.available=to_long(f[3]);ov.not_offered=to_long(f[4]);}
    else if(same(f[0],"key")&&n>=2)copy(ov.key,f[1],sizeof(ov.key));
    else if(same(f[0],"source")&&n>=9&&nsources<2){struct source *s=&sources[nsources++];copy(s->name,f[1],8);s->priority=to_long(f[2]);copy(s->mirror,f[3],160);
        s->generation=to_long(f[4]);s->issued=to_long(f[5]);s->expires=to_long(f[6]);s->entries=to_long(f[7]);s->checked=to_long(f[8]);}
    else if(same(f[0],"cache")&&n>=2)cache_auto=same(f[1],"1");
    else if(same(f[0],"system")&&n>=7){ov.free_bytes=to_long(f[1]);ov.mutable_root=same(f[2],"1");ov.busy=same(f[3],"1");ov.may_apply=same(f[4],"1");ov.offset=to_long(f[5]);ov.now=to_long(f[6]);}
    else if(same(f[0],"error")&&n>=2)copy(ov.error,f[1],sizeof(ov.error));
}
static struct pkg previous[72];static int nprevious;
static void on_list(char **f,int n){
    if(same(f[0],"error")&&n>=2){copy(list_error,f[1],sizeof(list_error));return;}
    if(!same(f[0],"pkg")||n<17||npkgs>=72)return;
    struct pkg *p=&pkgs[npkgs++];
    copy(p->name,f[1],48);copy(p->kind,f[2],8);copy(p->repo,f[3],8);p->state=(int)to_long(f[4]);copy(p->installed,f[5],16);copy(p->candidate,f[6],16);
    copy(p->latest,f[7],16);p->bytes=to_long(f[8]);p->payload=to_long(f[9]);p->files=to_long(f[10]);p->programs=same(f[11],"1");
    copy(p->title,f[12],40);copy(p->summary,f[13],96);copy(p->category,f[14],16);copy(p->icon,f[15],24);copy(p->reason,f[16],160);
    p->mark=0;for(int i=0;i<nprevious;i++)if(same(previous[i].name,p->name))p->mark=previous[i].mark;
    /* A mark that no longer applies (installed meanwhile) is dropped. */
    if(p->mark==1&&p->state!=Z_PKG_UPDATE&&p->state!=Z_PKG_AVAILABLE)p->mark=0;
    if(p->mark==2&&!p->installed[0])p->mark=0;
}
static int have_service=1,first_load=1,plan_dirty=1;
static void load(void){
    ov=(typeof(ov)){0};nsources=0;
    long r=read_op(Z_PKG_OVERVIEW,0,0,0,on_overview);
    if(r==-38){have_service=0;return;}
    ov.have=r>0;
    for(int i=0;i<npkgs;i++){previous[i]=pkgs[i];}
    nprevious=npkgs;
    npkgs=0;list_error[0]=0;read_op(Z_PKG_LIST,0,0,0,on_list);
    if(first_load&&npkgs){
        /* Updates are in the plan from the start; take out what you do not want. */
        for(int i=0;i<npkgs;i++)if(pkgs[i].state==Z_PKG_UPDATE)pkgs[i].mark=1;
        first_load=0;
    }
    plan_dirty=1;ui.dirty=1;
}
static const char *shown_name(const struct pkg *p){return p->title[0]?p->title:same(p->name,"zenith-kernel")?"Zenith kernel":p->name;}
static const char *icon_of(const struct pkg *p){
    if(same(p->kind,"kernel"))return "monitor";
    if(p->icon[0])return p->icon;
    return p->programs?"app-window":"package";
}
static char *when_text(char *o,long when){
    if(when<=0)return put(o,"never");
    long ago=ov.now-when;
    if(ago<60)return put(o,"just now");
    if(ago<3600){o=num(o,(unsigned long)(ago/60));return put(o," min ago");}
    if(ago<86400){o=num(o,(unsigned long)(ago/3600));return put(o,ago<7200?" hour ago":" hours ago");}
    o=num(o,(unsigned long)(ago/86400));return put(o,ago<172800?" day ago":" days ago");
}
static char *date_text(char *o,long when){
    long local=when+ov.offset,today=(ov.now+ov.offset)/86400,day=local/86400;
    if(day==today)o=put(o,"Today ");else if(day==today-1)o=put(o,"Yesterday ");else{o=num(o,(unsigned long)(today-day));o=put(o," days ago ");}
    long s=local%86400;if(s<0)s+=86400;
    *o++=(char)('0'+s/36000);*o++=(char)('0'+s/3600%10);*o++=':';*o++=(char)('0'+s%3600/600);*o++=(char)('0'+s%600/60);*o=0;return o;
}

/* ---------- The plan. */
struct action { char verb[10],name[48],from[16],to[16]; long bytes; int also; };
static struct { struct action actions[40]; int n; char blockers[6][200]; int nblock; long download,disk,free_bytes; int restart; char commands[4][200]; int ncommands; int have; } plan;
static char plan_input[4096];
static int build_plan_input(void){
    char *o=plan_input;*o=0;int n=0;
    for(int i=0;i<npkgs;i++){
        struct pkg *p=&pkgs[i];if(!p->mark)continue;
        if((unsigned long)(o-plan_input)>sizeof(plan_input)-80)break;
        if(same(p->kind,"kernel")){o=put(o,"kernel\n");n++;continue;}
        o=put(o,p->mark==2?"remove ":p->state==Z_PKG_UPDATE?"upgrade ":"install ");o=put(o,p->name);o=put(o,"\n");n++;
    }
    return n;
}
static void on_plan(char **f,int n){
    if(same(f[0],"action")&&n>=8&&plan.n<40){struct action *a=&plan.actions[plan.n++];copy(a->verb,f[1],10);copy(a->name,f[2],48);copy(a->from,f[3],16);copy(a->to,f[4],16);a->bytes=to_long(f[6]);a->also=same(f[7],"1");}
    else if(same(f[0],"blocked")&&n>=2&&plan.nblock<6)copy(plan.blockers[plan.nblock++],f[1],200);
    else if(same(f[0],"sizes")&&n>=4){plan.download=to_long(f[1]);plan.disk=to_long(f[2]);plan.free_bytes=to_long(f[3]);}
    else if(same(f[0],"restart")&&n>=2)plan.restart=same(f[1],"1");
    else if(same(f[0],"command")&&n>=2&&plan.ncommands<4)copy(plan.commands[plan.ncommands++],f[1],200);
}
static char plan_error[300];
static void compute_plan(void){
    plan_dirty=0;plan=(typeof(plan)){0};plan_error[0]=0;
    int n=build_plan_input();
    if(!n){ui.dirty=1;return;}
    long r=read_op(Z_PKG_PLAN,0,plan_input,(unsigned int)length(plan_input),on_plan);
    if(r<0){if(r==-5)copy(plan_error,reply,sizeof(plan_error));else copy(plan_error,"The plan could not be made.",sizeof(plan_error));}
    else plan.have=1;
    char line[80],*o=put(line,"ORBIT_PLAN ");o=num(o,(unsigned long)plan.n);o=put(o," changes ");o=num(o,(unsigned long)plan.nblock);put(o," blocked\n");print(line);
    ui.dirty=1;
}
static int plan_changes(void){int n=0;for(int i=0;i<npkgs;i++)if(pkgs[i].mark)n++;return n;}

/* ---------- Pages. */
enum { PAGE_OVERVIEW,PAGE_UPDATES,PAGE_INSTALLED,PAGE_BROWSE,PAGE_HISTORY,PAGE_SOURCES,PAGES,PAGE_DETAILS=PAGES,PAGE_PLAN };
static const char *page_names[PAGES+2]={"Overview","Updates","Installed","Browse","History","Sources","Details","Plan"};
static const char *page_icons[PAGES]={"house","download","package","search","clock","list"};
static int page,back_page=PAGE_BROWSE;
enum { ID_NAV=10,ID_CHECK=30,ID_REVIEW=31,ID_APPLY=32,ID_COMMAND=33,ID_CANCEL_JOB=34,ID_BACK=35,ID_TOGGLE_PLAN=36,ID_FETCH=37,ID_SEARCH=38,
       ID_PLAN_PAGE=39,ID_CLEAR=40,ID_MARK=100,ID_ROW=300 };
static struct zui_scroll page_scroll,plan_scroll;
static void go(int p){if(p==page)return;page=p;zui_scroll_jump(&page_scroll,0);char line[48],*o=put(line,"ORBIT_PAGE ");o=put(o,page_names[p]);put(o,"\n");print(line);ui.dirty=1;}

/* ---------- Jobs: refresh and apply run in the kernel; the window polls. */
static struct { long id; char kind[10]; int state,mine,restart; long done,total,changes; char step[200],message[300]; } job;
static long job_id;
static char note[300];static int note_bad;
static void say(const char *s,int is_bad){copy(note,s,sizeof(note));note_bad=is_bad;ui.dirty=1;}
static void on_job(char **f,int n){
    if(same(f[0],"job")&&n>=3){job.id=to_long(f[1]);copy(job.kind,f[2],10);if(n>=9){job.state=(int)to_long(f[3]);job.mine=same(f[4],"1");job.done=to_long(f[5]);job.total=to_long(f[6]);job.restart=same(f[7],"1");job.changes=to_long(f[8]);}else job.state=0;}
    else if(same(f[0],"step")&&n>=2)copy(job.step,f[1],200);
    else if(same(f[0],"message")&&n>=2)copy(job.message,f[1],300);
}
static void poll_job(void){
    if(job_id<=0)return;
    job=(typeof(job)){0};read_op(Z_PKG_JOB,0,0,0,on_job);
    if(job.id!=job_id){job_id=0;return;}
    ui.dirty=1;
    if(job.state==1)return;
    char line[400],*o=put(line,"ORBIT_JOB ");o=put(o,job.kind);o=put(o,job.state==2?" done ":job.state==4?" cancelled ":" failed ");o=put(o,job.message);put(o,"\n");print(line);
    say(job.message[0]?job.message:job.state==2?"Done.":"That did not work.",job.state!=2);
    int applied=same(job.kind,"apply")&&job.state==2;
    job_id=0;
    if(applied){for(int i=0;i<npkgs;i++)pkgs[i].mark=0;nprevious=0;}
    load();
}
static int pending_apply;static long auth_handle;static int granted;
static void start_apply(void);
static void ask_permission(void){
    if(auth_handle>0)return;
    long h=zenith_authorize("Authorize",Z_ACTION_PACKAGES);
    if(h<0){say(h==-38?"This Zenith cannot ask for permission. Run Orbit as root to apply.":"Changing software needs an administrator.",1);return;}
    auth_handle=h;pending_apply=1;print("ORBIT_AUTHORIZE asking\n");say("Waiting for an administrator to allow the change...",0);
}
static void poll_permission(void){
    if(auth_handle<=0)return;
    static struct zenith_portal_response r;long s=zenith_portal_poll(auth_handle,&r);
    if(s<0){auth_handle=0;return;}
    if(!Z_PORTAL_FINAL(r.state))return;
    zenith_portal_close(auth_handle);auth_handle=0;
    if(r.state==Z_PORTAL_APPROVED){granted=1;print("ORBIT_AUTHORIZE granted\n");pending_apply=0;start_apply();}
    else{pending_apply=0;print("ORBIT_AUTHORIZE denied\n");say("The change was not allowed.",1);}
}
static void start_job(unsigned int op){
    char message[300]={0};
    long r=zenith_packages(op,0,message,sizeof(message)-1,op==Z_PKG_APPLY?plan_input:0,op==Z_PKG_APPLY?(unsigned int)length(plan_input):0);
    if(r==-13&&!granted&&op==Z_PKG_APPLY){ask_permission();return;}
    if(r==-16){say("Another package change or update is running. Try again when it has finished.",1);return;}
    if(r<0){say(message[0]?message:r==-38?"This Zenith kernel cannot change software from programs (it needs 0.0.70).":"That did not work.",1);return;}
    job_id=r;job=(typeof(job)){0};job.state=1;copy(job.kind,op==Z_PKG_APPLY?"apply":"refresh",10);
    print(op==Z_PKG_APPLY?"ORBIT_APPLY start\n":"ORBIT_REFRESH start\n");note[0]=0;ui.dirty=1;
}
static void start_apply(void){
    if(job_id>0)return;
    if(plan_dirty)compute_plan();
    if(!build_plan_input()||!plan.have||plan.nblock)return;
    start_job(Z_PKG_APPLY);
}
static void start_refresh(void){if(job_id<=0)start_job(Z_PKG_REFRESH);}

/* ---------- Details of one package. */
struct can { char title[48],detail[96]; int allowed; };
struct mine { long time; char words[120],user[32],source[16]; };
static struct { char name[48],installed[16],candidate[16],latest[16],repo[8],reason[200],title[40],summary[96],description[1000],category[16],icon[24],replaces[200];
                int state,noabout_download,have,fetched; long bytes,payload;
                struct can can[16]; int ncan; char needs[16][96]; int nneeds; char conflicts[8][48]; int nconflicts;
                char files[64][96]; long sizes[64]; int nfiles; char changes[16][2][160]; int nchanges; char planned[4][160]; int nplanned;
                struct mine mine[12]; int nmine; char digest[72],signed_by[72]; } d;
static void on_details(char **f,int n){
    if(same(f[0],"state")&&n>=9){d.state=(int)to_long(f[1]);copy(d.installed,f[2],16);copy(d.candidate,f[3],16);copy(d.latest,f[4],16);copy(d.repo,f[5],8);d.bytes=to_long(f[6]);d.payload=to_long(f[7]);copy(d.reason,f[8],200);}
    else if(same(f[0],"title")&&n>=2)copy(d.title,f[1],40);
    else if(same(f[0],"summary")&&n>=2)copy(d.summary,f[1],96);
    else if(same(f[0],"description")&&n>=2)copy(d.description,f[1],1000);
    else if(same(f[0],"category")&&n>=2)copy(d.category,f[1],16);
    else if(same(f[0],"icon")&&n>=2)copy(d.icon,f[1],24);
    else if(same(f[0],"replaces")&&n>=2)copy(d.replaces,f[1],200);
    else if(same(f[0],"noabout")&&n>=2)d.noabout_download=same(f[1],"download");
    else if(same(f[0],"can")&&n>=4&&d.ncan<16){copy(d.can[d.ncan].title,f[1],48);copy(d.can[d.ncan].detail,f[2],96);d.can[d.ncan].allowed=same(f[3],"1");d.ncan++;}
    else if(same(f[0],"needs")&&n>=2&&d.nneeds<16)copy(d.needs[d.nneeds++],f[1],96);
    else if(same(f[0],"conflicts")&&n>=2&&d.nconflicts<8)copy(d.conflicts[d.nconflicts++],f[1],48);
    else if(same(f[0],"file")&&n>=3&&d.nfiles<64){copy(d.files[d.nfiles],f[1],96);d.sizes[d.nfiles++]=to_long(f[2]);}
    else if(same(f[0],"change")&&n>=3&&d.nchanges<16){copy(d.changes[d.nchanges][0],f[1],160);copy(d.changes[d.nchanges][1],f[2],160);d.nchanges++;}
    else if(same(f[0],"planned")&&n>=2&&d.nplanned<4)copy(d.planned[d.nplanned++],f[1],160);
    else if(same(f[0],"mine")&&n>=5&&d.nmine<12){struct mine *m=&d.mine[d.nmine++];m->time=to_long(f[1]);copy(m->words,f[2],120);copy(m->user,f[3],32);copy(m->source,f[4],16);}
    else if(same(f[0],"digest")&&n>=2)copy(d.digest,f[1],72);
    else if(same(f[0],"signed")&&n>=2)copy(d.signed_by,f[1],72);
    else if(same(f[0],"fetched"))d.fetched=1;
}
static void open_details(const char *name,int fetch){
    char keep[48];copy(keep,name,48);
    d=(typeof(d)){0};copy(d.name,keep,48);
    long r=read_op(Z_PKG_DETAILS,fetch?1:0,keep,(unsigned int)length(keep),on_details);
    d.have=r>0;
    if(r==-16)say("Another package change is running; the description can be read when it has finished.",1);
    if(page!=PAGE_DETAILS){back_page=page;go(PAGE_DETAILS);}
    char line[160],*o=put(line,"ORBIT_DETAILS ");o=put(o,keep);o=put(o," can=");o=num(o,(unsigned long)d.ncan);o=put(o," files=");o=num(o,(unsigned long)d.nfiles);
    o=put(o,d.title[0]?" about":" no-about");put(o,"\n");print(line);
    zui_scroll_jump(&page_scroll,0);ui.dirty=1;
}
static struct pkg *find(const char *name){for(int i=0;i<npkgs;i++)if(same(pkgs[i].name,name))return &pkgs[i];return 0;}

/* ---------- History. */
struct transaction { long time; char user[32],source[16],command[200],message[200]; int ok,nchanges; char changes[6][120]; };
static struct transaction history[64];static int nhistory;
static void on_history(char **f,int n){
    if(same(f[0],"transaction")&&n>=8&&nhistory<64){struct transaction *t=&history[nhistory++];*t=(struct transaction){0};t->time=to_long(f[1]);copy(t->user,f[3],32);copy(t->source,f[4],16);t->ok=same(f[5],"1");copy(t->command,f[6],200);copy(t->message,f[7],200);}
    else if(same(f[0],"change")&&n>=2&&nhistory>0){struct transaction *t=&history[nhistory-1];if(t->nchanges<6)copy(t->changes[t->nchanges++],f[1],120);else t->nchanges++;}
}
static void load_history(void){nhistory=0;read_op(Z_PKG_HISTORY,0,0,0,on_history);ui.dirty=1;}

/* ---------- Drawing. Rows register their controls first and paint their words after the controls (a hovered
 * row's highlight is a control). */
struct row_paint { struct zui_rect r; int index,kind; };
static struct row_paint rows[96];static int nrows;
static struct zui_rect rows_clip;
static struct { int x,y; } checks[96];static int nchecks;
static int search_focus;static char search_text[48];static struct zui_input search_input;static struct zui_rect search_rect;
static int show_command;
static void appearance(void){
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
static void checkbox(int x,int y,int index,int enabled){
    struct pkg *p=&pkgs[index];
    zui_add(&ui,ID_MARK+index,ZUI_BUTTON,(struct zui_rect){x,y,26,26},"",p->mark!=0,enabled);
    if(p->mark&&nchecks<96){checks[nchecks].x=x+5;checks[nchecks].y=y+5;nchecks++;}
}
static char *versions(char *o,const struct pkg *p){
    if(p->state==Z_PKG_UPDATE){o=put(o,p->installed[0]?p->installed:"?");o=put(o," -> ");return put(o,p->candidate);}
    if(p->state==Z_PKG_AVAILABLE){o=put(o,"New  ");return put(o,p->candidate);}
    if(p->installed[0])return put(o,p->installed);
    return put(o,p->latest);
}
/* One package row: [checkbox] icon, name and summary, versions and a badge. kind 0 updates, 1 installed, 2 browse. */
static void package_row(int x,int y,int w,int index,int kind){
    struct zui_rect r={x,y,w,64};
    int left=kind==2?0:40;
    if(kind!=2)checkbox(x+14,y+19,index,job_id<=0);
    if(zui_add(&ui,ID_ROW+index,ZUI_NAVIGATION,(struct zui_rect){x+left+6,y+4,w-left-12,56},"",0,1))ui.controls[ui.count-1].no_chevron=1;
    if(nrows<96){rows[nrows].r=r;rows[nrows].index=index;rows[nrows].kind=kind;nrows++;}
}
static void paint_row(const struct row_paint *rp){
    struct pkg *p=&pkgs[rp->index];int x=rp->r.x,y=rp->r.y,w=rp->r.w,left=rp->kind==2?0:40;
    zui_round_outline(&ui,(struct zui_rect){x,y,w,60},10,ui.theme.border);
    zui_icon(&ui,icon_of(p),x+left+14,y+18,24,ZUI_ICON_NORMAL);
    char v[64];versions(v,p);
    int vw=measure(v,&zui_caption);
    fit(x+left+50,y+10,shown_name(p),w-left-70-vw-20,&zui_label,ui.theme.text);
    const char *sub=p->summary[0]?p->summary:p->name;
    if(p->mark==2)sub="Will be removed";
    fit(x+left+50,y+33,sub,w-left-70-110,&zui_caption,p->mark==2?bad():ui.theme.secondary);
    text_right(x+w-16,y+12,v,&zui_caption,ui.theme.secondary);
    const char *badge=0;unsigned int color=ui.theme.secondary;
    if(same(p->kind,"kernel")&&p->state==Z_PKG_UPDATE){badge="Restart";color=warn();}
    else if(p->state==Z_PKG_NOT_OFFERED){badge="Not offered";color=warn();}
    else if(rp->kind==2&&p->state==Z_PKG_UPDATE){badge="Update";color=ui.theme.accent;}
    else if(rp->kind==2&&p->installed[0]){badge="Installed";color=good();}
    else if(p->state==Z_PKG_LOCAL){badge="Local";}
    if(p->mark==1&&rp->kind!=0&&!badge){badge="In plan";color=ui.theme.accent;}
    if(badge){int bw=measure(badge,&zui_caption)+16;chip(x+w-16-bw,y+34,badge,color);}
}
static void begin_rows(struct zui_rect clip){nrows=0;nchecks=0;rows_clip=clip;}
static void paint_rows(void){
    zui_clip(&ui,rows_clip);
    for(int i=0;i<nrows;i++)paint_row(&rows[i]);
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});
    /* A tick drawn here, so the box reads as chosen even without the icon package. */
    for(int i=0;i<nchecks;i++){
        int x=checks[i].x,y=checks[i].y;
        for(int k=0;k<5;k++)zui_fill(&ui,(struct zui_rect){x+2+k,y+7+k,2,2},ui.theme.on_accent);
        for(int k=0;k<9;k++)zui_fill(&ui,(struct zui_rect){x+6+k,y+11-k,2,2},ui.theme.on_accent);
    }
}

static void heading(int x,int y,int w,const char *title,const char *sub){
    text(x,y,title,&zui_heading,ui.theme.text);
    if(sub)fit(x,y+38,sub,w,&zui_caption,ui.theme.secondary);
}
static char *checked_line(char *o){
    long checked=0,generation=0;const char *name="";
    for(int i=0;i<nsources;i++)if(sources[i].checked>checked){checked=sources[i].checked;generation=sources[i].generation;name=sources[i].name;}
    if(!checked)return put(o,"The package lists have not been downloaded yet");
    o=put(o,"Checked ");o=when_text(o,checked);o=put(o," from ");o=put(o,name);o=put(o," - signed, index generation ");return num(o,(unsigned long)generation);
}
static void notice(int x,int *y,int w,const char *message,int is_bad){
    struct zui_rect r={x,*y,w,52};zui_round(&ui,r,10,ui.theme.subtle);
    zui_icon(&ui,is_bad?"triangle-alert":"info",x+14,*y+16,20,ZUI_ICON_NORMAL);
    fit(x+44,*y+16,message,w-60,&zui_body,is_bad?bad():ui.theme.text);*y+=62;
}
static void draw_overview(struct zui_rect c){
    int x=c.x+16,w=c.w-32;int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+16;
    char t[200];
    int narrow=w<620;   /* the buttons get their own row, so the headline keeps the width */
    struct zui_rect hero={x,y,w,narrow?156:112};panel(hero);
    const char *headline=!ov.have?"Software":ov.updates==0?"Everything is up to date":0;
    char h[64];if(!headline){char *o=num(h,(unsigned long)ov.updates);put(o,ov.updates==1?" update is ready":" updates are ready");headline=h;}
    zui_round(&ui,(struct zui_rect){x+18,y+24,14,14},7,ov.updates?ui.theme.accent:good());
    fit(x+44,y+14,headline,w-60-(narrow?0:300),&zui_heading,ui.theme.text);
    checked_line(t);fit(x+44,y+54,t,w-60,&zui_caption,ui.theme.secondary);
    if(ov.pending)fit(x+44,y+78,"A new kernel is waiting for a restart.",w-60,&zui_caption,warn());
    int by=narrow?y+106:y+14;
    zui_add(&ui,ID_CHECK,ZUI_BUTTON,(struct zui_rect){x+w-292,by,150,34},job_id>0&&same(job.kind,"refresh")?"Checking...":"Check for updates",0,job_id<=0);
    zui_add(&ui,ID_REVIEW,ZUI_BUTTON,(struct zui_rect){x+w-134,by,118,34},"Review",1,ov.updates>0);
    y+=hero.h+12;
    if(ov.error[0])notice(x,&y,w,ov.error,1);
    if(note[0]&&page==PAGE_OVERVIEW)notice(x,&y,w,note,note_bad);
    /* Four facts. */
    int cw=(w-36)/4,ch=96;if(cw<150){cw=(w-12)/2;}
    const char *labels[4]={"Installed","Kernel","Signed sources","Free space"};
    for(int i=0;i<4;i++){
        int col=cw*4+36<=w?i:i%2,line=cw*4+36<=w?0:i/2;
        struct zui_rect r={x+col*(cw+12),y+line*(ch+12),cw,ch};panel(r);
        text(r.x+16,r.y+14,labels[i],&zui_caption,ui.theme.secondary);
        char *o=t;*o=0;const char *small="";
        if(i==0){o=num(t,(unsigned long)ov.installed);put(o," packages");small=ov.available?"more in Browse":"";}
        else if(i==1){copy(t,same(ov.running,"0.0.0")?"from the installer":ov.running[0]?ov.running:"unknown",sizeof(t));small=ov.pending?"restart pending":"running";}
        else if(i==2){o=num(t,(unsigned long)nsources);put(o,nsources==1?" source":" sources");small=ov.key[0]?"key pinned":"no key yet";}
        else{size_text(t,ov.free_bytes);small="on the system disk";}
        fit(r.x+16,r.y+38,t,r.w-32,&zui_heading,ui.theme.text);
        fit(r.x+16,r.y+70,small,r.w-32,&zui_caption,ui.theme.secondary);
    }
    y+=cw*4+36<=w?ch+12:2*(ch+12);
    if(!nhistory)load_history();
    struct zui_rect r={x,y,w,48+(nhistory?(nhistory<4?nhistory:4):1)*44};panel(r);
    text(x+16,y+14,"Recent changes",&zui_label,ui.theme.text);
    int ry=y+48;
    for(int i=0;i<nhistory&&i<4;i++){
        struct transaction *tr=&history[i];char line[200],*o=date_text(line,tr->time);o=put(o,"  -  ");o=put(o,tr->source);o=put(o,", ");put(o,tr->user);
        fit(x+16,ry,tr->nchanges?tr->changes[0]:tr->message,w-32,&zui_body,tr->ok?ui.theme.text:bad());
        fit(x+16,ry+20,line,w-32,&zui_caption,ui.theme.secondary);ry+=44;
    }
    if(!nhistory)text(x+16,ry,"No package changes yet.",&zui_body,ui.theme.secondary);
    y+=r.h+12;
    zui_scroll_end(&ui,&page_scroll,y-top);
}
static void draw_updates(struct zui_rect c){
    int x=c.x+16,w=c.w-32;int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+16;
    char t[200];checked_line(t);heading(x,y,w-160,"Updates",t);
    zui_add(&ui,ID_CHECK,ZUI_BUTTON,(struct zui_rect){x+w-140,y+4,140,34},job_id>0&&same(job.kind,"refresh")?"Checking...":"Check now",0,job_id<=0);
    y+=70;
    if(note[0])notice(x,&y,w,note,note_bad);
    else if(list_error[0])notice(x,&y,w,list_error,1);
    begin_rows(c);
    int any=0;
    for(int i=0;i<npkgs;i++)if(pkgs[i].state==Z_PKG_UPDATE){package_row(x,y,w,i,0);y+=68;any=1;}
    if(!any){panel((struct zui_rect){x,y,w,64});zui_icon(&ui,"check",x+18,y+20,24,ZUI_ICON_NORMAL);text(x+52,y+22,"Everything is up to date.",&zui_body,ui.theme.text);y+=76;}
    int header=0;
    for(int i=0;i<npkgs;i++){
        struct pkg *p=&pkgs[i];
        if(p->state!=Z_PKG_NOT_OFFERED&&!(p->reason[0]&&p->installed[0]))continue;
        if(!header){y+=8;text(x,y,"Not offered",&zui_label,ui.theme.text);y+=28;header=1;}
        struct zui_rect r={x,y,w,58};zui_round(&ui,r,10,light()?0xFBF1DC:0x3A3222);
        zui_icon(&ui,"triangle-alert",x+14,y+18,20,ZUI_ICON_NORMAL);
        char line[240],*o=put(line,shown_name(p));o=put(o," ");put(o,p->reason);
        fit(x+44,y+10,line,w-60,&zui_body,ui.theme.text);
        fit(x+44,y+32,"Orbit shows why instead of leaving it out.",w-60,&zui_caption,ui.theme.secondary);y+=66;
    }
    zui_scroll_end(&ui,&page_scroll,y-top);
}
static void draw_installed(struct zui_rect c){
    int x=c.x+16,w=c.w-32;int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+16;
    char t[120],*o=num(t,(unsigned long)ov.installed);put(o," packages - tick one to remove it with the plan");
    heading(x,y,w,"Installed",t);y+=70;
    if(note[0])notice(x,&y,w,note,note_bad);
    begin_rows(c);
    for(int i=0;i<npkgs;i++){
        if(!pkgs[i].installed[0]||same(pkgs[i].kind,"kernel"))continue;
        /* Here the box means "remove". */
        package_row(x,y,w,i,1);y+=68;
    }
    zui_scroll_end(&ui,&page_scroll,y-top);
}
static void draw_browse(struct zui_rect c){
    int x=c.x+16,w=c.w-32;int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+16;
    heading(x,y,w,"Browse","Every package the sources offer, and what is installed");y+=70;
    search_rect=(struct zui_rect){x,y,w,36};
    zui_round(&ui,search_rect,8,ui.theme.surface);zui_round_outline(&ui,search_rect,8,search_focus?ui.theme.accent:ui.theme.border);
    zui_icon(&ui,"search",x+10,y+10,16,ZUI_ICON_TEXT);
    if(search_input.length||search_focus)zui_input_draw(&ui,(struct zui_rect){x+34,y+1,w-44,34},&search_input,search_focus);
    else text(x+36,y+9,"Search by name or description (Ctrl+F)",&zui_body,ui.theme.secondary);
    y+=48;
    begin_rows(c);
    for(int pass=0;pass<2;pass++)for(int i=0;i<npkgs;i++){
        struct pkg *p=&pkgs[i];if(same(p->kind,"kernel"))continue;
        int offered=p->state==Z_PKG_AVAILABLE||p->state==Z_PKG_NOT_OFFERED;
        if((pass==0)!=offered)continue;
        if(search_text[0]&&!contains(p->name,search_text)&&!contains(p->title,search_text)&&!contains(p->summary,search_text))continue;
        if(pass==0&&y==search_rect.y+48){text(x,y,"Not installed",&zui_label,ui.theme.text);y+=28;}
        package_row(x,y,w,i,2);y+=68;
    }
    zui_scroll_end(&ui,&page_scroll,y-top);
}
static void card_title(struct zui_rect r,const char *title,const char *sub){
    panel(r);text(r.x+16,r.y+14,title,&zui_label,ui.theme.text);
    if(sub)fit(r.x+16,r.y+36,sub,r.w-32,&zui_caption,ui.theme.secondary);
}
static int paragraph_height(const char *s,int w){
    /* A rough wrap estimate: average glyph width of the body font. */
    int per=w/7;if(per<10)per=10;int n=(int)length(s);return ((n+per-1)/per)*20;
}
static void draw_details(struct zui_rect c){
    int x=c.x+16,w=c.w-32;int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+12;
    struct pkg *p=find(d.name);
    zui_add(&ui,ID_BACK,ZUI_BUTTON,(struct zui_rect){x,y,110,30},page_names[back_page],0,1);y+=44;
    zui_round(&ui,(struct zui_rect){x,y,72,72},14,ui.theme.subtle);
    zui_icon(&ui,d.icon[0]?d.icon:p?icon_of(p):"package",x+20,y+20,32,ZUI_ICON_NORMAL);
    fit(x+88,y+2,d.title[0]?d.title:p?shown_name(p):d.name,w-88-196,&zui_heading,ui.theme.text);
    char t[240],*o=put(t,d.summary[0]?d.summary:d.name);if(d.summary[0]){o=put(o,", by the Zenith project");}
    fit(x+88,y+38,t,w-88-196,&zui_caption,ui.theme.secondary);
    int cx=x+88;
    if(d.signed_by[0])cx+=chip(cx,y+62,"Signed",good())+6;
    if(d.repo[0])cx+=chip(cx,y+62,d.repo,ui.theme.secondary)+6;
    if(d.payload){o=size_text(t,d.payload);put(o," installed");cx+=chip(cx,y+62,t,ui.theme.secondary)+6;}
    /* The action: into the plan, out of it, or remove. */
    const char *label=0;int enabled=job_id<=0;
    if(p){
        if(p->mark)label="Remove from plan";
        else if(p->state==Z_PKG_UPDATE)label="Add update to plan";
        else if(p->state==Z_PKG_AVAILABLE)label="Add to plan";
        else if(p->installed[0]&&!same(p->kind,"kernel"))label="Remove with plan";
        else if(p->state==Z_PKG_NOT_OFFERED){label="Not offered";enabled=0;}
    }
    if(label)zui_add(&ui,ID_MARK+(int)(p-pkgs),ZUI_BUTTON,(struct zui_rect){x+w-180,y+4,180,36},label,p&&!p->mark&&p->state!=Z_PKG_CURRENT&&p->state!=Z_PKG_LOCAL,enabled);
    o=put(t,"Installed: ");o=put(o,d.installed[0]?d.installed:"no");o=put(o,"  -  latest ");put(o,d.latest[0]?d.latest:"not offered");
    text_right(x+w,y+44,t,&zui_caption,ui.theme.secondary);
    y+=88;
    if(d.reason[0]){notice(x,&y,w,d.reason,1);}
    if(note[0]&&page==PAGE_DETAILS)notice(x,&y,w,note,note_bad);
    if(d.description[0]){int h=paragraph_height(d.description,w)+8;zui_paragraph(&ui,(struct zui_rect){x,y,w,h},d.description,&zui_body,ui.theme.text,0);y+=h+8;}
    else if(d.noabout_download){
        struct zui_rect r={x,y,w,52};zui_round(&ui,r,10,ui.theme.subtle);
        fit(x+16,y+16,"Its own description comes with the package.",w-200,&zui_body,ui.theme.text);
        zui_add(&ui,ID_FETCH,ZUI_BUTTON,(struct zui_rect){x+w-170,y+9,160,34},"Read description",0,1);y+=62;
    }
    int three=w>=760,cw=three?(w-24)/3:w,ch=0;
    /* What it can do. */
    int h1=60+d.ncan*48;int h2=180+(d.nfiles<12?d.nfiles:12)*20;int h3=110+d.nchanges*44+d.nplanned*44+d.nmine*40+80;
    ch=three?(h1>h2?h1:h2)>h3?(h1>h2?h1:h2):h3:0;
    struct zui_rect a={x,y,cw,three?ch:h1};card_title(a,"What it can do","Read from the package, in plain words");
    int ry=a.y+64;
    for(int i=0;i<d.ncan;i++){
        zui_icon(&ui,d.can[i].allowed?"check":"x",a.x+16,ry+4,16,d.can[i].allowed?ZUI_ICON_NORMAL:ZUI_ICON_DISABLED);
        fit(a.x+44,ry,d.can[i].title,a.w-60,&zui_label,ui.theme.text);
        fit(a.x+44,ry+20,d.can[i].detail,a.w-60,&zui_caption,ui.theme.secondary);ry+=48;
    }
    if(!three)y+=a.h+12;
    struct zui_rect b={three?x+cw+12:x,y,cw,three?ch:h2};card_title(b,"What installing changes",0);
    ry=b.y+46;
    int lw=90;
    text(b.x+16,ry,"Needs",&zui_caption,ui.theme.secondary);
    o=t;*o=0;int shown=0;for(int i=0;i<d.nneeds;i++){if(starts(d.needs[i],"kernel interface "))continue;if(shown++)o=put(o,", ");o=put(o,d.needs[i]);if(o-t>180)break;}
    int interfaces=0;for(int i=0;i<d.nneeds;i++)if(starts(d.needs[i],"kernel interface "))interfaces++;
    if(interfaces){if(shown)o=put(o,", ");o=num(o,(unsigned long)interfaces);put(o,interfaces==1?" kernel interface":" kernel interfaces");}
    fit(b.x+16+lw,ry,t[0]?t:"nothing",b.w-32-lw,&zui_caption,ui.theme.text);ry+=22;
    text(b.x+16,ry,"Conflicts",&zui_caption,ui.theme.secondary);
    o=t;*o=0;for(int i=0;i<d.nconflicts;i++){if(i)o=put(o,", ");o=put(o,d.conflicts[i]);}
    fit(b.x+16+lw,ry,t[0]?t:"none",b.w-32-lw,&zui_caption,ui.theme.text);ry+=22;
    text(b.x+16,ry,"Disk",&zui_caption,ui.theme.secondary);size_text(t,d.payload);fit(b.x+16+lw,ry,t,b.w-32-lw,&zui_caption,ui.theme.text);ry+=22;
    if(d.replaces[0]){text(b.x+16,ry,"Replaces",&zui_caption,ui.theme.secondary);fit(b.x+16+lw,ry,d.replaces,b.w-32-lw,&zui_caption,ui.theme.text);ry+=22;}
    zui_fill(&ui,(struct zui_rect){b.x+16,ry+4,b.w-32,1},ui.theme.border);ry+=14;
    text(b.x+16,ry,"Files",&zui_label,ui.theme.text);ry+=24;
    for(int i=0;i<d.nfiles&&i<12;i++){fit(b.x+16,ry,d.files[i],b.w-32,&zui_caption,ui.theme.secondary);ry+=20;}
    if(d.nfiles>12){o=put(t,"and ");o=num(o,(unsigned long)(d.nfiles-12));put(o," more");text(b.x+16,ry,t,&zui_caption,ui.theme.secondary);ry+=20;}
    if(d.digest[0]&&!same(d.digest,"-")){o=put(t,"SHA256 ");for(int i=0;i<4&&d.digest[i];i++)*o++=d.digest[i];o=put(o,"...");unsigned long n=length(d.digest);if(n>4)o=put(o,d.digest+n-4);
        text(b.x+16,b.y+b.h-28,t,&zui_caption,ui.theme.secondary);}
    if(!three)y+=b.h+12;
    struct zui_rect hh={three?x+2*(cw+12):x,y,three?w-2*(cw+12):w,three?ch:h3};card_title(hh,"History",0);
    ry=hh.y+46;
    for(int i=0;i<d.nchanges;i++){
        zui_round(&ui,(struct zui_rect){hh.x+18,ry+6,8,8},4,i==0?ui.theme.accent:ui.theme.border);
        fit(hh.x+36,ry,d.changes[i][0],hh.w-52,&zui_label,ui.theme.text);
        fit(hh.x+36,ry+20,d.changes[i][1],hh.w-52,&zui_caption,ui.theme.secondary);ry+=44;
    }
    for(int i=0;i<d.nplanned;i++){
        zui_round(&ui,(struct zui_rect){hh.x+18,ry+6,8,8},4,ui.theme.border);
        fit(hh.x+36,ry,"Next - planned",hh.w-52,&zui_label,ui.theme.secondary);
        fit(hh.x+36,ry+20,d.planned[i],hh.w-52,&zui_caption,ui.theme.secondary);ry+=44;
    }
    if(!d.nchanges&&!d.nplanned){text(hh.x+16,ry,"The package lists no changes.",&zui_caption,ui.theme.secondary);ry+=28;}
    zui_fill(&ui,(struct zui_rect){hh.x+16,ry+4,hh.w-32,1},ui.theme.border);ry+=16;
    text(hh.x+16,ry,"Your history here",&zui_label,ui.theme.text);ry+=24;
    for(int i=0;i<d.nmine;i++){
        o=date_text(t,d.mine[i].time);o=put(o,", ");o=put(o,d.mine[i].source);o=put(o," (");o=put(o,d.mine[i].user);put(o,")");
        fit(hh.x+16,ry,d.mine[i].words,hh.w-32,&zui_caption,ui.theme.text);fit(hh.x+16,ry+18,t,hh.w-32,&zui_caption,ui.theme.secondary);ry+=40;
    }
    if(!d.nmine){fit(hh.x+16,ry,d.installed[0]?"Installed before the history began":"Never installed",hh.w-32,&zui_caption,ui.theme.secondary);ry+=24;}
    y+=three?ch+12:hh.h+12;
    zui_scroll_end(&ui,&page_scroll,y-top);
}
static void draw_history(struct zui_rect c){
    int x=c.x+16,w=c.w-32;int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+16;
    heading(x,y,w,"History","Every package change, whoever made it: Orbit, the shell or the installer");y+=70;
    for(int i=0;i<nhistory;i++){
        struct transaction *t=&history[i];
        int shown=t->nchanges<6?t->nchanges:6;
        int h=70+shown*22+(t->command[0]?22:0)+(t->message[0]?22:0);
        struct zui_rect r={x,y,w,h};panel(r);
        char line[240],*o=date_text(line,t->time);o=put(o,"  -  ");o=put(o,t->source);o=put(o," (");o=put(o,t->user);put(o,")");
        zui_round(&ui,(struct zui_rect){x+18,y+20,10,10},5,t->ok?good():bad());
        fit(x+38,y+14,t->ok?"Completed":"Failed, nothing changed",w-60-260,&zui_label,ui.theme.text);
        text_right(x+w-16,y+16,line,&zui_caption,ui.theme.secondary);
        int ry=y+44;
        for(int k=0;k<shown;k++){fit(x+38,ry,t->changes[k],w-60,&zui_body,ui.theme.text);ry+=22;}
        if(t->nchanges>6){o=put(line,"and ");o=num(o,(unsigned long)(t->nchanges-6));put(o," more");text(x+38,ry,line,&zui_caption,ui.theme.secondary);}
        if(t->message[0]){fit(x+38,ry,t->message,w-60,&zui_caption,t->ok?ui.theme.secondary:bad());ry+=22;}
        if(t->command[0]){o=put(line,"$ ");put(o,t->command);fit(x+38,ry,line,w-60,&zui_caption,ui.theme.secondary);}
        y+=h+10;
    }
    if(!nhistory){panel((struct zui_rect){x,y,w,60});text(x+16,y+20,"No package changes have been recorded yet.",&zui_body,ui.theme.secondary);y+=72;}
    zui_scroll_end(&ui,&page_scroll,y-top);
}
static void draw_sources(struct zui_rect c){
    int x=c.x+16,w=c.w-32;int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+16;
    heading(x,y,w-160,"Sources","Where packages come from, and the key they must be signed with");
    zui_add(&ui,ID_CHECK,ZUI_BUTTON,(struct zui_rect){x+w-140,y+4,140,34},job_id>0&&same(job.kind,"refresh")?"Checking...":"Check now",0,job_id<=0);
    y+=70;
    if(note[0])notice(x,&y,w,note,note_bad);
    char t[240],*o;
    for(int i=0;i<nsources;i++){
        struct source *s=&sources[i];struct zui_rect r={x,y,w,150};panel(r);
        zui_icon(&ui,"list",x+16,y+16,20,ZUI_ICON_NORMAL);
        o=put(t,same(s->name,"core")?"Zenith Core":"Zenith Extra");text(x+46,y+14,t,&zui_label,ui.theme.text);
        o=put(t,"priority ");num(o,(unsigned long)s->priority);text_right(x+w-16,y+16,t,&zui_caption,ui.theme.secondary);
        int ry=y+48;
        text(x+16,ry,"Mirror",&zui_caption,ui.theme.secondary);fit(x+130,ry,s->mirror[0]?s->mirror:"not set",w-150,&zui_caption,ui.theme.text);ry+=22;
        text(x+16,ry,"Index",&zui_caption,ui.theme.secondary);
        if(s->generation>0){o=put(t,"generation ");o=num(o,(unsigned long)s->generation);o=put(o,", ");o=num(o,(unsigned long)s->entries);o=put(o," entries, signed");}else put(t,"not downloaded yet");
        fit(x+130,ry,t,w-150,&zui_caption,ui.theme.text);ry+=22;
        text(x+16,ry,"Checked",&zui_caption,ui.theme.secondary);when_text(t,s->checked);fit(x+130,ry,t,w-150,&zui_caption,ui.theme.text);ry+=22;
        text(x+16,ry,"Valid until",&zui_caption,ui.theme.secondary);
        if(s->expires>0){long left=s->expires-ov.now;o=t;if(left<=0)o=put(o,"expired - check for updates");else{o=put(o,"for ");o=num(o,(unsigned long)(left/86400));o=put(o," more days");}}else put(t,"-");
        fit(x+130,ry,t,w-150,&zui_caption,s->expires>0&&s->expires<=ov.now?bad():ui.theme.text);
        y+=162;
    }
    struct zui_rect k={x,y,w,138};panel(k);
    zui_icon(&ui,"shield",x+16,y+16,20,ZUI_ICON_NORMAL);text(x+46,y+14,"Signing key",&zui_label,ui.theme.text);
    o=t;if(ov.key[0]){o=put(o,"SHA256 ");put(o,ov.key);}else put(o,"No key is trusted yet; an administrator pins one with pkg trust FILE SHA256.");
    fit(x+16,y+50,t,w-32,&zui_caption,ui.theme.text);
    fit(x+16,y+74,"Every package list and package must carry this key's signature; older lists are refused.",w-32,&zui_caption,ui.theme.secondary);
    fit(x+16,y+98,cache_auto?"Downloaded packages are cleaned up automatically after each change.":"Automatic cleanup of downloaded packages is off (pkg clean auto on).",w-32,&zui_caption,ui.theme.secondary);
    y+=150;
    fit(x+4,y,"Change the mirror with pkg mirror HTTPS_URL, then check for updates.",w-8,&zui_caption,ui.theme.secondary);y+=28;
    zui_scroll_end(&ui,&page_scroll,y-top);
}
/* The plan: what changes, what else is needed, sizes, signatures, restart, what happens if it fails, and the command. */
static void plan_line(int x,int y,int w,const char *icon,const char *s,unsigned int color){
    zui_icon(&ui,icon,x,y+1,16,ZUI_ICON_NORMAL);fit(x+26,y,s,w-26,&zui_body,color);
}
static void draw_plan(struct zui_rect r,int as_page){
    if(!as_page)panel(r);
    int x=r.x+16,w=r.w-32;
    int top=zui_scroll_begin(&ui,&plan_scroll,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2}),y=top+14;
    text(x,y,"Your plan",&zui_label,ui.theme.text);
    char t[240],*o=num(t,(unsigned long)plan.n);put(o,plan.n==1?" change":" changes");
    text_right(x+w,y+2,t,&zui_caption,ui.theme.secondary);y+=34;
    zui_fill(&ui,(struct zui_rect){r.x+1,y,r.w-2,1},ui.theme.border);y+=12;
    if(job_id>0){
        text(x,y,same(job.kind,"apply")?"Applying the plan":"Checking for updates",&zui_label,ui.theme.text);y+=26;
        fit(x,y,job.step[0]?job.step:"Starting",w,&zui_caption,ui.theme.secondary);y+=24;
        int pct=job.total>0?(int)(job.done*100/job.total):0;
        zui_meter(&ui,(struct zui_rect){x,y,w,6},pct,ui.theme.accent);y+=18;
        if(job.total>0){o=size_text(t,job.done);o=put(o," of ");size_text(o,job.total);text(x,y,t,&zui_caption,ui.theme.secondary);y+=22;}
        zui_add(&ui,ID_CANCEL_JOB,ZUI_BUTTON,(struct zui_rect){x,y+8,w,34},"Cancel",0,1);y+=50;
        fit(x,y,"Nothing changes until all of it is ready.",w,&zui_caption,ui.theme.secondary);y+=24;
        zui_scroll_end(&ui,&plan_scroll,y-top);return;
    }
    if(!plan_changes()){
        fit(x,y,"Nothing chosen yet.",w,&zui_body,ui.theme.text);y+=26;
        fit(x,y,"Tick updates, or add packages",w,&zui_caption,ui.theme.secondary);y+=20;
        fit(x,y,"from Browse; removals from Installed.",w,&zui_caption,ui.theme.secondary);y+=30;
        if(note[0]){fit(x,y,note,w,&zui_caption,note_bad?bad():good());y+=24;}
        zui_scroll_end(&ui,&plan_scroll,y-top);return;
    }
    int also=0;
    for(int i=0;i<plan.n;i++){
        struct action *a=&plan.actions[i];if(a->also){also++;continue;}
        const char *verb=same(a->verb,"install")?"Install":same(a->verb,"upgrade")?"Upgrade":same(a->verb,"remove")?"Remove":"Kernel";
        text(x,y,verb,&zui_caption,ui.theme.secondary);
        struct pkg *p=find(a->name);o=put(t,p?shown_name(p):a->name);o=put(o," ");put(o,a->to);
        int vw=w-80;fit(x+w-(measure(t,&zui_caption)<vw?measure(t,&zui_caption):vw),y,t,vw,&zui_caption,same(a->verb,"remove")?bad():ui.theme.text);y+=24;
    }
    text(x,y,"Also needed",&zui_caption,ui.theme.secondary);
    if(!also){text_right(x+w,y,"Nothing else",&zui_caption,ui.theme.text);y+=24;}
    else{y+=22;for(int i=0;i<plan.n;i++){struct action *a=&plan.actions[i];if(!a->also)continue;o=put(t,a->name);o=put(o," ");put(o,a->to);text_right(x+w,y,t,&zui_caption,ui.theme.text);y+=22;}}
    y+=6;zui_fill(&ui,(struct zui_rect){r.x+1,y,r.w-2,1},ui.theme.border);y+=14;
    if(plan_error[0]){fit(x,y,plan_error,w,&zui_caption,bad());y+=24;}
    for(int i=0;i<plan.nblock;i++){zui_icon(&ui,"triangle-alert",x,y+1,16,ZUI_ICON_NORMAL);int h=zui_paragraph(&ui,(struct zui_rect){x+26,y,w-26,60},plan.blockers[i],&zui_caption,bad(),0);y+=(h>0?h:1)*18+8;}
    if(plan.have&&!plan.nblock){
        plan_line(x,y,w,"shield","All signatures verified",ui.theme.text);y+=28;
        o=put(t,"Download ");size_text(o,plan.download);plan_line(x,y,w,"download",plan.download?t:"Nothing to download",ui.theme.text);y+=28;
        o=put(t,plan.disk>=0?"Uses ":"Frees ");o=size_text(o,plan.disk>=0?plan.disk:-plan.disk);o=put(o,", ");o=size_text(o,plan.free_bytes-plan.disk);put(o," stays free");
        plan_line(x,y,w,"hard-drive",t,ui.theme.text);y+=28;
        if(plan.restart){plan_line(x,y,w,"rotate-ccw","Kernel takes effect at restart",warn());y+=28;}
        y+=4;
        struct zui_rect f={x,y,w,82};zui_round(&ui,f,10,ui.theme.subtle);
        text(x+12,y+10,"If it fails",&zui_label,ui.theme.text);
        fit(x+12,y+34,"Nothing changes until all of it is ready.",w-24,&zui_caption,ui.theme.secondary);
        fit(x+12,y+54,plan.restart?"The current kernel stays as the fallback.":"The previous versions stay installed.",w-24,&zui_caption,ui.theme.secondary);
        y+=94;
    }
    if(note[0]){fit(x,y,note,w,&zui_caption,note_bad?bad():good());y+=24;}
    if(!ov.mutable_root&&ov.have){fit(x,y,"Changes need an installed system.",w,&zui_caption,warn());y+=24;}
    int ready=plan.have&&!plan.nblock&&plan.n>0&&auth_handle<=0&&ov.mutable_root;
    zui_add(&ui,ID_APPLY,ZUI_BUTTON,(struct zui_rect){x,y,w,38},auth_handle>0?"Waiting for permission...":"Apply plan",1,ready);y+=46;
    zui_add(&ui,ID_COMMAND,ZUI_BUTTON,(struct zui_rect){x,y,w,34},show_command?"Hide the command":"See the exact command",0,plan.ncommands>0);y+=42;
    if(show_command||1){for(int i=0;i<plan.ncommands;i++){int cw=measure(plan.commands[i],&zui_caption);fit(cw<w?x+(w-cw)/2:x,y,plan.commands[i],w,&zui_caption,ui.theme.secondary);y+=20;}}
    zui_add(&ui,ID_CLEAR,ZUI_BUTTON,(struct zui_rect){x,y+8,w,30},"Clear the plan",0,1);y+=46;
    zui_scroll_end(&ui,&plan_scroll,y-top);
}
static void nav(struct zui_rect s){
    zui_icon(&ui,"package",s.x+18,s.y+16,20,ZUI_ICON_NORMAL);
    text(s.x+46,s.y+18,"Orbit",&zui_label,ui.theme.text);
    int y=s.y+56;
    static char counts[PAGES][16];
    for(int i=0;i<PAGES;i++){
        /* zui_add answers 1 when it registered the control, which is then the last one. */
        int at=zui_add(&ui,ID_NAV+i,ZUI_NAVIGATION,(struct zui_rect){s.x+8,y,s.w-16,36},page_names[i],page==i||(page==PAGE_DETAILS&&back_page==i),1)?ui.count-1:-1;
        if(at>=0){
            ui.controls[at].icon=page_icons[i];ui.controls[at].no_chevron=1;
            counts[i][0]=0;
            if(i==PAGE_UPDATES&&ov.updates)num(counts[i],(unsigned long)ov.updates);
            if(i==PAGE_INSTALLED)num(counts[i],(unsigned long)ov.installed);
            if(i==PAGE_SOURCES)num(counts[i],(unsigned long)nsources);
            ui.controls[at].detail=counts[i][0]?counts[i]:0;
        }
        y+=40;
    }
}
static void draw(void){
    int wide=ui.width>=860;
    struct zui_workspace w=zui_workspace_begin(&ui,ui.width<760?168:208);struct zui_rect c=w.content;
    int focus=ui.focus>=0&&ui.focus<ui.count?ui.controls[ui.focus].id:0;
    int pressed=ui.pressed>=0&&ui.pressed<ui.count?ui.controls[ui.pressed].id:0;
    ui.count=0;ui.pressed=-1;ui.focus=-1;nrows=0;nchecks=0;
    nav(w.sidebar);
    if(!have_service){
        zui_clip(&ui,c);struct zui_rect r={c.x+16,c.y+16,c.w-32,120};panel(r);
        text(r.x+20,r.y+16,"Orbit needs a newer kernel",&zui_heading,ui.theme.text);
        fit(r.x+20,r.y+60,"This Zenith kernel has no package service for programs (it needs 0.0.70). pkg in the terminal works.",r.w-40,&zui_body,ui.theme.text);
        zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});zui_draw_controls(&ui);ui.dirty=0;return;
    }
    struct zui_rect content=c,side={0,0,0,0};
    int plan_page=page==PAGE_PLAN;
    int with_plan=page!=PAGE_HISTORY&&page!=PAGE_SOURCES&&!plan_page;
    if(with_plan&&wide){side=(struct zui_rect){c.x+c.w-316,c.y+12,304,c.h-24};content.w-=320;}
    else if(with_plan&&(plan_changes()||job_id>0)){content.h-=56;}
    zui_clip(&ui,content);
    if(page==PAGE_OVERVIEW)draw_overview(content);
    else if(page==PAGE_UPDATES)draw_updates(content);
    else if(page==PAGE_INSTALLED)draw_installed(content);
    else if(page==PAGE_BROWSE)draw_browse(content);
    else if(page==PAGE_HISTORY)draw_history(content);
    else if(page==PAGE_SOURCES)draw_sources(content);
    else if(page==PAGE_DETAILS)draw_details(content);
    else{zui_add(&ui,ID_BACK,ZUI_BUTTON,(struct zui_rect){c.x+16,c.y+12,110,30},page_names[back_page],0,1);draw_plan((struct zui_rect){c.x,c.y+48,c.w,c.h-48},1);}
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});
    if(side.w){zui_clip(&ui,side);draw_plan(side,0);zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});}
    else if(with_plan&&(plan_changes()||job_id>0)){
        struct zui_rect bar={c.x+12,c.y+c.h-50,c.w-24,44};panel(bar);
        char t[120],*o;
        if(job_id>0){o=put(t,same(job.kind,"apply")?"Applying: ":"Checking: ");put(o,job.step);}
        else{o=put(t,"Your plan: ");o=num(o,(unsigned long)plan_changes());o=put(o,plan_changes()==1?" change":" changes");if(plan.nblock)put(o," - blocked");}
        fit(bar.x+16,bar.y+13,t,bar.w-180,&zui_body,plan.nblock?bad():ui.theme.text);
        zui_add(&ui,ID_PLAN_PAGE,ZUI_BUTTON,(struct zui_rect){bar.x+bar.w-140,bar.y+5,130,34},"Review plan",1,1);
    }
    zui_draw_controls(&ui);
    paint_rows();
    for(int i=0;i<ui.count;i++)if(ui.controls[i].enabled){if(ui.controls[i].id==focus)ui.focus=i;if(ui.controls[i].id==pressed)ui.pressed=i;}
    zui_update_hover(&ui);
    ui.dirty=0;
}
static void present(void){
    long r=material?zenith_gui_present_material(handle,ui.pixels,(unsigned long)ui.width*ui.height*4,ui.materials,ui.material_count):zenith_gui_present(handle,ui.pixels,(unsigned long)ui.width*ui.height*4);
    (void)r;
}

/* ---------- Actions. */
static void toggle(int index){
    if(index<0||index>=npkgs||job_id>0)return;
    struct pkg *p=&pkgs[index];
    if(p->mark)p->mark=0;
    else if(page==PAGE_INSTALLED||(page==PAGE_DETAILS&&p->installed[0]&&p->state!=Z_PKG_UPDATE))p->mark=same(p->kind,"kernel")?0:2;
    else if(p->state==Z_PKG_UPDATE||p->state==Z_PKG_AVAILABLE)p->mark=1;
    note[0]=0;plan_dirty=1;ui.dirty=1;
}
static void action(int id){
    if(id>=ID_NAV&&id<ID_NAV+PAGES){go(id-ID_NAV);if(page==PAGE_HISTORY)load_history();return;}
    if(id>=ID_MARK&&id<ID_MARK+72){toggle(id-ID_MARK);return;}
    if(id>=ID_ROW&&id<ID_ROW+72){int i=id-ID_ROW;if(i<npkgs)open_details(pkgs[i].name,0);return;}
    if(id==ID_CHECK){start_refresh();return;}
    if(id==ID_REVIEW){go(PAGE_UPDATES);return;}
    if(id==ID_APPLY){start_apply();return;}
    if(id==ID_COMMAND){show_command=!show_command;ui.dirty=1;return;}
    if(id==ID_CANCEL_JOB){zenith_packages(Z_PKG_CANCEL,0,0,0,0,0);return;}
    if(id==ID_BACK){go(back_page);return;}
    if(id==ID_PLAN_PAGE){back_page=page==PAGE_DETAILS?back_page:page;go(PAGE_PLAN);return;}
    if(id==ID_FETCH){say("Downloading the package to read its description...",0);draw();present();note[0]=0;open_details(d.name,1);return;}
    if(id==ID_CLEAR){for(int i=0;i<npkgs;i++)pkgs[i].mark=0;plan_dirty=1;note[0]=0;ui.dirty=1;return;}
}

/* ---------- Without a window: the same service for scripts and the gate. */
static void print_list(char **f,int n){
    if(same(f[0],"error")&&n>=2){char line[260],*o=put(line,"ORBIT_ERROR ");o=put(o,f[1]);put(o,"\n");print(line);return;}
    if(!same(f[0],"pkg")||n<17)return;
    char line[600],*o=put(line,"ORBIT_PACKAGE ");o=put(o,f[1]);o=put(o," state=");o=put(o,f[4]);o=put(o," installed=");o=put(o,f[5]);o=put(o," candidate=");o=put(o,f[6]);
    o=put(o," latest=");o=put(o,f[7]);o=put(o," title=");o=put(o,f[12]);o=put(o," reason=");o=put(o,f[16]);put(o,"\n");print(line);
}
static void print_fields(char **f,int n){
    char line[700],*o=put(line,"ORBIT_");for(const char *p=f[0];*p;p++)*o++=(char)(*p>='a'&&*p<='z'?*p-32:*p);*o=0;
    for(int i=1;i<n;i++){o=put(o," | ");if((unsigned long)(o-line)+length(f[i])>680)break;o=put(o,f[i]);}
    put(o,"\n");print(line);
}
static int wait_job(long id){
    for(int i=0;i<720;i++){
        long r=zenith_packages(Z_PKG_WAIT,5000,0,0,0,0);
        if(r!=0)break;
    }
    job=(typeof(job)){0};read_op(Z_PKG_JOB,0,0,0,on_job);
    char line[400],*o=put(line,"ORBIT_JOB ");o=put(o,job.kind);o=put(o,job.id!=id?" unknown ":job.state==2?" done ":job.state==4?" cancelled ":job.state==1?" running ":" failed ");
    o=put(o,job.message);put(o,"\n");print(line);
    return job.id==id&&job.state==2?0:1;
}
static int command(int argc,char **argv){
    char line[400],*o;long r;
    if(same(argv[1],"--status")){
        r=read_op(Z_PKG_OVERVIEW,0,0,0,print_fields);
        if(r<0){print(r==-38?"orbit: this kernel has no package service (needs 0.0.70)\n":"orbit: the package service did not answer\n");return 1;}
        return 0;
    }
    if(same(argv[1],"--list")){r=read_op(Z_PKG_LIST,0,0,0,print_list);return r<0;}
    if(same(argv[1],"--history")){r=read_op(Z_PKG_HISTORY,0,0,0,print_fields);return r<0;}
    if(same(argv[1],"--sources")){r=read_op(Z_PKG_SOURCES,0,0,0,print_fields);return r<0;}
    if(same(argv[1],"--details")&&argc>=3){
        r=read_op(Z_PKG_DETAILS,argc>=4&&same(argv[3],"--fetch"),argv[2],(unsigned int)length(argv[2]),print_fields);
        if(r<0){o=put(line,"ORBIT_DETAILS_FAILED ");o=snum(o,r);put(o,"\n");print(line);}
        return r<0;
    }
    if((same(argv[1],"--plan")||same(argv[1],"--apply"))&&argc>=3){
        /* orbit --plan install NAME [remove NAME] [kernel] ... */
        o=plan_input;*o=0;
        for(int i=2;i<argc;i++){
            if(same(argv[i],"kernel")){o=put(o,"kernel\n");continue;}
            if(i+1<argc&&(same(argv[i],"install")||same(argv[i],"upgrade")||same(argv[i],"remove"))){o=put(o,argv[i]);o=put(o," ");o=put(o,argv[i+1]);o=put(o,"\n");i++;continue;}
            print("usage: orbit --plan|--apply [install NAME] [upgrade NAME] [remove NAME] [kernel] ...\n");return 2;
        }
        if(same(argv[1],"--plan")){r=read_op(Z_PKG_PLAN,0,plan_input,(unsigned int)length(plan_input),print_fields);if(r<0){o=put(line,"ORBIT_PLAN_FAILED ");o=put(o,r==-5?reply:"");put(o,"\n");print(line);}return r<0;}
        char message[300]={0};
        r=zenith_packages(Z_PKG_APPLY,0,message,sizeof(message)-1,plan_input,(unsigned int)length(plan_input));
        if(r<0){o=put(line,r==-13?"ORBIT_DENIED ":r==-16?"ORBIT_BUSY ":"ORBIT_REFUSED ");o=put(o,message);put(o,"\n");print(line);return 1;}
        return wait_job(r);
    }
    if(same(argv[1],"--refresh")){
        char message[300]={0};r=zenith_packages(Z_PKG_REFRESH,0,message,sizeof(message)-1,0,0);
        if(r<0){o=put(line,r==-16?"ORBIT_BUSY ":"ORBIT_REFUSED ");o=put(o,message);put(o,"\n");print(line);return 1;}
        return wait_job(r);
    }
    print("usage: native run orbit [--status | --list | --details NAME [--fetch] | --plan ... | --apply ... | --refresh | --history | --sources]\n");return 2;
}

int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("orbit " SUNBEAM_VERSION "\n");return 0;}
    if(argc>=2&&argv[1][0]=='-')return command(argc,argv);
    if(argc>2){print("usage: native run orbit [updates|installed|browse|history|sources]\n");return 2;}
    if(argc==2)page=same(argv[1],"updates")?PAGE_UPDATES:same(argv[1],"installed")?PAGE_INSTALLED:same(argv[1],"browse")?PAGE_BROWSE:same(argv[1],"history")?PAGE_HISTORY:same(argv[1],"sources")?PAGE_SOURCES:PAGE_OVERVIEW;
    zui_icons_use_packages();
    zui_init(&ui,pixels,640,480);appearance();
    zui_input_init(&search_input,search_text,sizeof(search_text));
    struct zenith_material_caps caps={0};struct zenith_material_settings settings={0};
    material=!zenith_material_settings_read(&settings)&&!zenith_gui_material_caps(&caps)&&caps.version==2;
    handle=material?zenith_gui_open_material(640,480,"Orbit",5):zenith_gui_open(640,480,"Orbit",5);
    if(handle<0){print("orbit: graphical session required\n");return 1;}
    ui.foreground=material;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);return 1;}
    ui.hover_supported=zenith_gui_input(handle,15)==0||zenith_gui_input(handle,7)==0||zenith_gui_pointer_motion(handle)==0;
    load();load_history();
    if(have_service){
        char line[160],*o=put(line,"ORBIT_READY updates=");o=num(o,(unsigned long)ov.updates);o=put(o," installed=");o=num(o,(unsigned long)ov.installed);
        o=put(o," packages=");o=num(o,(unsigned long)npkgs);put(o,"\n");print(line);
    }else print("ORBIT_READY no-service\n");
    long last=zui_motion_clock();int polls=0,result=0,done=0;
    while(!done){
        long now=zui_motion_clock();
        if(job_id>0&&((now>=0&&now-last>=300)||(now<0&&++polls>=120))){last=now;polls=0;poll_job();}
        poll_permission();
        if(plan_dirty&&have_service)compute_plan();
        if(ui.dirty){draw();present();}
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_RESIZE){if(zui_apply_viewport(&ui,handle,640*480)<0){result=zui_window_closed(handle)?0:1;break;}continue;}
        if(e.type==Z_GUI_CLOSE||e.type==Z_GUI_CLOSE_INTENT){done=1;continue;}
        if(e.type==Z_GUI_RESET){appearance();continue;}
        if(e.type==Z_GUI_FOCUS&&e.code)print("ORBIT_FOCUSED\n");
        if(e.type==Z_GUI_KEY){
            int control=(e.modifiers&6)==2;
            if(search_focus&&page==PAGE_BROWSE){
                if(e.extra==1){search_focus=0;ui.dirty=1;continue;}
                if(e.extra==2){for(int i=0;i<npkgs;i++){struct pkg *p=&pkgs[i];if(same(p->kind,"kernel"))continue;
                    if(contains(p->name,search_text)||contains(p->title,search_text)||contains(p->summary,search_text)){search_focus=0;open_details(p->name,0);break;}}continue;}
                if(!control&&zui_input_event(&search_input,&e)){zui_scroll_jump(&page_scroll,0);ui.dirty=1;continue;}
            }
            if(control&&e.code>='1'&&e.code<='6'){action(ID_NAV+e.code-'1');continue;}
            if(control&&(e.code=='r'||e.code=='R')){start_refresh();continue;}
            if(control&&(e.code=='f'||e.code=='F')){go(PAGE_BROWSE);search_focus=1;ui.dirty=1;continue;}
            if(control&&e.extra==2){start_apply();continue;}
            if(e.extra==1&&ui.window_focused){
                if(page==PAGE_DETAILS||page==PAGE_PLAN){go(back_page);continue;}
                done=1;continue;
            }
        }
        if(e.type==Z_GUI_DOWN&&page==PAGE_BROWSE){int f=inside(search_rect,e.x,e.y);if(f!=search_focus){search_focus=f;ui.dirty=1;}}
        if(e.type==Z_GUI_DOWN&&e.code==3&&page==PAGE_DETAILS){go(back_page);continue;}
        int on_plan=0;
        if(ui.width>=860&&page!=PAGE_HISTORY&&page!=PAGE_SOURCES&&page!=PAGE_PLAN&&(e.type==Z_GUI_SCROLL||e.type==Z_GUI_MOVE||e.type==Z_GUI_DOWN||e.type==Z_GUI_UP||e.type==Z_GUI_DRAG))on_plan=e.x>=ui.width-320;
        if(on_plan?zui_scroll_event(&ui,&plan_scroll,&e):page==PAGE_PLAN?zui_scroll_event(&ui,&plan_scroll,&e):zui_scroll_event(&ui,&page_scroll,&e))ui.dirty=1;
        int id=zui_event(&ui,&e);
        if(id>0)action(id);
    }
    zenith_gui_close(handle);return result;
}
