/* Test the real screen and input callbacks without a running Zenith desktop.
 * The Web engine is replaced only at the navigation/action boundary here.
 * tools/netsurf-smoke.py exercises the complete engine in the guest. */
#define main native_browser_main
#include "../../../userland/netsurf/frontend.c"
#undef main
#include "netsurf/content.h"
#include "netsurf/layout.h"
#include <assert.h>

static char destination[2200];
static struct { unsigned nav, back, forward, reload, press, click; int x, y; } observed;

nserror nsurl_create(const char *s, nsurl **out)
{
    copy_text(destination, sizeof(destination), s);
    *out = (nsurl *)destination;
    return NSERROR_OK;
}
nserror netsurf_path_to_nsurl(const char *s, nsurl **out) { return nsurl_create(s, out); }
void nsurl_unref(nsurl *url) { assert(url == (nsurl *)destination); }
const char *messages_get_errorcode(nserror error) { (void)error; return "Navigation failed"; }
nserror browser_window_navigate(struct browser_window *bw, nsurl *url, nsurl *ref,
        enum browser_window_nav_flags flags, char *post, struct fetch_multipart_data *form, struct hlcache_handle *parent)
{
    assert(bw == window.browser && url == (nsurl *)destination && !ref && flags == BW_NAVIGATE_HISTORY);
    assert(!post && !form && !parent);
    if (!strncmp(destination,"mailto:",7)) return launch_external(url);
    observed.nav++; return NSERROR_OK;
}
bool browser_window_history_back_available(struct browser_window *bw) { return bw == window.browser; }
bool browser_window_history_forward_available(struct browser_window *bw) { return bw == window.browser; }
nserror browser_window_history_back(struct browser_window *bw, bool new_window)
{ assert(bw == window.browser && !new_window); observed.back++; return NSERROR_OK; }
nserror browser_window_history_forward(struct browser_window *bw, bool new_window)
{ assert(bw == window.browser && !new_window); observed.forward++; return NSERROR_OK; }
nserror browser_window_reload(struct browser_window *bw, bool all)
{ assert(bw == window.browser && !all); observed.reload++; return NSERROR_OK; }
void browser_window_stop(struct browser_window *bw) { (void)bw; }
bool browser_window_key_press(struct browser_window *bw, uint32_t k) { (void)bw; (void)k; return false; }
bool browser_window_scroll_at_point(struct browser_window *bw, int x, int y, int dx, int dy)
{ assert(bw == window.browser); (void)x; (void)y; (void)dx; (void)dy; return false; }
void browser_window_mouse_click(struct browser_window *bw,browser_mouse_state state,int x,int y)
{
    assert(bw == window.browser);
    if (state == BROWSER_MOUSE_PRESS_1) observed.press++;
    if (state == BROWSER_MOUSE_CLICK_1) observed.click++;
    observed.x = x; observed.y = y;
}
void browser_window_mouse_track(struct browser_window *bw,browser_mouse_state state,int x,int y)
{ (void)bw; (void)state; (void)x; (void)y; }
/* Nebula's chrome: find, zoom, the connection budget and the clock. */
static struct { unsigned searches, clears; char text[128]; bool backwards; float scale; unsigned refused; } chrome;
void browser_window_search(struct browser_window *bw, void *context, search_flags_t flags, const char *string)
{
    assert(bw == window.browser && context == &window && (flags & SEARCH_FLAG_SHOWALL));
    chrome.searches++; chrome.backwards = (flags & SEARCH_FLAG_BACKWARDS) != 0; copy_text(chrome.text, sizeof(chrome.text), string);
}
void browser_window_search_clear(struct browser_window *bw) { assert(bw == window.browser); chrome.clears++; }
nserror browser_window_set_scale(struct browser_window *bw, float scale, bool absolute)
{ assert(bw == window.browser && absolute); chrome.scale = scale; return NSERROR_OK; }
unsigned zenith_web_refused(void) { return chrome.refused; }
long zui_motion_clock(void) { return 1000; }

bool content_scaled_redraw(struct hlcache_handle *h,int width,int height,const struct redraw_context *ctx)
{ (void)h; (void)width; (void)height; (void)ctx; return false; }

static void pressed(int code, int extra, int modifiers)
{ key(&window, &(struct zenith_gui_event){.type=Z_GUI_KEY,.code=code,.extra=extra,.modifiers=modifiers}); }

static void test_input(void)
{
    struct gui_window *g=&window;
    zui_init(&g->ui,starting_pixels,START_W,START_H); g->ui.window_focused=1;
    g->browser=(struct browser_window *)g;
    copy_text(g->current,sizeof(g->current),"file:///welcome.html");
    reset_address(g,g->current);
    pressed('l',0,2); assert(g->address_focus && g->select_address);
    pressed('x',0,0); pressed(0xe9,0,0);
    assert(!strcmp(g->location,"x\xc3\xa9") && g->address.cursor==3);
    pressed(0,3,0); assert(!strcmp(g->location,"x") && g->address.cursor==1);
    pressed(0,2,0); assert(observed.nav==1 && !strcmp(destination,"https://x") && !g->address_focus);
    pressed('l',0,2); pressed('w',0,0); pressed(0,1,0);
    assert(!g->address_focus && !strcmp(g->current,g->location) && observed.nav==1);
    pressed('l',0,2); reset_address(g,"mailto:postmaster@localhost"); pressed(0,2,0);
    assert(g->address_focus && strstr(g->status,"Unsupported address") && observed.nav==1);
    reset_address(g,"localhost:8443/next"); pressed(0,2,0);
    assert(!g->address_focus && observed.nav==2 && !strcmp(destination,"https://localhost:8443/next"));
    pressed(0,5,4); pressed(0,6,4); pressed('r',0,2);
    assert(observed.back==1 && observed.forward==1 && observed.reload==1);

    g->ey=2000;
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_SCROLL,.code=-1,.x=30,.y=TOOLBAR+9});
    assert(g->sy==40 && g->page_dirty);
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DOWN,.code=1,.x=30,.y=TOOLBAR+9});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DRAG,.code=1,.x=31,.y=TOOLBAR+9});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_MOVE,.x=-1,.y=-1});
    assert(g->page_pressed && !g->dragging);
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_UP,.code=1,.x=30,.y=TOOLBAR+9});
    assert(observed.press==1 && observed.click==1 && observed.x==30 && observed.y==49);
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DOWN,.code=1,.x=30,.y=TOOLBAR+9});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_UP,.code=1,.x=30,.y=TOOLBAR-2});
    assert(observed.press==2 && observed.click==1 && !g->page_pressed);
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DOWN,.code=1,.x=30,.y=TOOLBAR+9});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DRAG,.code=1,.x=45,.y=TOOLBAR+9});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_UP,.code=1,.x=45,.y=TOOLBAR+9});
    assert(observed.press==3 && observed.click==1 && !g->page_pressed);

    /* Find on page: Ctrl+F opens the bar, typing searches as you go, Shift+Enter goes back, Esc clears. */
    pressed('f',0,2); assert(g->find_open && g->find_focus && !g->address_focus);
    pressed('c',0,0); pressed('o',0,0); assert(chrome.searches==2 && !strcmp(chrome.text,"co") && !chrome.backwards);
    pressed(0,2,1); assert(chrome.searches==3 && chrome.backwards);
    search_status(false, g); assert(g->find_missing);
    pressed(0,1,0); assert(!g->find_open && chrome.clears==1);
    /* Zoom steps from 100 %, and Ctrl+0 returns to it. */
    g->scale=1.f; pressed('=',0,2); assert(chrome.scale>1.09f && chrome.scale<1.11f);
    pressed('-',0,2); pressed('-',0,2); assert(chrome.scale>.89f && chrome.scale<.91f);
    pressed('0',0,2); assert(chrome.scale==1.f);
    /* Refused requests open the limit bar: the page moves down under it until it is closed. */
    assert(page_top(g)==TOOLBAR); chrome.refused=2; assert(page_top(g)==TOOLBAR+LIMIT_BAR);
    ui_action(g, LIMIT_CLOSE); assert(page_top(g)==TOOLBAR && g->limit_seen==2);
    /* Only a link's address or a loading message shows in the pill; "Done" does not. */
    status(g,"Done (0.2s)"); assert(!pill_status(g)); status(g,"https://example.org/"); assert(pill_status(g));
    pressed('q',0,2); assert(g->done);
    puts("PASS address editing, UTF-8 deletion, cancel, navigation, history, reload, scroll, pointer dispatch, find, zoom, limit bar and pill");
}

static void test_plot(void)
{
    enum {W=54,H=32,STRIDE=W+7};
    uint32_t storage[STRIDE*(H+2)];
    for (size_t i=0;i<sizeof(storage)/sizeof(*storage);i++) storage[i]=0x11223344;
    struct page_raster r={storage+STRIDE,W,H,STRIDE,{0,0,W,H}};
    const struct redraw_context ctx={.plot=&zenith_plotters,.priv=&r};
    struct rect clip={2,2,W-2,H-2},far={INT_MIN,INT_MIN,INT_MAX,INT_MAX};
    plot_style_t style={.fill_type=PLOT_OP_TYPE_SOLID,.fill_colour=0x332211,.stroke_type=PLOT_OP_TYPE_NONE};
    ctx.plot->clip(&ctx,&clip); ctx.plot->rectangle(&ctx,&style,&far);
    assert(r.pixels[2*STRIDE+2]==0xff112233);
    style.stroke_type=PLOT_OP_TYPE_DASH; style.stroke_colour=0x00ff00; style.stroke_width=PLOT_STYLE_SCALE;
    ctx.plot->line(&ctx,&style,&far);
    ctx.plot->disc(&ctx,&style,12,9,12);
    ctx.plot->arc(&ctx,&style,12,9,12,20,270);
    int tri[]={-4,2,20,9,3,25}; ctx.plot->polygon(&ctx,&style,tri,3);

    struct bitmap *bmp=zenith_bitmaps.create(2,1,BITMAP_CLEAR);
    assert(bmp && zenith_bitmaps.get_width(bmp)==2 && zenith_bitmaps.get_rowstride(bmp)==8);
    uint32_t *px=(uint32_t*)zenith_bitmaps.get_buffer(bmp);
    px[0]=0xffff0000; px[1]=0xff0000ff;
    ctx.plot->bitmap(&ctx,bmp,-1,-1,2,1,0,BITMAPF_REPEAT_X|BITMAPF_REPEAT_Y);
    assert(r.pixels[2*STRIDE+2]==0xff0000ff && r.pixels[2*STRIDE+3]==0xffff0000);
    px[0]=0x8000ff00; ctx.plot->bitmap(&ctx,bmp,3,2,2,1,0,BITMAPF_NONE);
    assert(r.pixels[2*STRIDE+3]==0xff7f8000);
    zenith_bitmaps.destroy(bmp);
    assert(!zenith_bitmaps.create(INT_MAX,INT_MAX,BITMAP_NONE));
    plot_font_style_t f={.size=12*PLOT_STYLE_SCALE,.weight=400};
    const char *s="A\xc3\xa9 bb";
    size_t length=strlen(s),offset;
    int width,actual,prefix;
    zenith_layout.width(&f,s,3,&prefix);
    zenith_layout.position(&f,s,length,prefix,&offset,&actual);
    assert(offset==3 && actual==prefix);
    zenith_layout.split(&f,s,length,prefix,&offset,&actual);
    assert(offset==3 && actual==prefix);
    zenith_layout.width(&f,s,length,&width);
    assert(width>prefix);
    f.size*=2;zenith_layout.width(&f,s,length,&actual);assert(actual>=width*2-5);
    ctx.plot->text(&ctx,&f,INT_MIN,INT_MAX,s,length);
    ctx.plot->text(&ctx,&f,-4,18,s,length);
    for(int y=-1;y<=H;y++) for(int x=0;x<STRIDE;x++)
        if(y<clip.y0||y>=clip.y1||x<clip.x0||x>=clip.x1) assert(storage[(y+1)*STRIDE+x]==0x11223344);
    puts("PASS clipped large-coordinate shapes, alpha blend, repeated bitmap and scaled UTF-8 text metrics");
}

int main(void) {test_input();test_plot();return 0;}
