/* SPDX-License-Identifier: GPL-2.0-only */
#include "launcher.h"
#include "../include/zenith_files.h"
#include "../ui/widgets.h"

enum { MAX_ENTRIES = 256, HOME_BUTTON = 1, UP_BUTTON, REFRESH_BUTTON, PLAY_BUTTON, ROW_ID = 32 };
struct entry { char name[256]; int folder; };
static struct entry entries[MAX_ENTRIES];
static _Alignas(16) unsigned char page[Z_FILES_PAGE];
static struct zui_list list = {.selected = -1, .base_id = ROW_ID};
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
            struct entry value = {.folder = directory};
            copy(value.name,f->name,f->name_bytes);
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
    *item = (struct zui_list_item){e->folder?"folder":"file-archive",e->name,0,e->folder,1};
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

static long draw(long window,struct zui *ui,long now)
{
    int w=ui->width,h=ui->height;
    int focused=ui->focus>=0 && ui->focus<ui->count ? ui->controls[ui->focus].id : 0;
    int pressed=ui->pressed>=0 && ui->pressed<ui->count ? ui->controls[ui->pressed].id : 0;
    ui->count=0;
    ui->dirty=0;
    zui_motion_frame(ui,now);
    zui_clip(ui,(struct zui_rect){0,0,w,h});
    zui_fill(ui,ui->clip,ui->theme.surface);
    text(ui,24,18,"Choose a WAD",&zui_heading,ui->theme.text);
    zui_add(ui,HOME_BUTTON,ZUI_BUTTON,(struct zui_rect){24,58,74,34},"Home",0,1);
    zui_add(ui,UP_BUTTON,ZUI_BUTTON,(struct zui_rect){106,58,64,34},"Up",0,folder[1]!=0);
    zui_add(ui,REFRESH_BUTTON,ZUI_BUTTON,(struct zui_rect){178,58,84,34},"Refresh",0,1);
    struct zui_rect address={24,102,w-48,34};
    zui_surface(ui,address,8,ZUI_SURFACE_CONTROL);
    zui_clip(ui,(struct zui_rect){34,102,w-68,34});
    const char *shown=folder;
    while (*shown && zui_measure(shown,length(shown),&zui_body)>w-92) shown++;
    while (((unsigned char)*shown & 0xc0) == 0x80) shown++;
    if (shown!=folder) text(ui,34,110,"...",&zui_body,ui->theme.secondary);
    text(ui,shown==folder?34:52,110,shown,&zui_body,ui->theme.text);
    zui_clip(ui,(struct zui_rect){0,0,w,h});

    struct zui_rect files={24,146,w-48,h-220};
    zui_surface(ui,files,12,ZUI_SURFACE_CONTENT);
    zui_list_draw(ui,&list,(struct zui_rect){files.x+6,files.y+6,files.w-12,files.h-12},count,row,0);
    if (!count) text(ui,42,167,"No WAD files or folders here.",&zui_body,ui->theme.secondary);

    const char *caption=message?message:selected_file()?entries[list.selected].name:"Select a base-game WAD to play.";
    zui_clip(ui,(struct zui_rect){24,h-65,w-158,56});
    text(ui,24,h-42,caption,&zui_body,message?ui->theme.text:ui->theme.secondary);
    zui_clip(ui,(struct zui_rect){0,0,w,h});
    zui_add(ui,PLAY_BUTTON,ZUI_BUTTON,(struct zui_rect){w-120,h-57,96,36},"Play",1,selected_file());
    ui->focus=ui->pressed=-1;
    for(int i=0;i<ui->count;i++) {
        if(ui->controls[i].id==focused) ui->focus=i;
        if(ui->controls[i].id==pressed) ui->pressed=i;
    }
    zui_update_hover(ui);
    zui_draw_controls(ui);
    return zenith_gui_present(window,ui->pixels,(unsigned long)w*h*4);
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
