#ifndef AURORA_BUFFER_H
#define AURORA_BUFFER_H
/* Aurora's text engine: a gap buffer with a line index, an undo history and search. No libc, no
 * allocation: every array belongs to the caller, so the program and the host tests size them.
 * Offsets are bytes of UTF-8 text with LF line ends (CRLF is converted when loading and saving). */

struct au_text {
    char *data; unsigned int capacity,gap_start,gap_end; /* text = data[0,gap_start) + data[gap_end,capacity) */
    unsigned int *lines; unsigned int line_capacity,line_count; /* start offset of every line; lines[0]=0 */
};
void au_text_init(struct au_text *t,char *data,unsigned int capacity,unsigned int *lines,unsigned int line_capacity);
unsigned int au_length(const struct au_text *t);
char au_at(const struct au_text *t,unsigned int position);
/* Copies up to count bytes from position; returns the number copied. */
unsigned int au_read(const struct au_text *t,unsigned int position,unsigned int count,char *out);
/* Replaces count bytes at position with text. 0, or -1 when the bytes or lines do not fit (nothing changes). */
int au_replace_raw(struct au_text *t,unsigned int position,unsigned int count,const char *text,unsigned int bytes);
/* Line of an offset, start of a line, end of a line (its LF or the text end). */
unsigned int au_line_of(const struct au_text *t,unsigned int position);
unsigned int au_line_start(const struct au_text *t,unsigned int line);
unsigned int au_line_end(const struct au_text *t,unsigned int line);

/* UTF-8 boundaries and classes. */
unsigned int au_next(const struct au_text *t,unsigned int position);
unsigned int au_previous(const struct au_text *t,unsigned int position);
unsigned int au_word_left(const struct au_text *t,unsigned int position);
unsigned int au_word_right(const struct au_text *t,unsigned int position);
int au_word_char(char c);
/* Display column of position within its line (tabs to the next multiple of tab), and the reverse. */
unsigned int au_column(const struct au_text *t,unsigned int position,unsigned int tab);
unsigned int au_at_column(const struct au_text *t,unsigned int line,unsigned int column,unsigned int tab);

/* Undo history: every change is one replacement; changes of one group undo together. */
struct au_change { unsigned int position,removed,inserted,store,caret_before,caret_after,group; };
struct au_history {
    struct au_change *changes; unsigned int change_capacity,count,current; /* current: changes applied */
    char *store; unsigned int store_capacity,used;
    unsigned int group; long saved; /* changes applied when saved; -1 when that state was dropped */
    int typing; /* the last change was typed text that the next typed character may extend */
};
void au_history_init(struct au_history *h,struct au_change *changes,unsigned int change_capacity,char *store,unsigned int store_capacity);
/* Starts a new undo group (a separate step). */
void au_history_break(struct au_history *h);
/* The edit everything goes through. typing=1 lets consecutive typed characters share one undo step.
 * Returns 0, or -1 when the text does not fit (nothing changes). Big changes still apply when the
 * history cannot hold them; the history is then cleared (and the document counts as unsaved). */
int au_edit(struct au_text *t,struct au_history *h,unsigned int position,unsigned int count,const char *text,unsigned int bytes,
            unsigned int caret_before,unsigned int caret_after,int typing);
/* Undo/redo one group; returns 1 and the caret to show, 0 when there is nothing to do. */
int au_undo(struct au_text *t,struct au_history *h,unsigned int *caret);
int au_redo(struct au_text *t,struct au_history *h,unsigned int *caret);
int au_modified(const struct au_history *h);
void au_mark_saved(struct au_history *h);
void au_history_clear(struct au_history *h);

/* Search. Returns the match offset or -1. Case-insensitive search folds ASCII letters only. */
enum { AU_CASE=1,AU_WORD=2 };
long au_find(const struct au_text *t,const char *needle,unsigned int bytes,unsigned int from,int forward,int flags);
/* Matches in the text, at most limit; *index (when not 0) gets the 1-based number of the match at position. */
unsigned int au_count(const struct au_text *t,const char *needle,unsigned int bytes,int flags,unsigned int limit,unsigned int position,unsigned int *index);

/* Loading and saving. au_check: 0 for valid UTF-8 text, else AU_NOT_TEXT (NUL bytes) or AU_NOT_UTF8. */
enum { AU_NOT_TEXT=1,AU_NOT_UTF8=2 };
int au_check(const char *bytes,unsigned int count);
/* Converts loaded bytes in place: drops a UTF-8 BOM and turns CRLF into LF. Returns the new length;
 * *crlf is 1 when most line ends were CRLF, *bom when the file started with a BOM. */
unsigned int au_normalize(char *bytes,unsigned int count,int *crlf,int *bom);
/* Encodes text [*position, end) for saving into out (capacity bytes), with CRLF when crlf. Advances
 * *position and returns the bytes written; call until *position reaches the end. */
unsigned int au_encode(const struct au_text *t,unsigned int *position,int crlf,char *out,unsigned int capacity);
/* Rebuilds the text from bytes (after au_normalize). -1 when it does not fit. */
int au_load(struct au_text *t,const char *bytes,unsigned int count);
#endif
