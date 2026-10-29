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
int main(void){
    scroll_tests();
    list_tests();
    puts("PASS widget scroll area and list view");
    return 0;
}
