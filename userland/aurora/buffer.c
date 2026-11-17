/* Aurora's text engine (buffer.h). Freestanding: also compiled on the host by tools/test_aurora.py. */
#include "buffer.h"

static void move_bytes(char *to,const char *from,unsigned int n){
    if(to==from||!n)return;
    if(to<from)for(unsigned int i=0;i<n;i++)to[i]=from[i];
    else for(unsigned int i=n;i>0;i--)to[i-1]=from[i-1];
}
static void move_words(unsigned int *to,const unsigned int *from,unsigned int n){
    if(to==from||!n)return;
    if(to<from)for(unsigned int i=0;i<n;i++)to[i]=from[i];
    else for(unsigned int i=n;i>0;i--)to[i-1]=from[i-1];
}
static char fold(char c){return c>='A'&&c<='Z'?(char)(c+32):c;}

void au_text_init(struct au_text *t,char *data,unsigned int capacity,unsigned int *lines,unsigned int line_capacity){
    t->data=data;t->capacity=capacity;t->gap_start=0;t->gap_end=capacity;
    t->lines=lines;t->line_capacity=line_capacity;t->line_count=1;lines[0]=0;
}
unsigned int au_length(const struct au_text *t){return t->capacity-(t->gap_end-t->gap_start);}
char au_at(const struct au_text *t,unsigned int p){
    if(p>=au_length(t))return 0;
    return p<t->gap_start?t->data[p]:t->data[p+(t->gap_end-t->gap_start)];
}
unsigned int au_read(const struct au_text *t,unsigned int p,unsigned int n,char *out){
    unsigned int length=au_length(t);if(p>=length)return 0;if(n>length-p)n=length-p;
    unsigned int done=0;
    if(p<t->gap_start){unsigned int a=t->gap_start-p;if(a>n)a=n;move_bytes(out,t->data+p,a);done=a;}
    if(done<n){unsigned int from=p+done+(t->gap_end-t->gap_start);move_bytes(out+done,t->data+from,n-done);}
    return n;
}
static void move_gap(struct au_text *t,unsigned int p){
    unsigned int gap=t->gap_end-t->gap_start;
    if(p<t->gap_start){unsigned int n=t->gap_start-p;move_bytes(t->data+t->gap_end-n,t->data+p,n);t->gap_start=p;t->gap_end=p+gap;}
    else if(p>t->gap_start){unsigned int n=p-t->gap_start;move_bytes(t->data+t->gap_start,t->data+t->gap_end,n);t->gap_start=p;t->gap_end=p+gap;}
}
unsigned int au_line_of(const struct au_text *t,unsigned int p){
    unsigned int low=0,high=t->line_count-1;
    while(low<high){unsigned int mid=(low+high+1)/2;if(t->lines[mid]<=p)low=mid;else high=mid-1;}
    return low;
}
unsigned int au_line_start(const struct au_text *t,unsigned int line){return line<t->line_count?t->lines[line]:au_length(t);}
unsigned int au_line_end(const struct au_text *t,unsigned int line){
    return line+1<t->line_count?t->lines[line+1]-1:au_length(t);
}
int au_replace_raw(struct au_text *t,unsigned int p,unsigned int count,const char *text,unsigned int bytes){
    unsigned int length=au_length(t);
    if(p>length||count>length-p)return -1;
    if((unsigned long)length-count+bytes>=t->capacity)return -1;
    /* Lines starting inside (p, p+count] go; one starts after every inserted LF. */
    unsigned int first=au_line_of(t,p)+1,last=first;
    while(last<t->line_count&&t->lines[last]<=p+count)last++;
    unsigned int added=0;for(unsigned int i=0;i<bytes;i++)added+=text[i]=='\n';
    unsigned long lines=(unsigned long)t->line_count-(last-first)+added;
    if(lines>t->line_capacity)return -1;
    move_gap(t,p);t->gap_end+=count;
    move_bytes(t->data+t->gap_start,text,bytes);t->gap_start+=bytes;
    unsigned int tail=t->line_count-last;
    move_words(t->lines+first+added,t->lines+last,tail);
    for(unsigned int i=0;i<tail;i++)t->lines[first+added+i]=t->lines[first+added+i]-count+bytes;
    unsigned int k=first;for(unsigned int i=0;i<bytes;i++)if(text[i]=='\n')t->lines[k++]=p+i+1;
    t->line_count=(unsigned int)lines;
    return 0;
}

unsigned int au_next(const struct au_text *t,unsigned int p){
    unsigned int n=au_length(t);if(p<n)p++;
    while(p<n&&((unsigned char)au_at(t,p)&0xc0)==0x80)p++;
    return p;
}
unsigned int au_previous(const struct au_text *t,unsigned int p){
    if(p)p--;
    while(p&&((unsigned char)au_at(t,p)&0xc0)==0x80)p--;
    return p;
}
int au_word_char(char c){return (c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||(unsigned char)c>=0x80;}
unsigned int au_word_left(const struct au_text *t,unsigned int p){
    if(p&&au_at(t,p-1)=='\n')return p-1;
    while(p&&!au_word_char(au_at(t,p-1))&&au_at(t,p-1)!='\n')p--;
    while(p&&au_word_char(au_at(t,p-1)))p--;
    return p;
}
unsigned int au_word_right(const struct au_text *t,unsigned int p){
    unsigned int n=au_length(t);
    if(p<n&&au_at(t,p)=='\n')return p+1;
    while(p<n&&au_word_char(au_at(t,p)))p++;
    while(p<n&&!au_word_char(au_at(t,p))&&au_at(t,p)!='\n')p++;
    return p;
}
unsigned int au_column(const struct au_text *t,unsigned int p,unsigned int tab){
    if(!tab)tab=4;
    unsigned int column=0;
    for(unsigned int q=au_line_start(t,au_line_of(t,p));q<p;q++){
        char c=au_at(t,q);
        if(c=='\t')column=(column/tab+1)*tab;
        else if(((unsigned char)c&0xc0)!=0x80)column++;
    }
    return column;
}
unsigned int au_at_column(const struct au_text *t,unsigned int line,unsigned int target,unsigned int tab){
    if(!tab)tab=4;
    unsigned int p=au_line_start(t,line),end=au_line_end(t,line),column=0;
    while(p<end){
        char c=au_at(t,p);unsigned int next=c=='\t'?(column/tab+1)*tab:column+1;
        if(next>target){if(target-column>next-target)p=au_next(t,p);break;}
        column=next;p=au_next(t,p);
        if(column==target)break;
    }
    return p;
}

void au_history_init(struct au_history *h,struct au_change *changes,unsigned int change_capacity,char *store,unsigned int store_capacity){
    *h=(struct au_history){changes,change_capacity,0,0,store,store_capacity,0,1,0,0};
}
void au_history_clear(struct au_history *h){
    int was_saved=!au_modified(h);
    h->count=h->current=h->used=0;h->typing=0;h->group++;h->saved=was_saved?0:-1;
}
void au_history_break(struct au_history *h){h->group++;h->typing=0;}
int au_modified(const struct au_history *h){return h->saved!=(long)h->current;}
void au_mark_saved(struct au_history *h){h->saved=(long)h->current;}
/* Drops the oldest whole groups until `changes` slots and `bytes` of store are free. 1 on success. */
static int make_room(struct au_history *h,unsigned int bytes){
    while(h->count&&(h->count+1>h->change_capacity||h->used+bytes>h->store_capacity)){
        unsigned int group=h->changes[0].group,drop=0;
        while(drop<h->count&&h->changes[drop].group==group)drop++;
        if(drop>h->current)return 0; /* never drop changes that are not applied */
        unsigned int from=drop<h->count?h->changes[drop].store:h->used;
        move_bytes(h->store,h->store+from,h->used-from);h->used-=from;
        for(unsigned int i=drop;i<h->count;i++){h->changes[i-drop]=h->changes[i];h->changes[i-drop].store-=from;}
        h->count-=drop;h->current-=drop;
        h->saved=h->saved>=(long)drop?h->saved-(long)drop:-1;
    }
    return h->count+1<=h->change_capacity&&h->used+bytes<=h->store_capacity;
}
int au_edit(struct au_text *t,struct au_history *h,unsigned int p,unsigned int count,const char *text,unsigned int bytes,
            unsigned int before,unsigned int after,int typing){
    unsigned int length=au_length(t);
    if(p>length||count>length-p)return -1;
    if(!count&&!bytes)return 0;
    /* A redo tail ends with a new change. */
    if(h->current<h->count){
        if(h->saved>(long)h->current)h->saved=-1;
        h->count=h->current;h->used=h->count?h->changes[h->count-1].store+h->changes[h->count-1].removed+h->changes[h->count-1].inserted:0;
        h->typing=0;
    }
    struct au_change *last=h->count?&h->changes[h->count-1]:0;
    if(typing&&h->typing&&last&&!count&&!last->removed&&p==last->position+last->inserted&&
       last->store+last->inserted==h->used&&h->used+bytes<=h->store_capacity&&h->saved!=(long)h->current&&
       !(bytes==1&&text[0]=='\n')){
        if(au_replace_raw(t,p,0,text,bytes))return -1;
        move_bytes(h->store+h->used,text,bytes);h->used+=bytes;last->inserted+=bytes;last->caret_after=after;
        return 0;
    }
    if(typing)au_history_break(h);
    if(!make_room(h,count+bytes)){
        if(au_replace_raw(t,p,count,text,bytes))return -1;
        h->count=h->current=h->used=0;h->saved=-1;h->typing=0;h->group++;
        return 0;
    }
    unsigned int store=h->used;
    au_read(t,p,count,h->store+store);
    if(au_replace_raw(t,p,count,text,bytes))return -1;
    move_bytes(h->store+store+count,text,bytes);h->used+=count+bytes;
    h->changes[h->count++]=(struct au_change){p,count,bytes,store,before,after,h->group};
    h->current=h->count;h->typing=typing&&!(bytes==1&&text[0]=='\n');
    return 0;
}
int au_undo(struct au_text *t,struct au_history *h,unsigned int *caret){
    if(!h->current)return 0;
    unsigned int group=h->changes[h->current-1].group;
    while(h->current&&h->changes[h->current-1].group==group){
        struct au_change *c=&h->changes[--h->current];
        au_replace_raw(t,c->position,c->inserted,h->store+c->store,c->removed);
        if(caret)*caret=c->caret_before;
    }
    h->typing=0;return 1;
}
int au_redo(struct au_text *t,struct au_history *h,unsigned int *caret){
    if(h->current>=h->count)return 0;
    unsigned int group=h->changes[h->current].group;
    while(h->current<h->count&&h->changes[h->current].group==group){
        struct au_change *c=&h->changes[h->current++];
        au_replace_raw(t,c->position,c->removed,h->store+c->store+c->removed,c->inserted);
        if(caret)*caret=c->caret_after;
    }
    h->typing=0;return 1;
}

static int match_at(const struct au_text *t,unsigned int p,const char *needle,unsigned int bytes,int flags){
    for(unsigned int i=0;i<bytes;i++){
        char a=au_at(t,p+i),b=needle[i];
        if(flags&AU_CASE?a!=b:fold(a)!=fold(b))return 0;
    }
    if(flags&AU_WORD){
        if(p&&au_word_char(au_at(t,p-1)))return 0;
        if(p+bytes<au_length(t)&&au_word_char(au_at(t,p+bytes)))return 0;
    }
    return 1;
}
long au_find(const struct au_text *t,const char *needle,unsigned int bytes,unsigned int from,int forward,int flags){
    unsigned int n=au_length(t);
    if(!bytes||bytes>n)return -1;
    unsigned int last=n-bytes;
    if(forward){for(unsigned int p=from;p<=last;p++)if(match_at(t,p,needle,bytes,flags))return p;}
    else{unsigned int p=from>last+1?last+1:from;while(p>0){p--;if(match_at(t,p,needle,bytes,flags))return p;}}
    return -1;
}
unsigned int au_count(const struct au_text *t,const char *needle,unsigned int bytes,int flags,unsigned int limit,unsigned int position,unsigned int *index){
    unsigned int n=au_length(t),found=0;
    if(index)*index=0;
    if(!bytes||bytes>n)return 0;
    for(unsigned int p=0;p+bytes<=n&&found<limit;){
        if(match_at(t,p,needle,bytes,flags)){found++;if(index&&p==position)*index=found;p+=bytes;}
        else p++;
    }
    return found;
}

int au_check(const char *s,unsigned int n){
    for(unsigned int i=0;i<n;){
        unsigned char c=(unsigned char)s[i];
        if(!c)return AU_NOT_TEXT;
        if(c<0x80){i++;continue;}
        unsigned int need,value;
        if(c>=0xc2&&c<=0xdf){need=1;value=c&0x1f;}
        else if(c>=0xe0&&c<=0xef){need=2;value=c&0x0f;}
        else if(c>=0xf0&&c<=0xf4){need=3;value=c&0x07;}
        else return AU_NOT_UTF8;
        if(i+need>=n)return AU_NOT_UTF8;
        for(unsigned int k=1;k<=need;k++){unsigned char d=(unsigned char)s[i+k];if((d&0xc0)!=0x80)return AU_NOT_UTF8;value=value<<6|(d&0x3f);}
        if((need==2&&value<0x800)||(need==3&&(value<0x10000||value>0x10ffff))||(value>=0xd800&&value<=0xdfff))return AU_NOT_UTF8;
        i+=need+1;
    }
    return 0;
}
unsigned int au_normalize(char *s,unsigned int n,int *crlf,int *bom){
    unsigned int from=0,to=0,pairs=0,bare=0;
    *bom=n>=3&&(unsigned char)s[0]==0xef&&(unsigned char)s[1]==0xbb&&(unsigned char)s[2]==0xbf;
    if(*bom)from=3;
    while(from<n){
        if(s[from]=='\r'&&from+1<n&&s[from+1]=='\n'){pairs++;s[to++]='\n';from+=2;continue;}
        if(s[from]=='\n')bare++;
        s[to++]=s[from++];
    }
    *crlf=pairs>bare;
    return to;
}
unsigned int au_encode(const struct au_text *t,unsigned int *position,int crlf,char *out,unsigned int capacity){
    unsigned int n=au_length(t),p=*position,o=0;
    while(p<n&&o+2<=capacity){
        char c=au_at(t,p++);
        if(c=='\n'&&crlf)out[o++]='\r';
        out[o++]=c;
    }
    *position=p;return o;
}
int au_load(struct au_text *t,const char *s,unsigned int n){
    if(n>=t->capacity)return -1;
    unsigned int lines=1;for(unsigned int i=0;i<n;i++)lines+=s[i]=='\n';
    if(lines>t->line_capacity)return -1;
    move_bytes(t->data,s,n);t->gap_start=n;t->gap_end=t->capacity;
    t->line_count=1;t->lines[0]=0;
    for(unsigned int i=0;i<n;i++)if(s[i]=='\n')t->lines[t->line_count++]=i+1;
    return 0;
}
