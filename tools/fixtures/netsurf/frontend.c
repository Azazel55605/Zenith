/* Test the real screen and input callbacks without a running Zenith desktop.
 * The Web engine is replaced only at the navigation/action boundary here.
 * tools/netsurf-smoke.py exercises the complete engine in the guest. */
#define main native_browser_main
#include "../../../userland/netsurf/frontend.c"
#undef main
#include "netsurf/content.h"
#include "netsurf/layout.h"
#include <assert.h>
#include "profile-stub.h"

/* The active tab stands in for the old single window; each fake browser_window is its own tab. */
#define window (*active_tab())
static unsigned created, destroyed;
nserror browser_window_create(enum browser_window_create_flags flags, nsurl *url, nsurl *referrer,
                              struct browser_window *existing, struct browser_window **out)
{
    (void)referrer;
    struct gui_window *g = create_window((struct browser_window *)1, (struct gui_window *)existing,
                                         (flags & BW_CREATE_FOREGROUND) ? GW_CREATE_FOREGROUND : 0);
    if (!g) return NSERROR_NOMEM;
    g->browser = (struct browser_window *)g;
    copy_text(g->current, sizeof(g->current), nsurl_access(url));
    created++;
    if (out) *out = g->browser;
    return NSERROR_OK;
}
void browser_window_destroy(struct browser_window *bw) { destroyed++; destroy_window((struct gui_window *)bw); }
nserror browser_window_schedule_reformat(struct browser_window *bw) { (void)bw; return NSERROR_OK; }
void download_context_destroy(download_context *ctx) { (void)ctx; }

static char destination[2200];
static const unsigned history_ids[] = {0, 1, 2};
static const struct history_entry *chosen;
static struct { unsigned nav, back, forward, reload, press, click; int x, y; } observed;

nserror nsurl_create(const char *s, nsurl **out)
{
    copy_text(destination, sizeof(destination), s);
    *out = (nsurl *)destination;
    return NSERROR_OK;
}
nserror netsurf_path_to_nsurl(const char *s, nsurl **out) { return nsurl_create(s, out); }
void nsurl_unref(nsurl *url) { assert(url == (nsurl *)destination); }
nsurl *nsurl_ref(nsurl *url) { return url; }
const char *nsurl_access(const nsurl *url) { return (const char *)url; }
size_t nsurl_length(const nsurl *url) { return strlen(nsurl_access(url)); }
static nsurl *loaded;
const char *browser_window_get_title(struct browser_window *bw) { (void)bw; return "Fixture Page"; }
struct hlcache_handle *browser_window_get_content(struct browser_window *bw) { (void)bw; return (struct hlcache_handle *)loaded; }
nsurl *hlcache_handle_get_url(const struct hlcache_handle *h) { return (nsurl *)h; }
nserror browser_window_get_features(struct browser_window *bw, int x, int y, struct browser_window_features *features)
{
    (void)bw;
    *features = (struct browser_window_features){.link = x == 24 && y == 56 ? (nsurl *)destination : NULL};
    return NSERROR_OK;
}
void browser_window_history_enumerate_back(const struct browser_window *bw, browser_window_history_enumerate_cb cb, void *arg)
{
    for (int i = 0; i < 2; ++i)
        if (!cb(bw, 0, 0, 0, 0, (const struct history_entry *)&history_ids[i], arg)) break;
}
void browser_window_history_enumerate_forward(const struct browser_window *bw, browser_window_history_enumerate_cb cb, void *arg)
{
    cb(bw, 0, 0, 0, 0, (const struct history_entry *)&history_ids[2], arg);
}
nserror browser_window_history_go(struct browser_window *bw, struct history_entry *entry, bool new_window)
{
    assert(bw == window.browser && !new_window);
    chosen = entry;
    return NSERROR_OK;
}
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
/* Nebula's chrome: find, zoom and the clock. */
static struct { unsigned searches, clears; char text[128]; bool backwards; float scale; } chrome;
void browser_window_search(struct browser_window *bw, void *context, search_flags_t flags, const char *string)
{
    assert(bw == window.browser && context == &window && (flags & SEARCH_FLAG_SHOWALL));
    chrome.searches++; chrome.backwards = (flags & SEARCH_FLAG_BACKWARDS) != 0; copy_text(chrome.text, sizeof(chrome.text), string);
}
void browser_window_search_clear(struct browser_window *bw) { assert(bw == window.browser); chrome.clears++; }
nserror browser_window_set_scale(struct browser_window *bw, float scale, bool absolute)
{ assert(bw == window.browser && absolute); chrome.scale = scale; return NSERROR_OK; }

long zui_motion_clock(void) { return 1000; }

bool content_scaled_redraw(struct hlcache_handle *h,int width,int height,const struct redraw_context *ctx)
{ (void)h; (void)width; (void)height; (void)ctx; return false; }

static void pressed(int code, int extra, int modifiers)
{ key(&window, &(struct zenith_gui_event){.type=Z_GUI_KEY,.code=code,.extra=extra,.modifiers=modifiers}); }

static void test_input(void)
{
    zui_init(&W.ui,starting_pixels,START_W,START_H); W.ui.window_focused=1;
    nsurl *first; nsurl_create("file:///welcome.html", &first);
    assert(browser_window_create(BW_CREATE_HISTORY, first, NULL, NULL, NULL)==NSERROR_OK);
    struct gui_window *g=active_tab();
    assert(g && W.count==1 && W.active==0);
    const struct zui_rect pr = page_rect(), back = toolbar_button(0), forward = toolbar_button(1);
    const int PX = pr.x + 30, PY = pr.y + 9;
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
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_SCROLL,.code=-1,.x=PX,.y=PY});
    assert(g->sy==40 && g->page_dirty);
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DOWN,.code=1,.x=PX,.y=PY});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DRAG,.code=1,.x=PX+1,.y=PY});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_MOVE,.x=-1,.y=-1});
    assert(W.page_pressed && !W.dragging);
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_UP,.code=1,.x=PX,.y=PY});
    assert(observed.press==1 && observed.click==1 && observed.x==30 && observed.y==49);
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DOWN,.code=1,.x=PX,.y=PY});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_UP,.code=1,.x=PX,.y=pr.y-2});
    assert(observed.press==2 && observed.click==1 && !W.page_pressed);
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DOWN,.code=1,.x=PX,.y=PY});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_DRAG,.code=1,.x=PX+15,.y=PY});
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_UP,.code=1,.x=PX+15,.y=PY});
    assert(observed.press==3 && observed.click==1 && !W.page_pressed);

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
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_SECONDARY,.code=2,.x=back.x+4,.y=back.y+6});
    assert(W.popover == POPOVER_BACK && W.entry_count == 2);
    ui_action(g, HISTORY_FIRST + 1);
    assert(chosen == (const struct history_entry *)&history_ids[1] && !W.popover);
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_SECONDARY,.code=2,.x=forward.x+4,.y=forward.y+6});
    assert(W.popover == POPOVER_FORWARD && W.entry_count == 1);
    ui_action(g, HISTORY_FIRST);
    assert(chosen == (const struct history_entry *)&history_ids[2]);
    g->sy = 40;
    pointer(g, &(struct zenith_gui_event){.type=Z_GUI_SECONDARY,.code=2,.x=pr.x+24,.y=pr.y+16});
    assert(W.popover == POPOVER_LINK && W.context_link == (nsurl *)destination);
    ui_action(g, LINK_OPEN);
    assert(observed.nav == 3 && !W.popover);
    loaded = (nsurl *)"https://saved.invalid/page";
    pressed('d', 0, 2);
    assert(W.profile.bookmark_count == 1 && !strcmp(W.profile.bookmarks[0].title, "Fixture Page"));
    W.profile.recent[0] = W.profile.bookmarks[0];
    W.profile.recent_count = 1;
    pressed('l', 0, 2);
    assert(W.popover == POPOVER_SUGGEST && W.suggestion_count == 1);
    pressed('f', 0, 0);
    assert(W.suggestion_count == 1 && W.selection == -1);
    pressed(0, 8, 0);
    assert(W.selection == 0);
    pressed(0, 2, 0);
    assert(!g->address_focus && !W.popover && !strcmp(destination, (const char *)loaded));
    pressed('l', 0, 2); pressed(0, 1, 0);
    assert(!g->address_focus && !W.popover);
    /* Only a link's address or a loading message shows in the pill; "Done" does not. */
    status(g,"Done (0.2s)"); assert(!pill_status(g)); status(g,"https://example.org/"); assert(pill_status(g));

    /* Tabs: Ctrl+T opens the start page after the active tab with the address ready; Ctrl+Tab and Ctrl+1 switch;
     * each tab keeps its own scroll; Ctrl+W closes and Ctrl+Shift+T reopens; the last tab closes the window. */
    struct gui_window *first_tab = g;
    g->sy = 40;
    pressed('t',0,2);
    assert(W.count==2 && W.active==1 && active_tab()!=first_tab && !strcmp(active_tab()->current, NEBULA_START_URL));
    assert(active_tab()->address_focus && active_tab()->sy==0);
    pressed(0,4,2); assert(W.active==0 && active_tab()==first_tab && first_tab->sy==40);
    pressed('2',0,2); assert(W.active==1);
    pressed('1',0,2); assert(W.active==0);
    copy_text(first_tab->current, sizeof(first_tab->current), "https://closed.invalid/");
    pressed('w',0,2); assert(W.count==1 && W.closed_count==1 && !strcmp(W.closed[0], "https://closed.invalid/"));
    pressed('t',0,3); assert(W.count==2 && W.closed_count==0 && !strcmp(active_tab()->current, "https://closed.invalid/"));
    /* Reordering keeps the active tab active. */
    struct gui_window *moved = active_tab();
    move_tab(W.active, 0); assert(W.tabs[0]==moved && active_tab()==moved);
    /* Past LIVE_TABS live tabs, the least recently used sleeps; selecting it wakes it at its address. */
    for (int i = W.count; i < LIVE_TABS + 1; i++) pressed('t',0,2);
    assert(W.count==LIVE_TABS+1);
    int sleeping = 0; for (int i = 0; i < W.count; i++) sleeping += W.tabs[i]->sleeping;
    assert(sleeping==1 && destroyed==2);
    int asleep = 0; while (!W.tabs[asleep]->sleeping) asleep++;
    activate(asleep); assert(!W.tabs[asleep]->sleeping && W.tabs[asleep]->browser);
    /* Sixteen tabs at most. */
    while (W.count < TABS_MAX) pressed('t',0,2);
    pressed('t',0,2); assert(W.count==TABS_MAX);
    /* The sidebar folds to a rail in this 640 px window and hides with Ctrl+Shift+S; the inset follows. */
    assert(sidebar_width()==RAIL_W && content_rect().x==RAIL_W+24);
    pressed('s',0,3); assert(sidebar_width()==0 && content_rect().x==12);
    pressed('s',0,3); assert(sidebar_width()==RAIL_W);
    while (W.count > 1) pressed('w',0,2);
    pressed('w',0,2); assert(W.done && W.count==0);
    puts("PASS address editing, UTF-8 deletion, cancel, navigation, history, reload, scroll, pointer dispatch, find, zoom, pill and tabs");
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
