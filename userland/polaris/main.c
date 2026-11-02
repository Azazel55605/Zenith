/* Polaris: the native terminal. Each shell lives in a kernel-owned session; this draws them, as in
 * docs/design/terminal-concept.svg: tabs and layouts, panes, prompt marks, a scrollback rail, a find
 * bar and keyboard hints, over the system's materials when the person chose them. */
#include "../ui/ui.h"
#include "../ui/widgets.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
#include "term.h"
enum { MAX_PANES=8, TAB_PANES=4, CELLS=1<<18, LINES=8192, PADDING=16, STRIP=48, STATUS=28, MARGIN=8, GAP=4, FIND=46, HIDDEN_POLL=16 };
enum { LAYOUT_TALL=0, LAYOUT_COLUMNS=1, LAYOUT_ROWS=2 };
enum { ID_TALL=1, ID_COLUMNS=2, ID_ROWS=3, ID_FIND=4, ID_CASE=10, ID_PROMPTS=11, ID_PREVIOUS=12, ID_NEXT=13, ID_CLOSE_FIND=14 };
enum { MAX_MATCHES=1024, MAX_HINTS=26, MAX_MARKS=256 };
struct pane {
    int used; long session; struct pterm term; struct zui_scroll view; struct zui_rect rect,text_area;
    unsigned int state[Z_TERMINAL_STATE_BYTES/4]; const char *text;
    int columns,following,selecting,has_selection;
    unsigned int anchor_row,anchor_column,head_row,head_column,total_rows;
    unsigned long last_flags,last_cursor,last_lengths;
    long running_since; char command[96];
};
struct tab { int panes[TAB_PANES],count,active,layout,news; char label[64]; };
struct match { unsigned int line,column; };
struct hint { unsigned int line,column,length; char text[256]; };
static unsigned int pixels[1920*1000];
static struct pt_cell pool_cells[MAX_PANES][CELLS];static struct pt_line pool_lines[MAX_PANES][LINES];
static struct pt_cell edit_cells[8192];static struct pt_line edit_lines[8];
static struct pterm edit;
static struct pane panes[MAX_PANES];static struct tab tabs[MAX_PANES];
static struct zui ui;static struct zui_tabs strip;
static long handle=-1,now_ms;static int tab_count,current,caret_on=1,is_light,material;
/* Find works on the focused pane only. */
static struct { int open,case_sensitive,prompts_only,stale,count,current; char query[128]; struct zui_input input; struct match matches[MAX_MATCHES]; } find;
static struct { int open,count; struct hint hints[MAX_HINTS]; } hints;
static char title[200];
static const unsigned int dark[16]={0xb4b7c0,0xf0707a,0x8bcba4,0xe7bb70,0x8baaff,0xb6a0ff,0x7ecac7,0xf1f2f4,0xb4b7c0,0xff8a93,0xa6e8b5,0xffdc8f,0x9db6ff,0xddb0f5,0x8fe6f2,0xffffff};
static const unsigned int light[16]={0x202126,0xae325d,0x277347,0x886010,0x3157b7,0x7042be,0x197571,0x5c606b,0x5c606b,0x9e284e,0x21623c,0x76530e,0x294b9f,0x6037a8,0x14635f,0x202126};
static unsigned int palette(int index){return (is_light?light:dark)[index];}
#define GREEN palette(2)
#define RED palette(1)
#define YELLOW palette(3)
static unsigned long text_length(const char *s){unsigned long n=0;while(s[n])n++;return n;}
static void print(const char *s){zenith_write(s,text_length(s));}
static int cell_width(void){return zui_mono.advance[0];}
static int cell_height(void){return zui_mono.height+2;}
static struct tab *tab_now(void){return &tabs[current];}
static struct pane *pane_now(void){struct tab *t=tab_now();return &panes[t->panes[t->active]];}
static int append(char *out,int at,int limit,const char *s){while(*s&&at+1<limit)out[at++]=*s++;out[at]=0;return at;}
static int append_number(char *out,int at,int limit,unsigned long v){
    char digits[24];int n=0;do{digits[n++]=(char)('0'+v%10);v/=10;}while(v);
    while(n&&at+1<limit)out[at++]=digits[--n];
    out[at]=0;return at;
}
static unsigned int cell_color(struct pt_cell c){
    int index=c.color?c.color-1:-1;
    if(index>=0&&index<8&&c.bold)index+=8;
    return index<0?ui.theme.text:palette(index);
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
        if(used&&(i==to||mark||next!=color||used+3>=sizeof(run))){zui_text(&ui,run_x,y,run,used,&zui_mono,color);used=0;}
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
static unsigned long query_length(void){return text_length(find.query);}
static void draw_matches(const struct pane *p,unsigned int line,unsigned int from,unsigned int to,int y){
    if(!find.open||p!=pane_now()||!find.count)return;
    unsigned long n=query_length();
    for(int i=0;i<find.count;i++){
        const struct match *m=&find.matches[i];
        if(m->line!=line||m->column+n<=from||m->column>=to)continue;
        unsigned int a=m->column<from?from:m->column,b=m->column+n>to?to:(unsigned int)(m->column+n);
        struct zui_rect r={p->text_area.x+(int)(a-from)*cell_width(),y,(int)(b-a)*cell_width(),cell_height()};
        if(i==find.current){zui_round(&ui,r,3,ui.theme.accent);}
        else{zui_round_alpha(&ui,r,3,ui.theme.accent,60);zui_round_outline(&ui,r,3,ui.theme.accent);}
    }
}
static void draw_row(const struct pane *p,const struct pterm *t,unsigned int line,unsigned int row,unsigned int part,int y,unsigned int length){
    unsigned int columns=(unsigned)p->columns,from=part*columns,to=from+columns>length?length:from+columns;
    int x=p->text_area.x;
    if(p->has_selection){
        unsigned int r0,c0,r1,c1;selection_bounds(p,&r0,&c0,&r1,&c1);
        if(row>=r0&&row<=r1){
            unsigned int a=row==r0?c0:0,b=row==r1?c1:columns;
            if(b>a)zui_round_alpha(&ui,(struct zui_rect){x+(int)a*cell_width(),y,(int)(b-a)*cell_width(),cell_height()},0,ui.theme.accent,70);
        }
    }
    if(t==&p->term)draw_matches(p,line,from,to,y);
    if(from<to)draw_cells(t,line,from,to,x,y+1);
    /* The current find hit draws its text in the window color, over the accent. */
    if(t==&p->term&&find.open&&p==pane_now()&&find.count&&find.matches[find.current].line==line){
        const struct match *m=&find.matches[find.current];unsigned long n=query_length();
        if(m->column<to&&m->column+n>from){
            unsigned int a=m->column<from?from:m->column,b=m->column+n>to?to:(unsigned int)(m->column+n);
            char run[512];unsigned long used=0;
            for(unsigned int i=a;i<b&&used+3<sizeof(run);i++)used+=(unsigned long)pt_utf8(pt_cell_at(t,line,i).ch,run+used);
            zui_text(&ui,x+(int)(a-from)*cell_width(),y+1,run,used,&zui_mono,ui.theme.on_accent);
        }
    }
}
/* A finished command's result beside its prompt: status and time, or how long it has run. */
static void draw_result(const struct pane *p,unsigned int line,int y,int latest){
    int status=0;unsigned int ms=0;int mark=pt_mark(&p->term,line,&status,&ms);
    if(!mark)return;
    int running=mark==PT_PROMPT&&latest&&(p->state[1]&Z_TERMINAL_BUSY);
    unsigned int color=running?ui.theme.accent:mark==PT_DONE&&status==0?GREEN:mark==PT_DONE?RED:ui.theme.secondary;
    if(mark==PT_PROMPT&&!running)return;
    zui_fill(&ui,(struct zui_rect){p->rect.x+6,y+2,3,cell_height()-4},color);
    char label[48];int n=0;
    if(running){
        long elapsed=p->running_since?now_ms-p->running_since:0;
        n=append(label,n,48,"running ");n=append_number(label,n,48,(unsigned long)(elapsed/1000));n=append(label,n,48,".");
        n=append_number(label,n,48,(unsigned long)(elapsed/100%10));n=append(label,n,48," s");
    }else{
        n=append_number(label,n,48,(unsigned long)status);n=append(label,n,48," - ");
        if(ms<1000){n=append_number(label,n,48,ms);n=append(label,n,48," ms");}
        else{n=append_number(label,n,48,ms/1000);n=append(label,n,48,".");n=append_number(label,n,48,ms/100%10);n=append(label,n,48," s");}
    }
    int w=zui_measure(label,(unsigned long)n,&zui_caption)+16;
    struct zui_rect chip={p->rect.x+p->rect.w-w-20,y+1,w,cell_height()-2};
    zui_round(&ui,chip,4,ui.theme.subtle);
    zui_text(&ui,chip.x+8,chip.y+(chip.h-zui_caption.height)/2,label,(unsigned long)n,&zui_caption,color);
}
static void draw_hints(const struct pane *p,unsigned int line,unsigned int from,unsigned int to,int y){
    if(!hints.open||p!=pane_now())return;
    for(int i=0;i<hints.count;i++){
        const struct hint *h=&hints.hints[i];
        if(h->line!=line||h->column<from||h->column>=to)continue;
        struct zui_rect chip={p->text_area.x+(int)(h->column-from)*cell_width()-2,y+1,cell_width()+4,cell_height()-2};
        zui_round(&ui,chip,3,YELLOW);
        char letter=(char)('a'+i);
        zui_text(&ui,chip.x+2,y+2,&letter,1,&zui_mono,ui.theme.surface);
    }
}
static void draw_pane(struct pane *p,int focused,int many){
    struct zui_rect r=p->rect;
    unsigned int columns=(unsigned)p->columns;
    /* Over a material, only the toolkit's light workspace tint, so the backdrop stays visible. */
    if(material)zui_round_alpha(&ui,r,8,ui.theme.surface,40);
    else zui_round(&ui,r,8,ui.theme.surface);
    zui_round_outline(&ui,r,8,focused&&many?ui.theme.accent:ui.theme.border);
    compose_edit(p);
    unsigned int history=0;
    for(unsigned int i=0;i+1<p->term.line_count;i++)history+=pt_rows(pt_length(&p->term,i),columns);
    unsigned int caret=edit_caret(p),edit_length=pt_length(&edit,0),edit_rows=pt_rows(edit_length,columns);
    if(caret/columns+1>edit_rows)edit_rows=caret/columns+1;
    p->total_rows=history+edit_rows;
    struct zui_rect area={r.x+1,r.y+1,r.w-2,r.h-2-(find.open&&focused?FIND:0)-(hints.open&&focused?32:0)};
    p->text_area=(struct zui_rect){r.x+PADDING,area.y,area.w-PADDING-14,area.h};
    int h=cell_height(),content=(int)p->total_rows*h+16;
    if(p->following)zui_scroll_jump(&p->view,content>area.h?content-area.h:0);
    int top=zui_scroll_begin(&ui,&p->view,area)+8,bottom=area.y+area.h;
    struct zui_scroll_mark marks[MAX_MARKS];int mark_count=0;
    unsigned int row=0,latest_prompt=0;int have_prompt=0;
    for(unsigned int i=0;i+1<p->term.line_count;i++)if(pt_mark(&p->term,i,0,0)){latest_prompt=i;have_prompt=1;}
    int match=0;
    for(unsigned int i=0;i+1<p->term.line_count;i++){
        unsigned int length=pt_length(&p->term,i),n=pt_rows(length,columns);
        int status=0,mark=pt_mark(&p->term,i,&status,0);
        if(mark&&mark_count<MAX_MARKS)marks[mark_count++]=(struct zui_scroll_mark){(int)row*h+8,mark==PT_DONE&&status?RED:ui.theme.accent};
        while(find.open&&focused&&match<find.count&&find.matches[match].line<i)match++;
        if(find.open&&focused&&match<find.count&&find.matches[match].line==i&&mark_count<MAX_MARKS)
            marks[mark_count++]=(struct zui_scroll_mark){(int)row*h+8,YELLOW};
        int y=top+(int)row*h;
        if(y+(int)n*h>area.y&&y<bottom){
            for(unsigned int part=0;part<n;part++){
                int yy=y+(int)part*h;
                if(yy+h>area.y&&yy<bottom){draw_row(p,&p->term,i,row+part,part,yy,length);draw_hints(p,i,part*columns,part*columns+columns,yy);}
            }
            if(mark)draw_result(p,i,y,have_prompt&&i==latest_prompt);
        }
        row+=n;
    }
    int y=top+(int)history*h;
    for(unsigned int part=0;part<edit_rows;part++)draw_row(p,&edit,0,history+part,part,y+(int)part*h,edit_length);
    if(focused&&ui.window_focused&&caret_on&&!(p->state[1]&Z_TERMINAL_RAW)&&!find.open)
        zui_fill(&ui,(struct zui_rect){p->text_area.x+(int)(caret%columns)*cell_width(),y+(int)(caret/columns)*h+1,2,h-2},ui.theme.accent);
    zui_scroll_end(&ui,&p->view,content);
    zui_scroll_marks(&ui,&p->view,marks,mark_count);
}
static void place_panes(void){
    struct tab *t=tab_now();
    struct zui_rect a={MARGIN,STRIP,ui.width-2*MARGIN,ui.height-STRIP-STATUS};
    int n=t->count;
    for(int i=0;i<n;i++){
        struct zui_rect r=a;
        if(n>1&&t->layout==LAYOUT_TALL){
            int left=(a.w-GAP)*3/5,rest=n-1,each=(a.h-GAP*(rest-1))/rest;
            if(i==0)r.w=left;
            else{r.x=a.x+left+GAP;r.w=a.w-left-GAP;r.y=a.y+(i-1)*(each+GAP);r.h=i==n-1?a.y+a.h-r.y:each;}
        }else if(n>1&&t->layout==LAYOUT_COLUMNS){
            int each=(a.w-GAP*(n-1))/n;r.w=each;r.x=a.x+i*(each+GAP);if(i==n-1)r.w=a.x+a.w-r.x;
        }else if(n>1){
            int each=(a.h-GAP*(n-1))/n;r.h=each;r.y=a.y+i*(each+GAP);if(i==n-1)r.h=a.y+a.h-r.y;
        }
        struct pane *p=&panes[t->panes[i]];
        int columns=(r.w-PADDING-16)/cell_width();
        if(columns<20)columns=20;
        if(r.x!=p->rect.x||r.y!=p->rect.y||r.w!=p->rect.w||r.h!=p->rect.h)p->has_selection=0;
        p->rect=r;
        if(columns!=p->columns){p->columns=columns;zenith_terminal_resize(p->session,columns);}
    }
}
/* Small drawn glyphs for the layout control; the shared icon set has none for these. */
static void layout_glyph(int x,int y,int layout,unsigned int color){
    struct zui_rect r={x,y,18,14};zui_round_outline(&ui,r,3,color);
    if(layout==LAYOUT_TALL){zui_fill(&ui,(struct zui_rect){x+10,y,1,14},color);zui_fill(&ui,(struct zui_rect){x+10,y+7,8,1},color);}
    else if(layout==LAYOUT_COLUMNS)zui_fill(&ui,(struct zui_rect){x+9,y,1,14},color);
    else zui_fill(&ui,(struct zui_rect){x,y+7,18,1},color);
}
/* Drawn stand-ins for icons, so a system without the icon package still shows its buttons. */
static void icon_or_drawn(const char *name,int x,int y,int role){
    if(zui_icon(&ui,name,x,y,16,role))return;
    unsigned int c=role==ZUI_ICON_NORMAL?ui.theme.accent:ui.theme.secondary;
    if(name[0]=='s'){
        zui_round_outline(&ui,(struct zui_rect){x+1,y+1,11,11},5,c);
        for(int i=0;i<4;i++)zui_fill(&ui,(struct zui_rect){x+10+i,y+10+i,2,2},c);
    }else for(int i=0;i<11;i++){
        zui_fill(&ui,(struct zui_rect){x+2+i,y+2+i,2,1},c);
        zui_fill(&ui,(struct zui_rect){x+12-i,y+2+i,2,1},c);
    }
}
static int secret_in_view(void){
    struct tab *t=tab_now();
    for(int i=0;i<t->count;i++)if(panes[t->panes[i]].state[1]&Z_TERMINAL_SECRET)return 1;
    return 0;
}
static void draw_status(void){
    struct tab *t=tab_now();struct pane *p=pane_now();
    int y=ui.height-STATUS,baseline=y+(STATUS-zui_caption.height)/2;
    char left[160];int n=0;
    n=append(left,n,160,t->layout==LAYOUT_TALL?"Tall layout":t->layout==LAYOUT_COLUMNS?"Columns layout":"Rows layout");
    n=append(left,n,160,", ");n=append_number(left,n,160,(unsigned long)t->count);n=append(left,n,160,t->count==1?" pane":" panes");
    n=append(left,n,160,"     ");n=append_number(left,n,160,(unsigned long)p->columns);n=append(left,n,160," x ");
    n=append_number(left,n,160,(unsigned long)((p->rect.h-16)/cell_height()));
    n=append(left,n,160,"     Scrollback ");n=append_number(left,n,160,p->term.line_count);n=append(left,n,160," / ");n=append_number(left,n,160,LINES);
    int left_width=zui_measure(left,(unsigned long)n,&zui_caption);
    zui_text(&ui,MARGIN+8,baseline,left,(unsigned long)n,&zui_caption,ui.theme.secondary);
    /* The right side gives way, longest first, before it would touch the left. */
    static const char *const options[]={"UTF-8     Hack 14     Ctrl+Shift+F find     Ctrl+Shift+H hints","UTF-8     Hack 14","UTF-8"};
    for(int i=0;i<3;i++){
        unsigned long rn=text_length(options[i]);int w=zui_measure(options[i],rn,&zui_caption);
        if(MARGIN+8+left_width+32+w>ui.width-MARGIN-8)continue;
        zui_text(&ui,ui.width-MARGIN-8-w,baseline,options[i],rn,&zui_caption,ui.theme.secondary);
        break;
    }
}
static void draw_find_bar(void){
    struct pane *p=pane_now();struct zui_rect r=p->rect;
    int y=r.y+r.h-FIND;
    zui_fill(&ui,(struct zui_rect){r.x+1,y,r.w-2,1},ui.theme.border);
    icon_or_drawn("search",r.x+12,y+15,ZUI_ICON_TEXT);
    int field=r.w>760?240:r.w>560?180:r.w/4;
    zui_input_draw(&ui,(struct zui_rect){r.x+36,y+7,field,32},&find.input,1);
    char count[40];int n=0;
    if(!find.count)n=append(count,n,40,query_length()?"No results":"");
    else{n=append_number(count,n,40,(unsigned long)find.current+1);n=append(count,n,40," of ");n=append_number(count,n,40,(unsigned long)find.count);if(find.count==MAX_MATCHES)n=append(count,n,40,"+");}
    int x=r.x+36+field+10;
    zui_text(&ui,x,y+(FIND-zui_caption.height)/2,count,(unsigned long)n,&zui_caption,ui.theme.secondary);
    x+=zui_measure("100 of 1000+",12,&zui_caption)+8;
    if(x+212<r.x+r.w-44){
        zui_add(&ui,ID_CASE,ZUI_SEGMENT,(struct zui_rect){x,y+8,36,30},"Aa",find.case_sensitive,1);
        zui_add(&ui,ID_PROMPTS,ZUI_SEGMENT,(struct zui_rect){x+40,y+8,68,30},"Prompts",find.prompts_only,1);
        zui_add(&ui,ID_PREVIOUS,ZUI_BUTTON,(struct zui_rect){x+114,y+8,48,30},"Prev",0,find.count>0);
        zui_add(&ui,ID_NEXT,ZUI_BUTTON,(struct zui_rect){x+164,y+8,48,30},"Next",0,find.count>0);
    }
    zui_add(&ui,ID_CLOSE_FIND,ZUI_BUTTON,(struct zui_rect){r.x+r.w-40,y+8,30,30},"",0,1);
}
static void draw(void){
    struct zui_rect all={0,0,ui.width,ui.height};
    ui.count=0;ui.focus=-1;
    place_panes();
    struct tab *t=tab_now();
    /* Whole-window material when the person chose it, never while a hidden prompt is in view. */
    int secret=secret_in_view();
    if(ui.foreground)zui_begin_material_frame(&ui);
    material=ui.foreground&&!secret&&ui.full_background&&zui_material_surface(&ui,all,0,ZUI_SURFACE_CONTENT,2);
    if(!material){
        int navigation=ui.foreground&&!secret&&zui_material_surface(&ui,(struct zui_rect){0,0,ui.width,STRIP},0,ZUI_SURFACE_NAVIGATION,2);
        zui_clip(&ui,(struct zui_rect){0,navigation?STRIP:0,ui.width,ui.height-(navigation?STRIP:0)});
        zui_fill(&ui,ui.clip,ui.theme.subtle);
    }
    zui_clip(&ui,all);
    struct zui_tab items[ZUI_MAX_TABS];
    for(int i=0;i<tab_count;i++)items[i]=(struct zui_tab){tabs[i].label,tabs[i].news,"terminal"};
    int free_pane=0;for(int i=0;i<MAX_PANES;i++)if(!panes[i].used)free_pane=1;
    int controls=3*44+8+48+MARGIN;
    zui_tabs_draw(&ui,&strip,(struct zui_rect){MARGIN-4,2,ui.width-controls-MARGIN,STRIP-4},items,tab_count,current,free_pane);
    int x=ui.width-controls;
    zui_segment_track(&ui,(struct zui_rect){x,9,3*44,32});
    zui_add(&ui,ID_TALL,ZUI_SEGMENT,(struct zui_rect){x+2,11,40,28},"",t->layout==LAYOUT_TALL,1);
    zui_add(&ui,ID_COLUMNS,ZUI_SEGMENT,(struct zui_rect){x+46,11,40,28},"",t->layout==LAYOUT_COLUMNS,1);
    zui_add(&ui,ID_ROWS,ZUI_SEGMENT,(struct zui_rect){x+90,11,40,28},"",t->layout==LAYOUT_ROWS,1);
    zui_add(&ui,ID_FIND,ZUI_BUTTON,(struct zui_rect){x+3*44+8,9,36,32},"",find.open,1);
    for(int i=0;i<t->count;i++)draw_pane(&panes[t->panes[i]],i==t->active,t->count>1);
    zui_clip(&ui,all);
    if(find.open)draw_find_bar();
    if(hints.open){
        struct zui_rect r=pane_now()->rect;
        struct zui_rect banner={r.x+8,r.y+r.h-36,r.w-16,28};
        zui_round(&ui,banner,6,ui.theme.subtle);
        const char *s="Hints: a letter types it at the prompt, Shift+letter copies it, Esc cancels";
        zui_text(&ui,banner.x+10,banner.y+(28-zui_caption.height)/2,s,text_length(s),&zui_caption,ui.theme.secondary);
    }
    zui_update_hover(&ui);zui_draw_controls(&ui);
    for(int k=0;k<3;k++)layout_glyph(x+2+k*44+11,18,k,t->layout==k?ui.theme.accent:ui.theme.secondary);
    icon_or_drawn("search",x+3*44+18,17,find.open?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
    if(find.open){struct zui_rect r=pane_now()->rect;icon_or_drawn("x",r.x+r.w-33,r.y+r.h-FIND+15,ZUI_ICON_TEXT);}
    draw_status();
    zui_clip(&ui,all);ui.dirty=0;
}
static long present(void){
    unsigned long bytes=(unsigned long)ui.width*(unsigned long)ui.height*4;
    return ui.foreground?zenith_gui_present_material(handle,pixels,bytes,ui.materials,(unsigned int)ui.material_count):zenith_gui_present(handle,pixels,bytes);
}
static int open_pane(struct tab *t,int columns){
    if(t->count>=TAB_PANES)return 0;
    for(int i=0;i<MAX_PANES;i++){
        if(panes[i].used)continue;
        long session=zenith_terminal_open(columns<20?20:columns);
        if(session<0)return 0;
        struct pane *p=&panes[i];
        p->used=1;p->session=session;p->columns=columns;p->following=1;p->has_selection=p->selecting=0;
        p->view=(struct zui_scroll){{0,0,0,0},{0,0,0,0},0,0,0,0,0,0,0};
        p->rect=(struct zui_rect){0,0,0,0};p->text=(const char *)p->state+24;
        for(unsigned int w=0;w<Z_TERMINAL_STATE_BYTES/4;w++)p->state[w]=0;
        p->last_flags=p->last_cursor=p->last_lengths=0;p->running_since=0;p->command[0]=0;
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
    if(!open_pane(t,(ui.width-2*MARGIN-PADDING-16)/cell_width()))return 0;
    current=tab_count++;find.stale=1;ui.dirty=1;
    return 1;
}
/* Returns 0 when no tab is left. */
static int close_pane(int tab_index,int position){
    struct tab *t=&tabs[tab_index];struct pane *p=&panes[t->panes[position]];
    zenith_terminal_close(p->session);p->used=0;
    for(int i=position;i+1<t->count;i++)t->panes[i]=t->panes[i+1];
    t->count--;
    if(t->active>=t->count)t->active=t->count?t->count-1:0;
    ui.dirty=1;find.stale=1;hints.open=0;
    if(t->count)return 1;
    for(int i=tab_index;i+1<tab_count;i++)tabs[i]=tabs[i+1];
    tab_count--;
    if(current>=tab_count)current=tab_count?tab_count-1:0;
    else if(current>tab_index)current--;
    return tab_count>0;
}
static void close_tab(int tab_index){while(tabs[tab_index].count>1)close_pane(tab_index,tabs[tab_index].count-1);}
static void position(const struct pane *p,int x,int y,unsigned int *row,unsigned int *column){
    long r=((long)y-p->text_area.y+p->view.visual-8)/cell_height();
    long c=((long)x-p->text_area.x+cell_width()/2)/cell_width();
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
static void set_tab(int index){if(index>=0&&index<tab_count&&index!=current){current=index;tabs[current].news=0;find.stale=1;hints.open=0;ui.dirty=1;}}
static struct pane *pane_at(int x,int y,int *which){
    struct tab *t=tab_now();
    for(int i=0;i<t->count;i++){
        struct zui_rect r=panes[t->panes[i]].rect;
        if(x>=r.x&&y>=r.y&&x-r.x<r.w&&y-r.y<r.h){if(which)*which=i;return &panes[t->panes[i]];}
    }
    return 0;
}
/* ---- find */
static int lower(int c){return c>='A'&&c<='Z'?c+32:c;}
static void search(void){
    struct pane *p=pane_now();find.count=0;find.stale=0;
    unsigned long n=query_length();
    if(!n)return;
    for(unsigned int i=0;i+1<p->term.line_count&&find.count<MAX_MATCHES;i++){
        if(find.prompts_only&&!pt_mark(&p->term,i,0,0))continue;
        unsigned int length=pt_length(&p->term,i);
        for(unsigned int c=0;c+n<=length&&find.count<MAX_MATCHES;c++){
            unsigned long k=0;
            for(;k<n;k++){
                int a=pt_cell_at(&p->term,i,c+(unsigned int)k).ch,b=(unsigned char)find.query[k];
                if(find.case_sensitive?a!=b:lower(a)!=lower(b))break;
            }
            if(k==n)find.matches[find.count++]=(struct match){i,c};
        }
    }
    if(find.current>=find.count)find.current=find.count?find.count-1:0;
}
static void reveal_match(void){
    if(!find.count)return;
    struct pane *p=pane_now();unsigned int row=0,columns=(unsigned)p->columns;
    for(unsigned int i=0;i<find.matches[find.current].line;i++)row+=pt_rows(pt_length(&p->term,i),columns);
    row+=find.matches[find.current].column/columns;
    int target=(int)row*cell_height()-p->view.rect.h/2;
    if(target<0)target=0;
    int max=zui_scroll_maximum(&p->view);
    p->following=0;p->view.offset=target>max?max:target;ui.dirty=1;
}
static void step_match(int direction){
    if(!find.count)return;
    find.current=(find.current+direction+find.count)%find.count;reveal_match();
}
static void toggle_find(void){
    find.open=!find.open;hints.open=0;
    if(find.open){find.stale=1;}
    else pane_now()->following=1;
    ui.dirty=1;
}
/* ---- hints: paths and links in view, each behind a letter */
static int path_start(const struct pterm *t,unsigned int line,unsigned int c,unsigned int length){
    int a=pt_cell_at(t,line,c).ch,b=c+1<length?pt_cell_at(t,line,c+1).ch:0;
    if(a=='/'&&b>' '&&b!='/')return 1;
    if(a=='~'&&b=='/')return 1;
    if(a=='h'&&c+7<length){
        char w[8];for(int i=0;i<8;i++)w[i]=(char)pt_cell_at(t,line,c+(unsigned)i).ch;
        if(w[1]=='t'&&w[2]=='t'&&w[3]=='p'&&(w[4]==':'||(w[4]=='s'&&w[5]==':')))return 1;
    }
    return 0;
}
static void open_hints(void){
    struct pane *p=pane_now();hints.count=0;find.open=0;
    unsigned int columns=(unsigned)p->columns,row=0;int h=cell_height();
    int first_row=(p->view.visual-8)/h,last_row=first_row+p->view.rect.h/h+1;
    if(first_row<0)first_row=0;
    for(unsigned int i=0;i+1<p->term.line_count&&hints.count<MAX_HINTS;i++){
        unsigned int length=pt_length(&p->term,i),n=pt_rows(length,columns);
        if((int)(row+n)>first_row&&(int)row<=last_row){
            for(unsigned int c=0;c<length&&hints.count<MAX_HINTS;c++){
                int before=c?pt_cell_at(&p->term,i,c-1).ch:' ';
                if(before!=' '&&before!='\'' &&before!='"'&&before!='(')continue;
                if(!path_start(&p->term,i,c,length))continue;
                unsigned int end=c;
                while(end<length&&end-c<255){int ch=pt_cell_at(&p->term,i,end).ch;if(ch<=' '||ch=='\''||ch=='"'||ch>126)break;end++;}
                while(end>c+1){int ch=pt_cell_at(&p->term,i,end-1).ch;if(ch=='.'||ch==','||ch==':'||ch==';'||ch==')')end--;else break;}
                struct hint *hint=&hints.hints[hints.count++];
                hint->line=i;hint->column=c;hint->length=end-c;
                for(unsigned int k=0;k<hint->length;k++)hint->text[k]=(char)pt_cell_at(&p->term,i,c+k).ch;
                hint->text[hint->length]=0;c=end;
            }
        }
        row+=n;
    }
    hints.open=hints.count>0;ui.dirty=1;
}
static void use_hint(int index,int copy){
    struct pane *p=pane_now();struct hint *h=&hints.hints[index];
    if(copy)zenith_terminal_copy(p->session,h->text,h->length);
    else{
        for(unsigned int k=0;k<h->length;k++){struct zenith_terminal_key key={h->text[k],0,0};zenith_terminal_key(p->session,&key);}
        struct zenith_terminal_key space={' ',0,0};zenith_terminal_key(p->session,&space);
        p->following=1;
    }
    hints.open=0;ui.dirty=1;
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
    if(control_shift&&code=='f'){toggle_find();return 1;}
    if(control_shift&&code=='h'){open_hints();return 1;}
    if(control_shift&&e->extra==7){t->active=(t->active+t->count-1)%t->count;find.stale=1;ui.dirty=1;return 1;}
    if(control_shift&&e->extra==8){t->active=(t->active+1)%t->count;find.stale=1;ui.dirty=1;return 1;}
    if(e->extra==4&&(e->modifiers&~1)==2){set_tab((current+(e->modifiers&1?tab_count-1:1))%tab_count);return 1;}
    return 0;
}
static void control(int id,int *done){
    struct tab *t=tab_now();
    if(id>=ID_TALL&&id<=ID_ROWS){t->layout=id-ID_TALL;ui.dirty=1;}
    else if(id==ID_FIND||id==ID_CLOSE_FIND)toggle_find();
    else if(id==ID_CASE){find.case_sensitive=!find.case_sensitive;find.stale=1;ui.dirty=1;}
    else if(id==ID_PROMPTS){find.prompts_only=!find.prompts_only;find.stale=1;ui.dirty=1;}
    else if(id==ID_PREVIOUS)step_match(-1);
    else if(id==ID_NEXT)step_match(1);
    (void)done;
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
    if(e->type!=Z_GUI_KEY){
        int action=e->y<STRIP?zui_tabs_event(&strip,e):0;
        if(action==ZUI_TABS_ADD){open_tab();return;}
        if(action>0){set_tab(action-1);return;}
        if(action<0){int i=-action-1;close_tab(i);if(!close_pane(i,0))*done=1;return;}
        int id=zui_event(&ui,e);ui.focus=-1;
        if(id){control(id,done);return;}
        if(e->y<STRIP||e->y>=ui.height-STATUS)return;
        if(find.open&&e->y>=p->rect.y+p->rect.h-FIND&&pane_at(e->x,e->y,0)==p)return;
    }
    if(e->type==Z_GUI_DOWN){
        int which=0;struct pane *under=pane_at(e->x,e->y,&which);
        if(!under)return;
        if(which!=tab_now()->active){tab_now()->active=which;find.stale=1;hints.open=0;ui.dirty=1;}
        p=under;
    }
    if(e->type!=Z_GUI_KEY&&zui_scroll_event(&ui,&p->view,e)){p->following=p->view.offset==zui_scroll_maximum(&p->view);return;}
    if(p->view.dragging){if(e->type==Z_GUI_UP)p->view.dragging=0;return;}
    if(e->type==Z_GUI_DOWN){
        position(p,e->x,e->y,&p->anchor_row,&p->anchor_column);p->head_row=p->anchor_row;p->head_column=p->anchor_column;
        p->selecting=1;p->has_selection=0;ui.dirty=1;return;
    }
    if(e->type==Z_GUI_DRAG&&p->selecting){position(p,e->x,e->y,&p->head_row,&p->head_column);p->has_selection=p->head_row!=p->anchor_row||p->head_column!=p->anchor_column;ui.dirty=1;return;}
    if(e->type==Z_GUI_UP){p->selecting=0;return;}
    if(e->type!=Z_GUI_KEY)return;
    int raw=(p->state[1]&Z_TERMINAL_RAW)!=0;
    if(hints.open){
        int letter=e->code>='a'&&e->code<='z'?e->code-'a':e->code>='A'&&e->code<='Z'?e->code-'A':-1;
        if(letter>=0&&letter<hints.count&&!(e->modifiers&6)){use_hint(letter,(e->modifiers&1)||(e->code>='A'&&e->code<='Z'));return;}
        hints.open=0;ui.dirty=1;
        if(e->extra==1)return;
    }
    if(!raw&&shortcut(e,done))return;
    if(find.open){
        if(e->extra==1){toggle_find();return;}
        if(e->extra==2){step_match(e->modifiers&1?-1:1);return;}
        if(zui_input_event(&find.input,e)){find.stale=1;ui.dirty=1;}
        return;
    }
    if(!raw&&!e->modifiers&&e->extra==ZUI_KEY_PAGE_UP){page(p,-1);return;}
    if(!raw&&!e->modifiers&&e->extra==ZUI_KEY_PAGE_DOWN){page(p,1);return;}
    /* The command a person submits names the window while it runs. */
    if(e->extra==2&&!raw&&!(p->state[1]&Z_TERMINAL_SECRET)&&!(p->state[1]&Z_TERMINAL_BUSY)){
        unsigned int n=p->state[4]<sizeof(p->command)-1?p->state[4]:(unsigned)sizeof(p->command)-1;
        const char *s=p->text+p->state[3];unsigned int k=0;
        for(unsigned int i=0;i<n;i++){unsigned char b=(unsigned char)s[i];if(b>=' '&&b<0x7f)p->command[k++]=(char)b;}
        p->command[k]=0;
    }
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
/* The window says what runs where, as the concept does: "pkg update — /root — Polaris". */
static void update_title(void){
    struct pane *p=pane_now();char next[200];int n=0;
    if((p->state[1]&Z_TERMINAL_BUSY)&&p->command[0]){n=append(next,n,200,p->command);n=append(next,n,200," \xe2\x80\x94 ");}
    n=append(next,n,200,tab_now()->label);n=append(next,n,200," \xe2\x80\x94 Polaris");
    int same=1;for(int i=0;i<=n;i++)if(next[i]!=title[i]){same=0;break;}
    if(same)return;
    for(int i=0;i<=n;i++)title[i]=next[i];
    zenith_gui_set_title(handle,title,(unsigned long)n);
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
        if((p->state[1]&Z_TERMINAL_BUSY)&&!(p->last_flags&Z_TERMINAL_BUSY))p->running_since=now_ms;
        p->last_flags=p->state[1];p->last_cursor=p->state[2];p->last_lengths=lengths;change=1;
    }
    if(p->state[1]&Z_TERMINAL_EXIT)return -1;
    return change;
}
static int open_window(void){
    struct zenith_material_caps caps={0};struct zenith_material_settings settings={0};
    int materials=!zenith_material_settings_read(&settings)&&!zenith_gui_material_caps(&caps)&&caps.version==2&&(caps.features&3)==3;
    handle=materials?zenith_gui_open_material(640,480,"Polaris",7):zenith_gui_open(640,480,"Polaris",7);
    if(handle<0&&materials){materials=0;handle=zenith_gui_open(640,480,"Polaris",7);}
    ui.foreground=handle>=0&&materials;
    return handle>=0;
}
int main(int argc,char **argv){
    if(argc==2){const char *a=argv[1];if(a[0]=='-'&&a[1]=='-'&&a[2]=='v'){print("polaris " SUNBEAM_VERSION "\n");return 0;}}
    zui_icons_use_packages();
    if(!zui_init(&ui,pixels,640,480))return 1;
    if(!pt_init(&edit,edit_cells,8192,edit_lines,8))return 1;
    if(!zui_input_init(&find.input,find.query,sizeof(find.query)))return 1;
    zui_apply_appearance(&ui);
    if(!open_window()){print("polaris: graphical session required\n");return 1;}
    int result=0;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){print("polaris: interactive client support required\n");result=1;goto end;}
    ui.hover_supported=zenith_gui_pointer_motion(handle)==0;
    if(!open_tab()){print("polaris: this kernel has no terminal sessions\n");result=1;goto end;}
    long clock=zui_motion_clock();if(clock>=0){ui.now=clock;now_ms=clock;}
    zui_apply_motion(&ui,clock);ui.dirty=1;
    print("POLARIS_READY\n");
    long last_blink=0;int done=0;
    for(unsigned int i=0;!done;i++){
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);
        if(r<0){result=1;break;}
        if(clock>=0)now_ms=zui_motion_clock();
        if(r>0)handle_event(&e,&done);
        if(done==2){result=1;break;}
        if(done)break;
        /* Sessions in view are read every pass and others now and then: reads count against a budget. */
        for(int t=tab_count-1;t>=0&&!done;t--){
            if(t!=current&&(i%HIDDEN_POLL)!=0)continue;
            for(int k=tabs[t].count-1;k>=0;k--){
                struct pane *p=&panes[tabs[t].panes[k]];
                int output=0,change=read_pane(p,&output);
                if(change<0){if(!close_pane(t,k))done=1;break;}
                if(t==current&&change)ui.dirty=1;
                if(t==current&&output&&p==pane_now())find.stale=1;
                if(t!=current&&output&&!tabs[t].news){tabs[t].news=1;ui.dirty=1;}
            }
        }
        if(done)break;
        if(find.open&&find.stale){search();ui.dirty=1;}
        if((i&31)==0){for(int t=0;t<tab_count;t++)update_label(&tabs[t]);update_title();ui.dirty|=(i&63)==0;}
        if((i&63)==0){zui_apply_appearance(&ui);is_light=ui.theme.surface>0x808080;if((i&255)==0)zui_apply_motion(&ui,clock);}
        if((pane_now()->state[1]&Z_TERMINAL_BUSY)&&(i&31)==0)ui.dirty=1;   /* the running time */
        if(clock>=0&&(ui.dirty||ui.animating||(i&15)==0)){
            if(now_ms/500!=last_blink){last_blink=now_ms/500;int on=last_blink%2==0;if(on!=caret_on){caret_on=on;ui.dirty=1;}}
            if(ui.dirty||ui.animating){int moving=ui.animating;zui_motion_frame(&ui,now_ms);if(moving)ui.dirty=1;}
        }
        if(ui.dirty){draw();if(present()<0){result=1;break;}}
    }
end:
    for(int i=0;i<MAX_PANES;i++)if(panes[i].used)zenith_terminal_close(panes[i].session);
    zenith_gui_close(handle);return result;
}
