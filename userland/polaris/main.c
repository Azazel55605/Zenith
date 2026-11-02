/* Polaris: the native terminal. Each shell lives in a kernel-owned session; this draws them. */
#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../include/zenith_settings.h"
#include "term.h"
enum { MAX_PANES=8, TAB_PANES=4, CELLS=1<<18, LINES=8192, PADDING=8, STRIP=44, HIDDEN_POLL=16 };
enum { LAYOUT_TALL=0, LAYOUT_COLUMNS=1, LAYOUT_ROWS=2 };
enum { ID_TALL=1, ID_COLUMNS=2, ID_ROWS=3, ID_SPLIT=4 };
struct pane {
    int used; long session; struct pterm term; struct zui_scroll view; struct zui_rect rect;
    unsigned int state[Z_TERMINAL_STATE_BYTES/4]; const char *text;
    int columns,following,selecting,has_selection;
    unsigned int anchor_row,anchor_column,head_row,head_column,total_rows;
    unsigned long last_flags,last_cursor,last_lengths;
};
struct tab { int panes[TAB_PANES],count,active,layout,news; char label[64]; };
static unsigned int pixels[1920*1000];
static struct pt_cell pool_cells[MAX_PANES][CELLS];static struct pt_line pool_lines[MAX_PANES][LINES];
static struct pt_cell edit_cells[8192];static struct pt_line edit_lines[8];
static struct pterm edit;
static struct pane panes[MAX_PANES];static struct tab tabs[MAX_PANES];
static struct zui ui;static struct zui_tabs strip;
static long handle=-1;static int tab_count,current,caret_on=1,is_light;
static const unsigned int dark[16]={0xb4b7c0,0xf0707a,0x8bcba4,0xe7bb70,0x8baaff,0xb6a0ff,0x7ecac7,0xf1f2f4,0xb4b7c0,0xff8a93,0xa6e8b5,0xffdc8f,0x9db6ff,0xddb0f5,0x8fe6f2,0xffffff};
static const unsigned int light[16]={0x202126,0xae325d,0x277347,0x886010,0x3157b7,0x7042be,0x197571,0x5c606b,0x5c606b,0x9e284e,0x21623c,0x76530e,0x294b9f,0x6037a8,0x14635f,0x202126};
static unsigned long text_length(const char *s){unsigned long n=0;while(s[n])n++;return n;}
static void print(const char *s){zenith_write(s,text_length(s));}
static int cell_width(void){return zui_mono.advance[0];}
static int cell_height(void){return zui_mono.height+2;}
static struct tab *tab_now(void){return &tabs[current];}
static struct pane *pane_now(void){struct tab *t=tab_now();return &panes[t->panes[t->active]];}
static unsigned int cell_color(struct pt_cell c){
    int index=c.color?c.color-1:-1;
    if(index>=0&&index<8&&c.bold)index+=8;
    return index<0?ui.theme.text:(is_light?light:dark)[index];
}
/* The session marks a pasted line break with this; the atlas is ASCII, so it is drawn by hand. */
#define LINE_BREAK_MARK 0xb6
static void draw_line_break(int x,int y){
    int w=cell_width(),h=zui_mono.height;
    zui_fill(&ui,(struct zui_rect){x+w-3,y+3,1,h/2},ui.theme.accent);
    zui_fill(&ui,(struct zui_rect){x+1,y+3+h/2-1,w-3,1},ui.theme.accent);
    zui_fill(&ui,(struct zui_rect){x+2,y+3+h/2-2,1,3},ui.theme.accent);
}
static void draw_cells(const struct pterm *t,unsigned int line,unsigned int from,unsigned int to,int x,int y){
    char run[512];unsigned long used=0;int run_x=x;unsigned int color=0;
    for(unsigned int i=from;i<=to;i++){
        struct pt_cell c=i<to?pt_cell_at(t,line,i):(struct pt_cell){0,0,0};
        int mark=i<to&&c.ch==LINE_BREAK_MARK;
        unsigned int next=i<to?cell_color(c):color;
        if(used&&(i==to||mark||next!=color||used+3>=sizeof(run))){
            zui_text(&ui,run_x,y,run,used,&zui_mono,color);used=0;
        }
        if(mark){draw_line_break(x+(int)(i-from)*cell_width(),y);continue;}
        if(i<to){if(!used)run_x=x+(int)(i-from)*cell_width();color=next;used+=(unsigned long)pt_utf8(c.ch?c.ch:' ',run+used);}
    }
}
/* The session keeps the edit line; the last logical line is what it continues, like `read -p`. */
static void compose_edit(const struct pane *p){
    pt_clear(&edit);edit.color=edit.bold=0;
    unsigned int tail=p->term.line_count-1,n=pt_length(&p->term,tail);
    for(unsigned int i=0;i<n&&i<4000;i++)pt_put(&edit,pt_cell_at(&p->term,tail,i));
    unsigned int prompt=p->state[3],input=p->state[4];
    pt_write(&edit,p->text,prompt);
    if(p->state[1]&Z_TERMINAL_SECRET){for(unsigned int i=0;i<p->state[2]&&i<256;i++)pt_write(&edit,"*",1);}
    else pt_write(&edit,p->text+prompt,input);
}
static unsigned int edit_caret(const struct pane *p){
    unsigned int visible=0;int escape=0;
    for(unsigned int i=0;i<p->state[3];i++){
        unsigned char b=(unsigned char)p->text[i];
        if(escape==1){escape=b=='['?2:0;continue;}
        if(escape==2){if(b>=0x40&&b<0x7f)escape=0;continue;}
        if(b==0x1b){escape=1;continue;}
        if((b&0xc0)!=0x80)visible++;
    }
    return pt_length(&p->term,p->term.line_count-1)+visible+p->state[2];
}
static void selection_bounds(const struct pane *p,unsigned int *r0,unsigned int *c0,unsigned int *r1,unsigned int *c1){
    int anchor_first=p->anchor_row<p->head_row||(p->anchor_row==p->head_row&&p->anchor_column<=p->head_column);
    *r0=anchor_first?p->anchor_row:p->head_row;*c0=anchor_first?p->anchor_column:p->head_column;
    *r1=anchor_first?p->head_row:p->anchor_row;*c1=anchor_first?p->head_column:p->anchor_column;
}
static void draw_row(const struct pane *p,const struct pterm *t,unsigned int line,unsigned int row,unsigned int part,int y,unsigned int length){
    unsigned int columns=(unsigned)p->columns,from=part*columns,to=from+columns>length?length:from+columns;
    int x=p->rect.x+PADDING;
    if(p->has_selection){
        unsigned int r0,c0,r1,c1;selection_bounds(p,&r0,&c0,&r1,&c1);
        if(row>=r0&&row<=r1){
            unsigned int a=row==r0?c0:0,b=row==r1?c1:columns;
            if(b>a)zui_round_alpha(&ui,(struct zui_rect){x+(int)a*cell_width(),y,(int)(b-a)*cell_width(),cell_height()},0,ui.theme.accent,70);
        }
    }
    if(from<to)draw_cells(t,line,from,to,x,y+1);
}
static void draw_pane(struct pane *p,int focused){
    struct zui_rect r=p->rect;
    unsigned int columns=(unsigned)p->columns;
    compose_edit(p);
    unsigned int history=0;
    for(unsigned int i=0;i+1<p->term.line_count;i++)history+=pt_rows(pt_length(&p->term,i),columns);
    unsigned int caret=edit_caret(p),edit_length=pt_length(&edit,0),edit_rows=pt_rows(edit_length,columns);
    if(caret/columns+1>edit_rows)edit_rows=caret/columns+1;
    p->total_rows=history+edit_rows;
    int h=cell_height(),content=(int)p->total_rows*h+2*PADDING;
    if(p->following)zui_scroll_jump(&p->view,content>r.h?content-r.h:0);
    int top=zui_scroll_begin(&ui,&p->view,r)+PADDING,bottom=r.y+r.h;
    unsigned int row=0;
    for(unsigned int i=0;i+1<p->term.line_count;i++){
        unsigned int length=pt_length(&p->term,i),n=pt_rows(length,columns);
        int y=top+(int)row*h;
        if(y+(int)n*h>r.y&&y<bottom)for(unsigned int part=0;part<n;part++){int yy=y+(int)part*h;if(yy+h>r.y&&yy<bottom)draw_row(p,&p->term,i,row+part,part,yy,length);}
        row+=n;
        if(top+(int)row*h>=bottom)break;
    }
    int y=top+(int)history*h;
    for(unsigned int part=0;part<edit_rows;part++)draw_row(p,&edit,0,history+part,part,y+(int)part*h,edit_length);
    if(focused&&ui.window_focused&&caret_on&&!(p->state[1]&Z_TERMINAL_RAW))
        zui_fill(&ui,(struct zui_rect){r.x+PADDING+(int)(caret%columns)*cell_width(),y+(int)(caret/columns)*h+1,2,h-2},ui.theme.accent);
    zui_scroll_end(&ui,&p->view,content);
}
static void place_panes(void){
    struct tab *t=tab_now();struct zui_rect a={0,STRIP,ui.width,ui.height-STRIP};
    int n=t->count;
    for(int i=0;i<n;i++){
        struct zui_rect r=a;
        if(n>1&&t->layout==LAYOUT_TALL){
            int left=a.w*3/5,rest=n-1;
            if(i==0)r.w=left;
            else{r.x=left+1;r.w=a.w-left-1;r.h=a.h/rest;r.y=a.y+(i-1)*r.h;if(i==n-1)r.h=a.y+a.h-r.y;}
        }else if(n>1&&t->layout==LAYOUT_COLUMNS){
            r.w=a.w/n;r.x=a.x+i*r.w;if(i==n-1)r.w=a.x+a.w-r.x;
        }else if(n>1){
            r.h=a.h/n;r.y=a.y+i*r.h;if(i==n-1)r.h=a.y+a.h-r.y;
        }
        struct pane *p=&panes[t->panes[i]];
        int columns=(r.w-2*PADDING-12)/cell_width();
        if(columns<20)columns=20;
        if(r.x!=p->rect.x||r.y!=p->rect.y||r.w!=p->rect.w||r.h!=p->rect.h)p->has_selection=0;
        p->rect=r;
        if(columns!=p->columns){p->columns=columns;zenith_terminal_resize(p->session,columns);}
    }
}
static void draw(void){
    struct zui_rect all={0,0,ui.width,ui.height};
    ui.count=0;ui.focus=-1;
    zui_clip(&ui,all);zui_fill(&ui,all,ui.theme.surface);
    place_panes();
    struct tab *t=tab_now();
    /* Chrome first: the tab row, then how this tab divides its space. */
    zui_fill(&ui,(struct zui_rect){0,0,ui.width,STRIP},ui.theme.subtle);
    struct zui_tab items[ZUI_MAX_TABS];
    for(int i=0;i<tab_count;i++)items[i]=(struct zui_tab){tabs[i].label,tabs[i].news};
    int free_pane=0;for(int i=0;i<MAX_PANES;i++)if(!panes[i].used)free_pane=1;
    int controls=3*62+56+16;
    zui_tabs_draw(&ui,&strip,(struct zui_rect){0,0,ui.width-controls,STRIP},items,tab_count,current,free_pane);
    int x=ui.width-controls+8;
    zui_segment_track(&ui,(struct zui_rect){x,6,3*62,32});
    zui_add(&ui,ID_TALL,ZUI_SEGMENT,(struct zui_rect){x+2,8,60,28},"Tall",t->layout==LAYOUT_TALL,1);
    zui_add(&ui,ID_COLUMNS,ZUI_SEGMENT,(struct zui_rect){x+64,8,60,28},"Cols",t->layout==LAYOUT_COLUMNS,1);
    zui_add(&ui,ID_ROWS,ZUI_SEGMENT,(struct zui_rect){x+126,8,58,28},"Rows",t->layout==LAYOUT_ROWS,1);
    zui_add(&ui,ID_SPLIT,ZUI_BUTTON,(struct zui_rect){x+3*62+6,6,50,32},"Split",0,free_pane&&t->count<TAB_PANES);
    zui_update_hover(&ui);zui_draw_controls(&ui);
    zui_fill(&ui,(struct zui_rect){0,STRIP-1,ui.width,1},ui.theme.border);
    for(int i=0;i<t->count;i++){
        struct pane *p=&panes[t->panes[i]];
        draw_pane(p,i==t->active);
        zui_clip(&ui,all);
        if(i>0){
            if(t->layout==LAYOUT_ROWS||(t->layout==LAYOUT_TALL&&i>1))zui_fill(&ui,(struct zui_rect){p->rect.x,p->rect.y,p->rect.w,1},ui.theme.border);
            else zui_fill(&ui,(struct zui_rect){p->rect.x-(t->layout==LAYOUT_TALL?1:0),p->rect.y,1,p->rect.h},ui.theme.border);
        }
    }
    if(t->count>1){
        struct zui_rect r=panes[t->panes[t->active]].rect;
        zui_fill(&ui,(struct zui_rect){r.x,r.y,r.w,2},ui.theme.accent);
    }
    zui_clip(&ui,all);ui.dirty=0;
}
static long present(void){return zenith_gui_present(handle,pixels,(unsigned long)ui.width*(unsigned long)ui.height*4);}
static int open_pane(struct tab *t,int columns){
    if(t->count>=TAB_PANES)return 0;
    for(int i=0;i<MAX_PANES;i++){
        if(panes[i].used)continue;
        long session=zenith_terminal_open(columns);
        if(session<0)return 0;
        struct pane *p=&panes[i];
        p->used=1;p->session=session;p->columns=columns;p->following=1;p->has_selection=p->selecting=0;
        p->view=(struct zui_scroll){{0,0,0,0},{0,0,0,0},0,0,0,0,0,0,0};
        p->rect=(struct zui_rect){0,0,0,0};p->text=(const char *)p->state+24;
        for(unsigned int w=0;w<Z_TERMINAL_STATE_BYTES/4;w++)p->state[w]=0;
        p->last_flags=p->last_cursor=p->last_lengths=0;
        pt_init(&p->term,pool_cells[i],CELLS,pool_lines[i],LINES);
        t->panes[t->count]=i;t->active=t->count++;ui.dirty=1;
        return 1;
    }
    return 0;
}
static int open_tab(void){
    if(tab_count>=MAX_PANES)return 0;
    struct tab *t=&tabs[tab_count];
    *t=(struct tab){{0,0,0,0},0,0,LAYOUT_TALL,0,"Shell"};
    if(!open_pane(t,(ui.width-2*PADDING-12)/cell_width()))return 0;
    current=tab_count++;ui.dirty=1;
    return 1;
}
/* Returns 0 when no tab is left. */
static int close_pane(int tab_index,int position){
    struct tab *t=&tabs[tab_index];struct pane *p=&panes[t->panes[position]];
    zenith_terminal_close(p->session);p->used=0;
    for(int i=position;i+1<t->count;i++)t->panes[i]=t->panes[i+1];
    t->count--;
    if(t->active>=t->count)t->active=t->count?t->count-1:0;
    ui.dirty=1;
    if(t->count)return 1;
    for(int i=tab_index;i+1<tab_count;i++)tabs[i]=tabs[i+1];
    tab_count--;
    if(current>=tab_count)current=tab_count?tab_count-1:0;
    else if(current>tab_index)current--;
    return tab_count>0;
}
static void close_tab(int tab_index){while(tabs[tab_index].count>1)close_pane(tab_index,tabs[tab_index].count-1);}
static void position(const struct pane *p,int x,int y,unsigned int *row,unsigned int *column){
    long r=((long)y-p->rect.y+p->view.visual-PADDING)/cell_height();
    long c=((long)x-p->rect.x-PADDING+cell_width()/2)/cell_width();
    *row=r<0?0:r>=(long)p->total_rows?p->total_rows-1:(unsigned)r;
    *column=c<0?0:c>p->columns?(unsigned)p->columns:(unsigned)c;
}
/* Wrapped rows join directly and real line ends become newlines. */
static char copied[65536];
static void copy_selection(struct pane *p){
    if(!p->has_selection||(p->state[1]&Z_TERMINAL_SECRET))return;
    compose_edit(p);
    unsigned int r0,c0,r1,c1,row=0,columns=(unsigned)p->columns;selection_bounds(p,&r0,&c0,&r1,&c1);unsigned long used=0;
    for(unsigned int i=0;i<p->term.line_count;i++){
        const struct pterm *t=i+1<p->term.line_count?&p->term:&edit;unsigned int line=i+1<p->term.line_count?i:0;
        unsigned int length=pt_length(t,line),n=pt_rows(length,columns);
        for(unsigned int part=0;part<n;part++,row++){
            if(row<r0||row>r1)continue;
            unsigned int from=part*columns,to=from+columns>length?length:from+columns;
            unsigned int a=from+(row==r0?c0:0),b=row==r1?from+c1:to;if(b>to)b=to;
            for(unsigned int k=a;k<b&&used+4<sizeof(copied);k++)used+=(unsigned long)pt_utf8(pt_cell_at(t,line,k).ch,copied+used);
            if(row<r1&&part+1==n&&used+1<sizeof(copied))copied[used++]='\n';
        }
    }
    if(used)zenith_terminal_copy(p->session,copied,used);
}
static void page(struct pane *p,int direction){
    p->following=0;
    int max=zui_scroll_maximum(&p->view),next=zui_scroll_step(p->view.offset,direction*(p->rect.h/3),max);
    p->view.offset=next;if(next==max)p->following=1;
    ui.dirty=1;
}
static void set_tab(int index){if(index>=0&&index<tab_count&&index!=current){current=index;tabs[current].news=0;ui.dirty=1;}}
static struct pane *pane_at(int x,int y,int *which){
    struct tab *t=tab_now();
    for(int i=0;i<t->count;i++){
        struct zui_rect r=panes[t->panes[i]].rect;
        if(x>=r.x&&y>=r.y&&x-r.x<r.w&&y-r.y<r.h){if(which)*which=i;return &panes[t->panes[i]];}
    }
    return 0;
}
/* Ctrl+Shift chords and Ctrl+Tab; returns 1 if it was one of ours. */
static int shortcut(const struct zenith_gui_event *e,int *done){
    int control_shift=e->modifiers==3;
    struct tab *t=tab_now();struct pane *p=pane_now();
    int code=e->code>='A'&&e->code<='Z'?e->code+32:e->code;
    if(control_shift&&code=='c'){copy_selection(p);return 1;}
    if(control_shift&&code=='v'){zenith_terminal_paste(p->session);p->following=1;return 1;}
    if(control_shift&&code=='t'){open_tab();return 1;}
    if(control_shift&&code=='e'){if(open_pane(t,p->columns))ui.dirty=1;return 1;}
    if(control_shift&&code=='w'){if(!close_pane(current,t->active))*done=1;return 1;}
    if(control_shift&&code=='l'){t->layout=(t->layout+1)%3;ui.dirty=1;return 1;}
    if(control_shift&&e->extra==7){t->active=(t->active+t->count-1)%t->count;ui.dirty=1;return 1;}
    if(control_shift&&e->extra==8){t->active=(t->active+1)%t->count;ui.dirty=1;return 1;}
    if(e->extra==4&&(e->modifiers&~1)==2){set_tab((current+(e->modifiers&1?tab_count-1:1))%tab_count);return 1;}
    return 0;
}
static void handle_event(const struct zenith_gui_event *e,int *done){
    if(e->type==Z_GUI_CLOSE||e->type==Z_GUI_CLOSE_INTENT){*done=1;return;}
    if(e->type==Z_GUI_RESIZE){if(zui_apply_viewport(&ui,handle,1920*1000)<0)*done=2;return;}
    if(e->type==Z_GUI_MOVE){zui_event(&ui,e);return;}
    if(e->type==Z_GUI_FOCUS||e->type==Z_GUI_RESET){
        zui_event(&ui,e);
        for(int i=0;i<MAX_PANES;i++){panes[i].selecting=0;panes[i].view.dragging=0;}
        ui.dirty=1;return;
    }
    struct pane *p=pane_now();
    if(e->type==Z_GUI_SCROLL){
        struct pane *under=pane_at(e->x,e->y,0);
        if(under&&zui_scroll_event(&ui,&under->view,e))under->following=under->view.offset==zui_scroll_maximum(&under->view);
        return;
    }
    if(e->type!=Z_GUI_KEY&&e->y<STRIP){
        int action=zui_tabs_event(&strip,e);
        if(action==ZUI_TABS_ADD)open_tab();
        else if(action>0)set_tab(action-1);
        else if(action<0){int i=-action-1;close_tab(i);if(!close_pane(i,0))*done=1;}
        int id=zui_event(&ui,e);ui.focus=-1;
        struct tab *t=tab_now();
        if(id>=ID_TALL&&id<=ID_ROWS){t->layout=id-ID_TALL;ui.dirty=1;}
        if(id==ID_SPLIT&&open_pane(t,pane_now()->columns))ui.dirty=1;
        return;
    }
    if(e->type==Z_GUI_DOWN||e->type==Z_GUI_UP||e->type==Z_GUI_DRAG){zui_event(&ui,e);ui.focus=-1;}
    if(e->type==Z_GUI_DOWN){
        int which=0;struct pane *under=pane_at(e->x,e->y,&which);
        if(!under)return;
        if(which!=tab_now()->active){tab_now()->active=which;ui.dirty=1;}
        p=under;
    }
    if(zui_scroll_event(&ui,&p->view,e)){p->following=p->view.offset==zui_scroll_maximum(&p->view);return;}
    if(p->view.dragging){if(e->type==Z_GUI_UP)p->view.dragging=0;return;}
    if(e->type==Z_GUI_DOWN){
        position(p,e->x,e->y,&p->anchor_row,&p->anchor_column);p->head_row=p->anchor_row;p->head_column=p->anchor_column;
        p->selecting=1;p->has_selection=0;ui.dirty=1;return;
    }
    if(e->type==Z_GUI_DRAG&&p->selecting){position(p,e->x,e->y,&p->head_row,&p->head_column);p->has_selection=p->head_row!=p->anchor_row||p->head_column!=p->anchor_column;ui.dirty=1;return;}
    if(e->type==Z_GUI_UP){p->selecting=0;return;}
    if(e->type!=Z_GUI_KEY)return;
    int raw=(p->state[1]&Z_TERMINAL_RAW)!=0;
    if(!raw&&shortcut(e,done))return;
    if(!raw&&!e->modifiers&&e->extra==ZUI_KEY_PAGE_UP){page(p,-1);return;}
    if(!raw&&!e->modifiers&&e->extra==ZUI_KEY_PAGE_DOWN){page(p,1);return;}
    p->has_selection=0;p->following=1;
    struct zenith_terminal_key k={e->code,e->modifiers,e->extra};
    zenith_terminal_key(p->session,&k);ui.dirty=1;
}
/* A tab's name is where its focused shell is. */
static void update_label(struct tab *t){
    struct pane *p=&panes[t->panes[t->active]];
    unsigned int skip=p->state[3]+p->state[4],n=p->state[5];const char *d=p->text+skip;
    if(!n||skip+n+24>Z_TERMINAL_STATE_BYTES)return;
    unsigned int from=0;
    for(unsigned int i=0;i+1<n;i++)if(d[i]=='/')from=i+1;
    unsigned int k=0;
    for(unsigned int i=from;i<n&&k+1<sizeof(t->label);i++)t->label[k++]=d[i];
    if(!k)t->label[k++]='/';
    t->label[k]=0;
}
/* Returns 1 on output or a change worth drawing, -1 when the shell ended. */
static int read_pane(struct pane *p,int *got_output){
    static char incoming[16384];
    for(int n=0;n<8;n++){
        long got=zenith_terminal_read(p->session,incoming,sizeof(incoming));
        if(got<=0)break;
        pt_write(&p->term,incoming,(unsigned long)got);p->term.changed=1;p->has_selection=0;*got_output=1;
    }
    int change=p->term.changed;p->term.changed=0;
    if(zenith_terminal_state(p->session,p->state)<24)return -1;
    unsigned long lengths=p->state[3]*65536UL+p->state[4];
    if(p->state[1]!=p->last_flags||p->state[2]!=p->last_cursor||lengths!=p->last_lengths){
        p->last_flags=p->state[1];p->last_cursor=p->state[2];p->last_lengths=lengths;change=1;
    }
    if(p->state[1]&Z_TERMINAL_EXIT)return -1;
    return change;
}
int main(int argc,char **argv){
    if(argc==2){const char *a=argv[1];if(a[0]=='-'&&a[1]=='-'&&a[2]=='v'){print("polaris " SUNBEAM_VERSION "\n");return 0;}}
    zui_icons_use_packages();
    if(!zui_init(&ui,pixels,640,480))return 1;
    if(!pt_init(&edit,edit_cells,8192,edit_lines,8))return 1;
    handle=zenith_gui_open(640,480,"Polaris",7);
    if(handle<0){print("polaris: graphical session required\n");return 1;}
    int result=0;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){print("polaris: interactive client support required\n");result=1;goto end;}
    ui.hover_supported=zenith_gui_pointer_motion(handle)==0;
    if(!open_tab()){print("polaris: this kernel has no terminal sessions\n");result=1;goto end;}
    long clock=zui_motion_clock();if(clock>=0)ui.now=clock;
    zui_apply_motion(&ui,clock);zui_apply_appearance(&ui);ui.dirty=1;
    print("POLARIS_READY\n");
    long last_blink=0;int done=0;
    for(unsigned int i=0;!done;i++){
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);
        if(r<0){result=1;break;}
        if(r>0)handle_event(&e,&done);
        if(done==2){result=1;break;}
        if(done)break;
        /* Sessions in view are read every pass and others now and then: reads count against a budget. */
        for(int t=tab_count-1;t>=0&&!done;t--){
            if(t!=current&&(i%HIDDEN_POLL)!=0)continue;
            for(int k=tabs[t].count-1;k>=0;k--){
                int output=0,change=read_pane(&panes[tabs[t].panes[k]],&output);
                if(change<0){if(!close_pane(t,k))done=1;break;}
                if(t==current&&change)ui.dirty=1;
                if(t!=current&&output&&!tabs[t].news){tabs[t].news=1;ui.dirty=1;}
            }
        }
        if(done)break;
        for(int t=0;t<tab_count;t++)if((i&31)==0){char before=tabs[t].label[0];update_label(&tabs[t]);if(before!=tabs[t].label[0])ui.dirty=1;}
        if((i&63)==0){zui_apply_appearance(&ui);if((i&255)==0)zui_apply_motion(&ui,clock);}
        is_light=ui.theme.surface>0x808080;
        if(clock>=0&&(ui.dirty||ui.animating||(i&15)==0)){
            long now=zui_motion_clock();
            if(now/500!=last_blink){last_blink=now/500;int on=last_blink%2==0;if(on!=caret_on){caret_on=on;ui.dirty=1;}}
            if(ui.dirty||ui.animating){int moving=ui.animating;zui_motion_frame(&ui,now);if(moving)ui.dirty=1;}
        }
        if(ui.dirty){draw();if(present()<0){result=1;break;}}
    }
end:
    for(int i=0;i<MAX_PANES;i++)if(panes[i].used)zenith_terminal_close(panes[i].session);
    zenith_gui_close(handle);return result;
}
