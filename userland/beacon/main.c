/* Beacon: the network, in plain words (docs/components/BEACON.md, concept docs/design/beacon-concept.svg, after
 * NetworkManager with a clearer flow). Opens on an answer: are you online, and how. Connections are grouped by kind and
 * absent hardware says so. Traffic for the last minute, hour, day and week, a history of what happened, diagnostics
 * that check each step from the adapter outward and stop at the first real failure, ping, and an editor whose changes
 * keep the old settings until the new ones connect. Reads and changes the network through call 667
 * (zenith-native-network-center-abi 1.0, kernel 0.0.69). */
#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
#include "../include/zenith_files.h"
#include "../include/zenith_portal.h"
#include "../include/zenith_network.h"

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
static char *two(char *o,unsigned long v){*o++=(char)('0'+v/10%10);*o++=(char)('0'+v%10);*o=0;return o;}
static char *ip_text(char *o,const unsigned char *a){for(int i=0;i<4;i++){o=num(o,a[i]);if(i<3)*o++='.';}*o=0;return o;}
static int ip_zero(const unsigned char *a){return !a[0]&&!a[1]&&!a[2]&&!a[3];}
static int prefix_of(const unsigned char *m){int n=0;for(int i=0;i<4;i++)for(int b=7;b>=0;b--)if(m[i]>>b&1)n++;else return n;return n;}
static char *rate_text(char *o,unsigned long r){
    /* Bits per second, as network speeds are spoken of: 0 b/s, 840 kb/s, 8.2 Mb/s. */
    unsigned long b=r*8;
    if(b<1000){o=num(o,b);return put(o," b/s");}
    const char *units[]={" kb/s"," Mb/s"," Gb/s"};int u=0;unsigned long scale=1000;
    while(u<2&&b>=scale*1000){scale*=1000;u++;}
    unsigned long whole=b/scale,tenth=b%scale*10/scale;o=num(o,whole);if(whole<100){*o++='.';o=num(o,tenth);}
    return put(o,units[u]);
}
static char *bytes_text(char *o,unsigned long b){
    if(b<1000){o=num(o,b);return put(o," B");}
    const char *units[]={" KB"," MB"," GB"," TB"};int u=0;unsigned long scale=1000;
    while(u<3&&b>=scale*1000){scale*=1000;u++;}
    unsigned long whole=b/scale,tenth=b%scale*10/scale;o=num(o,whole);if(whole<100){*o++='.';o=num(o,tenth);}
    return put(o,units[u]);
}
static char *duration_text(char *o,long s){
    if(s<60){o=num(o,(unsigned long)s);return put(o," s");}
    if(s<3600){o=num(o,(unsigned long)(s/60));return put(o," min");}
    if(s<86400){o=num(o,(unsigned long)(s/3600));o=put(o," h");if(s%3600/60){o=put(o," ");o=num(o,(unsigned long)(s%3600/60));o=put(o," min");}return o;}
    o=num(o,(unsigned long)(s/86400));o=put(o," d");if(s%86400/3600){o=put(o," ");o=num(o,(unsigned long)(s%86400/3600));o=put(o," h");}return o;
}
static void text(int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(&ui,x,y,s,length(s),f,c);}
static int measure(const char *s,const struct zui_font *f){return zui_measure(s,length(s),f);}
static void text_right(int right,int y,const char *s,const struct zui_font *f,unsigned int c){text(right-measure(s,f),y,s,f,c);}
static void fit(int x,int y,const char *s,int width,const struct zui_font *f,unsigned int c){
    unsigned long n=length(s);
    if(zui_measure(s,n,f)<=width){zui_text(&ui,x,y,s,n,f,c);return;}
    int dots=zui_measure("...",3,f);
    while(n&&zui_measure(s,n,f)+dots>width)n--;
    zui_text(&ui,x,y,s,n,f,c);zui_text(&ui,x+zui_measure(s,n,f),y,"...",3,f,c);
}
static int inside(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x<r.x+r.w&&y<r.y+r.h;}
static int light(void){unsigned int s=ui.theme.surface;return ((s>>16&255)+(s>>8&255)+(s&255))>384;}
static unsigned int good(void){return light()?0x277347:0x8BCBA4;}
static unsigned int warn(void){return light()?0x886010:0xE6C47A;}
static unsigned int bad(void){return light()?0xAE325D:0xF29BAE;}
static unsigned int down_color(void){return light()?0x1F6F8B:0x7CC7E8;}
static unsigned int up_color(void){return light()?0xA2416B:0xF0A0C0;}
static void panel(struct zui_rect r){
    if(ui.foreground&&ui.full_background&&!ui.theme_preview)zui_round_alpha(&ui,r,12,ui.theme.raised,90);
    else zui_round(&ui,r,12,ui.theme.raised);
    zui_round_outline(&ui,r,12,ui.theme.border);
}

/* ---------- Data from the kernel. */
static struct zenith_network_status net;
static struct zenith_network_history traffic;
static struct zenith_network_events events;
static int have_status,range=Z_NET_MINUTE;
static unsigned int seen_events,seen_state=99;
static void refresh(void){
    struct zenith_network_status s;
    long r=zenith_network(Z_NET_STATUS,0,&s,sizeof(s),0,0);
    have_status=r==(long)sizeof(s)&&s.version==1;
    if(!have_status)return;
    if(s.state!=seen_state){
        char line[200],*o=put(line,"BEACON_STATE ");o=num(o,s.state);o=put(o," ");o=ip_text(o,s.address);o=put(o," ");put(o,s.state_text);
        o=line+length(line);put(o,"\n");print(line);seen_state=s.state;
    }
    net=s;
    zenith_network(Z_NET_HISTORY,(unsigned int)range,&traffic,sizeof(traffic),0,0);
    if(s.events!=seen_events){zenith_network(Z_NET_EVENTS,0,&events,sizeof(events),0,0);seen_events=s.events;}
    ui.dirty=1;
}
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

/* ---------- Words for the state. */
static int internet_ok=-1;   /* from the last diagnostics: -1 unknown, 0 no, 1 yes */
static const char *headline(void){
    if(!have_status)return "The network service is not available";
    switch(net.state){
    case Z_NET_NO_ADAPTER:return "No network adapter";
    case Z_NET_OFF:return net.mode==2?"The network is turned off":"Disconnected";
    case Z_NET_CABLE_OUT:return "The cable is out";
    case Z_NET_CONNECTING:return "Connecting...";
    case Z_NET_ONLINE:return internet_ok==0?"Connected to the local network only":internet_ok==1?"Connected to the internet":"Connected";
    default:return "Not connected";
    }
}
static unsigned int state_color(void){
    if(!have_status)return bad();
    return net.state==Z_NET_ONLINE?(internet_ok==0?warn():good()):net.state==Z_NET_CONNECTING||net.state==Z_NET_OFF?warn():bad();
}
static char *subline(char *o){
    if(!have_status)return put(o,"This Zenith kernel does not report the network to programs (it needs 0.0.69).");
    if(net.state==Z_NET_NO_ADAPTER)return put(o,"Zenith supports Intel E1000E wired adapters. Add one to the VM.");
    if(net.state==Z_NET_CABLE_OUT)return put(o,"Wired \xc2\xb7 no link. Connect the cable or the VM's network.");
    if(net.state==Z_NET_OFF)return put(o,net.mode==2?"Wired \xc2\xb7 turned off in the connection settings":"Wired \xc2\xb7 disconnected until you connect again");
    if(net.state==Z_NET_FAILED){o=put(o,"Wired \xc2\xb7 ");return put(o,net.mode==0?"no answer from DHCP":"the manual settings did not connect");}
    o=put(o,"Wired \xc2\xb7 ");o=put(o,net.mode==1?"manual":"DHCP");
    if(net.link_mbps){o=put(o," \xc2\xb7 ");o=num(o,net.link_mbps);o=put(o," Mb/s");}
    if(net.connected_seconds>=0){o=put(o," \xc2\xb7 up for ");o=duration_text(o,net.connected_seconds);}
    return o;
}
static char *clock_text(char *o,long when){
    long local=when+net.utc_offset,now=net.utc_now+net.utc_offset;
    long day=local/86400,today=now/86400;
    if(day==today-1)o=put(o,"Yesterday ");
    else if(day<today-1){long ago=today-day;o=num(o,(unsigned long)ago);o=put(o," days ago ");}
    long s=local%86400;if(s<0)s+=86400;
    o=two(o,(unsigned long)(s/3600));*o++=':';return two(o,(unsigned long)(s%3600/60));
}

/* ---------- Navigation: Connections (Wired, Wi-Fi, VPN) and Tools (Traffic, Diagnostics, History). */
enum { PAGE_WIRED,PAGE_WIFI,PAGE_VPN,PAGE_TRAFFIC,PAGE_DIAGNOSTICS,PAGE_HISTORY,PAGES };
static const char *page_names[PAGES]={"Wired","Wi-Fi","VPN","Traffic","Diagnostics","History"};
static const char *page_icons[PAGES]={"cable","wifi-off","shield","activity","stethoscope","clock"};
static int page;
enum { ID_NAV=10,ID_CONNECT=40,ID_EDIT=41,ID_SEE_ALL=42,ID_RANGE=50,ID_RUN=60,ID_FIX=61,ID_PING=62,ID_PING_FIELD=63,
       ID_MODE_AUTO=70,ID_MODE_MANUAL=71,ID_APPLY=72,ID_CANCEL=73,ID_FIELD=80 };
/* A new page crossfades in (call 629, as in Meridian); older kernels refuse it and the page appears at once. */
static int fade_page;
static void go(int p){if(p==page)return;page=p;char line[48],*o=put(line,"BEACON_PAGE ");o=put(o,page_names[p]);put(o,"\n");print(line);ui.dirty=1;fade_page=1;}

/* ---------- Asking for permission: changes need root or the org.zenith.network.manage grant. */
enum { PENDING_NONE,PENDING_APPLY,PENDING_CONNECT,PENDING_DISCONNECT };
static int pending;static long auth_handle;static int granted;
static char note[200];static int note_bad;
static void say(const char *s,int is_bad){copy(note,s,sizeof(note));note_bad=is_bad;ui.dirty=1;}
static void run_change(int what);
static void ask_permission(int what){
    if(auth_handle>0)return;
    long h=zenith_authorize("Authorize",Z_ACTION_NETWORK);
    if(h<0){say(h==-38?"This Zenith cannot ask for permission. Run Beacon as root.":"Changing the network needs an administrator.",1);return;}
    auth_handle=h;pending=what;print("BEACON_AUTHORIZE asking\n");say("Waiting for an administrator to allow the change...",0);
}
static void poll_permission(void){
    if(auth_handle<=0)return;
    static struct zenith_portal_response r;long s=zenith_portal_poll(auth_handle,&r);
    if(s<0){auth_handle=0;return;}
    if(!Z_PORTAL_FINAL(r.state))return;
    zenith_portal_close(auth_handle);auth_handle=0;
    if(r.state==Z_PORTAL_APPROVED){granted=1;print("BEACON_AUTHORIZE granted\n");int what=pending;pending=PENDING_NONE;run_change(what);}
    else{pending=PENDING_NONE;print("BEACON_AUTHORIZE denied\n");say("The change was not allowed.",1);}
}

/* ---------- Editing the connection. */
static int editing,edit_manual,edit_field;
static char field_text[4][40];static struct zui_input fields[4];
static const char *field_labels[4]={"Address","Gateway","DNS","Prefix"};
static int parse_ip(const char *s,unsigned char *out){
    unsigned long part=0;int parts=0,digits=0;
    for(;;s++){
        if(*s>='0'&&*s<='9'){part=part*10+(unsigned long)(*s-'0');if(++digits>3||part>255)return 0;continue;}
        if(*s=='.'||!*s){if(!digits||parts>3)return 0;out[parts++]=(unsigned char)part;part=0;digits=0;if(!*s)break;continue;}
        return 0;
    }
    return parts==4;
}
static void open_editor(void){
    if(!have_status)return;
    editing=1;edit_manual=net.mode==1;edit_field=0;
    char t[40];
    for(int i=0;i<4;i++)zui_input_init(&fields[i],field_text[i],sizeof(field_text[i]));
    const unsigned char *a=net.mode==1?net.saved_address:net.address,*g=net.mode==1?net.saved_gateway:net.gateway,*d=net.mode==1?net.saved_dns:net.dns[0];
    ip_text(t,a);if(!ip_zero(a))copy(field_text[0],t,40);
    ip_text(t,g);if(!ip_zero(g))copy(field_text[1],t,40);
    ip_text(t,d);if(!ip_zero(d))copy(field_text[2],t,40);
    int p=prefix_of(net.mode==1?net.saved_mask:net.mask);num(t,(unsigned long)(p?p:24));copy(field_text[3],t,40);
    for(int i=0;i<4;i++){fields[i].length=fields[i].cursor=(unsigned int)length(field_text[i]);}
    note[0]=0;print("BEACON_EDIT open\n");ui.dirty=1;
}
static struct zenith_network_settings proposal;
static int build_proposal(void){
    proposal=(struct zenith_network_settings){0};proposal.version=1;proposal.mode=edit_manual?1:0;
    if(!edit_manual)return 1;
    if(!parse_ip(field_text[0],proposal.address)){say("The address is not a valid IPv4 address, such as 192.168.1.20.",1);return 0;}
    unsigned long prefix=0;for(const char *p=field_text[3];*p;p++){if(*p<'0'||*p>'9'){prefix=99;break;}prefix=prefix*10+(unsigned long)(*p-'0');}
    if(prefix<8||prefix>30){say("The prefix is a number from 8 to 30 (24 is the usual 255.255.255.0).",1);return 0;}
    unsigned long mask=prefix?0xFFFFFFFFul<<(32-prefix)&0xFFFFFFFFul:0;
    for(int i=0;i<4;i++)proposal.mask[i]=(unsigned char)(mask>>(24-8*i));
    if(field_text[1][0]&&!parse_ip(field_text[1],proposal.gateway)){say("The gateway is not a valid IPv4 address.",1);return 0;}
    if(field_text[2][0]&&!parse_ip(field_text[2],proposal.dns)){say("The DNS server is not a valid IPv4 address.",1);return 0;}
    return 1;
}
/* Draws one frame so the person sees "Applying..." before a call that can take seconds (DHCP). */
static void present(void);
static void draw(void);
static void run_change(int what){
    char message[256]={0};long r;
    say(what==PENDING_APPLY?"Applying. The old settings stay until the new ones connect...":what==PENDING_CONNECT?"Connecting...":"Disconnecting...",0);
    draw();present();
    if(what==PENDING_APPLY)r=zenith_network(Z_NET_APPLY,0,message,sizeof(message),&proposal,sizeof(proposal));
    else r=zenith_network(Z_NET_CONNECT,what==PENDING_CONNECT,message,sizeof(message),0,0);
    if(r==-13&&!granted){ask_permission(what);return;}
    if(r==-38){say("This Zenith kernel cannot change the network from programs (it needs 0.0.69).",1);return;}
    message[sizeof(message)-1]=0;
    if(what==PENDING_APPLY){
        if(r==0){editing=0;say("The new settings are active.",0);print("BEACON_APPLIED\n");}
        else{char line[300],*o=put(line,"BEACON_KEPT ");o=put(o,message);put(o,"\n");print(line);say(message[0]?message:"The settings could not be applied.",1);}
    }else{
        if(r==0){say(what==PENDING_CONNECT?"Connected.":"Disconnected. Connect again whenever you like.",0);print(what==PENDING_CONNECT?"BEACON_CONNECT on\n":"BEACON_CONNECT off\n");}
        else say(message[0]?message:"That did not work.",1);
    }
    refresh();
}

/* ---------- Diagnostics: one step per pass of the loop, so the window keeps drawing. */
static const char *step_names[6]={"Adapter","Address","Gateway","DNS","Internet","Secure web"};
static struct zenith_network_check checks[6];
static int check_done[6],checking=-1,ping_count,ping_running,pinged[4];
static long ping_ms[4];
static char ping_host[64];static struct zui_input ping_input;static int ping_focus;
static void start_checks(void){for(int i=0;i<6;i++)check_done[i]=0;checking=0;print("BEACON_CHECKS start\n");ui.dirty=1;}
static void check_step(void){
    if(checking<0)return;
    if(checking>=6){checking=-1;return;}
    struct zenith_network_check c={0};
    long r=zenith_network(Z_NET_CHECK,(unsigned int)checking+1,&c,sizeof(c),0,0);
    if(r!=(long)sizeof(c)){c.result=Z_CHECK_FAILED;copy(c.detail,r==-38?"This Zenith kernel cannot run diagnostics (it needs 0.0.69).":"The check could not run.",sizeof(c.detail));}
    c.subject[31]=0;c.detail[127]=0;checks[checking]=c;check_done[checking]=1;
    char line[240],*o=put(line,"BEACON_CHECK ");o=put(o,step_names[checking]);o=put(o," ");
    o=put(o,c.result==Z_CHECK_OK?"ok":c.result==Z_CHECK_SLOW?"slow":c.result==Z_CHECK_FAILED?"failed":"skipped");o=put(o," ");o=num(o,c.milliseconds);o=put(o," ms ");o=put(o,c.subject);put(o,"\n");print(line);
    if(checking==4)internet_ok=c.result==Z_CHECK_FAILED?0:1;
    /* Stop guessing at the first real failure: later steps depend on it. */
    if(c.result==Z_CHECK_FAILED){
        if(checking<4)internet_ok=0;
        checking=-1;print("BEACON_CHECKS done\n");ui.dirty=1;return;
    }
    if(++checking>=6){checking=-1;print("BEACON_CHECKS done\n");}
    ui.dirty=1;
}
static void ping_step(void){
    if(!ping_running)return;
    if(ping_count>=4){ping_running=0;return;}
    char message[160]={0};
    long r=zenith_network(Z_NET_PING,0,message,sizeof(message),ping_host,(unsigned int)length(ping_host));
    ping_ms[ping_count]=r;pinged[ping_count]=1;
    char line[120],*o=put(line,"BEACON_PING ");o=put(o,ping_host);o=put(o," ");if(r>=0){o=num(o,(unsigned long)r);put(o," ms\n");}else put(o,"no reply\n");print(line);
    if(++ping_count>=4)ping_running=0;
    ui.dirty=1;
}
static void start_ping(void){
    if(!ping_input.length){say("Type a name or address to ping.",1);return;}
    copy(ping_host,ping_input.text,sizeof(ping_host));ping_count=0;ping_running=1;for(int i=0;i<4;i++)pinged[i]=0;ping_focus=0;ui.dirty=1;
}

/* ---------- Drawing. */
static struct zui_rect ping_field_rect,field_rects[4];
static void row(int x,int y,int w,const char *label,const char *value){
    text(x,y,label,&zui_caption,ui.theme.secondary);
    fit(x+110,y-2,value,w-110,&zui_body,ui.theme.text);
}
/* Traffic as two areas (down under up) scaled to the larger of the peak and 100 kb/s. */
static unsigned char graph_in[168],graph_out[168];
static unsigned long graph_scale;
static void traffic_graph(struct zui_rect g,int grid){
    unsigned long peak=12500;int n=(int)traffic.count;if(n>168)n=168;
    for(int i=0;i<n;i++){if(traffic.samples[i].received>peak)peak=traffic.samples[i].received;if(traffic.samples[i].sent>peak)peak=traffic.samples[i].sent;}
    /* A round top: 1, 2 or 5 times a power of ten. */
    unsigned long top=1;while(top<peak){if(top*2>=peak){top*=2;break;}if(top*5>=peak){top*=5;break;}top*=10;}
    graph_scale=top;
    for(int i=0;i<n;i++){graph_in[i]=(unsigned char)(traffic.samples[i].received*100/top);graph_out[i]=(unsigned char)(traffic.samples[i].sent*100/top);}
    static const int capacity[4]={60,60,96,168};
    zui_graph(&ui,g,graph_in,1,n,capacity[range],down_color(),grid);
    zui_graph(&ui,g,graph_out,1,n,capacity[range],up_color(),0);
}
static void range_chips(int x,int y){
    static const char *labels[4]={"1 min","1 h","24 h","7 d"};
    zui_segment_track(&ui,(struct zui_rect){x,y,4*58,30});
    for(int i=0;i<4;i++)zui_add(&ui,ID_RANGE+i,ZUI_SEGMENT,(struct zui_rect){x+i*58,y,58,30},labels[i],range==i,1);
}
static int legend(int x,int y,unsigned int color,const char *label){
    zui_round(&ui,(struct zui_rect){x,y+4,10,10},3,color);
    text(x+16,y,label,&zui_caption,ui.theme.secondary);
    return x+16+measure(label,&zui_caption)+18;
}
static void traffic_card(struct zui_rect r,int big){
    panel(r);char a[48],b[48],*o;
    text(r.x+16,r.y+14,"Traffic",&zui_label,ui.theme.text);
    range_chips(r.x+r.w-16-4*58,r.y+10);
    o=put(a,"Down ");rate_text(o,net.receive_rate);o=put(b,"Up ");rate_text(o,net.send_rate);
    int lx=legend(r.x+16,r.y+48,down_color(),a);legend(lx,r.y+48,up_color(),b);
    struct zui_rect g={r.x+16,r.y+74,r.w-32,r.h-74-(big?44:28)};
    if(g.h>20)traffic_graph(g,1);
    o=put(a,"Top ");rate_text(o,graph_scale);text(r.x+16,r.y+r.h-22-(big?16:0),a,&zui_caption,ui.theme.secondary);
    static const char *ago[4]={"1 min ago","1 h ago","24 h ago","7 d ago"};
    text_right(r.x+r.w-16,r.y+r.h-22-(big?16:0),ago[range],&zui_caption,ui.theme.secondary);
    if(big){o=put(a,"Received since start: ");o=bytes_text(o,net.received);o=put(o,"  \xc2\xb7  sent: ");bytes_text(o,net.sent);text(r.x+16,r.y+r.h-20,a,&zui_caption,ui.theme.secondary);}
}
static void connection_card(struct zui_rect r){
    panel(r);char t[96],*o;int x=r.x+16,w=r.w-32,y=r.y+46;
    text(x,r.y+14,"This connection",&zui_label,ui.theme.text);
    o=ip_text(t,net.address);if(!ip_zero(net.mask)){o=put(o," / ");num(o,(unsigned long)prefix_of(net.mask));}
    row(x,y,w,"Address",ip_zero(net.address)?"none":t);y+=28;
    ip_text(t,net.gateway);row(x,y,w,"Gateway",ip_zero(net.gateway)?"none":t);y+=28;
    o=t;*o=0;for(unsigned int i=0;i<net.dns_count&&i<4;i++){if(i)o=put(o,", ");o=ip_text(o,net.dns[i]);}
    if(net.flags&Z_NET_DNS_OVERRIDE)o=put(o," (fixed)");
    row(x,y,w,"DNS",net.dns_count?t:"none");y+=28;
    o=t;for(int i=0;i<6;i++){const char *hex="0123456789abcdef";*o++=hex[net.mac[i]>>4];*o++=hex[net.mac[i]&15];if(i<5)*o++=':';}*o=0;
    row(x,y,w,"Hardware",net.state==Z_NET_NO_ADAPTER?"none":t);y+=28;
    if(net.mode==1)row(x,y,w,"Lease","set manually, no lease");
    else if(net.renew_seconds>=0){o=put(t,"Renews in ");duration_text(o,net.renew_seconds);row(x,y,w,"Lease",t);}
    else row(x,y,w,"Lease","none");
    y+=28;
    net.hostname[63]=0;row(x,y,w,"Name",net.hostname);
}
static void history_rows(int x,int y,int w,int count){
    int n=(int)events.count;if(n>count)n=count;
    for(int i=0;i<n;i++){
        char t[48];clock_text(t,events.events[i].time);events.events[i].text[47]=0;
        text(x,y,t,&zui_caption,ui.theme.secondary);
        int k=(int)events.events[i].kind;unsigned int c=k==3||k==4||k==9?bad():k==10?warn():ui.theme.text;
        fit(x+120,y-2,events.events[i].text,w-120,&zui_body,c);
        y+=28;
    }
    if(!n)text(x,y,"Nothing has happened yet.",&zui_body,ui.theme.secondary);
}
static void history_card(struct zui_rect r){
    panel(r);text(r.x+16,r.y+14,"History",&zui_label,ui.theme.text);
    zui_add(&ui,ID_SEE_ALL,ZUI_BUTTON,(struct zui_rect){r.x+r.w-96,r.y+8,80,30},"See all",0,1);
    history_rows(r.x+16,r.y+50,r.w-32,(r.h-60)/28);
}
static struct zui_scroll page_scroll;
static void draw_wired(struct zui_rect c){
    int x=c.x+16,w=c.w-32;
    int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+16;
    /* The answer first. */
    struct zui_rect hero={x,y,w,96};panel(hero);
    zui_round(&ui,(struct zui_rect){x+18,y+22,14,14},7,state_color());
    fit(x+44,y+14,headline(),w-44-260,&zui_heading,ui.theme.text);
    char sub[160];subline(sub);fit(x+44,y+52,sub,w-60,&zui_caption,ui.theme.secondary);
    int on=have_status&&(net.state==Z_NET_ONLINE||net.state==Z_NET_CONNECTING||net.state==Z_NET_FAILED||net.state==Z_NET_CABLE_OUT);
    zui_add(&ui,ID_CONNECT,ZUI_BUTTON,(struct zui_rect){x+w-232,y+14,116,34},on?"Disconnect":"Connect",0,have_status&&net.state!=Z_NET_NO_ADAPTER&&!(net.mode==2&&!on));
    zui_add(&ui,ID_EDIT,ZUI_BUTTON,(struct zui_rect){x+w-108,y+14,92,34},"Edit",0,have_status&&net.state!=Z_NET_NO_ADAPTER);
    y+=108;
    if(note[0]){fit(x+4,y,note,w-8,&zui_caption,note_bad?bad():ui.theme.secondary);y+=24;}
    int wide=w>=760;
    if(wide){
        int lw=(w-12)*3/5,rw=w-12-lw;
        traffic_card((struct zui_rect){x,y,lw,236},0);connection_card((struct zui_rect){x+lw+12,y,rw,236});y+=248;
    }else{
        traffic_card((struct zui_rect){x,y,w,220},0);y+=232;connection_card((struct zui_rect){x,y,w,214});y+=226;
    }
    history_card((struct zui_rect){x,y,w,170});y+=182;
    zui_scroll_end(&ui,&page_scroll,y-top);
}
static void draw_absent(struct zui_rect c,const char *title,const char *body,const char *more){
    struct zui_rect r={c.x+16,c.y+16,c.w-32,140};panel(r);
    zui_round(&ui,(struct zui_rect){r.x+18,r.y+22,14,14},7,ui.theme.secondary);
    text(r.x+44,r.y+14,title,&zui_heading,ui.theme.text);
    fit(r.x+44,r.y+54,body,r.w-60,&zui_body,ui.theme.text);
    fit(r.x+44,r.y+84,more,r.w-60,&zui_caption,ui.theme.secondary);
}
static void draw_traffic(struct zui_rect c){
    traffic_card((struct zui_rect){c.x+16,c.y+16,c.w-32,c.h-32},1);
}
static void draw_diagnostics(struct zui_rect c){
    int x=c.x+16,w=c.w-32;
    int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+16;
    text(x,y,"Diagnostics",&zui_heading,ui.theme.text);
    text(x,y+38,"What works, step by step, from this machine outward",&zui_caption,ui.theme.secondary);
    zui_add(&ui,ID_RUN,ZUI_BUTTON,(struct zui_rect){x+w-120,y+4,120,34},checking>=0?"Checking...":"Run again",1,checking<0);
    y+=70;
    struct zui_rect list={x,y,w,6*46+16};panel(list);
    int failed_at=-1,slow_at=-1;
    for(int i=0;i<6;i++){
        int ry=y+8+i*46;struct zenith_network_check *k=&checks[i];
        unsigned int dot=!check_done[i]?ui.theme.border:k->result==Z_CHECK_OK?good():k->result==Z_CHECK_SLOW?warn():k->result==Z_CHECK_FAILED?bad():ui.theme.secondary;
        if(checking==i)dot=ui.theme.accent;
        zui_round(&ui,(struct zui_rect){x+18,ry+15,12,12},6,dot);
        text(x+44,ry+6,step_names[i],&zui_label,ui.theme.text);
        if(check_done[i]){
            fit(x+44,ry+26,k->result==Z_CHECK_OK||k->result==Z_CHECK_SLOW?k->detail:k->subject,w/2-60,&zui_caption,ui.theme.secondary);
            fit(x+w/2,ry+8,k->subject,w/2-110,&zui_body,ui.theme.text);
            if(k->milliseconds&&k->result!=Z_CHECK_FAILED){char ms[24],*o=num(ms,k->milliseconds);put(o," ms");text_right(x+w-16,ry+8,ms,&zui_body,k->result==Z_CHECK_SLOW?warn():ui.theme.secondary);}
            if(k->result==Z_CHECK_FAILED&&failed_at<0)failed_at=i;
            if(k->result==Z_CHECK_SLOW&&slow_at<0)slow_at=i;
        }else text(x+w/2,ry+8,checking==i?"checking...":checking>=0||failed_at<0?"":"not checked",&zui_caption,ui.theme.secondary);
        if(i<5)zui_fill(&ui,(struct zui_rect){x+44,ry+45,w-60,1},ui.theme.border);
    }
    y+=list.h+12;
    /* One concrete suggestion, from the first problem. */
    int at=failed_at>=0?failed_at:slow_at;
    if(at>=0){
        struct zui_rect s={x,y,w,72};panel(s);
        zui_round(&ui,(struct zui_rect){x+18,y+18,12,12},6,failed_at>=0?bad():warn());
        fit(x+44,y+12,checks[at].detail,w-60-(at==3||at==1?150:0),&zui_body,ui.theme.text);
        if(at==3||at==1||at==2)zui_add(&ui,ID_FIX,ZUI_BUTTON,(struct zui_rect){x+w-150,y+20,134,32},at==3?"Try another server":"Edit settings",0,1);
        y+=84;
    }
    /* Ping. */
    struct zui_rect p={x,y,w,190};panel(p);
    text(x+16,y+14,"Ping",&zui_label,ui.theme.text);
    ping_field_rect=(struct zui_rect){x+16,y+46,w-32-110,34};
    zui_round(&ui,ping_field_rect,8,ui.theme.surface);zui_round_outline(&ui,ping_field_rect,8,ping_focus?ui.theme.accent:ui.theme.border);
    if(ping_input.length||ping_focus)zui_input_draw(&ui,(struct zui_rect){ping_field_rect.x+8,ping_field_rect.y+1,ping_field_rect.w-12,ping_field_rect.h-2},&ping_input,ping_focus);
    else text(ping_field_rect.x+10,ping_field_rect.y+8,"Name or address, such as the gateway",&zui_body,ui.theme.secondary);
    zui_add(&ui,ID_PING,ZUI_BUTTON,(struct zui_rect){x+w-16-98,y+46,98,34},ping_running?"Pinging...":"Ping",1,!ping_running);
    int py=y+92,answered=0;long total=0;
    for(int i=0;i<4;i++){
        if(!pinged[i])continue;
        char t[96],*o=put(t,ping_host);o=put(o,": ");if(ping_ms[i]>=0){o=put(o,"reply in ");o=num(o,(unsigned long)ping_ms[i]);put(o," ms");answered++;total+=ping_ms[i];}else put(o,"no reply");
        text(x+16,py,t,&zui_caption,ping_ms[i]>=0?ui.theme.text:bad());py+=20;
    }
    if(!ping_running&&ping_count==4){
        char t[96],*o=put(t,"4 sent, ");o=num(o,(unsigned long)answered);o=put(o," answered");
        if(answered){o=put(o,", average ");o=num(o,(unsigned long)(total/answered));put(o," ms");}
        text_right(x+w-16,y+92,t,&zui_caption,answered==4?good():answered?warn():bad());
    }
    y+=p.h+12;
    fit(x+4,y,"Trace is not available yet: the network stack cannot choose a packet's hop limit.",w-8,&zui_caption,ui.theme.secondary);y+=28;
    zui_scroll_end(&ui,&page_scroll,y-top);
}
static void draw_history(struct zui_rect c){
    int x=c.x+16,w=c.w-32;
    int top=zui_scroll_begin(&ui,&page_scroll,c),y=top+16;
    text(x,y,"History",&zui_heading,ui.theme.text);
    char sub[96],*o=num(sub,events.count);o=put(o,events.count==1?" event":" events");put(o,", kept across restarts");
    text(x,y+38,sub,&zui_caption,ui.theme.secondary);y+=70;
    int h=(int)(events.count?events.count:1)*28+24;
    panel((struct zui_rect){x,y,w,h});history_rows(x+16,y+14,w-32,64);y+=h+12;
    zui_scroll_end(&ui,&page_scroll,y-top);
}
static void draw_editor(void){
    /* A modal card over the page. */
    for(int y=0;y<ui.height;y++)for(int x=0;x<ui.width;x++){unsigned int *p=&ui.pixels[y*ui.width+x];if(!ui.foreground){*p=((*p>>1)&0x7F7F7F);continue;}
        unsigned int a=*p>>24;unsigned int r=(*p>>16&255)/2,g=(*p>>8&255)/2,b=(*p&255)/2;a=a+(255-a)/2;*p=a<<24|r<<16|g<<8|b;}
    int w=ui.width<520?ui.width-32:480,h=edit_manual?396:236,x=(ui.width-w)/2,y=(ui.height-h)/2;
    struct zui_rect card={x,y,w,h};zui_round(&ui,card,14,ui.theme.raised);zui_round_outline(&ui,card,14,ui.theme.border);
    text(x+20,y+18,"Edit connection",&zui_heading,ui.theme.text);
    zui_segment_track(&ui,(struct zui_rect){x+20,y+60,w-40,32});
    zui_add(&ui,ID_MODE_AUTO,ZUI_SEGMENT,(struct zui_rect){x+20,y+60,(w-40)/2,32},"Automatic",!edit_manual,1);
    zui_add(&ui,ID_MODE_MANUAL,ZUI_SEGMENT,(struct zui_rect){x+20+(w-40)/2,y+60,(w-40)/2,32},"Manual",edit_manual,1);
    int fy=y+108;
    if(edit_manual){
        static const int order[4]={0,3,1,2};
        for(int k=0;k<4;k++){
            int i=order[k];
            text(x+20,fy+8,field_labels[i],&zui_body,ui.theme.text);
            struct zui_rect f={x+130,fy,w-150,34};field_rects[i]=f;
            zui_round(&ui,f,8,ui.theme.surface);zui_round_outline(&ui,f,8,edit_field==i?ui.theme.accent:ui.theme.border);
            zui_input_draw(&ui,(struct zui_rect){f.x+8,f.y+1,f.w-12,f.h-2},&fields[i],edit_field==i);
            fy+=44;
        }
    }else{
        fit(x+20,fy,"Address, gateway and DNS come from the network (DHCP).",w-40,&zui_body,ui.theme.text);fy+=36;
    }
    fit(x+20,fy+4,"Applying keeps the old settings until the new ones connect.",w-40,&zui_caption,ui.theme.secondary);fy+=28;
    if(note[0])fit(x+20,fy+2,note,w-40,&zui_caption,note_bad?bad():ui.theme.secondary);
    zui_add(&ui,ID_APPLY,ZUI_BUTTON,(struct zui_rect){x+w-120,y+h-54,100,36},"Apply",1,auth_handle<=0);
    zui_add(&ui,ID_CANCEL,ZUI_BUTTON,(struct zui_rect){x+w-232,y+h-54,100,36},"Cancel",0,1);
}
static void nav(struct zui_rect s){
    text(s.x+20,s.y+18,"Beacon",&zui_label,ui.theme.text);
    int y=s.y+56;
    text(s.x+20,y,"Connections",&zui_caption,ui.theme.secondary);y+=22;
    for(int i=0;i<PAGES;i++){
        if(i==PAGE_TRAFFIC){y+=10;text(s.x+20,y,"Tools",&zui_caption,ui.theme.secondary);y+=22;}
        /* zui_add answers 1 when it registered the control, which is then the last one. */
        int at=zui_add(&ui,ID_NAV+i,ZUI_NAVIGATION,(struct zui_rect){s.x+8,y,s.w-16,36},page_names[i],page==i,1)?ui.count-1:-1;
        if(at>=0){
            ui.controls[at].icon=page_icons[i];ui.controls[at].no_chevron=1;
            ui.controls[at].detail=i==PAGE_WIRED?(!have_status?"":net.state==Z_NET_ONLINE?"On":net.state==Z_NET_NO_ADAPTER?"None":"Off"):i==PAGE_WIFI?"No adapter":i==PAGE_VPN?"None":0;
        }
        y+=40;
    }
}
static void draw(void){
    struct zui_workspace w=zui_workspace_begin(&ui,ui.width<760?168:208);struct zui_rect c=w.content;
    int focus=ui.focus>=0&&ui.focus<ui.count?ui.controls[ui.focus].id:0;
    int pressed=ui.pressed>=0&&ui.pressed<ui.count?ui.controls[ui.pressed].id:0;
    ui.count=0;ui.pressed=-1;ui.focus=-1;
    nav(w.sidebar);
    zui_clip(&ui,c);
    if(page==PAGE_WIRED)draw_wired(c);
    else if(page==PAGE_WIFI)draw_absent(c,"No Wi-Fi adapter","Zenith has no Wi-Fi drivers yet, so wireless networks cannot be used.","Wired networks work now. Wi-Fi will appear here once a supported adapter and its driver exist.");
    else if(page==PAGE_VPN)draw_absent(c,"No VPN","VPN connections need a VPN service, which Zenith does not have yet.","When it exists, VPN connections will be listed and switched here.");
    else if(page==PAGE_TRAFFIC)draw_traffic(c);
    else if(page==PAGE_DIAGNOSTICS)draw_diagnostics(c);
    else draw_history(c);
    zui_clip(&ui,(struct zui_rect){0,0,ui.width,ui.height});
    zui_draw_controls(&ui);
    if(editing){
        /* The editor's controls are drawn over the dim layer on their own; the page's stay registered but inert. */
        static struct zui_control page_controls[ZUI_MAX_CONTROLS];
        int before=ui.count;draw_editor();int n=ui.count-before;
        for(int i=0;i<before;i++){page_controls[i]=ui.controls[i];page_controls[i].enabled=0;}
        for(int i=0;i<n;i++)ui.controls[i]=ui.controls[before+i];
        ui.count=n;zui_draw_controls(&ui);
        for(int i=n-1;i>=0;i--)ui.controls[before+i]=ui.controls[i];
        for(int i=0;i<before;i++)ui.controls[i]=page_controls[i];
        ui.count=before+n;
    }
    for(int i=0;i<ui.count;i++)if(ui.controls[i].enabled){if(ui.controls[i].id==focus)ui.focus=i;if(ui.controls[i].id==pressed)ui.pressed=i;}
    zui_update_hover(&ui);
    ui.dirty=0;
}
static void present(void){
    if(fade_page){int side=ui.width<760?168:208;zenith_gui_fade(handle,side+24,12,ui.width-side-36,ui.height-24,1);fade_page=0;}
    long r=material?zenith_gui_present_material(handle,ui.pixels,(unsigned long)ui.width*ui.height*4,ui.materials,ui.material_count):zenith_gui_present(handle,ui.pixels,(unsigned long)ui.width*ui.height*4);
    (void)r;
}

/* ---------- Actions. */
static void action(int id){
    if(id>=ID_NAV&&id<ID_NAV+PAGES){go(id-ID_NAV);return;}
    if(id>=ID_RANGE&&id<ID_RANGE+4){range=id-ID_RANGE;refresh();return;}
    if(id==ID_SEE_ALL){go(PAGE_HISTORY);return;}
    if(id==ID_EDIT||id==ID_FIX){open_editor();if(id==ID_FIX&&checks[3].result!=Z_CHECK_OK){edit_manual=1;edit_field=2;}return;}
    if(id==ID_CONNECT){
        int on=net.state==Z_NET_ONLINE||net.state==Z_NET_CONNECTING||net.state==Z_NET_FAILED||net.state==Z_NET_CABLE_OUT;
        run_change(on?PENDING_DISCONNECT:PENDING_CONNECT);return;
    }
    if(id==ID_RUN){start_checks();return;}
    if(id==ID_PING){start_ping();return;}
    if(id==ID_MODE_AUTO){edit_manual=0;ui.dirty=1;return;}
    if(id==ID_MODE_MANUAL){edit_manual=1;edit_field=0;ui.dirty=1;return;}
    if(id==ID_CANCEL){editing=0;note[0]=0;ui.dirty=1;print("BEACON_EDIT closed\n");return;}
    if(id==ID_APPLY){if(build_proposal())run_change(PENDING_APPLY);return;}
}

/* ---------- Without a window: the same service for scripts and the gate. */
static int command(int argc,char **argv){
    char line[400],*o;
    if(same(argv[1],"--status")){
        refresh();if(!have_status){print("beacon: this kernel does not report the network (needs 0.0.69)\n");return 1;}
        o=put(line,"BEACON_STATUS state=");o=num(o,net.state);o=put(o," mode=");o=num(o,net.mode);o=put(o," address=");o=ip_text(o,net.address);
        o=put(o,"/");o=num(o,(unsigned long)prefix_of(net.mask));o=put(o," gateway=");o=ip_text(o,net.gateway);o=put(o," dns=");o=num(o,net.dns_count);
        o=put(o," received=");o=num(o,net.received);o=put(o," events=");o=num(o,net.events);o=put(o," connected=");
        if(net.connected_seconds<0)o=put(o,"no");else o=num(o,(unsigned long)net.connected_seconds);put(o,"\n");print(line);
        o=put(line,"BEACON_HEADLINE ");o=put(o,headline());o=put(o," | ");o=subline(o);put(o,"\n");print(line);return 0;
    }
    if(same(argv[1],"--events")){
        refresh();zenith_network(Z_NET_EVENTS,0,&events,sizeof(events),0,0);
        for(unsigned int i=0;i<events.count&&i<64;i++){events.events[i].text[47]=0;o=put(line,"BEACON_EVENT ");o=num(o,events.events[i].kind);o=put(o," ");o=put(o,events.events[i].text);put(o,"\n");print(line);}
        return 0;
    }
    if(same(argv[1],"--history")){
        refresh();for(int r=0;r<4;r++){struct zenith_network_history h;long n=zenith_network(Z_NET_HISTORY,(unsigned int)r,&h,sizeof(h),0,0);
            o=put(line,"BEACON_HISTORY ");o=num(o,(unsigned long)r);o=put(o," ");if(n>0){o=num(o,h.count);o=put(o," x ");o=num(o,h.step_seconds);put(o," s\n");}else put(o,"unavailable\n");print(line);}
        return 0;
    }
    if(same(argv[1],"--check")){
        refresh();start_checks();while(checking>=0)check_step();return internet_ok==0?3:0;
    }
    if(same(argv[1],"--ping")&&argc==3){
        copy(field_text[0],argv[2],sizeof(field_text[0]));zui_input_init(&ping_input,ping_host,sizeof(ping_host));
        copy(ping_host,argv[2],sizeof(ping_host));ping_input.length=(unsigned int)length(ping_host);
        start_ping();while(ping_running)ping_step();return 0;
    }
    if(same(argv[1],"--connect")&&argc==3){
        char message[200]={0};long r=zenith_network(Z_NET_CONNECT,same(argv[2],"on"),message,sizeof(message),0,0);
        o=put(line,"BEACON_CONNECT ");o=put(o,argv[2]);o=put(o," result=");if(r<0){*o++='-';o=num(o,(unsigned long)-r);}else o=num(o,(unsigned long)r);o=put(o," ");o=put(o,message);put(o,"\n");print(line);
        return r<0;
    }
    if(same(argv[1],"--apply")&&(argc==3||argc==6)){
        /* beacon --apply auto | --apply manual ADDRESS/PREFIX GATEWAY DNS */
        if(argc==3&&same(argv[2],"auto")){edit_manual=0;}
        else if(argc==6&&same(argv[2],"manual")){
            edit_manual=1;const char *a=argv[3];int i=0;while(a[i]&&a[i]!='/'&&i<39){field_text[0][i]=a[i];i++;}field_text[0][i]=0;
            copy(field_text[3],a[i]=='/'?a+i+1:"24",40);copy(field_text[1],argv[4],40);copy(field_text[2],argv[5],40);
        }else{print("usage: beacon --apply auto | --apply manual ADDRESS/PREFIX GATEWAY DNS\n");return 2;}
        if(!build_proposal()){o=put(line,"BEACON_INVALID ");o=put(o,note);put(o,"\n");print(line);return 2;}
        char message[256]={0};long r=zenith_network(Z_NET_APPLY,0,message,sizeof(message),&proposal,sizeof(proposal));
        message[255]=0;o=put(line,r==0?"BEACON_APPLIED ":r==-13?"BEACON_DENIED ":"BEACON_KEPT ");o=put(o,message);put(o,"\n");print(line);
        return r<0;
    }
    print("usage: native run beacon [--status | --events | --history | --check | --ping HOST | --connect on|off | --apply ...]\n");return 2;
}

int main(int argc,char **argv){
    if(argc==2&&same(argv[1],"--version")){print("beacon " SUNBEAM_VERSION "\n");return 0;}
    if(argc>=2&&argv[1][0]=='-')return command(argc,argv);
    if(argc>2){print("usage: native run beacon [diagnostics|traffic|history]\n");return 2;}
    if(argc==2)page=same(argv[1],"diagnostics")?PAGE_DIAGNOSTICS:same(argv[1],"traffic")?PAGE_TRAFFIC:same(argv[1],"history")?PAGE_HISTORY:PAGE_WIRED;
    zui_icons_use_packages();
    zui_init(&ui,pixels,640,480);update_appearance();
    zui_input_init(&ping_input,ping_host,sizeof(ping_host));
    struct zenith_material_caps caps={0};struct zenith_material_settings settings={0};
    material=!zenith_material_settings_read(&settings)&&!zenith_gui_material_caps(&caps)&&caps.version==2;
    handle=material?zenith_gui_open_material(640,480,"Beacon",6):zenith_gui_open(640,480,"Beacon",6);
    if(handle<0){print("beacon: graphical session required\n");return 1;}
    ui.foreground=material;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);return 1;}
    ui.hover_supported=zenith_gui_input(handle,15)==0||zenith_gui_input(handle,7)==0||zenith_gui_pointer_motion(handle)==0;
    refresh();
    /* The ping field starts with the gateway: the first thing worth asking. */
    if(have_status&&!ip_zero(net.gateway)){ip_text(ping_host,net.gateway);ping_input.length=ping_input.cursor=(unsigned int)length(ping_host);}
    if(page==PAGE_DIAGNOSTICS)start_checks();
    print("BEACON_READY\n");
    long last=zui_motion_clock();int polls=0,result=0,done=0;
    while(!done){
        long now=zui_motion_clock();
        if((now>=0&&now-last>=1000)||(now<0&&++polls>=400)){last=now;polls=0;refresh();}
        check_step();ping_step();poll_permission();
        zui_motion_tick(&ui,now);
        if(ui.dirty){draw();present();}
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_RESIZE){if(zui_apply_viewport(&ui,handle,640*480)<0){result=zui_window_closed(handle)?0:1;break;}continue;}
        if(e.type==Z_GUI_CLOSE||e.type==Z_GUI_CLOSE_INTENT){done=1;continue;}
        if(e.type==Z_GUI_RESET){update_appearance();continue;}
        if(e.type==Z_GUI_FOCUS&&e.code)print("BEACON_FOCUSED\n");
        if(e.type==Z_GUI_KEY){
            int control=(e.modifiers&6)==2;
            if(editing){
                if(e.extra==1){action(ID_CANCEL);continue;}
                if(e.extra==2&&!control){action(ID_APPLY);continue;}
                if(edit_manual&&e.extra==4&&!control){static const int next[4]={3,2,0,1};edit_field=(e.modifiers&1)?(edit_field==0?2:edit_field==3?0:edit_field==1?3:1):next[edit_field];ui.dirty=1;continue;}
                if(edit_manual&&zui_input_event(&fields[edit_field],&e)){note[0]=0;ui.dirty=1;continue;}
                if(control&&(e.code=='a'||e.code=='A')){action(ID_MODE_AUTO);continue;}
                if(control&&(e.code=='m'||e.code=='M')){action(ID_MODE_MANUAL);continue;}
            }else{
                if(ping_focus){
                    if(e.extra==1){ping_focus=0;ui.dirty=1;continue;}
                    if(e.extra==2){start_ping();continue;}
                    if(zui_input_event(&ping_input,&e)){ui.dirty=1;continue;}
                }
                if(control&&e.code>='1'&&e.code<='6'){go(e.code-'1');continue;}
                if(control&&(e.code=='e'||e.code=='E')){open_editor();continue;}
                if(control&&(e.code=='r'||e.code=='R')){go(PAGE_DIAGNOSTICS);start_checks();continue;}
                if(control&&(e.code=='p'||e.code=='P')){go(PAGE_DIAGNOSTICS);ping_focus=1;ui.dirty=1;continue;}
                if(e.extra==1&&ui.window_focused){done=1;continue;}
            }
        }
        if(e.type==Z_GUI_DOWN){
            if(editing&&edit_manual){for(int i=0;i<4;i++)if(inside(field_rects[i],e.x,e.y)){edit_field=i;ui.dirty=1;}}
            if(!editing&&page==PAGE_DIAGNOSTICS){int f=inside(ping_field_rect,e.x,e.y);if(f!=ping_focus){ping_focus=f;ui.dirty=1;}}
        }
        if(!editing&&(page==PAGE_WIRED||page==PAGE_DIAGNOSTICS||page==PAGE_HISTORY)&&zui_scroll_event(&ui,&page_scroll,&e))ui.dirty=1;
        int id=zui_event(&ui,&e);
        if(id>0){
            if(editing&&!(id>=ID_MODE_AUTO&&id<=ID_CANCEL))continue;
            action(id);
        }
    }
    zenith_gui_close(handle);return result;
}
