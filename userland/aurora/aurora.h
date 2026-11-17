#ifndef AURORA_H
#define AURORA_H
/* Aurora, the text editor (docs/components/AURORA.md). editor.c holds every document, the drawing and the
 * input handling; it reaches the system only through the au_sys_* calls below, which main.c implements with
 * Zenith's native calls and tools/test_aurora.py's host test implements over a temporary directory. */
#include "../ui/ui.h"
#include "buffer.h"

struct au_info { unsigned long size; unsigned int mode; long mtime; int is_dir; };
/* Reads a whole file: bytes read, -27 when it holds more than capacity bytes, or another negative error. */
long au_sys_read(const char *path,char *out,unsigned long capacity);
long au_sys_info(const char *path,struct au_info *info);
/* Creates (or truncates) a file for writing; a descriptor or a negative error. */
long au_sys_create(const char *path);
long au_sys_write(long fd,const char *data,unsigned long bytes);
long au_sys_close(long fd);
long au_sys_rename(const char *from,const char *to);
long au_sys_unlink(const char *path);
long au_sys_chmod(const char *path,unsigned int mode);
/* Calls each(context,name,is_dir) for the entries of a folder; the count or a negative error. */
long au_sys_list(const char *folder,void (*each)(void *context,const char *name,int is_dir),void *context);
/* The system clipboard: set it, and read it right after the person asked to paste (negative otherwise). */
long au_sys_copy(const char *text,unsigned long bytes);
long au_sys_paste(char *out,unsigned long capacity);
long au_sys_clock(void);
void au_sys_log(const char *line);
void au_sys_title(const char *title);
void au_sys_unsaved(int unsaved);

/* The editor. aurora_start opens the files named (absolute paths) or one empty document. */
void aurora_start(struct zui *ui,const char *home,int count,char **paths);
void aurora_draw(void);
/* Handles one event; returns 1 when Aurora should end (every document closed or the window closed). */
int aurora_event(const struct zenith_gui_event *event);
/* The window's close button or Alt+F4: asks about unsaved documents first. 1 when Aurora may end now. */
int aurora_close(void);
/* Re-reads appearance-dependent state after Z_GUI_RESET; checks open files for changes on disk on focus. */
void aurora_focus(int focused);
/* After each handled event: keeps the caret in view (uses the last frame's layout). */
void aurora_after_event(void);
/* Events that older headers name elsewhere. */
#ifndef Z_GUI_SECONDARY
#define Z_GUI_SECONDARY 12
#endif
#ifndef Z_GUI_CLOSE_INTENT
#define Z_GUI_CLOSE_INTENT 9
#endif
#endif
