#ifndef ZENITH_POLARIS_TERM_H
#define ZENITH_POLARIS_TERM_H
/* The scrollback as logical lines: wrapped only when drawn, so resizing reflows everything.
 * Pure and bounded; the caller owns the storage. Understands the sequences the shell and
 * native tools write: colors, bold, clear screen, erase line, carriage return, backspace, tab. */
struct pt_cell { unsigned short ch; unsigned char color,bold; }; /* color 0 is the default, 1..16 the ANSI palette */
struct pt_line { unsigned int start,length; unsigned int ms; unsigned char mark,status; }; /* mark: PT_PROMPT or PT_DONE */
enum { PT_PROMPT=1, PT_DONE=2 };
struct pterm {
    struct pt_cell *cells; unsigned int cell_capacity,next_cell;
    struct pt_line *lines; unsigned int line_capacity,first_line,line_count;
    unsigned int column; unsigned char color,bold;
    /* Decoder state, kept between writes because a read can end anywhere. */
    int escape,parameters[8],parameter_count; unsigned int utf8; int utf8_left;
    char osc[48]; int osc_length;
    unsigned long dropped,prompt; int has_prompt; /* lines dropped so far; the latest prompt line, counting those */
    int changed;
};
enum { PT_MAX_LINE=8192 };
int pt_init(struct pterm *t,struct pt_cell *cells,unsigned int cell_capacity,struct pt_line *lines,unsigned int line_capacity);
void pt_clear(struct pterm *t);
void pt_write(struct pterm *t,const char *bytes,unsigned long count);
unsigned int pt_length(const struct pterm *t,unsigned int line);
/* The line's prompt mark: 0, PT_PROMPT while its command runs or if it never ran one, PT_DONE with status and time. */
int pt_mark(const struct pterm *t,unsigned int line,int *status,unsigned int *ms);
struct pt_cell pt_cell_at(const struct pterm *t,unsigned int line,unsigned int column);
/* Appends one cell of another terminal's tail, keeping its color. */
void pt_put(struct pterm *t,struct pt_cell cell);
/* Rows a line takes at this width; an empty line still takes one. */
unsigned int pt_rows(unsigned int length,unsigned int columns);
/* Encodes a cell's character; returns its bytes (1..3). */
int pt_utf8(unsigned short ch,char *out);
#endif
