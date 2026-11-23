/* SPDX-License-Identifier: GPL-2.0-only */
#include "launcher.h"
#include "../include/zenith_files.h"
#include "../ui/widgets.h"

enum { MAX_ENTRIES = 256, HOME_BUTTON = 1, UP_BUTTON, REFRESH_BUTTON, PLAY_BUTTON, PLACE_ID = 8, ROW_ID = 32 };
struct entry { char name[256]; int folder; unsigned long size; char detail[64]; };
/* The places in the sidebar: the account home, its doom and Downloads folders when they exist, and the whole disk. */
enum { PLACE_HOME, PLACE_DOOM, PLACE_DOWNLOADS, PLACE_ROOT, PLACES };
static const char *const place_names[PLACES] = {"Home", "Doom", "Downloads", "Computer"};
static const char *const place_icons[PLACES] = {"house", "folder", "download", "hard-drive"};
static char places[PLACES][DOOM_PATH_BYTES];
static int place_exists[PLACES];
static struct entry entries[MAX_ENTRIES];
static _Alignas(16) unsigned char page[Z_FILES_PAGE];
static struct zui_list list = {.selected = -1, .base_id = ROW_ID, .row_height = 46};
static char home[DOOM_PATH_BYTES], folder[DOOM_PATH_BYTES], preference[DOOM_PATH_BYTES];
static int count, limited;
static const char *message;

static unsigned long length(const char *s)
{
    unsigned long n = 0;
    while (s[n]) n++;
    return n;
}
static void copy(char *out, const char *in, unsigned long n)
{
    for (unsigned long i = 0; i < n; i++) out[i] = in[i];
    out[n] = 0;
}

static int join(char *out, const char *parent, const char *name)
{
    unsigned long p = length(parent), n = length(name);
    int slash = p && parent[p-1] != '/';
    if (p + (unsigned long)slash + n >= DOOM_PATH_BYTES) return 0;
    copy(out,parent,p);
    if (slash) out[p++] = '/';
    copy(out+p,name,n);
    return 1;
}

static int lower(int c) { return c >= 'A' && c <= 'Z' ? c + 'a' - 'A' : c; }

static int wad_extension(const char *name, unsigned int n)
{
    return n > 4 && name[n-4] == '.' && lower(name[n-3]) == 'w'
        && lower(name[n-2]) == 'a' && lower(name[n-1]) == 'd';
}

static int before(const struct entry *a, const struct entry *b)
{
    if (a->folder != b->folder) return a->folder;
    const unsigned char *x = (const unsigned char *)a->name, *y = (const unsigned char *)b->name;
    while (*x && lower(*x) == lower(*y)) { x++; y++; }
    return lower(*x) < lower(*y);
}

/* The game a well-known WAD holds, matched by its file name (any case). */
static const char *game_of(const char *name)
{
    static const char *const known[][2] = {
        {"doom.wad","The Ultimate Doom"},{"doom1.wad","Doom (shareware)"},{"doom2.wad","Doom II: Hell on Earth"},
        {"plutonia.wad","Final Doom: The Plutonia Experiment"},{"tnt.wad","Final Doom: TNT: Evilution"},
        {"freedoom1.wad","Freedoom: Phase 1"},{"freedoom2.wad","Freedoom: Phase 2"},{"freedm.wad","FreeDM"},
        {"doombfg.wad","Doom (BFG Edition)"},{"doom2bfg.wad","Doom II (BFG Edition)"},
    };
    for (unsigned int i = 0; i < sizeof(known) / sizeof(known[0]); i++) {
        const char *a = name, *b = known[i][0];
        while (*a && lower(*a) == *b) { a++; b++; }
        if (!*a && !*b) return known[i][1];
    }
    return 0;
}
static char *put(char *o, const char *s) { while (*s) *o++ = *s++; *o = 0; return o; }
static char *number(char *o, unsigned long v)
{
    char d[24]; int n = 0;
    do { d[n++] = (char)('0' + v % 10); v /= 10; } while (v);
    while (n) *o++ = d[--n];
    *o = 0; return o;
}
/* "12.4 MB" or "640 KB". */
static char *size_text(char *o, unsigned long bytes)
{
    if (bytes >= 1024UL * 1024) { unsigned long tenths = bytes * 10 / (1024UL * 1024); o = number(o,tenths / 10); o = put(o,"."); o = number(o,tenths % 10); return put(o," MB"); }
    o = number(o,(bytes + 1023) / 1024); return put(o," KB");
}
static void describe(struct entry *e)
{
    char *o = e->detail; *o = 0;
    if (e->folder) { put(o,"Folder"); return; }
    const char *game = game_of(e->name);
    if (game) { unsigned long n = length(game); if (n > 48) n = 48; copy(o,game,n); o += n; o = put(o," · "); }
    size_text(o,e->size);
}

static long read_entries(const char *path)
{
    struct zenith_files_list request = {.version = 1, .path_bytes = (unsigned int)length(path)};
    if (!request.path_bytes || request.path_bytes > sizeof(request.path)) return -36;
    for (unsigned int i = 0; i < request.path_bytes; i++) request.path[i] = path[i];
    long r = zenith_files_list(&request,page);
    if (r < 0) return r;

    count = limited = 0;
    for (;;) {
        const struct zenith_files_page *header = (const void *)page;
        unsigned int at = sizeof(*header);
        for (unsigned int i = 0; i < header->count && at + sizeof(struct zenith_file_entry) <= sizeof(page); i++) {
            const struct zenith_file_entry *f = (const void *)(page + at);
            if (f->record_bytes < sizeof(*f) || f->record_bytes > sizeof(page)-at) return -5;
            at += f->record_bytes;
            if (!f->name_bytes || f->name_bytes >= sizeof(entries[0].name)
                || f->name_bytes > f->record_bytes - sizeof(*f)) continue;
            unsigned int kind = (f->target_mode ? f->target_mode : f->mode) & 0170000;
            int directory = kind == 0040000;
            if ((directory && f->name[0] == '.' && (f->name_bytes == 1 || (f->name_bytes == 2 && f->name[1]=='.')))
                || (!directory && (kind != 0100000 || !wad_extension(f->name,f->name_bytes)))) continue;
            if (count == MAX_ENTRIES) { limited = 1; break; }
            struct entry value = {.folder = directory, .size = f->size};
            copy(value.name,f->name,f->name_bytes);
            describe(&value);
            int pos = count++;
            while (pos && before(&value,&entries[pos-1])) { entries[pos] = entries[pos-1]; pos--; }
            entries[pos] = value;
        }
        if (limited || header->next <= request.offset || header->next >= header->total) break;
        request.offset = header->next;
        r = zenith_files_list(&request,page);
        if (r < 0) { limited = 1; break; }
    }
    return 0;
}

static int browse(struct zui *ui, const char *path)
{
    long r = read_entries(path);
    if (r < 0) {
        message = r == -13 ? "Permission denied for this folder." : "Could not open this folder.";
        ui->dirty = 1;
        return 0;
    }
    if (path != folder) copy(folder,path,length(path));
    list.selected = -1;
    ui->focus = ui->pressed = -1;
    zui_scroll_jump(&list.scroll,0);
    message = limited ? "Only the first 256 matching entries are shown." : 0;
    ui->dirty = 1;
    return 1;
}

static void initialize_folder(struct zui *ui, char **envp, const char *start)
{
    for (char **entry = envp; entry && *entry; entry++) {
        const char *s = *entry;
        if (length(s) > 5 && s[0]=='H' && s[1]=='O' && s[2]=='M' && s[3]=='E' && s[4]=='=' && s[5]=='/'
            && length(s+5)<sizeof(home)) {
            copy(home,s+5,length(s+5));
            break;
        }
    }
    /* Desktop launches supply the account home as cwd rather than envp. */
    if (!home[0] && zenith_call(79,(unsigned long)home,sizeof(home),0) < 0) home[0] = 0;
    if (!home[0]) copy(home,"/",1);

    copy(places[PLACE_HOME],home,length(home)); place_exists[PLACE_HOME] = 1;
    copy(places[PLACE_ROOT],"/",1); place_exists[PLACE_ROOT] = 1;
    struct zenith_files_list probe = {.version = 1};
    for (int i = PLACE_DOOM; i <= PLACE_DOWNLOADS; i++) {
        if (!join(places[i],home,i == PLACE_DOOM ? "doom" : "Downloads")) continue;
        probe.path_bytes = (unsigned int)length(places[i]);
        for (unsigned int k = 0; k < probe.path_bytes; k++) probe.path[k] = places[i][k];
        place_exists[i] = zenith_files_list(&probe,page) >= 0;
    }
    char path[DOOM_PATH_BYTES];
    if (join(preference,home,".zenith-doom-folder") && !start) {
        long fd = zenith_call(2,(unsigned long)preference,length(preference),Z_OPEN_READ);
        if (fd >= 0) {
            long n = zenith_call(0,fd,(unsigned long)path,sizeof(path));
            zenith_call(3,fd,0,0);
            if (n > 0 && n < (long)sizeof(path) && path[0]=='/') {
                path[n]=0;
                if (length(path)==(unsigned long)n && browse(ui,path)) return;
            }
        }
    }
    if (start) {
        if (browse(ui,start)) return;
        const char *error = message;
        browse(ui,home);
        message = error;
    } else if (!join(path,home,"doom") || !browse(ui,path)) {
        if (!browse(ui,home)) browse(ui,"/");
    }
}

static void remember_folder(void)
{
    if (!preference[0]) return;
    unsigned long pid = (unsigned long)zenith_identity(39);
    char file[48] = ".zenith-doom-folder.tmp-";
    unsigned long at = length(file);
    char digits[24];
    unsigned int n = 0;
    do { digits[n++] = (char)('0' + pid % 10); pid /= 10; } while (pid);
    while (n) file[at++] = digits[--n];
    file[at] = 0;
    char path[DOOM_PATH_BYTES];
    if (!join(path,home,file)) return;
    long fd = zenith_call(2,(unsigned long)path,length(path),Z_OPEN_WRITE|Z_OPEN_CREATE|Z_OPEN_TRUNCATE);
    if (fd < 0) return;
    unsigned long bytes = length(folder);
    int ok = zenith_call(1,fd,(unsigned long)folder,bytes) == (long)bytes && zenith_call(74,fd,0,0)==0;
    if (zenith_call(3,fd,0,0) != 0) ok = 0;
    if (!ok || zenith_rename(path,length(path),preference,length(preference)) != 0)
        zenith_path(2,path,length(path),0,0,0,0);
}

static int row(void *context, int index, struct zui_list_item *item)
{
    (void)context;
    if (index<0 || index>=count) return 0;
    const struct entry *e = &entries[index];
    *item = (struct zui_list_item){e->folder?"folder":"file-archive",e->name,e->detail,e->folder,1};
    return 1;
}

static void text(struct zui *ui,int x,int y,const char *s,const struct zui_font *font,unsigned int color)
{
    zui_text(ui,x,y,s,length(s),font,color);
}

static int selected_file(void)
{
    return list.selected>=0 && list.selected<count && !entries[list.selected].folder;
}

static int same_path(const char *a, const char *b)
{
    while (*a && *a == *b) { a++; b++; }
    return *a == *b;
}
/* A line of text cut with "..." to a width (UTF-8 safe at the cut). */
static void fit(struct zui *ui, int x, int y, const char *s, int width, const struct zui_font *font, unsigned int color)
{
    unsigned long n = length(s);
    if (zui_measure(s,n,font) <= width) { zui_text(ui,x,y,s,n,font,color); return; }
    int dots = zui_measure("...",3,font);
    while (n && zui_measure(s,n,font) > width - dots) { n--; while (n && ((unsigned char)s[n] & 0xc0) == 0x80) n--; }
    zui_text(ui,x,y,s,n,font,color);
    zui_text(ui,x + zui_measure(s,n,font),y,"...",3,font,color);
}

/* The newer app layout (Beacon, Orbit): a sidebar of places, a heading with the folder and its path, the list on a
 * content card, and a footer with the chosen game and Play. */
static long draw(long window,struct zui *ui,long now)
{
    int focused=ui->focus>=0 && ui->focus<ui->count ? ui->controls[ui->focus].id : 0;
    int pressed=ui->pressed>=0 && ui->pressed<ui->count ? ui->controls[ui->pressed].id : 0;
    ui->count=0;
    ui->dirty=0;
    zui_motion_frame(ui,now);
    struct zui_workspace ws=zui_workspace_begin(ui,ui->width<760?168:208);
    struct zui_rect side=ws.sidebar,c=ws.content;

    /* Sidebar: the app and its places. */
    text(ui,side.x+20,side.y+18,"Doom",&zui_label,ui->theme.text);
    int y=side.y+56;
    text(ui,side.x+20,y,"Places",&zui_caption,ui->theme.secondary);y+=22;
    for (int i=0;i<PLACES;i++) {
        if (!place_exists[i]) continue;
        int at=zui_add(ui,PLACE_ID+i,ZUI_NAVIGATION,(struct zui_rect){side.x+8,y,side.w-16,36},place_names[i],same_path(folder,places[i]),1)?ui->count-1:-1;
        if (at>=0) { ui->controls[at].icon=place_icons[i]; ui->controls[at].no_chevron=1; }
        y+=40;
    }

    /* Heading: the folder's name and its path; Up and Refresh as icon buttons. */
    int x=c.x+24,w=c.w-48;
    const char *name=folder;
    for (const char *p=folder;*p;p++) if (*p=='/' && p[1]) name=p+1;
    zui_clip(ui,c);
    fit(ui,x,c.y+16,same_path(folder,"/")?"Computer":name,w-96,&zui_heading,ui->theme.text);
    fit(ui,x,c.y+52,folder,w-96,&zui_caption,ui->theme.secondary);
    zui_add(ui,UP_BUTTON,ZUI_BUTTON,(struct zui_rect){x+w-80,c.y+18,36,34},"",0,folder[1]!=0);
    if (ui->count) ui->controls[ui->count-1].icon="arrow-up";
    zui_add(ui,REFRESH_BUTTON,ZUI_BUTTON,(struct zui_rect){x+w-36,c.y+18,36,34},"",0,1);
    if (ui->count) ui->controls[ui->count-1].icon="refresh-cw";

    /* The folders and WADs on a card. */
    int footer=64;
    struct zui_rect card={x,c.y+84,w,c.h-84-footer-8};
    zui_surface(ui,card,zui_radius(ui,12,card.h),ZUI_SURFACE_CONTENT);
    zui_list_draw(ui,&list,(struct zui_rect){card.x+6,card.y+6,card.w-12,card.h-12},count,row,0);
    if (!count) {
        int cy=card.y+card.h/2-40;
        zui_icon(ui,"file-archive",card.x+(card.w-32)/2,cy,32,ZUI_ICON_TEXT);
        const char *line1="No WAD files here",*line2="Put Doom, Doom II or Freedoom WADs in a doom folder in your home,";
        const char *line3="or open the folder that holds them.";
        text(ui,card.x+(card.w-zui_measure(line1,length(line1),&zui_label))/2,cy+44,line1,&zui_label,ui->theme.text);
        fit(ui,card.x+(card.w-zui_measure(line2,length(line2),&zui_caption))/2,cy+68,line2,card.w-24,&zui_caption,ui->theme.secondary);
        text(ui,card.x+(card.w-zui_measure(line3,length(line3),&zui_caption))/2,cy+86,line3,&zui_caption,ui->theme.secondary);
    }

    /* Footer: what Play would start, or what went wrong. */
    int fy=c.y+c.h-footer;
    zui_fill(ui,(struct zui_rect){x,fy,w,1},ui->theme.border);
    const char *title=message?message:selected_file()?(game_of(entries[list.selected].name)?game_of(entries[list.selected].name):entries[list.selected].name):"Choose a game";
    /* Under the game's name: the file and its size (the row already says which game it is). */
    static char chosen[300];
    if (!message && selected_file()) { char *o=chosen; const struct entry *e=&entries[list.selected]; unsigned long n=length(e->name); if (n>200) n=200; copy(o,e->name,n); o+=n; o=put(o," · "); size_text(o,e->size); }
    const char *detail=message?0:selected_file()?chosen:"Pick a WAD from the list, then Play.";
    fit(ui,x,fy+14,title,w-130,&zui_label,message?ui->theme.text:ui->theme.text);
    if (detail) fit(ui,x,fy+36,detail,w-130,&zui_caption,ui->theme.secondary);
    zui_add(ui,PLAY_BUTTON,ZUI_BUTTON,(struct zui_rect){x+w-110,fy+14,110,36},"Play",1,selected_file());
    zui_clip(ui,(struct zui_rect){0,0,ui->width,ui->height});

    ui->focus=ui->pressed=-1;
    for(int i=0;i<ui->count;i++) {
        if(ui->controls[i].id==focused) ui->focus=i;
        if(ui->controls[i].id==pressed) ui->pressed=i;
    }
    zui_update_hover(ui);
    zui_draw_controls(ui);
    /* A material window: the workspace above filled in the material regions (sidebar and background). */
    unsigned long bytes=(unsigned long)ui->width*ui->height*4;
    return ui->foreground ? zenith_gui_present_material(window,ui->pixels,bytes,ui->materials,ui->material_count)
                          : zenith_gui_present(window,ui->pixels,bytes);
}

int doom_launcher(long window,struct zui *ui,char **envp,const char *start,const char *(*load)(const char *))
{
    zui_icons_use_packages();
    zui_apply_appearance(ui);
    zui_apply_motion(ui,zui_motion_clock());
    ui->hover_supported=zenith_gui_pointer_motion(window)==0;
    initialize_folder(ui,envp,start);
    for (;;) {
        long now=zui_motion_clock();
        zui_motion_tick(ui,now);
        if(ui->dirty && draw(window,ui,now)<0) return zui_window_closed(window)?0:-1;
        struct zenith_gui_event e;
        long r=zenith_gui_poll(window,&e);
        if(r<0) return -1;
        if(!r) continue;
        if(e.type==Z_GUI_CLOSE || (e.type==Z_GUI_KEY && e.extra==1)) return 0;
        if(e.type==Z_GUI_RESIZE) {
            if(zui_apply_viewport(ui,window,DOOM_WINDOW_PIXELS)<0) return zui_window_closed(window)?0:-1;
            ui->pressed=-1;
            continue;
        }
        if(e.type==Z_GUI_RESET || e.type==Z_GUI_FOCUS) {
            zui_apply_appearance(ui);
            zui_apply_motion(ui,now);
        }
        int action=zui_event(ui,&e);
        if(!action && ui->focus<0 && e.type==Z_GUI_KEY && e.extra==2 && list.selected>=0 && list.selected<count)
            action=ROW_ID+list.selected;
        /* Use list navigation for arrows and scrolling. Activation uses the
         * toolkit's press/release pairing and the currently focused control. */
        if(e.type==Z_GUI_UP) zui_scroll_event(ui,&list.scroll,&e);
        else if(!(e.type==Z_GUI_KEY && e.extra==2)) {
            int old=list.selected;
            zui_list_event(ui,&list,&e,row,0);
            if(list.selected!=old) {
                message=0;
                ui->focus=-1;
                for(int i=0;i<ui->count;i++) if(ui->controls[i].id==ROW_ID+list.selected) { ui->focus=i; break; }
            }
        }
        if(action==HOME_BUTTON) browse(ui,home);
        else if(action>=PLACE_ID && action<PLACE_ID+PLACES && place_exists[action-PLACE_ID]) browse(ui,places[action-PLACE_ID]);
        else if(action==UP_BUTTON) {
            unsigned long n=length(folder);
            while(n>1 && folder[n-1]=='/') n--;
            while(n>1 && folder[n-1]!='/') n--;
            if(n>1) n--;
            char path[DOOM_PATH_BYTES];
            copy(path,folder,n);
            browse(ui,path);
        } else if(action==REFRESH_BUTTON) browse(ui,folder);
        else if(action>=ROW_ID && action-ROW_ID<count) {
            list.selected=action-ROW_ID;
            message=0;
            if(entries[list.selected].folder) {
                char path[DOOM_PATH_BYTES];
                if(join(path,folder,entries[list.selected].name)) browse(ui,path);
                else message="This path is too long.";
            } else if(e.type==Z_GUI_KEY && e.extra==2) action=PLAY_BUTTON;
            ui->dirty=1;
        }
        if(action==PLAY_BUTTON && selected_file()) {
            char path[DOOM_PATH_BYTES];
            if(!join(path,folder,entries[list.selected].name)) message="This path is too long.";
            else {
                message="Loading WAD...";
                if(draw(window,ui,now)<0) return zui_window_closed(window)?0:-1;
                message=load(path);
                if(!message) { remember_folder(); return 1; }
            }
            ui->dirty=1;
        }
    }
}
