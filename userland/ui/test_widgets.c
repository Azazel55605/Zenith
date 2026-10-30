/* Host-only widget contract tests: the same code linked into native clients. */
#include <assert.h>
#include <limits.h>
#include <stdio.h>
#include "widgets.h"
static unsigned int px[800*600];
static struct zenith_gui_event ev(int type,int code,int x,int y,int extra){return (struct zenith_gui_event){type,code,x,y,0,extra};}
static void scroll_tests(void){
    struct zui u;assert(zui_init(&u,px,800,600));u.window_focused=1;
    struct zui_scroll s={0};struct zui_rect view={100,100,300,200};
    assert(zui_scroll_begin(&u,&s,view)==100);zui_scroll_end(&u,&s,1000);
    assert(zui_scroll_maximum(&s)==800);
    struct zenith_gui_event w=ev(Z_GUI_SCROLL,-120,150,150,0);
    assert(zui_scroll_event(&u,&s,&w)&&s.offset==60);
    w.code=INT_MIN;assert(zui_scroll_event(&u,&s,&w)&&s.offset==180);   /* clamped to 120 px */
    w.code=120;w.x=50;assert(!zui_scroll_event(&u,&s,&w));               /* outside rect */
    for(int i=0;i<100;i++){w=ev(Z_GUI_SCROLL,-120,150,150,0);zui_scroll_event(&u,&s,&w);}
    assert(s.offset==800);
    zui_scroll_reveal(&s,10,20);assert(s.offset==10);
    zui_scroll_reveal(&s,500,50);assert(s.offset==350);
    zui_scroll_begin(&u,&s,view);zui_scroll_end(&u,&s,100);assert(s.offset==0&&zui_scroll_maximum(&s)==0);
    /* No scrollbar pixels when content fits. */
    zui_fill(&u,(struct zui_rect){0,0,800,600},0x123456);
    zui_scroll_begin(&u,&s,view);zui_scroll_end(&u,&s,100);
    for(int y=100;y<300;y++)assert(px[y*800+397]==0x123456);
    /* Thumb drag moves proportionally; the clip is restored after end. */
    s.offset=0;zui_scroll_begin(&u,&s,view);assert(u.clip.x==100&&u.clip.h==200);
    zui_scroll_end(&u,&s,1000);assert(u.clip.x==0&&u.clip.w==800);
    struct zenith_gui_event d=ev(Z_GUI_DOWN,1,396,110,0),m=ev(Z_GUI_DRAG,1,396,150,0),up=ev(Z_GUI_UP,1,396,150,0);
    assert(!zui_scroll_event(&u,&s,&d)&&s.dragging);assert(zui_scroll_event(&u,&s,&m));
    assert(s.offset==40*800/(200-40));zui_scroll_event(&u,&s,&up);assert(!s.dragging);
    /* Track click pages by one viewport. */
    s.offset=0;struct zenith_gui_event t=ev(Z_GUI_DOWN,1,396,280,0);
    assert(zui_scroll_event(&u,&s,&t)&&s.offset==200);
}
static const char *names[]={"Alpha","Bravo","Charlie"};
static int rows(void *c,int i,struct zui_list_item *o){
    int n=*(int*)c;if(i<0||i>=n)return 0;
    *o=(struct zui_list_item){i%2?"chevron-right":"zap",names[i%3],0,1,i!=1};return 1;
}
static void list_tests(void){
    struct zui u;assert(zui_init(&u,px,800,600));u.window_focused=1;
    struct zui_list l={0};l.base_id=1000;int n=0;
    zui_list_draw(&u,&l,(struct zui_rect){0,0,300,400},n,rows,&n);assert(u.count==0);
    n=10000;u.count=0;zui_list_draw(&u,&l,(struct zui_rect){0,0,300,400},n,rows,&n);
    assert(u.count==10&&l.scroll.content==400000);                    /* only visible rows */
    struct zenith_gui_event k={Z_GUI_KEY,0,0,0,0,11};                  /* End */
    zui_list_event(&u,&l,&k,rows,&n);assert(l.selected==9999&&l.scroll.offset==400000-400);
    k.extra=10;zui_list_event(&u,&l,&k,rows,&n);assert(l.selected==0&&l.scroll.offset==0);
    k.extra=8;zui_list_event(&u,&l,&k,rows,&n);assert(l.selected==2);  /* Down skips disabled row 1 */
    k=(struct zenith_gui_event){Z_GUI_KEY,'b',0,0,0,0};zui_list_event(&u,&l,&k,rows,&n);
    assert(l.selected==4);                                             /* next enabled "Bravo" */
    k=(struct zenith_gui_event){Z_GUI_KEY,0,0,0,0,2};assert(zui_list_event(&u,&l,&k,rows,&n)==5);
    u.count=0;l.scroll.offset=0;zui_list_draw(&u,&l,(struct zui_rect){0,0,300,400},n,rows,&n);
    struct zenith_gui_event d={Z_GUI_DOWN,1,50,85,0,0},up={Z_GUI_UP,1,50,85,0,0};
    zui_event(&u,&d);assert(zui_list_event(&u,&l,&up,rows,&n)==3);    /* third row clicked */
    struct zenith_gui_event dd={Z_GUI_DOWN,1,50,45,0,0},uu={Z_GUI_UP,1,50,45,0,0};
    zui_event(&u,&dd);assert(zui_list_event(&u,&l,&uu,rows,&n)==0);   /* disabled row ignored */
}
static void combo_tests(void){
    struct zui u;assert(zui_init(&u,px,800,600));u.window_focused=1;int n=20;
    struct zui_combo c={0};c.id=50;c.popup.base_id=2000;c.value=3;
    zui_combo_draw(&u,&c,(struct zui_rect){100,560,200,36},n,rows,&n);
    struct zenith_gui_event d={Z_GUI_DOWN,1,150,570,0,0},up={Z_GUI_UP,1,150,570,0,0};
    zui_event(&u,&d);assert(zui_combo_event(&u,&c,&up,rows,&n)==0&&c.open);
    assert(c.popup.selected==3);
    u.count=0;zui_combo_draw(&u,&c,(struct zui_rect){100,560,200,36},n,rows,&n);
    assert(c.panel.y+c.panel.h<=560&&c.panel.y>=0&&c.panel.h==328);   /* opens upward */
    struct zenith_gui_event k={Z_GUI_KEY,0,0,0,0,8};zui_combo_event(&u,&c,&k,rows,&n);
    k.extra=2;assert(zui_combo_event(&u,&c,&k,rows,&n)==5&&!c.open&&c.value==4);
    u.count=0;zui_combo_draw(&u,&c,(struct zui_rect){100,560,200,36},n,rows,&n);
    zui_event(&u,&d);zui_combo_event(&u,&c,&up,rows,&n);assert(c.open);
    k.extra=1;assert(zui_combo_event(&u,&c,&k,rows,&n)==0&&!c.open&&c.value==4); /* Esc keeps value */
    /* Opens downward with room below; a click outside the panel cancels. */
    c=(struct zui_combo){0};c.id=51;c.popup.base_id=3000;u.count=0;
    zui_combo_draw(&u,&c,(struct zui_rect){100,40,200,36},n,rows,&n);
    struct zenith_gui_event d2={Z_GUI_DOWN,1,150,50,0,0},u2={Z_GUI_UP,1,150,50,0,0};
    zui_event(&u,&d2);zui_combo_event(&u,&c,&u2,rows,&n);assert(c.open);
    u.count=0;zui_combo_draw(&u,&c,(struct zui_rect){100,40,200,36},n,rows,&n);assert(c.panel.y==80);
    struct zenith_gui_event out={Z_GUI_UP,1,700,500,0,0};
    assert(zui_combo_event(&u,&c,&out,rows,&n)==0&&!c.open&&c.value==0);
    c.value=15;zui_event(&u,&d2);zui_combo_event(&u,&c,&u2,rows,&n);
    u.count=0;zui_combo_draw(&u,&c,(struct zui_rect){100,40,200,36},n,rows,&n);
    assert(15*40>=c.popup.scroll.offset&&15*40+40<=c.popup.scroll.offset+c.panel.h-8); /* selection visible */
}
static void form_tests(void){
    struct zui u;assert(zui_init(&u,px,800,600));struct zui_form f;
    zui_form_begin(&f,(struct zui_rect){0,0,1000,600},0,1000);
    assert(f.w==640&&f.x==180&&!f.narrow);
    struct zui_rect r=zui_form_row(&u,&f,"Mode",0,200,36);
    assert(r.x==f.x+f.label_w+16&&r.y==0&&r.w==200);
    struct zui_rect r2=zui_form_row(&u,&f,"Accent","Help text",200,36);assert(r2.y>r.y+36);
    int section=zui_form_section(&u,&f,"Effects");assert(section>r2.y+36);
    zui_form_begin(&f,(struct zui_rect){0,0,600,600},0,600);assert(f.narrow&&f.w==600);
    r=zui_form_row(&u,&f,"Mode",0,200,36);assert(r.x==f.x&&r.y==24);   /* label stacked above */
    r=zui_form_row(&u,&f,"Wide",0,2000,36);assert(r.w==f.w);            /* control clamped */
    assert(zui_form_height(&f,0)>r.y+36);
    /* Scrolled origin: rows follow the content top. */
    zui_form_begin(&f,(struct zui_rect){0,100,1000,400},-300,1000);
    r=zui_form_row(&u,&f,"Mode",0,200,36);assert(r.y==-300&&zui_form_height(&f,-300)==60);
}
int main(void){
    scroll_tests();
    list_tests();
    combo_tests();
    form_tests();
    puts("PASS widget scroll area, list view, combo box and form layout");
    return 0;
}
