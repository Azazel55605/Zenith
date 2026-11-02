/* Host tests for the Polaris scrollback model. */
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "term.h"
static struct pt_cell cells[4096];static struct pt_line lines[64];
static void text(const struct pterm *t,unsigned int line,char *out){
    unsigned int n=pt_length(t,line);for(unsigned int i=0;i<n;i++)out[i]=(char)pt_cell_at(t,line,i).ch;out[n]=0;
}
static void same(const struct pterm *t,unsigned int line,const char *want){char got[9000];text(t,line,got);if(strcmp(got,want)){fprintf(stderr,"line %u: '%s' != '%s'\n",line,got,want);assert(0);}}
#define W(s) pt_write(&t,s,strlen(s))
int main(void){
    struct pterm t;assert(pt_init(&t,cells,4096,lines,64));
    assert(!pt_init(&t,cells,1,lines,64));assert(pt_init(&t,cells,4096,lines,64));
    W("hello\nworld");assert(t.line_count==2);same(&t,0,"hello");same(&t,1,"world");
    /* Overwrite, erase and backspace. */
    W("\rWO");same(&t,1,"WOrld");W("\x1b[K");same(&t,1,"WO");W("\x1b[2K");same(&t,1,"");
    W("ab\bc");same(&t,1,"ac");
    /* Colors and bold, split across writes anywhere. */
    W("\n");W("\x1b");W("[3");W("1;1");W("mR\x1b[0mx");
    assert(pt_cell_at(&t,2,0).color==2&&pt_cell_at(&t,2,0).bold==1);
    assert(pt_cell_at(&t,2,1).color==0&&!pt_cell_at(&t,2,1).bold);
    W("\x1b[92mg\x1b[39m");assert(pt_cell_at(&t,2,2).color==11);
    /* UTF-8, split too; a bad sequence shows one replacement, not garbage. */
    W("\n");W("\xc3");W("\xa9");W("\xe2\x86");W("\x91");assert(pt_cell_at(&t,3,0).ch==0xe9&&pt_cell_at(&t,3,1).ch==0x2191);
    char e[4];assert(pt_utf8(0xe9,e)==2&&(unsigned char)e[0]==0xc3);assert(pt_utf8(0x2191,e)==3);assert(pt_utf8('a',e)==1);
    W("\xc3z");assert(pt_cell_at(&t,3,2).ch=='?'&&pt_cell_at(&t,3,3).ch=='z');
    /* Tabs and wrapping. */
    W("\n\tx");same(&t,4,"        x");
    assert(pt_rows(0,80)==1&&pt_rows(80,80)==1&&pt_rows(81,80)==2&&pt_rows(5,0)==1);
    /* Clear screen. */
    W("\x1b[2J");assert(t.line_count==1);same(&t,0,"");
    /* Old lines go first when the line table fills; the newest stay right. */
    char line[32];
    for(int i=0;i<500;i++){int n=snprintf(line,sizeof line,"L%d\n",i);pt_write(&t,line,(unsigned long)n);}
    assert(t.line_count==64);same(&t,62,"L499");same(&t,61,"L498");same(&t,0,"L437");
    /* Old cells go first when the ring fills, however long the run. */
    pt_clear(&t);
    static char long_line[300];memset(long_line,'y',299);
    for(int i=0;i<200;i++){int n=snprintf(line,sizeof line,"%04d",i);pt_write(&t,line,(unsigned long)n);pt_write(&t,long_line,299);pt_write(&t,"\n",1);}
    assert(t.line_count>=2&&t.line_count<=64);
    {char got[400];text(&t,t.line_count-2,got);assert(!strncmp(got,"0199",4)&&strlen(got)==303);
     text(&t,t.line_count-3,got);assert(!strncmp(got,"0198",4)&&strlen(got)==303);
     for(unsigned int i=0;i+1<t.line_count;i++){text(&t,i,got);assert(strlen(got)==303&&got[4]=='y'&&got[302]=='y');}}
    /* One very long line is cut into bounded lines and never escapes the ring. */
    pt_clear(&t);static char huge[20000];memset(huge,'z',sizeof huge);pt_write(&t,huge,sizeof huge);
    assert(t.line_count>=1);for(unsigned int i=0;i<t.line_count;i++)assert(pt_length(&t,i)<=PT_MAX_LINE);
    W("\nend");same(&t,t.line_count-1,"end");
    /* Prompt marks: a command's line learns its status and time, however the sequence is split. */
    pt_clear(&t);W("\x1b]133;A\x07$ false\n");W("out\n");W("\x1b]13");W("3;D;1;42\x07$ ");
    {int st=0;unsigned int ms=0;assert(pt_mark(&t,0,&st,&ms)==PT_DONE&&st==1&&ms==42);assert(!pt_mark(&t,1,0,0));}
    same(&t,0,"$ false");same(&t,1,"out");same(&t,2,"$ ");
    W("\x1b]133;A\x1b\\$ sleep\n");assert(pt_mark(&t,2,0,0)==PT_PROMPT);           /* ST ends it too */
    W("\x1b]0;a window title\x07x");same(&t,3,"x");                                  /* other OSC ignored */
    /* A prompt line that scrolled away is not marked by a late D. */
    pt_clear(&t);W("\x1b]133;A\x07$ long\n");for(int i=0;i<100;i++)W("line\n");W("\x1b]133;D;0;5\x07");
    for(unsigned int i=0;i<t.line_count;i++)assert(pt_mark(&t,i,0,0)!=PT_DONE);
    puts("PASS Polaris scrollback: lines, escapes, UTF-8, wrapping and bounded rings");return 0;
}
