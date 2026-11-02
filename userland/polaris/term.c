#include "term.h"
static struct pt_line *line_at(const struct pterm *t,unsigned int i){return &t->lines[(t->first_line+i)%t->line_capacity];}
void pt_clear(struct pterm *t){
    t->first_line=0;t->line_count=1;t->next_cell=0;t->column=0;t->lines[0]=(struct pt_line){0,0,0,0,0};t->changed=1;
    t->has_prompt=0;
}
int pt_init(struct pterm *t,struct pt_cell *cells,unsigned int cell_capacity,struct pt_line *lines,unsigned int line_capacity){
    if(!t||!cells||!lines||cell_capacity<16||line_capacity<2)return 0;
    t->cells=cells;t->cell_capacity=cell_capacity;t->lines=lines;t->line_capacity=line_capacity;
    t->color=t->bold=0;t->escape=0;t->parameter_count=0;t->utf8=0;t->utf8_left=0;t->osc_length=0;t->dropped=0;
    pt_clear(t);return 1;
}
/* Cells live in a ring; a line never wraps around it, which keeps every line contiguous. */
static void drop_first(struct pterm *t){t->first_line=(t->first_line+1)%t->line_capacity;t->line_count--;t->dropped++;}
static int overlaps(const struct pterm *t,unsigned int from,unsigned int to){
    /* Lines fill the ring in order, so the oldest with any cells is the next to be overwritten. */
    for(unsigned int i=0;i+1<t->line_count;i++){
        const struct pt_line *l=line_at(t,i);
        if(l->length)return l->start<to&&from<l->start+l->length;
    }
    return 0;
}
static void new_line(struct pterm *t){
    struct pt_line *last=line_at(t,t->line_count-1);
    unsigned int start=last->start+last->length;
    if(t->line_count==t->line_capacity)drop_first(t);
    t->line_count++;
    *line_at(t,t->line_count-1)=(struct pt_line){start,0,0,0,0};
    t->column=0;t->next_cell=start;
}
static void put(struct pterm *t,struct pt_cell c){
    struct pt_line *l=line_at(t,t->line_count-1);
    if(t->column>=PT_MAX_LINE){new_line(t);l=line_at(t,t->line_count-1);}
    if(t->column<l->length){t->cells[l->start+t->column++]=c;t->changed=1;return;}
    unsigned int end=l->start+l->length;
    if(end>=t->cell_capacity){
        /* Move this line to the start of the ring. A line that fills the ring starts over. */
        if(l->length>=t->cell_capacity/2){l->length=0;t->column=0;end=l->start;}
        unsigned int length=l->length;
        for(unsigned int i=0;i<length;i++){struct pt_cell v=t->cells[l->start+i];t->cells[i]=v;}
        while(t->line_count>1&&overlaps(t,0,length+1))drop_first(t);
        l=line_at(t,t->line_count-1);l->start=0;end=length;
    }
    while(t->line_count>1&&overlaps(t,end,end+1))drop_first(t);
    l=line_at(t,t->line_count-1);
    t->cells[end]=c;l->length++;t->column=l->length;t->next_cell=end+1;t->changed=1;
}
void pt_put(struct pterm *t,struct pt_cell c){put(t,c);}
static void sgr(struct pterm *t){
    if(!t->parameter_count){t->color=t->bold=0;return;}
    for(int i=0;i<t->parameter_count;i++){
        int p=t->parameters[i];
        if(p==0){t->color=0;t->bold=0;}
        else if(p==1)t->bold=1;
        else if(p==22)t->bold=0;
        else if(p==39)t->color=0;
        else if(p>=30&&p<=37)t->color=(unsigned char)(p-29);
        else if(p>=90&&p<=97)t->color=(unsigned char)(p-81);
    }
}
static void control(struct pterm *t,char final){
    int first=t->parameter_count?t->parameters[0]:0;
    struct pt_line *l=line_at(t,t->line_count-1);
    if(final=='m')sgr(t);
    else if(final=='J'&&first==2)pt_clear(t);
    else if(final=='K'){
        if(first==2){l->length=0;t->column=0;}
        else if(first==0&&t->column<l->length)l->length=t->column;
        t->changed=1;
    }
}
static unsigned long number(const char *s,int *at){unsigned long v=0;while(s[*at]>='0'&&s[*at]<='9'){if(v<100000000UL)v=v*10+(unsigned long)(s[*at]-'0');(*at)++;}return v;}
/* OSC 133: A starts a prompt line, D;status;ms ends that line's command. Others are ignored. */
static void osc(struct pterm *t){
    const char *s=t->osc;
    if(t->osc_length<5||s[0]!='1'||s[1]!='3'||s[2]!='3'||s[3]!=';')return;
    if(s[4]=='A'){t->prompt=t->dropped+t->line_count-1;t->has_prompt=1;line_at(t,t->line_count-1)->mark=PT_PROMPT;return;}
    if(s[4]!='D'||!t->has_prompt||t->prompt<t->dropped)return;
    int at=5;unsigned long status=0,ms=0;
    if(s[at]==';'){at++;status=number(s,&at);}
    if(s[at]==';'){at++;ms=number(s,&at);}
    struct pt_line *l=line_at(t,(unsigned int)(t->prompt-t->dropped));
    l->mark=PT_DONE;l->status=(unsigned char)(status>255?255:status);l->ms=(unsigned int)ms;t->changed=1;
}
static void character(struct pterm *t,unsigned int c){
    struct pt_cell cell={(unsigned short)(c>0xffff?'?':c),t->color,t->bold};
    put(t,cell);
}
static void byte(struct pterm *t,unsigned char b){
    if(t->escape==3){
        if(b==0x07||b==0x1b){t->osc[t->osc_length]=0;osc(t);t->escape=b==0x1b?4:0;return;}
        if(t->osc_length<(int)sizeof(t->osc)-1)t->osc[t->osc_length++]=(char)b;
        return;
    }
    if(t->escape==4){t->escape=0;if(b=='\\')return;}
    if(t->escape==1){
        if(b==']'){t->escape=3;t->osc_length=0;return;}
        if(b=='['){t->escape=2;t->parameter_count=0;for(int i=0;i<8;i++)t->parameters[i]=0;return;}
        t->escape=0;return;
    }
    if(t->escape==2){
        if(b>='0'&&b<='9'){
            if(!t->parameter_count)t->parameter_count=1;
            int *p=&t->parameters[t->parameter_count-1];
            if(*p<100000)*p=*p*10+(b-'0');
            return;
        }
        if(b==';'){if(!t->parameter_count)t->parameter_count=1;if(t->parameter_count<8)t->parameter_count++;return;}
        if(b>=0x20&&b<0x40)return;
        t->escape=0;control(t,(char)b);return;
    }
    if(t->utf8_left){
        if((b&0xc0)==0x80){
            t->utf8=(t->utf8<<6)|(b&0x3f);
            if(!--t->utf8_left)character(t,t->utf8);
            return;
        }
        t->utf8_left=0;character(t,'?');
    }
    if(b>=0xf0&&b<0xf8){t->utf8=b&7;t->utf8_left=3;return;}
    if(b>=0xe0&&b<0xf0){t->utf8=b&15;t->utf8_left=2;return;}
    if(b>=0xc2&&b<0xe0){t->utf8=b&31;t->utf8_left=1;return;}
    if(b>=0x80){character(t,'?');return;}
    if(b==0x1b){t->escape=1;return;}
    if(b=='\n'){new_line(t);t->changed=1;return;}
    if(b=='\r'){t->column=0;return;}
    if(b=='\b'){if(t->column)t->column--;return;}
    if(b=='\t'){unsigned int stop=(t->column/8+1)*8;while(t->column<stop)character(t,' ');return;}
    if(b>=0x20&&b<0x7f)character(t,b);
}
void pt_write(struct pterm *t,const char *bytes,unsigned long count){for(unsigned long i=0;i<count;i++)byte(t,(unsigned char)bytes[i]);}
int pt_mark(const struct pterm *t,unsigned int line,int *status,unsigned int *ms){
    if(line>=t->line_count)return 0;
    const struct pt_line *l=line_at(t,line);
    if(status)*status=l->status;
    if(ms)*ms=l->ms;
    return l->mark;
}
unsigned int pt_length(const struct pterm *t,unsigned int line){return line<t->line_count?line_at(t,line)->length:0;}
struct pt_cell pt_cell_at(const struct pterm *t,unsigned int line,unsigned int column){
    if(line>=t->line_count||column>=line_at(t,line)->length)return (struct pt_cell){' ',0,0};
    return t->cells[line_at(t,line)->start+column];
}
unsigned int pt_rows(unsigned int length,unsigned int columns){return !length||!columns?1:(length+columns-1)/columns;}
int pt_utf8(unsigned short c,char *o){
    if(c<0x80){o[0]=(char)c;return 1;}
    if(c<0x800){o[0]=(char)(0xc0|c>>6);o[1]=(char)(0x80|(c&63));return 2;}
    o[0]=(char)(0xe0|c>>12);o[1]=(char)(0x80|((c>>6)&63));o[2]=(char)(0x80|(c&63));return 3;
}
