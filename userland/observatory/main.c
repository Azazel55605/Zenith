/* Observatory: what the system is doing. Performance (CPU, memory, disk, network and display,
 * with two minutes of history from the kernel), Apps (the windows on the desktop grouped by the
 * program that owns them) and Processes (the caller's programs, or everyone's for root, with a
 * polite stop). Reads zenith-native-monitor-abi 1.0; disk, network and Apps use call 649
 * (kernel 0.0.56) and are left out on older kernels. */
#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
#include "../include/zenith_monitor.h"

#define MAX_W ZUI_MAX_WIDTH
#define MAX_H ZUI_MAX_HEIGHT
#define MIN_W 600
#define MIN_H 480
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
/* One decimal place, rounded: 1.8 GiB, 512 MiB. */
static char *size_text(char *o,unsigned long bytes){
    const unsigned long mib=1024UL*1024,gib=mib*1024;
    if(bytes>=gib){unsigned long tenths=(bytes*10+gib/2)/gib;o=num(o,tenths/10);if(tenths<1000){*o++='.';o=num(o,tenths%10);}return put(o," GiB");}
    return put(num(o,(bytes+mib/2)/mib)," MiB");
}
/* Bytes per second, decimal units as drives and networks are sold: 0 B/s, 840 KB/s, 3.1 MB/s. */
static char *rate_text(char *o,unsigned long rate){
    if(rate>=1000000000UL){unsigned long t=(rate+50000000UL)/100000000UL;o=num(o,t/10);*o++='.';o=num(o,t%10);return put(o," GB/s");}
    if(rate>=1000000UL){unsigned long t=(rate+50000UL)/100000UL;o=num(o,t/10);if(t<1000){*o++='.';o=num(o,t%10);}return put(o," MB/s");}
    if(rate>=1000UL)return put(num(o,(rate+500)/1000)," KB/s");
    return put(num(o,rate)," B/s");
}
static char *bytes_text(char *o,unsigned long bytes){
    if(bytes>=1000000000UL){unsigned long t=(bytes+50000000UL)/100000000UL;o=num(o,t/10);*o++='.';o=num(o,t%10);return put(o," GB");}
    if(bytes>=1000000UL){unsigned long t=(bytes+50000UL)/100000UL;o=num(o,t/10);*o++='.';o=num(o,t%10);return put(o," MB");}
    if(bytes>=1000UL)return put(num(o,(bytes+500)/1000)," KB");
    return put(num(o,bytes)," B");
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
static unsigned int disk_color(void){return light()?0x277347:0x8BCBA4;}
static unsigned int disk_write_color(void){return light()?0x886010:0xE6C47A;}
static unsigned int net_color(void){return light()?0x1F6F8B:0x7CC7E8;}
static unsigned int net_out_color(void){return light()?0xA2416B:0xF0A0C0;}
/* Cards and panels: opaque normally, a light tint when the whole window is a material. */
static void panel(struct zui_rect r,int selected){
    if(ui.foreground&&ui.full_background&&!ui.theme_preview)zui_round_alpha(&ui,r,10,selected?ui.theme.accent:ui.theme.raised,selected?46:90);
    else{zui_round(&ui,r,10,ui.theme.raised);if(selected)zui_round_alpha(&ui,r,10,ui.theme.accent,34);}
    zui_round_outline(&ui,r,10,selected?ui.theme.accent:ui.theme.border);
}

/* ---------- Smooth graphs: between samples the history glides left instead of stepping once a
 * second. Only with animations on (the system's motion choice); otherwise phase stays 0. */
static long sampled_at=-1,frame_clock=-1;
static int graph_phase;
static void zui_graph_smooth(struct zui *u,struct zui_rect r,const unsigned char *v,int s,int c,int cap,unsigned int col,int g){zui_graph_phase(u,r,v,s,c,cap,col,g,graph_phase);}
static int smooth(void){return ui.motion_percent>0&&frame_clock>=0&&sampled_at>=0;}
/* ---------- Data. */
static struct zenith_monitor monitor;
static int have_monitor;
static unsigned char list_page[4096] __attribute__((aligned(16)));
static struct zenith_process_list *processes=(struct zenith_process_list *)list_page;
static int have_processes;
/* Display history is client-side: the kernel reports the last second only. */
static unsigned char frames_history[Z_MONITOR_HISTORY];
static int frames_count;
static unsigned char previous_list[4096];
static struct zenith_activity activity,previous_activity;
static int have_activity;
/* Rates as 0..100 of the larger of the history's peak and 1 MB/s, for zui_graph. */
static unsigned char disk_read_history[Z_MONITOR_HISTORY],disk_write_history[Z_MONITOR_HISTORY];
static unsigned char net_in_history[Z_MONITOR_HISTORY],net_out_history[Z_MONITOR_HISTORY];
static unsigned long disk_scale,net_scale;
static unsigned long scale_for(unsigned long peak){
    unsigned long s=1000000;           /* 1 MB/s, then 2, 5, 10, 20, 50... */
    while(s<peak){unsigned long n=s*2;if(n>=peak){s=n;break;}n=s*5;if(n>=peak){s=n;break;}s*=10;}
    return s;
}
static void rescale(void){
    unsigned long dpeak=0,npeak=0;unsigned int n=activity.samples>Z_MONITOR_HISTORY?Z_MONITOR_HISTORY:activity.samples;
    for(unsigned int i=0;i<n;i++){
        const struct zenith_activity_sample *h=&activity.history[i];
        if(h->read>dpeak)dpeak=h->read;
        if(h->written>dpeak)dpeak=h->written;
        if(h->received>npeak)npeak=h->received;
        if(h->sent>npeak)npeak=h->sent;
    }
    disk_scale=scale_for(dpeak);net_scale=scale_for(npeak);
    for(unsigned int i=0;i<n;i++){
        const struct zenith_activity_sample *h=&activity.history[i];
        disk_read_history[i]=(unsigned char)(h->read*100/disk_scale);disk_write_history[i]=(unsigned char)(h->written*100/disk_scale);
        net_in_history[i]=(unsigned char)(h->received*100/net_scale);net_out_history[i]=(unsigned char)(h->sent*100/net_scale);
    }
}
static unsigned long previous_uptime;
/* Polled four times a second; redraws only when a new sample or a changed program list arrived. */
static void refresh(void){
    have_monitor=zenith_monitor_read(&monitor)==0&&monitor.version==1&&monitor.samples<=Z_MONITOR_HISTORY;
    int had=have_processes;
    have_processes=zenith_process_list_read(list_page)==0&&processes->version==1&&processes->count<=63;
    int changed=had!=have_processes;
    int had_activity=have_activity;
    have_activity=zenith_activity_read(&activity)==0&&activity.version==1&&activity.samples<=Z_MONITOR_HISTORY
        &&activity.disk_count<=Z_ACTIVITY_DISKS&&activity.network_count<=Z_ACTIVITY_NETWORKS&&activity.window_count<=Z_ACTIVITY_WINDOWS;
    if(have_activity!=had_activity)changed=1;
    if(have_activity){
        const unsigned char *a=(const unsigned char *)&activity,*b=(const unsigned char *)&previous_activity;
        /* The sample age moves on every read: compare everything else. */
        int differs=0;for(unsigned int i=0;i<sizeof(activity);i++)if(i<24||i>=28)if(a[i]!=b[i]){differs=1;break;}
        if(differs){previous_activity=activity;rescale();changed=1;}
    }
    for(int i=0;i<4096;i++)if(list_page[i]!=previous_list[i]){changed=1;previous_list[i]=list_page[i];}
    /* A new kernel sample: its time moved by most of a second. Old kernels report age 0. */
    unsigned long sample_time=have_monitor?monitor.uptime_ms-monitor.sample_age_ms:0;
    if(have_monitor&&monitor.samples&&sample_time>=previous_uptime+(monitor.sample_age_ms?500:900)){
        unsigned int fps=monitor.frames_per_second>60?60:monitor.frames_per_second;
        if(frames_count==Z_MONITOR_HISTORY){for(int i=1;i<Z_MONITOR_HISTORY;i++)frames_history[i-1]=frames_history[i];frames_count--;}
        frames_history[frames_count++]=(unsigned char)(fps*100/60);
        previous_uptime=sample_time;changed=1;
        if(frame_clock>=0)sampled_at=frame_clock-(long)monitor.sample_age_ms;
    }
    if(changed)ui.dirty=1;
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
    long clock=zui_motion_clock();if(clock>=0)zui_apply_motion(&ui,clock);
    ui.dirty=1;
}

/* ---------- Navigation: Performance, Apps (when the kernel lists windows), Processes. */
enum { PAGE_PERFORMANCE=0,PAGE_PROCESSES=1,PAGE_APPS=2 };
static int page_shown;
static struct zui_list sidebar;
static int nav_count(void){return have_activity?3:2;}
static int nav_page(int row){return row==0?PAGE_PERFORMANCE:have_activity&&row==1?PAGE_APPS:PAGE_PROCESSES;}
static int nav_row(int page){return page==PAGE_PERFORMANCE?0:page==PAGE_APPS?1:have_activity?2:1;}
static int nav_rows(void *context,int i,struct zui_list_item *o){
    (void)context;
    if(i>=nav_count())return 0;
    int page=nav_page(i);
    if(page==PAGE_PERFORMANCE)*o=(struct zui_list_item){"activity","Performance",0,0,1};
    else if(page==PAGE_APPS)*o=(struct zui_list_item){"layout-grid","Apps",0,0,1};
    else *o=(struct zui_list_item){"list","Processes",0,0,1};
    return 1;
}

/* ---------- Performance. */
enum { CARD_CPU=0,CARD_MEMORY=1,CARD_DISK=2,CARD_NETWORK=3,CARD_DISPLAY=4,CARDS=5 };
static int card;
static struct zui_rect card_rects[CARDS];
/* The cards shown, in order: disk and network only when the kernel reports them. */
static int card_list(int *out){
    int n=0;out[n++]=CARD_CPU;out[n++]=CARD_MEMORY;
    if(have_activity){out[n++]=CARD_DISK;out[n++]=CARD_NETWORK;}
    out[n++]=CARD_DISPLAY;return n;
}
static const char *card_titles[CARDS]={"CPU","Memory","Disk","Network","Display"};
static const char *card_icons[CARDS]={"cpu","memory-stick","hard-drive","network","monitor"};
static unsigned int card_color(int index){
    return index==CARD_CPU?ui.theme.accent:index==CARD_MEMORY?memory_color():index==CARD_DISK?disk_color():index==CARD_NETWORK?net_color():display_color();
}
static unsigned long disk_rate(int write){unsigned long r=0;for(unsigned int i=0;i<activity.disk_count;i++)r+=write?activity.disks[i].write_rate:activity.disks[i].read_rate;return r;}
static unsigned long net_rate(int out){unsigned long r=0;for(unsigned int i=0;i<activity.network_count;i++)r+=out?activity.networks[i].send_rate:activity.networks[i].receive_rate;return r;}
static const struct zenith_activity_network *primary_network(void){
    for(unsigned int i=0;i<activity.network_count;i++)if(activity.networks[i].flags&Z_NET_PRIMARY)return &activity.networks[i];
    return activity.network_count?&activity.networks[0]:0;
}
static char *address_text(char *o,const unsigned char *a){
    for(int i=0;i<4;i++){if(i)*o++='.';o=num(o,a[i]);}return o;
}
static void stat(int x,int y,const char *label,const char *value){
    text(x,y,label,&zui_caption,ui.theme.secondary);
    text(x,y+18,value,&zui_label,ui.theme.text);
}
/* A card's graph: one series, or two (read and write, in and out) over each other. */
static void card_graph(int index,struct zui_rect g,int capacity,int grid){
    unsigned int color=card_color(index);
    if(index==CARD_CPU)zui_graph_smooth(&ui,g,&monitor.history[0].total,16,(int)monitor.samples,capacity,color,grid);
    else if(index==CARD_MEMORY)zui_graph_smooth(&ui,g,&monitor.history[0].memory,16,(int)monitor.samples,capacity,color,grid);
    else if(index==CARD_DISK){
        zui_graph_smooth(&ui,g,disk_read_history,1,(int)activity.samples,capacity,color,grid);
        zui_graph_smooth(&ui,g,disk_write_history,1,(int)activity.samples,capacity,disk_write_color(),0);
    }else if(index==CARD_NETWORK){
        zui_graph_smooth(&ui,g,net_in_history,1,(int)activity.samples,capacity,color,grid);
        zui_graph_smooth(&ui,g,net_out_history,1,(int)activity.samples,capacity,net_out_color(),0);
    }else zui_graph_smooth(&ui,g,frames_history,1,frames_count,capacity,color,grid);
}
static void draw_card(int index,struct zui_rect r,int compact){
    char value[48],caption[64],*o;
    card_rects[index]=r;panel(r,card==index);
    /* Narrow cards (five in a row) drop the icon and put the value under the title. */
    int narrow=r.w<150,tx=narrow?r.x+10:r.x+36;
    if(!narrow)zui_icon(&ui,card_icons[index],r.x+12,r.y+12,16,ZUI_ICON_NORMAL);
    text(tx,r.y+(narrow?8:12),card_titles[index],&zui_label,ui.theme.text);
    if(index==CARD_CPU){
        o=num(value,monitor.latest.total);put(o,"%");
        o=num(caption,(unsigned long)busy_cores());o=put(o," of ");o=num(o,monitor.cpus);put(o,monitor.cpus==1?" CPU busy":" CPUs busy");
    }else if(index==CARD_MEMORY){
        if(compact){o=num(value,monitor.latest.memory);put(o,"%");}
        else{o=size_text(value,monitor.memory_total-monitor.memory_free);o=put(o," / ");size_text(o,monitor.memory_total);}
        o=num(caption,monitor.latest.memory);put(o,"% in use");
    }else if(index==CARD_DISK){
        rate_text(value,disk_rate(0)+disk_rate(1));
        o=put(caption,"read ");o=rate_text(o,disk_rate(0));o=put(o," \xc2\xb7 write ");rate_text(o,disk_rate(1));
    }else if(index==CARD_NETWORK){
        rate_text(value,net_rate(0)+net_rate(1));
        o=put(caption,"in ");o=rate_text(o,net_rate(0));o=put(o," \xc2\xb7 out ");rate_text(o,net_rate(1));
    }else{
        o=num(value,monitor.frames_per_second);put(o," fps");
        o=put(caption,"worst ");o=num(o,(monitor.frame_worst_us+500)/1000);put(o," ms");
    }
    /* The value sits beside the title when both fit, otherwise under it; the graph takes what is left. */
    if(narrow){
        int vw=zui_measure(value,length(value),&zui_caption);
        if(vw<=r.w-20)text(tx,r.y+28,value,&zui_caption,ui.theme.secondary);
        int top=r.y+46,low=r.y+r.h-6;
        if(low-top>=6)card_graph(index,(struct zui_rect){r.x+8,top,r.w-16,low-top},60,0);
        return;
    }
    int beside=!compact&&36+zui_measure(card_titles[index],length(card_titles[index]),&zui_label)+16+zui_measure(value,length(value),&zui_label)<=r.w-12;
    int under=!beside&&r.h>=72;
    if(beside)text_right(r.x+r.w-12,r.y+12,value,&zui_label,ui.theme.text);
    else if(under)text(r.x+12,r.y+32,value,&zui_label,ui.theme.text);
    int top=beside?r.y+38:under?r.y+52:r.y+34,low=compact?r.y+r.h-6:r.y+r.h-30;
    if(low-top>=8)card_graph(index,(struct zui_rect){r.x+12,top,r.w-24,low-top},60,0);
    if(!compact)text(r.x+12,r.y+r.h-24,caption,&zui_caption,ui.theme.secondary);
}
/* A legend entry under the main graph: a swatch and its name. */
static int legend(int x,int y,unsigned int color,const char *label){
    zui_round(&ui,(struct zui_rect){x,y+4,10,10},3,color);
    text(x+16,y,label,&zui_caption,ui.theme.secondary);
    return x+16+zui_measure(label,length(label),&zui_caption)+18;
}
static void draw_performance(struct zui_rect c){
    if(!have_monitor){
        text(c.x+24,c.y+24,"This system does not report performance.",&zui_label,ui.theme.text);
        text(c.x+24,c.y+48,"Observatory needs a newer Zenith kernel. Update Zenith, then reopen it.",&zui_body,ui.theme.secondary);
        return;
    }
    int cards[CARDS],n=card_list(cards);
    int known=0;for(int i=0;i<n;i++)if(cards[i]==card)known=1;
    if(!known)card=CARD_CPU;
    for(int i=0;i<CARDS;i++)card_rects[i]=(struct zui_rect){0,0,0,0};
    /* Wide: cards in a column beside the main view. Narrow (the 640x480 a window opens at):
     * compact cards in a row above it. */
    int compact=c.w<720,x=c.x+12,y=c.y+12,mx,mw,my;
    if(compact){
        int gap=8,cw=(c.w-24-(n-1)*gap)/n,ch=80;
        for(int i=0;i<n;i++)draw_card(cards[i],(struct zui_rect){x+i*(cw+gap),y,cw,ch},1);
        mx=x;mw=c.w-24;my=y+ch+16;
    }else{
        int cw=c.w>900?262:220,gap=12,ch=(c.h-24-(n-1)*gap)/n;
        if(ch>108)ch=108;
        int small=ch<96;
        for(int i=0;i<n;i++)draw_card(cards[i],(struct zui_rect){x,y+i*(ch+gap),cw,ch},small);
        mx=x+cw+20;mw=c.x+c.w-12-mx;my=c.y+12;
    }
    int bottom=c.y+c.h-12;
    if(mw<200)return;
    char sub[128],*o=sub;
    const struct zenith_activity_network *net=have_activity?primary_network():0;
    if(card==CARD_CPU){o=put(o,monitor.processor[0]?monitor.processor:"Processor");o=put(o,", ");o=num(o,monitor.cpus);o=put(o,monitor.cpus==1?" CPU in use":" CPUs in use");}
    else if(card==CARD_MEMORY){
        /* Installed comes from the firmware; Zenith's allocator may use only its largest region. */
        unsigned long installed=monitor.memory_installed>monitor.memory_total?monitor.memory_installed:monitor.memory_total;
        o=size_text(o,installed);o=put(o," installed");
        if(installed>monitor.memory_total+(64UL<<20)){o=put(o,", ");o=size_text(o,monitor.memory_total);put(o," usable by Zenith");}
    }
    else if(card==CARD_DISK){
        if(!activity.disk_count)put(o,"No disks attached");
        else for(unsigned int i=0;i<activity.disk_count&&i<4;i++){if(i)o=put(o,", ");o=put(o,activity.disks[i].name);}
    }
    else if(card==CARD_NETWORK){
        if(!net)put(o,"No network adapter");
        else{o=put(o,net->name);o=put(o,net->flags&Z_NET_LINK_UP?", link up":", link down");}
    }
    else put(o,"Frames the desktop composed");
    text(mx,my,card_titles[card],&zui_heading,ui.theme.text);
    text(mx+zui_measure(card_titles[card],length(card_titles[card]),&zui_heading)+14,my+10,sub,&zui_caption,ui.theme.secondary);
    int stats_h=compact?100:118,cores=card==CARD_CPU&&monitor.cpus>1?(int)monitor.cpus:0,cores_h=cores?(compact?64:92):0;
    int legend_h=card==CARD_DISK||card==CARD_NETWORK?22:0;
    int gy=my+44,gh=bottom-gy-stats_h-cores_h-(cores?12:0)-legend_h;
    if(gh<60)gh=60;
    struct zui_rect graph={mx,gy,mw,gh};panel(graph,0);
    struct zui_rect inner={graph.x+1,graph.y+24,graph.w-2,graph.h-48};
    card_graph(card,inner,Z_MONITOR_HISTORY,1);
    char top_label[32];
    if(card==CARD_DISK)rate_text(top_label,disk_scale);
    else if(card==CARD_NETWORK)rate_text(top_label,net_scale);
    else put(top_label,card==CARD_DISPLAY?"60 fps":"100%");
    text(graph.x+10,graph.y+6,top_label,&zui_caption,ui.theme.secondary);
    text(graph.x+10,graph.y+graph.h-20,"2 min ago",&zui_caption,ui.theme.secondary);
    text_right(graph.x+graph.w-10,graph.y+graph.h-20,"now",&zui_caption,ui.theme.secondary);
    int sy=gy+gh+12;
    if(legend_h){
        int lx=mx;
        lx=legend(lx,sy-4,card_color(card),card==CARD_DISK?"Read":"Received");
        legend(lx,sy-4,card==CARD_DISK?disk_write_color():net_out_color(),card==CARD_DISK?"Written":"Sent");
        sy+=legend_h;
    }
    if(cores){
        int nc=cores>8?8:cores,gap=10,w=(mw-gap*(nc-1))/nc;
        for(int i=0;i<nc;i++){
            struct zui_rect t={mx+i*(w+gap),sy,w,cores_h};panel(t,0);
            char label[16],pct[8];o=put(label,"CPU ");num(o,(unsigned long)i);
            int v=monitor.latest.cpu[i];
            if(v==Z_MONITOR_OFFLINE)put(pct,"off");else{o=num(pct,(unsigned long)v);put(o,"%");}
            /* Narrow tiles (many CPUs) stack the share under the name instead of colliding with it. */
            int fits=zui_measure(label,length(label),&zui_caption)+12+zui_measure(pct,length(pct),&zui_caption)<=t.w-20;
            text(t.x+10,t.y+8,label,&zui_caption,ui.theme.secondary);
            if(fits)text_right(t.x+t.w-10,t.y+8,pct,&zui_caption,ui.theme.text);else text(t.x+10,t.y+24,pct,&zui_caption,ui.theme.text);
            int gt=fits?30:44;
            zui_graph_smooth(&ui,(struct zui_rect){t.x+6,t.y+gt,t.w-12,t.h-gt-6},&monitor.history[0].cpu[i],16,(int)monitor.samples,60,card_color(CARD_CPU),0);
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
        o=num(d,monitor.latest.memory);put(o,"%");num(e,monitor.programs);
        size_text(f,monitor.memory_installed>monitor.memory_total?monitor.memory_installed:monitor.memory_total);
        stat(mx,sy,"In use",a);stat(mx+col,sy,"Free",b);stat(mx+2*col,sy,"Installed",f);
        stat(mx,sy+48,"Share in use",d);stat(mx+col,sy+48,"Programs",e);stat(mx+2*col,sy+48,"Usable by Zenith",cc);
    }else if(card==CARD_DISK){
        unsigned long read=0,written=0;
        for(unsigned int i=0;i<activity.disk_count;i++){read+=activity.disks[i].read;written+=activity.disks[i].written;}
        rate_text(a,disk_rate(0));rate_text(b,disk_rate(1));num(cc,activity.disk_count);
        bytes_text(d,read);bytes_text(e,written);duration_text(f,monitor.uptime_ms);
        stat(mx,sy,"Read",a);stat(mx+col,sy,"Write",b);stat(mx+2*col,sy,"Disks",cc);
        stat(mx,sy+48,"Read since boot",d);stat(mx+col,sy+48,"Written since boot",e);stat(mx+2*col,sy+48,"Uptime",f);
        text(mx,sy+96,"Counts ext2 volumes and raw disk access; FAT volumes are not counted yet.",&zui_caption,ui.theme.secondary);
    }else if(card==CARD_NETWORK){
        rate_text(a,net_rate(0));rate_text(b,net_rate(1));
        if(net&&(net->address[0]|net->address[1]|net->address[2]|net->address[3]))address_text(cc,net->address);else put(cc,"None");
        bytes_text(d,net?net->received:0);bytes_text(e,net?net->sent:0);
        o=f;
        if(net){static const char hex[]="0123456789abcdef";for(int i=0;i<6;i++){if(i)*o++=':';*o++=hex[net->mac[i]>>4];*o++=hex[net->mac[i]&15];}*o=0;}
        else put(f,"-");
        stat(mx,sy,"Receive",a);stat(mx+col,sy,"Send",b);stat(mx+2*col,sy,"IPv4 address",cc);
        stat(mx,sy+48,"Received since boot",d);stat(mx+col,sy+48,"Sent since boot",e);stat(mx+2*col,sy+48,"Hardware address",f);
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
static const struct zenith_process_entry *find_process(int pid){
    if(!have_processes)return 0;
    for(unsigned int i=0;i<processes->count;i++)if(entry((int)i)->pid==pid)return entry((int)i);
    return 0;
}
static int end_pid;
static void ask_end_pid(int pid){
    const struct zenith_process_entry *s=find_process(pid);
    if(!s||(s->flags&Z_PROCESS_HAS_EXITED)||stopping(s))return;
    end_pid=pid;
    char *o=put(confirm_title,"End ");o=put(o,s->name);put(o,"?");
    confirm.title=confirm_title;confirm.message="Observatory asks it to stop, as kill does. Unsaved work in it may be lost.";
    confirm.open=1;ui.dirty=1;
}
static void ask_end(void){ask_end_pid(selected_pid);}
static void end_selected(void){
    const struct zenith_process_entry *s=find_process(end_pid);
    if(!s)return;
    long r=zenith_process_stop(s->pid);
    status_line[0]=0;
    if(r==-13)put(status_line,"You can end only your own programs.");
    else if(r==-3)put(status_line,"That program has already ended.");
    else if(r<0)put(status_line,"Could not ask it to stop.");
    refresh();
}

/* ---------- Apps: the desktop's windows grouped by the program that owns them. */
#define END_APP_BUTTON 81
#define MAX_APPS 25
static int app_pids[MAX_APPS],app_count,selected_app=-1;
static struct zui_rect app_rects[MAX_APPS],apps_area;
static struct zui_scroll apps_scroll;
static int app_heights[MAX_APPS];
static void arrange_apps(void){
    app_count=0;
    if(!have_activity)return;
    /* Programs in the order their first window appears; the desktop's own windows last. */
    int desktop=0;
    for(unsigned int i=0;i<activity.window_count;i++){
        int pid=activity.windows[i].pid;
        if(pid==0){desktop=1;continue;}
        int seen=0;for(int k=0;k<app_count;k++)if(app_pids[k]==pid)seen=1;
        if(!seen&&app_count<MAX_APPS-1)app_pids[app_count++]=pid;
    }
    if(desktop)app_pids[app_count++]=0;
    int found=0;for(int k=0;k<app_count;k++)if(app_pids[k]==selected_app)found=1;
    if(!found)selected_app=app_count?app_pids[0]:-1;
}
static int app_windows(int pid){int n=0;for(unsigned int i=0;i<activity.window_count;i++)if(activity.windows[i].pid==pid)n++;return n;}
static const char *app_name(int pid){
    if(pid==0)return "Desktop";
    const struct zenith_process_entry *p=find_process(pid);
    return p?p->name:"Program";
}
static void draw_apps(struct zui_rect c){
    if(!have_activity){
        text(c.x+24,c.y+24,"This system does not list windows.",&zui_label,ui.theme.text);
        text(c.x+24,c.y+48,"Observatory needs a newer Zenith kernel. Update Zenith, then reopen it.",&zui_body,ui.theme.secondary);
        return;
    }
    arrange_apps();
    int x=c.x+12,w=c.w-24,y=c.y+12;
    text(x,y+6,"Apps",&zui_heading,ui.theme.text);
    char sub[64],*o=num(sub,(unsigned long)app_count);o=put(o,app_count==1?" app, ":" apps, ");o=num(o,activity.window_count);put(o,activity.window_count==1?" window":" windows");
    text(x+zui_measure("Apps",4,&zui_heading)+14,y+16,sub,&zui_caption,ui.theme.secondary);
    const struct zenith_process_entry *sp=selected_app>0?find_process(selected_app):0;
    zui_add(&ui,END_APP_BUTTON,ZUI_BUTTON,(struct zui_rect){x+w-140,y,140,34},"End app",0,sp&&!(sp->flags&Z_PROCESS_HAS_EXITED)&&!stopping(sp));
    if(status_line[0])text_right(x+w-152,y+9,status_line,&zui_caption,warn());
    apps_area=(struct zui_rect){x,y+48,w,c.y+c.h-12-(y+48)};
    zui_scroll_begin(&ui,&apps_scroll,apps_area);
    int top=apps_area.y-apps_scroll.visual,total=0;
    for(int k=0;k<app_count;k++){
        int pid=app_pids[k],windows=app_windows(pid),h=48+windows*26+8;
        app_heights[k]=h;
        struct zui_rect r={x,top,w,h};app_rects[k]=r;
        if(top+h>=apps_area.y&&top<=apps_area.y+apps_area.h){
            panel(r,pid==selected_app);
            const struct zenith_process_entry *p=pid?find_process(pid):0;
            zui_icon(&ui,pid?process_icon(app_name(pid)):"monitor",r.x+14,r.y+15,16,ZUI_ICON_NORMAL);
            text(r.x+38,r.y+14,app_name(pid),&zui_label,ui.theme.text);
            char info[96];o=info;
            if(pid){o=put(o,"PID ");o=num(o,(unsigned long)pid);if(p){o=put(o," \xc2\xb7 ");o=put(o,p->user);o=put(o," \xc2\xb7 ");o=put(o,state_text(p));
                if(p->cpu>=0&&!(p->flags&Z_PROCESS_HAS_EXITED)){o=put(o," on CPU ");o=num(o,(unsigned long)p->cpu);}}}
            else put(o,"Built into the desktop");
            text_right(r.x+r.w-14,r.y+15,info,&zui_caption,ui.theme.secondary);
            int wy=r.y+44;
            for(unsigned int i=0;i<activity.window_count;i++){
                const struct zenith_activity_window *win=&activity.windows[i];
                if(win->pid!=pid)continue;
                zui_icon(&ui,"app-window",r.x+38,wy+1,16,ZUI_ICON_TEXT);
                text(r.x+62,wy,win->title[0]?win->title:"Untitled",&zui_body,win->flags&Z_WINDOW_MINIMIZED?ui.theme.secondary:ui.theme.text);
                const char *state=win->flags&Z_WINDOW_FOCUSED?"Focused":win->flags&Z_WINDOW_MINIMIZED?"Minimized":"Open";
                text_right(r.x+r.w-14,wy,state,&zui_caption,win->flags&Z_WINDOW_FOCUSED?good():ui.theme.secondary);
                wy+=26;
            }
        }
        top+=h+10;total+=h+10;
    }
    if(!app_count)text(x+12,apps_area.y+10,"No windows are open.",&zui_body,ui.theme.secondary);
    zui_scroll_end(&ui,&apps_scroll,total);
}
static void select_app_step(int step){
    if(!app_count)return;
    int at=-1;for(int k=0;k<app_count;k++)if(app_pids[k]==selected_app)at=k;
    at=at<0?0:at+step;
    if(at<0)at=0;
    if(at>=app_count)at=app_count-1;
    selected_app=app_pids[at];
    int y=0;for(int k=0;k<at;k++)y+=app_heights[k]+10;
    zui_scroll_reveal(&apps_scroll,y,app_heights[at]);ui.dirty=1;
}

/* ---------- Main loop. */
static void draw(void){
    struct zui_workspace w=zui_workspace_begin(&ui,ui.width<800?156:196);struct zui_rect c=w.content;
    int focus=ui.focus>=0&&ui.focus<ui.count?ui.controls[ui.focus].id:0;
    int pressed=ui.pressed>=0&&ui.pressed<ui.count?ui.controls[ui.pressed].id:0;
    ui.count=0;ui.pressed=-1;ui.focus=-1;
    text(24,24,"Observatory",&zui_label,ui.theme.text);
    if(page_shown==PAGE_APPS&&!have_activity)page_shown=PAGE_PERFORMANCE;
    sidebar.selected=nav_row(page_shown);
    zui_list_draw(&ui,&sidebar,(struct zui_rect){w.sidebar.x,w.sidebar.y+44,w.sidebar.w,w.sidebar.h-44},nav_count(),nav_rows,0);
    zui_clip(&ui,c);
    if(page_shown==PAGE_PERFORMANCE)draw_performance(c);else if(page_shown==PAGE_APPS)draw_apps(c);else draw_processes(c);
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
    if(page_shown==PAGE_APPS){
        if(!inside(apps_area,e->x,e->y))return;
        for(int k=0;k<app_count;k++)if(inside(app_rects[k],e->x,e->y)&&selected_app!=app_pids[k]){selected_app=app_pids[k];status_line[0]=0;ui.dirty=1;}
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
/* --probe: the activity snapshot as text (tests; no window). */
static int probe(void){
    refresh();
    if(!have_activity){print("OBSERVATORY_NO_ACTIVITY\n");return 1;}
    char line[160],*o=put(line,"OBSERVATORY_ACTIVITY disks=");o=num(o,activity.disk_count);o=put(o," networks=");o=num(o,activity.network_count);
    o=put(o," windows=");o=num(o,activity.window_count);o=put(o," samples=");o=num(o,activity.samples);put(o,"\n");print(line);
    for(unsigned int i=0;i<activity.disk_count;i++){o=put(line,"OBSERVATORY_DISK ");o=put(o,activity.disks[i].name);o=put(o," read=");o=num(o,activity.disks[i].read);o=put(o," written=");o=num(o,activity.disks[i].written);put(o,"\n");print(line);}
    for(unsigned int i=0;i<activity.network_count;i++){const struct zenith_activity_network *n=&activity.networks[i];
        o=put(line,"OBSERVATORY_NETWORK ");o=put(o,n->name);o=put(o," received=");o=num(o,n->received);o=put(o," sent=");o=num(o,n->sent);o=put(o," flags=");o=num(o,n->flags);o=put(o," address=");o=address_text(o,n->address);put(o,"\n");print(line);}
    for(unsigned int i=0;i<activity.window_count;i++){o=put(line,"OBSERVATORY_WINDOW pid=");o=num(o,(unsigned long)activity.windows[i].pid);o=put(o," ");o=put(o,activity.windows[i].title);put(o,"\n");print(line);}
    return 0;
}
int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("observatory " SUNBEAM_VERSION "\n");return 0;}
    if(argc==2&&same(argv[1],"--probe"))return probe();
    if(argc!=1&&!(argc==2&&(same(argv[1],"processes")||same(argv[1],"apps")))){print("usage: native run observatory [apps|processes]\n");return 2;}
    if(argc==2)page_shown=same(argv[1],"apps")?PAGE_APPS:PAGE_PROCESSES;
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
        long now=zui_motion_clock();frame_clock=now;
        if((now>=0&&now-last>=250)||(now<0&&++polls>=200)){last=now;polls=0;refresh();}
        /* About 30 frames a second while gliding, and only on the Performance page. */
        static long drawn=-1;
        if(smooth()&&page_shown==PAGE_PERFORMANCE&&now-drawn>=33){
            long into=now-sampled_at;graph_phase=into<=0?0:into>=1000?255:(int)(into*256/1000);
            ui.dirty=1;
        }
        if(ui.dirty)drawn=now;
        if(ui.dirty){
            draw();
            long r=material?zenith_gui_present_material(handle,ui.pixels,(unsigned long)ui.width*ui.height*4,ui.materials,ui.material_count):zenith_gui_present(handle,ui.pixels,(unsigned long)ui.width*ui.height*4);
            if(r<0){result=zui_window_closed(handle)?0:1;break;}
        }
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_RESIZE){
            if(zui_apply_viewport(&ui,handle,640*480)<0){result=zui_window_closed(handle)?0:1;break;}
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
        if(e.type==Z_GUI_KEY&&page_shown==PAGE_APPS&&!e.modifiers){
            if(e.extra==7){select_app_step(-1);continue;}
            if(e.extra==8){select_app_step(1);continue;}
        }
        if(e.type==Z_GUI_KEY&&page_shown==PAGE_PROCESSES&&!e.modifiers){
            if(e.extra==7){select_step(-1);continue;}
            if(e.extra==8){select_step(1);continue;}
        }
        if(e.type==Z_GUI_KEY&&page_shown==PAGE_PROCESSES&&(e.modifiers&2)&&(e.code=='f'||e.code=='F')){filter_focus=1;ui.dirty=1;continue;}
        if(e.type==Z_GUI_DOWN||e.type==Z_GUI_UP)pointer(&e);
        if(page_shown==PAGE_PROCESSES&&zui_scroll_event(&ui,&rows_scroll,&e))ui.dirty=1;
        if(page_shown==PAGE_APPS&&zui_scroll_event(&ui,&apps_scroll,&e))ui.dirty=1;
        int list=zui_list_event(&ui,&sidebar,&e,nav_rows,0);
        if(list>0&&nav_page(list-1)!=page_shown){page_shown=nav_page(list-1);filter_focus=0;status_line[0]=0;ui.dirty=1;continue;}
        int action=zui_event(&ui,&e);
        if(action==END_BUTTON)ask_end();
        if(action==END_APP_BUTTON&&selected_app>0)ask_end_pid(selected_app);
        if(action>=10&&action<10+nav_count()&&nav_page(action-10)!=page_shown){page_shown=nav_page(action-10);filter_focus=0;status_line[0]=0;ui.dirty=1;}
    }
    zenith_gui_close(handle);return result;
}
