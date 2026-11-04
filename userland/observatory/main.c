/* Observatory: what the system is doing. Performance (CPU, memory and display, with
 * two minutes of history from the kernel) and Processes (the caller's programs, or
 * everyone's for root, with a polite stop). Reads zenith-native-monitor-abi 1.0. */
#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
#include "../include/zenith_monitor.h"

#define MAX_W 1920
#define MAX_H 1000
#define MIN_W 600
#define MIN_H 480
static unsigned int pixels[MAX_W*MAX_H];
static struct zui ui;
static long handle;
static int material;

/* ---------- Text helpers: no libc in native programs. */
static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){zenith_write(s,length(s));}
static char *put(char *o,const char *s){while(*s)*o++=*s++;*o=0;return o;}
static char *num(char *o,unsigned long v){char t[24];int n=0;do{t[n++]=(char)('0'+v%10);v/=10;}while(v);while(n)*o++=t[--n];*o=0;return o;}
/* One decimal place, rounded: 1.8 GiB, 512 MiB. */
static char *size_text(char *o,unsigned long bytes){
    const unsigned long mib=1024UL*1024,gib=mib*1024;
    if(bytes>=gib){unsigned long tenths=(bytes*10+gib/2)/gib;o=num(o,tenths/10);if(tenths<1000){*o++='.';o=num(o,tenths%10);}return put(o," GiB");}
    return put(num(o,(bytes+mib/2)/mib)," MiB");
}
static char *duration_text(char *o,unsigned long ms){
    unsigned long s=ms/1000,m=s/60,h=m/60,d=h/24;
    if(d){o=put(num(o,d)," d ");return put(num(o,h%24)," h");}
    if(h){o=put(num(o,h)," h ");return put(num(o,m%60)," min");}
    if(m)return put(num(o,m)," min");
    return put(num(o,s)," s");
}
static int contains(const char *haystack,const char *needle){
    if(!*needle)return 1;
    for(;*haystack;haystack++){
        const char *h=haystack,*n=needle;
        while(*h&&*n){char a=*h,b=*n;if(a>='A'&&a<='Z')a+=32;if(b>='A'&&b<='Z')b+=32;if(a!=b)break;h++;n++;}
        if(!*n)return 1;
    }
    return 0;
}
static void text(int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(&ui,x,y,s,length(s),f,c);}
static void text_right(int right,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(&ui,right-zui_measure(s,length(s),f),y,s,length(s),f,c);}
static int inside(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x<r.x+r.w&&y<r.y+r.h;}

/* ---------- Colors that follow light and dark. */
static int light(void){unsigned int s=ui.theme.surface;return ((s>>16&255)+(s>>8&255)+(s&255))>384;}
static unsigned int good(void){return light()?0x277347:0x8BCBA4;}
static unsigned int warn(void){return light()?0x886010:0xE6C47A;}
static unsigned int memory_color(void){return light()?0x7042BE:0xB6A0FF;}
static unsigned int display_color(void){return light()?0x3157B7:0x8BAAFF;}
/* Cards and panels: opaque normally, a light tint when the whole window is a material. */
static void panel(struct zui_rect r,int selected){
    if(ui.foreground&&ui.full_background&&!ui.theme_preview)zui_round_alpha(&ui,r,10,selected?ui.theme.accent:ui.theme.raised,selected?46:90);
    else{zui_round(&ui,r,10,ui.theme.raised);if(selected)zui_round_alpha(&ui,r,10,ui.theme.accent,34);}
    zui_round_outline(&ui,r,10,selected?ui.theme.accent:ui.theme.border);
}

/* ---------- Data. */
static struct zenith_monitor monitor;
static int have_monitor;
static unsigned char list_page[4096] __attribute__((aligned(16)));
static struct zenith_process_list *processes=(struct zenith_process_list *)list_page;
static int have_processes;
/* Display history is client-side: the kernel reports the last second only. */
static unsigned char frames_history[Z_MONITOR_HISTORY];
static int frames_count;
static unsigned long last_frame_sample;
static void refresh(void){
    have_monitor=zenith_monitor_read(&monitor)==0&&monitor.version==1&&monitor.samples<=Z_MONITOR_HISTORY;
    have_processes=zenith_process_list_read(list_page)==0&&processes->version==1&&processes->count<=63;
    /* The kernel samples at most once a second; keep one display point per sample. */
    if(have_monitor&&monitor.samples&&monitor.uptime_ms>=last_frame_sample+900){
        unsigned int fps=monitor.frames_per_second>60?60:monitor.frames_per_second;
        if(frames_count==Z_MONITOR_HISTORY){for(int i=1;i<Z_MONITOR_HISTORY;i++)frames_history[i-1]=frames_history[i];frames_count--;}
        frames_history[frames_count++]=(unsigned char)(fps*100/60);
        last_frame_sample=monitor.uptime_ms;
    }
    ui.dirty=1;
}
static int busy_cores(void){int n=0;for(unsigned int i=0;i<monitor.cpus&&i<8;i++)if(monitor.latest.cpu[i]!=Z_MONITOR_OFFLINE&&monitor.latest.cpu[i]>=50)n++;return n;}

/* ---------- Appearance. */
static void update_appearance(void){
    struct zenith_appearance state={0};struct zenith_scoped_appearance scoped={0};
    int personal=zenith_scoped_appearance_read(1,&scoped)==0;
    if(personal)state=scoped.appearance;
    if((personal||!zenith_appearance_read(&state))&&state.active_mode<2&&state.active_accent<6){const unsigned int *p=state.palette[state.active_mode][state.active_accent];ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};}
    struct zenith_material_settings material_state={0};
    if(personal)ui.full_background=(scoped.materials.saved&5)==5;
    else if(!zenith_material_settings_read(&material_state))ui.full_background=(material_state.saved&5)==5;
    ui.dirty=1;
}

/* ---------- Navigation. */
enum { PAGE_PERFORMANCE=0,PAGE_PROCESSES=1 };
static int page_shown;
static struct zui_list sidebar;
static int nav_rows(void *context,int i,struct zui_list_item *o){
    (void)context;
    if(i==0){*o=(struct zui_list_item){"activity","Performance",0,0,1};return 1;}
    if(i==1){*o=(struct zui_list_item){"list","Processes",0,0,1};return 1;}
    return 0;
}

/* ---------- Performance. */
enum { CARD_CPU=0,CARD_MEMORY=1,CARD_DISPLAY=2,CARDS=3 };
static int card;
static struct zui_rect card_rects[CARDS];
static void stat(int x,int y,const char *label,const char *value){
    text(x,y,label,&zui_caption,ui.theme.secondary);
    text(x,y+18,value,&zui_label,ui.theme.text);
}
static void draw_card(int index,struct zui_rect r,int compact){
    static const char *titles[CARDS]={"CPU","Memory","Display"},*icons[CARDS]={"cpu","memory-stick","monitor"};
    char value[48],caption[64],*o;
    unsigned int color=index==CARD_CPU?ui.theme.accent:index==CARD_MEMORY?memory_color():display_color();
    card_rects[index]=r;panel(r,card==index);
    zui_icon(&ui,icons[index],r.x+12,r.y+12,16,ZUI_ICON_NORMAL);
    text(r.x+36,r.y+12,titles[index],&zui_label,ui.theme.text);
    /* Compact cards put the value under the title: side by side they collide at 640 px. */
    struct zui_rect g=compact?(struct zui_rect){r.x+12,r.y+52,r.w-24,r.h-58}:(struct zui_rect){r.x+12,r.y+38,r.w-24,38};
    if(index==CARD_CPU){
        o=num(value,monitor.latest.total);put(o,"%");
        o=num(caption,(unsigned long)busy_cores());o=put(o," of ");o=num(o,monitor.cpus);put(o,monitor.cpus==1?" CPU busy":" CPUs busy");
        zui_graph(&ui,g,&monitor.history[0].total,16,(int)monitor.samples,60,color,0);
    }else if(index==CARD_MEMORY){
        if(compact){o=num(value,monitor.latest.memory);put(o,"%");}
        else{o=size_text(value,monitor.memory_total-monitor.memory_free);o=put(o," / ");size_text(o,monitor.memory_total);}
        o=num(caption,monitor.latest.memory);put(o,"% in use");
        zui_graph(&ui,g,&monitor.history[0].memory,16,(int)monitor.samples,60,color,0);
    }else{
        o=num(value,monitor.frames_per_second);put(o," fps");
        o=put(caption,"worst ");o=num(o,(monitor.frame_worst_us+500)/1000);put(o," ms");
        zui_graph(&ui,g,frames_history,1,frames_count,60,color,0);
    }
    if(compact)text(r.x+12,r.y+32,value,&zui_label,ui.theme.text);else text_right(r.x+r.w-12,r.y+12,value,&zui_label,ui.theme.text);
    if(!compact)text(r.x+12,r.y+r.h-24,caption,&zui_caption,ui.theme.secondary);
}
static void draw_performance(struct zui_rect c){
    if(!have_monitor){
        text(c.x+24,c.y+24,"This system does not report performance.",&zui_label,ui.theme.text);
        text(c.x+24,c.y+48,"Observatory needs a newer Zenith kernel. Update Zenith, then reopen it.",&zui_body,ui.theme.secondary);
        return;
    }
    /* Wide: cards in a column beside the main view. Narrow (the 640x480 a window opens at):
     * compact cards in a row above it. */
    int compact=c.w<720,x=c.x+12,y=c.y+12,mx,mw,my;
    if(compact){
        int cw=(c.w-24-2*10)/3,ch=80;
        for(int i=0;i<CARDS;i++)draw_card(i,(struct zui_rect){x+i*(cw+10),y,cw,ch},1);
        mx=x;mw=c.w-24;my=y+ch+16;
    }else{
        int cw=c.w>900?262:220,ch=108;
        for(int i=0;i<CARDS;i++)draw_card(i,(struct zui_rect){x,y+i*(ch+12),cw,ch},0);
        mx=x+cw+20;mw=c.x+c.w-12-mx;my=c.y+12;
    }
    int bottom=c.y+c.h-12;
    if(mw<200)return;
    static const char *titles[CARDS]={"CPU","Memory","Display"};
    char sub[128],*o=sub;
    if(card==CARD_CPU){o=put(o,monitor.processor[0]?monitor.processor:"Processor");o=put(o,", ");o=num(o,monitor.cpus);o=put(o,monitor.cpus==1?" CPU in use":" CPUs in use");}
    else if(card==CARD_MEMORY){o=size_text(o,monitor.memory_total);put(o," installed");}
    else put(o,"Frames the desktop composed");
    text(mx,my,titles[card],&zui_heading,ui.theme.text);
    text(mx+zui_measure(titles[card],length(titles[card]),&zui_heading)+14,my+10,sub,&zui_caption,ui.theme.secondary);
    int stats_h=compact?100:118,cores=card==CARD_CPU&&monitor.cpus>1?(int)monitor.cpus:0,cores_h=cores?(compact?64:92):0;
    int gy=my+44,gh=bottom-gy-stats_h-cores_h-(cores?12:0);
    if(gh<60)gh=60;
    struct zui_rect graph={mx,gy,mw,gh};panel(graph,0);
    unsigned int color=card==CARD_CPU?ui.theme.accent:card==CARD_MEMORY?memory_color():display_color();
    struct zui_rect inner={graph.x+1,graph.y+24,graph.w-2,graph.h-48};
    if(card==CARD_CPU)zui_graph(&ui,inner,&monitor.history[0].total,16,(int)monitor.samples,Z_MONITOR_HISTORY,color,1);
    else if(card==CARD_MEMORY)zui_graph(&ui,inner,&monitor.history[0].memory,16,(int)monitor.samples,Z_MONITOR_HISTORY,color,1);
    else zui_graph(&ui,inner,frames_history,1,frames_count,Z_MONITOR_HISTORY,color,1);
    text(graph.x+10,graph.y+6,card==CARD_DISPLAY?"60 fps":"100%",&zui_caption,ui.theme.secondary);
    text(graph.x+10,graph.y+graph.h-20,"2 min ago",&zui_caption,ui.theme.secondary);
    text_right(graph.x+graph.w-10,graph.y+graph.h-20,"now",&zui_caption,ui.theme.secondary);
    int sy=gy+gh+12;
    if(cores){
        int n=cores>8?8:cores,gap=10,w=(mw-gap*(n-1))/n;
        for(int i=0;i<n;i++){
            struct zui_rect t={mx+i*(w+gap),sy,w,cores_h};panel(t,0);
            char label[16],pct[8];o=put(label,"CPU ");num(o,(unsigned long)i);
            int v=monitor.latest.cpu[i];
            if(v==Z_MONITOR_OFFLINE)put(pct,"off");else{o=num(pct,(unsigned long)v);put(o,"%");}
            text(t.x+10,t.y+8,label,&zui_caption,ui.theme.secondary);
            text_right(t.x+t.w-10,t.y+8,pct,&zui_caption,ui.theme.text);
            zui_graph(&ui,(struct zui_rect){t.x+6,t.y+30,t.w-12,t.h-36},&monitor.history[0].cpu[i],16,(int)monitor.samples,60,color,0);
        }
        sy+=cores_h+16;
    }else sy+=4;
    int col=mw/3;char a[48],b[48],cc[48],d[48],e[48],f[48];
    if(card==CARD_CPU){
        o=num(a,monitor.latest.total);put(o,"%");
        num(b,monitor.programs);
        o=num(cc,monitor.cpus);o=put(o," of ");num(o,monitor.detected?monitor.detected:monitor.cpus);
        duration_text(d,monitor.uptime_ms);
        o=num(e,(monitor.frame_average_us+500)/1000);put(o," ms");
        put(f,"Not reported");
        stat(mx,sy,"Utilization",a);stat(mx+col,sy,"Programs",b);stat(mx+2*col,sy,"CPUs in use",cc);
        stat(mx,sy+48,"Uptime",d);stat(mx+col,sy+48,"Average frame",e);stat(mx+2*col,sy+48,"Temperature",f);
        if(monitor.detected>monitor.cpus)
            text(mx,sy+96,"Zenith uses one CPU unless it is started with smp=native.",&zui_caption,ui.theme.secondary);
    }else if(card==CARD_MEMORY){
        size_text(a,monitor.memory_total-monitor.memory_free);size_text(b,monitor.memory_free);size_text(cc,monitor.memory_total);
        o=num(d,monitor.latest.memory);put(o,"%");num(e,monitor.programs);duration_text(f,monitor.uptime_ms);
        stat(mx,sy,"In use",a);stat(mx+col,sy,"Free",b);stat(mx+2*col,sy,"Installed",cc);
        stat(mx,sy+48,"Share in use",d);stat(mx+col,sy+48,"Programs",e);stat(mx+2*col,sy+48,"Uptime",f);
    }else{
        o=num(a,monitor.frames_per_second);put(o," fps");
        o=num(b,monitor.frame_average_us/1000);*o++='.';o=num(o,monitor.frame_average_us%1000/100);put(o," ms");
        o=num(cc,(monitor.frame_worst_us+500)/1000);put(o," ms");
        put(d,"16.7 ms");
        stat(mx,sy,"Presented",a);stat(mx+col,sy,"Average frame",b);stat(mx+2*col,sy,"Worst, last second",cc);
        stat(mx,sy+48,"Frame budget",d);
        text(mx+col,sy+52,"The desktop draws only when something changes,",&zui_caption,ui.theme.secondary);
        text(mx+col,sy+68,"so an idle screen shows few frames.",&zui_caption,ui.theme.secondary);
    }
}

/* ---------- Processes. */
enum { SORT_NAME=0,SORT_PID=1,SORT_USER=2,SORT_STATE=3,SORT_CPU=4,COLUMNS=5 };
static int sort_column=SORT_PID,sort_down;
static int selected_pid;
static int order[63],shown;
static char filter_text[64];
static struct zui_input filter;
static int filter_focus;
static struct zui_rect filter_rect,header_rects[COLUMNS],row_area;
static struct zui_scroll rows_scroll;
static struct zui_dialog confirm={0,90,"End this program?","",{"Cancel","End process"},2};
static char confirm_title[80],status_line[96];
#define END_BUTTON 80
static const struct zenith_process_entry *entry(int i){return &processes->entries[i];}
static int stopping(const struct zenith_process_entry *p){return (p->flags&Z_PROCESS_STOP_REQUESTED)&&!(p->flags&Z_PROCESS_HAS_EXITED);}
static const char *state_text(const struct zenith_process_entry *p){
    if(p->flags&Z_PROCESS_HAS_EXITED)return "Exited";
    if(stopping(p))return "Stopping";
    return p->state==Z_PROCESS_STARTING?"Starting":p->state==Z_PROCESS_RUNNING?"Running":"Waiting";
}
static int state_rank(const struct zenith_process_entry *p){return p->flags&Z_PROCESS_HAS_EXITED?4:stopping(p)?3:p->state==Z_PROCESS_RUNNING?0:p->state==Z_PROCESS_STARTING?1:2;}
static int compare_text(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return (unsigned char)*a-(unsigned char)*b;}
static int compare(int a,int b){
    const struct zenith_process_entry *x=entry(a),*y=entry(b);int r=0;
    /* Programs that ended always follow the live ones, newest first. */
    int ex=(x->flags&Z_PROCESS_HAS_EXITED)!=0,ey=(y->flags&Z_PROCESS_HAS_EXITED)!=0;
    if(ex!=ey)return ex-ey;
    if(ex)return y->pid-x->pid;
    if(sort_column==SORT_NAME)r=compare_text(x->name,y->name);
    else if(sort_column==SORT_USER)r=compare_text(x->user,y->user);
    else if(sort_column==SORT_STATE)r=state_rank(x)-state_rank(y);
    else if(sort_column==SORT_CPU)r=x->cpu-y->cpu;
    if(!r)r=x->pid-y->pid;
    return sort_down?-r:r;
}
static void arrange(void){
    shown=0;
    if(!have_processes)return;
    for(unsigned int i=0;i<processes->count;i++){
        const struct zenith_process_entry *p=entry((int)i);
        if(contains(p->name,filter_text)||contains(p->user,filter_text))order[shown++]=(int)i;
    }
    for(int i=1;i<shown;i++){int v=order[i],j=i-1;while(j>=0&&compare(order[j],v)>0){order[j+1]=order[j];j--;}order[j+1]=v;}
}
static const struct zenith_process_entry *selected(void){
    for(int i=0;i<shown;i++)if(entry(order[i])->pid==selected_pid)return entry(order[i]);
    return 0;
}
static const char *process_icon(const char *name){
    if(contains(name,"polaris")||same(name,"sh"))return "terminal";
    if(contains(name,"pkg"))return "package";
    if(contains(name,"settings"))return "sliders-horizontal";
    if(contains(name,"observatory"))return "activity";
    return "app-window";
}
static void draw_processes(struct zui_rect c){
    if(!have_processes){
        text(c.x+24,c.y+24,"This system does not list programs.",&zui_label,ui.theme.text);
        text(c.x+24,c.y+48,"Observatory needs a newer Zenith kernel. Update Zenith, then reopen it.",&zui_body,ui.theme.secondary);
        return;
    }
    arrange();
    int x=c.x+12,w=c.w-24,y=c.y+12;
    filter_rect=(struct zui_rect){x,y,w>700?320:w/2,34};
    zui_round(&ui,filter_rect,8,ui.theme.surface);zui_round_outline(&ui,filter_rect,8,filter_focus?ui.theme.accent:ui.theme.border);
    zui_icon(&ui,"search",filter_rect.x+10,filter_rect.y+9,16,ZUI_ICON_TEXT);
    if(filter.length||filter_focus)zui_input_draw(&ui,(struct zui_rect){filter_rect.x+32,filter_rect.y+1,filter_rect.w-40,32},&filter,filter_focus);
    else text(filter_rect.x+36,filter_rect.y+9,"Filter programs",&zui_body,ui.theme.secondary);
    const struct zenith_process_entry *s=selected();
    zui_add(&ui,END_BUTTON,ZUI_BUTTON,(struct zui_rect){x+w-140,y,140,34},"End process",0,s&&!(s->flags&Z_PROCESS_HAS_EXITED)&&!stopping(s));
    if(status_line[0])text_right(x+w-152,y+9,status_line,&zui_caption,warn());
    /* Columns: Name, PID, User, State, CPU. */
    static const char *heads[COLUMNS]={"Name","PID","User","State","CPU"};
    int cols[COLUMNS+1]={x+12,x+w*34/100,x+w*46/100,x+w*64/100,x+w*82/100,x+w};
    int hy=y+50;
    for(int i=0;i<COLUMNS;i++){
        header_rects[i]=(struct zui_rect){cols[i]-6,hy-4,cols[i+1]-cols[i],26};
        text(cols[i],hy,heads[i],&zui_label,sort_column==i?ui.theme.text:ui.theme.secondary);
        if(sort_column==i)zui_icon(&ui,sort_down?"chevron-down":"chevron-right",cols[i]+zui_measure(heads[i],length(heads[i]),&zui_label)+4,hy+1,14,ZUI_ICON_TEXT);
    }
    zui_fill(&ui,(struct zui_rect){x,hy+24,w,1},ui.theme.border);
    int narrow=w<600,details_h=s?(narrow?168:150):0,ry=hy+30;
    row_area=(struct zui_rect){x,ry,w,c.y+c.h-12-details_h-(s?12:0)-ry};
    const int row=40;
    zui_scroll_begin(&ui,&rows_scroll,row_area);
    for(int i=0;i<shown;i++){
        int top=row_area.y+i*row-rows_scroll.visual;
        if(top+row<row_area.y||top>row_area.y+row_area.h)continue;
        const struct zenith_process_entry *p=entry(order[i]);
        struct zui_rect r={x,top,w,row-4};
        if(p->pid==selected_pid){zui_round_alpha(&ui,r,8,ui.theme.accent,40);zui_round(&ui,(struct zui_rect){x,top+8,3,row-20},1,ui.theme.accent);}
        int ty=top+10;unsigned int dim=p->flags&Z_PROCESS_HAS_EXITED?ui.theme.secondary:ui.theme.text;
        zui_icon(&ui,process_icon(p->name),cols[0],ty+1,16,ZUI_ICON_TEXT);
        text(cols[0]+26,ty,p->name,p->pid==selected_pid?&zui_label:&zui_body,dim);
        char v[24];num(v,(unsigned long)p->pid);text(cols[1],ty,v,&zui_body,ui.theme.secondary);
        text(cols[2],ty,p->user,&zui_body,ui.theme.secondary);
        const char *st=state_text(p);
        unsigned int sc=stopping(p)?warn():p->state==Z_PROCESS_RUNNING&&!(p->flags&Z_PROCESS_HAS_EXITED)?good():ui.theme.secondary;
        if(p->flags&Z_PROCESS_HAS_EXITED&&p->status>=0){char e[32],*o=put(e,"Exited, ");num(o,(unsigned long)p->status);text(cols[3],ty,e,&zui_body,sc);}
        else text(cols[3],ty,st,&zui_body,sc);
        if(p->cpu>=0&&!(p->flags&Z_PROCESS_HAS_EXITED)){char cpu[16],*o=put(cpu,"CPU ");num(o,(unsigned long)p->cpu);text(cols[4],ty,cpu,&zui_body,ui.theme.secondary);}
        else text(cols[4],ty,"-",&zui_body,ui.theme.secondary);
    }
    if(!shown)text(x+12,row_area.y+10,filter.length?"No program matches the filter.":"No programs are running.",&zui_body,ui.theme.secondary);
    zui_scroll_end(&ui,&rows_scroll,shown*row);
    if(s){
        struct zui_rect d={x,c.y+c.h-12-details_h,w,details_h};panel(d,0);
        char title[80],*o=put(title,s->name);o=put(o,", PID ");num(o,(unsigned long)s->pid);
        zui_icon(&ui,process_icon(s->name),d.x+14,d.y+15,16,ZUI_ICON_NORMAL);
        text(d.x+38,d.y+14,title,&zui_label,ui.theme.text);
        char runs[48],core[24],allowed[48],state[40];
        o=put(runs,s->user);o=put(o,"  (UID ");o=num(o,s->uid);put(o,")");
        if(s->cpu>=0){o=put(core,"CPU ");num(o,(unsigned long)s->cpu);}else put(core,"Not yet scheduled");
        o=allowed;int any=0;
        for(int i=0;i<8;i++)if(s->affinity&(1<<i)){if(any)o=put(o,", ");o=num(o,(unsigned long)i);any=1;}
        if(s->affinity==255)put(allowed,"Any CPU");else if(!any)put(allowed,"None");
        o=put(state,state_text(s));
        if(s->flags&Z_PROCESS_HAS_EXITED&&s->status>=0){o=put(o,", status ");num(o,(unsigned long)s->status);}
        const char *keys[4]={"Runs as","State","Last ran on","Allowed CPUs"},*values[4]={runs,state,core,allowed};
        for(int i=0;i<4;i++){
            int kx=narrow?d.x+16:d.x+16+(i/2)*(w/2),ky=narrow?d.y+44+i*24:d.y+48+(i%2)*28;
            text(kx,ky,keys[i],&zui_body,ui.theme.secondary);text(kx+(narrow?110:120),ky,values[i],&zui_body,ui.theme.text);
        }
        text(d.x+16,d.y+details_h-28,stopping(s)?"Asked to stop. A program ends at its next safe point.":"End process asks the program to stop, the same way kill does.",&zui_caption,ui.theme.secondary);
    }
}
static void select_step(int step){
    if(!shown)return;
    int at=-1;for(int i=0;i<shown;i++)if(entry(order[i])->pid==selected_pid)at=i;
    at=at<0?(step>0?0:shown-1):at+step;
    if(at<0)at=0;
    if(at>=shown)at=shown-1;
    selected_pid=entry(order[at])->pid;zui_scroll_reveal(&rows_scroll,at*40,40);ui.dirty=1;
}
static void ask_end(void){
    const struct zenith_process_entry *s=selected();
    if(!s||(s->flags&Z_PROCESS_HAS_EXITED)||stopping(s))return;
    char *o=put(confirm_title,"End ");o=put(o,s->name);put(o,"?");
    confirm.title=confirm_title;confirm.message="Observatory asks it to stop, as kill does. Unsaved work in it may be lost.";
    confirm.open=1;ui.dirty=1;
}
static void end_selected(void){
    const struct zenith_process_entry *s=selected();
    if(!s)return;
    long r=zenith_process_stop(s->pid);
    status_line[0]=0;
    if(r==-13)put(status_line,"You can end only your own programs.");
    else if(r==-3)put(status_line,"That program has already ended.");
    else if(r<0)put(status_line,"Could not ask it to stop.");
    refresh();
}

/* ---------- Main loop. */
static void draw(void){
    struct zui_workspace w=zui_workspace_begin(&ui,ui.width<800?156:196);struct zui_rect c=w.content;
    int focus=ui.focus>=0&&ui.focus<ui.count?ui.controls[ui.focus].id:0;
    int pressed=ui.pressed>=0&&ui.pressed<ui.count?ui.controls[ui.pressed].id:0;
    ui.count=0;ui.pressed=-1;ui.focus=-1;
    text(24,24,"Observatory",&zui_label,ui.theme.text);
    sidebar.selected=page_shown;
    zui_list_draw(&ui,&sidebar,(struct zui_rect){w.sidebar.x,w.sidebar.y+44,w.sidebar.w,w.sidebar.h-44},2,nav_rows,0);
    zui_clip(&ui,c);
    if(page_shown==PAGE_PERFORMANCE)draw_performance(c);else draw_processes(c);
    for(int i=0;i<ui.count;i++)if(ui.controls[i].enabled){if(ui.controls[i].id==focus)ui.focus=i;if(ui.controls[i].id==pressed)ui.pressed=i;}
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});zui_update_hover(&ui);zui_draw_controls(&ui);
    if(confirm.open)zui_dialog_draw(&ui,&confirm);
    ui.dirty=0;
}
static void pointer(const struct zenith_gui_event *e){
    if(e->type!=Z_GUI_DOWN)return;
    if(page_shown==PAGE_PERFORMANCE){
        for(int i=0;i<CARDS;i++)if(inside(card_rects[i],e->x,e->y)&&card!=i){card=i;ui.dirty=1;}
        return;
    }
    int was=filter_focus;filter_focus=inside(filter_rect,e->x,e->y);
    if(was!=filter_focus)ui.dirty=1;
    for(int i=0;i<COLUMNS;i++)if(inside(header_rects[i],e->x,e->y)){
        if(sort_column==i)sort_down=!sort_down;else{sort_column=i;sort_down=i==SORT_CPU;}
        ui.dirty=1;return;
    }
    if(inside(row_area,e->x,e->y)){
        int i=(e->y-row_area.y+rows_scroll.visual)/40;
        if(i>=0&&i<shown){selected_pid=entry(order[i])->pid;status_line[0]=0;ui.dirty=1;}
    }
}
int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("observatory " SUNBEAM_VERSION "\n");return 0;}
    if(argc!=1&&!(argc==2&&same(argv[1],"processes"))){print("usage: native run observatory [processes]\n");return 2;}
    if(argc==2)page_shown=PAGE_PROCESSES;
    zui_icons_use_packages();
    /* 640x480 is the largest surface the host admits at open; maximize goes beyond it. */
    zui_init(&ui,pixels,640,480);update_appearance();
    if(!zui_input_init(&filter,filter_text,sizeof(filter_text)))return 1;
    struct zenith_material_caps caps={0};struct zenith_material_settings settings={0};
    material=!zenith_material_settings_read(&settings)&&!zenith_gui_material_caps(&caps)&&caps.version==2;
    handle=material?zenith_gui_open_material(640,480,"Observatory",11):zenith_gui_open(640,480,"Observatory",11);
    if(handle<0){print("observatory: graphical session required\n");return 1;}
    ui.foreground=material;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);return 1;}
    ui.hover_supported=zenith_gui_pointer_motion(handle)==0;
    sidebar.base_id=10;
    refresh();
    print("OBSERVATORY_READY\n");
    long last=zui_motion_clock();int polls=0,result=0,done=0;
    while(!done){
        long now=zui_motion_clock();
        if((now>=0&&now-last>=1000)||(now<0&&++polls>=200)){last=now;polls=0;refresh();}
        if(ui.dirty){
            draw();
            long r=material?zenith_gui_present_material(handle,pixels,(unsigned long)ui.width*ui.height*4,ui.materials,ui.material_count):zenith_gui_present(handle,pixels,(unsigned long)ui.width*ui.height*4);
            if(r<0){result=1;break;}
        }
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_RESIZE){
            struct zenith_viewport v={0};
            if(zenith_gui_viewport(handle,&v)||v.width<MIN_W||v.height<MIN_H||v.width>MAX_W||v.height>MAX_H){result=1;break;}
            long accepted=zenith_gui_resize_accept(handle,&v);if(accepted==-11)continue;
            if(accepted||!zui_resize(&ui,v.width,v.height,MAX_W*MAX_H)){result=1;break;}
            continue;
        }
        if(e.type==Z_GUI_CLOSE||e.type==Z_GUI_CLOSE_INTENT){done=1;continue;}
        if(e.type==Z_GUI_RESET){update_appearance();continue;}
        if(e.type==Z_GUI_FOCUS&&e.code)print("OBSERVATORY_FOCUSED\n");
        if(confirm.open){
            int choice=zui_dialog_event(&ui,&confirm,&e);
            if(choice==2)end_selected();
            if(choice)ui.dirty=1;
            continue;
        }
        if(e.type==Z_GUI_KEY&&page_shown==PAGE_PROCESSES&&filter_focus){
            if(e.extra==1){filter_focus=0;filter.length=filter.cursor=0;filter_text[0]=0;ui.dirty=1;continue;}
            if(e.extra!=7&&e.extra!=8&&zui_input_event(&filter,&e)){ui.dirty=1;continue;}
        }
        if(e.type==Z_GUI_KEY&&e.extra==1&&ui.window_focused&&!filter_focus){done=1;continue;}
        if(e.type==Z_GUI_KEY&&page_shown==PAGE_PROCESSES&&!e.modifiers){
            if(e.extra==7){select_step(-1);continue;}
            if(e.extra==8){select_step(1);continue;}
        }
        if(e.type==Z_GUI_KEY&&page_shown==PAGE_PROCESSES&&(e.modifiers&2)&&(e.code=='f'||e.code=='F')){filter_focus=1;ui.dirty=1;continue;}
        if(e.type==Z_GUI_DOWN||e.type==Z_GUI_UP)pointer(&e);
        if(page_shown==PAGE_PROCESSES&&zui_scroll_event(&ui,&rows_scroll,&e))ui.dirty=1;
        int list=zui_list_event(&ui,&sidebar,&e,nav_rows,0);
        if(list>0&&list-1!=page_shown){page_shown=list-1;filter_focus=0;ui.dirty=1;continue;}
        int action=zui_event(&ui,&e);
        if(action==END_BUTTON)ask_end();
        if(action>=10&&action<12&&action-10!=page_shown){page_shown=action-10;filter_focus=0;ui.dirty=1;}
    }
    zenith_gui_close(handle);return result;
}
