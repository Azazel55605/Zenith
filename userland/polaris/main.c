/* Polaris: the native terminal. The shell lives in a kernel-owned session; this draws it. */
#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../include/zenith_settings.h"
#include "../include/zenith_system_settings.h"
#include "term.h"
extern const struct zui_font zui_mono;
enum { CELLS=1<<20, LINES=32768, PADDING=10 };
static unsigned int pixels[1920*1000];
static struct pt_cell cells[CELLS];static struct pt_line lines[LINES];
static struct pt_cell edit_cells[8192];static struct pt_line edit_lines[8];
static struct zui ui;static struct pterm term,edit;static struct zui_scroll view;
static long handle=-1,session=-1;
static unsigned int state_words[Z_TERMINAL_STATE_BYTES/4];static const char *state_text;
static struct zenith_system_settings system_settings;
static int columns,rows,following=1,selecting,has_selection,caret_on=1;
static unsigned int anchor_row,anchor_column,head_row,head_column,total_rows,edit_first_row;
static const unsigned int dark[16]={0xb4b7c0,0xf0707a,0x8bcba4,0xe7bb70,0x8baaff,0xb6a0ff,0x7ecac7,0xf1f2f4,0xb4b7c0,0xff8a93,0xa6e8b5,0xffdc8f,0x9db6ff,0xddb0f5,0x8fe6f2,0xffffff};
static const unsigned int light[16]={0x202126,0xae325d,0x277347,0x886010,0x3157b7,0x7042be,0x197571,0x5c606b,0x5c606b,0x9e284e,0x21623c,0x76530e,0x294b9f,0x6037a8,0x14635f,0x202126};
static int is_light;
static unsigned long text_length(const char *s){unsigned long n=0;while(s[n])n++;return n;}
static void print(const char *s){zenith_write(s,text_length(s));}
static int cell_width(void){return zui_mono.advance[0];}
static int cell_height(void){return zui_mono.height+2;}
static unsigned int cell_color(struct pt_cell c){
    int index=c.color?c.color-1:-1;
    if(index>=0&&index<8&&c.bold)index+=8;
    return index<0?ui.theme.text:(is_light?light:dark)[index];
}
static void draw_cells(const struct pterm *t,unsigned int line,unsigned int from,unsigned int to,int x,int y){
    char run[512];unsigned long used=0;int run_x=x;unsigned int color=0;
    for(unsigned int i=from;i<=to;i++){
        struct pt_cell c=i<to?pt_cell_at(t,line,i):(struct pt_cell){0,0,0};
        unsigned int next=i<to?cell_color(c):color;
        if(used&&(i==to||next!=color||used+3>=sizeof(run))){
            zui_text(&ui,run_x,y,run,used,&zui_mono,color);
            run_x=x+(int)(i-from)*cell_width();used=0;
        }
        if(i<to){if(!used)run_x=x+(int)(i-from)*cell_width();color=next;used+=(unsigned long)pt_utf8(c.ch?c.ch:' ',run+used);}
    }
}
/* The session keeps the edit line; the last logical line is what it continues, like `read -p`. */
static void compose_edit(void){
    pt_clear(&edit);edit.color=edit.bold=0;
    unsigned int tail=term.line_count-1,n=pt_length(&term,tail);
    for(unsigned int i=0;i<n&&i<4000;i++)pt_put(&edit,pt_cell_at(&term,tail,i));
    const char *t=state_text;unsigned int prompt=state_words[3],input=state_words[4];
    pt_write(&edit,t,prompt);
    if(state_words[1]&Z_TERMINAL_SECRET){for(unsigned int i=0;i<state_words[2]&&i<256;i++)pt_write(&edit,"*",1);}
    else pt_write(&edit,t+prompt,input);
}
static unsigned int edit_caret(void){
    unsigned int tail=pt_length(&term,term.line_count-1),visible=0;
    const char *t=state_text;int escape=0;
    for(unsigned int i=0;i<state_words[3];i++){
        unsigned char b=(unsigned char)t[i];
        if(escape==1){escape=b=='['?2:0;continue;}
        if(escape==2){if(b>=0x40&&b<0x7f)escape=0;continue;}
        if(b==0x1b){escape=1;continue;}
        if((b&0xc0)!=0x80)visible++;
    }
    return tail+visible+state_words[2];
}
static void selection_bounds(unsigned int *r0,unsigned int *c0,unsigned int *r1,unsigned int *c1){
    int anchor_first=anchor_row<head_row||(anchor_row==head_row&&anchor_column<=head_column);
    *r0=anchor_first?anchor_row:head_row;*c0=anchor_first?anchor_column:head_column;
    *r1=anchor_first?head_row:anchor_row;*c1=anchor_first?head_column:anchor_column;
}
static void draw_row(const struct pterm *t,unsigned int line,unsigned int row,unsigned int part,int y,unsigned int length){
    unsigned int from=part*(unsigned)columns,to=from+(unsigned)columns>length?length:from+(unsigned)columns;
    int x=PADDING;
    if(has_selection){
        unsigned int r0,c0,r1,c1;selection_bounds(&r0,&c0,&r1,&c1);
        if(row>=r0&&row<=r1){
            unsigned int a=row==r0?c0:0,b=row==r1?c1:(unsigned)columns;
            if(b>a)zui_round_alpha(&ui,(struct zui_rect){x+(int)a*cell_width(),y,(int)(b-a)*cell_width(),cell_height()},0,ui.theme.accent,70);
        }
    }
    if(from<to)draw_cells(t,line,from,to,x,y+1);
}
static void draw(void){
    ui.count=0;
    struct zui_rect all={0,0,ui.width,ui.height};
    zui_clip(&ui,all);zui_fill(&ui,all,ui.theme.surface);
    compose_edit();
    unsigned int history=0;
    for(unsigned int i=0;i+1<term.line_count;i++)history+=pt_rows(pt_length(&term,i),(unsigned)columns);
    unsigned int caret=edit_caret(),edit_length=pt_length(&edit,0),edit_rows=pt_rows(edit_length,(unsigned)columns);
    if(caret/(unsigned)columns+1>edit_rows)edit_rows=caret/(unsigned)columns+1;
    total_rows=history+edit_rows;edit_first_row=history;
    int h=cell_height(),content=(int)total_rows*h+2*PADDING;
    struct zui_rect area={0,0,ui.width,ui.height};
    if(following)zui_scroll_jump(&view,content>area.h?content-area.h:0);
    int top=zui_scroll_begin(&ui,&view,area)+PADDING;
    unsigned int row=0;int bottom=area.y+area.h;
    for(unsigned int i=0;i+1<term.line_count;i++){
        unsigned int length=pt_length(&term,i),n=pt_rows(length,(unsigned)columns);
        int y=top+(int)row*h;
        if(y+(int)n*h>0&&y<bottom)for(unsigned int part=0;part<n;part++){int yy=y+(int)part*h;if(yy+h>0&&yy<bottom)draw_row(&term,i,row+part,part,yy,length);}
        row+=n;
        if(top+(int)row*h>=bottom)break;
    }
    int y=top+(int)history*h;
    for(unsigned int part=0;part<edit_rows;part++)draw_row(&edit,0,history+part,part,y+(int)part*h,edit_length);
    if(ui.window_focused&&caret_on&&!(state_words[1]&Z_TERMINAL_RAW)){
        int cx=PADDING+(int)(caret%(unsigned)columns)*cell_width(),cy=y+(int)(caret/(unsigned)columns)*h;
        zui_fill(&ui,(struct zui_rect){cx,cy+1,2,h-2},ui.theme.accent);
    }
    zui_scroll_end(&ui,&view,content);
    zui_clip(&ui,all);ui.dirty=0;
}
static long present(void){return zenith_gui_present(handle,pixels,(unsigned long)ui.width*(unsigned long)ui.height*4);}
static void measure(void){
    int c=(ui.width-2*PADDING-12)/cell_width(),r=(ui.height-2*PADDING)/cell_height();
    columns=c<20?20:c;rows=r<3?3:r;
}
static int apply_viewport(void){
    struct zenith_viewport v={0};
    if(zenith_gui_viewport(handle,&v)||v.version!=1||!(v.flags&1)||v.width<600||v.height<480||v.width>1920||v.height>1000)return 0;
    if(v.width==(unsigned)ui.width&&v.height==(unsigned)ui.height)return 1;
    long r=zenith_gui_resize_accept(handle,&v);if(r==-11)return 1;if(r)return 0;
    if(!zui_resize(&ui,(int)v.width,(int)v.height,1920*1000))return 0;
    measure();zenith_terminal_resize(session,columns);has_selection=0;ui.dirty=1;return 1;
}
static void theme(void){
    struct zenith_appearance a={0};
    if(zenith_appearance_read(&a)||a.version!=1)return;
    unsigned int before=ui.theme.surface,accent=ui.theme.accent;
    zui_appearance(&ui,(int)a.active_mode,(int)a.active_accent);is_light=(int)a.active_mode;
    if(before!=ui.theme.surface||accent!=ui.theme.accent)ui.dirty=1;
}
static void motion(long clock){
    if(clock<0)return;
    if(zenith_system_settings_read(&system_settings))return;
    ui.motion_percent=zui_motion_percent(system_settings.version==2?system_settings.animation:0);
}
static void position(int x,int y,unsigned int *row,unsigned int *column){
    long r=((long)y+view.visual-PADDING)/cell_height();
    long c=((long)x-PADDING+cell_width()/2)/cell_width();
    *row=r<0?0:r>=(long)total_rows?total_rows-1:(unsigned)r;
    *column=c<0?0:c>columns?(unsigned)columns:(unsigned)c;
}
/* Wrapped rows join directly and real line ends become newlines. */
static char copied[65536];
static void copy_selection(void){
    if(!has_selection||(state_words[1]&Z_TERMINAL_SECRET))return;
    unsigned int r0,c0,r1,c1,row=0;selection_bounds(&r0,&c0,&r1,&c1);unsigned long used=0;
    for(unsigned int i=0;i<term.line_count;i++){
        const struct pterm *t=i+1<term.line_count?&term:&edit;unsigned int line=i+1<term.line_count?i:0;
        unsigned int length=pt_length(t,line),n=pt_rows(length,(unsigned)columns);
        for(unsigned int part=0;part<n;part++,row++){
            if(row<r0||row>r1)continue;
            unsigned int from=part*(unsigned)columns,to=from+(unsigned)columns>length?length:from+(unsigned)columns;
            unsigned int a=from+(row==r0?c0:0),b=row==r1?from+c1:to;if(b>to)b=to;
            for(unsigned int k=a;k<b&&used+4<sizeof(copied);k++)used+=(unsigned long)pt_utf8(pt_cell_at(t,line,k).ch,copied+used);
            if(row<r1&&part+1==n&&used+1<sizeof(copied))copied[used++]='\n';
        }
    }
    if(used)zenith_terminal_copy(session,copied,used);
}
static void page(int direction){
    following=0;int max=zui_scroll_maximum(&view),next=zui_scroll_step(view.offset,direction*(ui.height/3),max);
    view.offset=next;if(next==max)following=1;ui.dirty=1;
}
static void handle_event(const struct zenith_gui_event *e,int *done){
    if(e->type==Z_GUI_CLOSE||e->type==Z_GUI_CLOSE_INTENT){*done=1;return;}
    if(e->type==Z_GUI_RESIZE){if(!apply_viewport())*done=2;return;}
    if(e->type==Z_GUI_FOCUS||e->type==Z_GUI_RESET){zui_event(&ui,e);selecting=0;view.dragging=0;ui.dirty=1;return;}
    if(e->type==Z_GUI_SCROLL){
        if(zui_scroll_event(&ui,&view,e))following=view.offset==zui_scroll_maximum(&view);
        return;
    }
    if(zui_scroll_event(&ui,&view,e)){following=view.offset==zui_scroll_maximum(&view);return;}
    if(view.dragging){if(e->type==Z_GUI_UP)view.dragging=0;return;}
    if(e->type==Z_GUI_DOWN){
        position(e->x,e->y,&anchor_row,&anchor_column);head_row=anchor_row;head_column=anchor_column;
        selecting=1;has_selection=0;ui.dirty=1;return;
    }
    if(e->type==Z_GUI_DRAG&&selecting){position(e->x,e->y,&head_row,&head_column);has_selection=head_row!=anchor_row||head_column!=anchor_column;ui.dirty=1;return;}
    if(e->type==Z_GUI_UP){selecting=0;return;}
    if(e->type!=Z_GUI_KEY)return;
    int raw=(state_words[1]&Z_TERMINAL_RAW)!=0;
    if(!raw&&e->modifiers==3&&(e->code=='c'||e->code=='C')){copy_selection();return;}
    if(!raw&&e->modifiers==3&&(e->code=='v'||e->code=='V')){zenith_terminal_paste(session);return;}
    if(!raw&&!e->modifiers&&e->extra==ZUI_KEY_PAGE_UP){page(-1);return;}
    if(!raw&&!e->modifiers&&e->extra==ZUI_KEY_PAGE_DOWN){page(1);return;}
    has_selection=0;following=1;
    struct zenith_terminal_key k={e->code,e->modifiers,e->extra};
    zenith_terminal_key(session,&k);ui.dirty=1;
}
int main(int argc,char **argv){
    if(argc==2){const char *a=argv[1];if(a[0]=='-'&&a[1]=='-'&&a[2]=='v'){print("polaris " SUNBEAM_VERSION "\n");return 0;}}
    if(!zui_init(&ui,pixels,640,480))return 1;
    if(!pt_init(&term,cells,CELLS,lines,LINES)||!pt_init(&edit,edit_cells,8192,edit_lines,8))return 1;
    handle=zenith_gui_open(640,480,"Polaris",7);
    if(handle<0){print("polaris: graphical session required\n");return 1;}
    int result=0;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){print("polaris: interactive client support required\n");result=1;goto end;}
    ui.hover_supported=0;measure();
    session=zenith_terminal_open(columns);
    if(session<0){print("polaris: this kernel has no terminal sessions\n");result=1;goto end;}
    state_text=(const char *)state_words+24;
    long clock=zui_motion_clock();if(clock>=0)ui.now=clock;
    motion(clock);theme();ui.dirty=1;
    print("POLARIS_READY\n");
    static char incoming[16384];
    unsigned long last_flags=0,last_cursor=0,last_lengths=0;long last_blink=0;
    int done=0;
    for(unsigned int i=0;!done;i++){
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);
        if(r<0){result=1;break;}
        if(r>0)handle_event(&e,&done);
        if(done==2){result=1;break;}
        for(int n=0;n<8;n++){
            long got=zenith_terminal_read(session,incoming,sizeof(incoming));
            if(got<=0)break;
            pt_write(&term,incoming,(unsigned long)got);term.changed=1;has_selection=0;
        }
        if(term.changed){term.changed=0;ui.dirty=1;}
        if(zenith_terminal_state(session,state_words)<24){result=1;break;}
        unsigned long lengths=state_words[3]*65536UL+state_words[4];
        if(state_words[1]!=last_flags||state_words[2]!=last_cursor||lengths!=last_lengths){last_flags=state_words[1];last_cursor=state_words[2];last_lengths=lengths;ui.dirty=1;}
        if(state_words[1]&Z_TERMINAL_EXIT)break;
        if((i&63)==0){theme();if((i&255)==0)motion(clock);}
        if(clock>=0&&(ui.dirty||ui.animating||(i&15)==0)){
            long now=zui_motion_clock();
            if(now/500!=last_blink){last_blink=now/500;int on=last_blink%2==0;if(on!=caret_on){caret_on=on;ui.dirty=1;}}
            if(ui.dirty||ui.animating){int moving=ui.animating;zui_motion_frame(&ui,now);if(moving)ui.dirty=1;}
        }
        if(ui.dirty){draw();if(present()<0){result=1;break;}}
    }
end:
    if(session>=0)zenith_terminal_close(session);
    zenith_gui_close(handle);return result;
}
