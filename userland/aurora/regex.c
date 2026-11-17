/* Aurora's regular expressions (regex.h): parsed into a small node graph, matched by bounded backtracking. */
#include "regex.h"

enum { N_END=0xffff };
enum { T_CHAR=1,T_ANY,T_SET,T_BOL,T_EOL,T_WORDB,T_NWORDB,T_GROUP,T_BRANCH };
#define STEPS 2000000

static char fold(char c){return c>='A'&&c<='Z'?(char)(c+32):c;}
static int word(char c){return (c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||(unsigned char)c>=0x80;}

/* ---------- Parser. */
struct parser { struct rx *rx; const char *p,*end; };
static int node(struct rx *rx,int type){
    if(rx->count>=RX_NODES){rx->error="the expression is too long";return -1;}
    struct rx_node *n=&rx->nodes[rx->count];*n=(struct rx_node){(unsigned char)type,0,0,0,1,1,N_END,N_END,N_END,0};return rx->count++;
}
static int new_set(struct rx *rx){
    if(rx->sets_used>=32){rx->error="too many [...] sets";return -1;}
    for(int i=0;i<32;i++)rx->sets[rx->sets_used][i]=0;
    return rx->sets_used++;
}
static void set_add(struct rx *rx,int s,unsigned char c){
    rx->sets[s][c>>3]|=(unsigned char)(1<<(c&7));
    if(rx->flags&RX_ICASE){unsigned char o=c>='a'&&c<='z'?(unsigned char)(c-32):c>='A'&&c<='Z'?(unsigned char)(c+32):c;rx->sets[s][o>>3]|=(unsigned char)(1<<(o&7));}
}
static void set_class(struct rx *rx,int s,char k){
    for(int c=0;c<256;c++){
        int in=k=='d'?(c>='0'&&c<='9'):k=='w'?word((char)c):k=='s'?(c==' '||c=='\t'||c=='\r'||c=='\f'||c=='\v'):0;
        if(in)set_add(rx,s,(unsigned char)c);
    }
}
static int parse_alternatives(struct parser *ps,int group);
static int parse_atom(struct parser *ps){
    struct rx *rx=ps->rx;char c=*ps->p++;int n;
    if(c=='('){
        int capture=1;
        if(ps->p+1<ps->end&&ps->p[0]=='?'&&ps->p[1]==':'){capture=0;ps->p+=2;}
        n=node(rx,T_GROUP);if(n<0)return -1;
        rx->nodes[n].group=(unsigned char)(capture&&rx->groups<RX_GROUPS?rx->groups++:255);
        if(parse_alternatives(ps,n)<0)return -1;
        if(ps->p>=ps->end||*ps->p!=')'){rx->error="a ( has no )";return -1;}
        ps->p++;return n;
    }
    if(c=='['){
        n=node(rx,T_SET);if(n<0)return -1;int s=new_set(rx);if(s<0)return -1;rx->nodes[n].set=(unsigned short)s;
        if(ps->p<ps->end&&*ps->p=='^'){rx->nodes[n].negate=1;ps->p++;}
        int first=1;
        while(ps->p<ps->end&&(*ps->p!=']'||first)){
            first=0;unsigned char a=(unsigned char)*ps->p++;
            if(a=='\\'&&ps->p<ps->end){char e=*ps->p++;
                if(e=='d'||e=='w'||e=='s'){set_class(rx,s,e);continue;}
                a=(unsigned char)(e=='t'?'\t':e=='n'?'\n':e);}
            if(ps->p+1<ps->end&&*ps->p=='-'&&ps->p[1]!=']'){
                ps->p++;unsigned char b=(unsigned char)*ps->p++;
                if(b=='\\'&&ps->p<ps->end)b=(unsigned char)*ps->p++;
                if(b<a){rx->error="a range runs backwards";return -1;}
                for(unsigned int x=a;x<=b;x++)set_add(rx,s,(unsigned char)x);
            }else set_add(rx,s,a);
        }
        if(ps->p>=ps->end){rx->error="a [ has no ]";return -1;}
        ps->p++;return n;
    }
    if(c=='.')return node(rx,T_ANY);
    if(c=='^')return node(rx,T_BOL);
    if(c=='$')return node(rx,T_EOL);
    if(c=='*'||c=='+'||c=='?'||c=='{'){rx->error="nothing to repeat";return -1;}
    if(c=='\\'){
        if(ps->p>=ps->end){rx->error="a \\ ends the expression";return -1;}
        char e=*ps->p++;
        if(e=='b')return node(rx,T_WORDB);
        if(e=='B')return node(rx,T_NWORDB);
        if(e=='d'||e=='w'||e=='s'||e=='D'||e=='W'||e=='S'){
            n=node(rx,T_SET);if(n<0)return -1;int s=new_set(rx);if(s<0)return -1;
            rx->nodes[n].set=(unsigned short)s;set_class(rx,s,fold(e));rx->nodes[n].negate=e>='A'&&e<='Z';return n;
        }
        c=e=='t'?'\t':e=='n'?'\n':e;
    }
    n=node(rx,T_CHAR);if(n<0)return -1;rx->nodes[n].set=(unsigned char)c;return n;
}
static int number(struct parser *ps,int *value){
    int v=0,digits=0;while(ps->p<ps->end&&*ps->p>='0'&&*ps->p<='9'&&digits<5){v=v*10+(*ps->p++-'0');digits++;}
    *value=v;return digits;
}
/* A sequence of atoms up to | or ) or the end; returns its first node (N_END when empty). */
static int parse_sequence(struct parser *ps){
    struct rx *rx=ps->rx;int first=N_END,last=-1;
    while(ps->p<ps->end&&*ps->p!='|'&&*ps->p!=')'){
        int n=parse_atom(ps);if(n<0)return -1;
        struct rx_node *a=&rx->nodes[n];
        if(ps->p<ps->end&&(*ps->p=='*'||*ps->p=='+'||*ps->p=='?'||*ps->p=='{')){
            if(a->type==T_BOL||a->type==T_EOL||a->type==T_WORDB||a->type==T_NWORDB){rx->error="an anchor cannot repeat";return -1;}
            char q=*ps->p++;
            if(q=='*'){a->min=0;a->max=N_END;}
            else if(q=='+'){a->min=1;a->max=N_END;}
            else if(q=='?'){a->min=0;a->max=1;}
            else{
                int lo,hi;if(!number(ps,&lo)){rx->error="{ needs a number";return -1;}
                hi=lo;
                if(ps->p<ps->end&&*ps->p==','){ps->p++;if(!number(ps,&hi))hi=N_END;}
                if(ps->p>=ps->end||*ps->p!='}'||hi<lo||lo>1000){rx->error="a bad {n,m}";return -1;}
                ps->p++;a->min=(unsigned short)lo;a->max=(unsigned short)hi;
            }
            if(ps->p<ps->end&&*ps->p=='?'){a->lazy=1;ps->p++;}
        }
        if(last<0)first=n;else rx->nodes[last].next=(unsigned short)n;
        last=n;
    }
    return first;
}
/* The branches of a group: each a T_BRANCH whose child is its sequence. */
static int parse_alternatives(struct parser *ps,int group){
    struct rx *rx=ps->rx;int last=-1;
    for(;;){
        int b=node(rx,T_BRANCH);if(b<0)return -1;
        int s=parse_sequence(ps);if(s<0&&rx->error)return -1;
        rx->nodes[b].child=(unsigned short)s;
        if(last<0)rx->nodes[group].child=(unsigned short)b;else rx->nodes[last].alt=(unsigned short)b;
        last=b;
        if(ps->p<ps->end&&*ps->p=='|'){ps->p++;continue;}
        return 0;
    }
}
int rx_compile(struct rx *rx,const char *pattern,unsigned int bytes,int flags){
    rx->count=0;rx->sets_used=0;rx->groups=1;rx->flags=flags;rx->error=0;
    struct parser ps={rx,pattern,pattern+bytes};
    int g=node(rx,T_GROUP);if(g<0)return -1;
    rx->nodes[g].group=0;rx->start=g;
    if(parse_alternatives(&ps,g)<0)return -1;
    if(ps.p<ps.end){rx->error="a ) has no (";return -1;}
    return 0;
}

/* ---------- Matcher: continuation-passing backtracking. A continuation is what follows a group (its next node
 * and, for a repeated group, another round). */
struct cont { int node; const struct cont *up; int group,count; unsigned int start; };
struct state { const struct rx *rx; const struct au_text *t; unsigned int length; long *groups; long steps; };
static long match(struct state *s,int n,unsigned int p,const struct cont *k);
static int one(struct state *s,const struct rx_node *a,unsigned int p){
    if(p>=s->length)return 0;
    char c=au_at(s->t,p);if(c=='\n')return 0;
    if(a->type==T_ANY)return 1;
    if(a->type==T_CHAR){char x=(char)a->set;return s->rx->flags&RX_ICASE?fold(c)==fold(x):c==x;}
    int in=(s->rx->sets[a->set][(unsigned char)c>>3]>>((unsigned char)c&7))&1;
    return a->negate?!in:in;
}
static long group_round(struct state *s,int g,unsigned int p,const struct cont *k,int count);
/* After a group's branch: close the capture, then repeat or continue. */
static long close_group(struct state *s,const struct cont *k,unsigned int p){
    const struct rx_node *g=&s->rx->nodes[k->node];
    long saved=0;
    if(g->group<RX_GROUPS){saved=s->groups[2*g->group+1];s->groups[2*g->group+1]=p;}
    long r;
    if(k->count<g->max&&p!=k->start){
        /* Greedy: one more round first; lazy: the rest first. */
        if(!g->lazy){r=group_round(s,k->node,p,k->up,k->count);if(r>=0)return r;}
        if(k->count>=g->min){r=match(s,g->next,p,k->up);if(r>=0)return r;}
        if(g->lazy){r=group_round(s,k->node,p,k->up,k->count);if(r>=0)return r;}
    }else if(k->count>=g->min){r=match(s,g->next,p,k->up);if(r>=0)return r;}
    if(g->group<RX_GROUPS)s->groups[2*g->group+1]=saved;
    return -1;
}
/* One more pass through group g (count passes done so far). */
static long group_round(struct state *s,int g,unsigned int p,const struct cont *k,int count){
    const struct rx_node *a=&s->rx->nodes[g];
    long saved=0;if(a->group<RX_GROUPS){saved=s->groups[2*a->group];s->groups[2*a->group]=p;}
    struct cont here={g,k,a->group,count+1,p};
    for(int b=a->child;b!=N_END;b=s->rx->nodes[b].alt){
        long r=match(s,s->rx->nodes[b].child,p,&here);
        if(r>=0)return r;
    }
    if(a->group<RX_GROUPS)s->groups[2*a->group]=saved;
    return -1;
}
static long match(struct state *s,int n,unsigned int p,const struct cont *k){
    if(++s->steps>STEPS)return -2;
    if(n==N_END){if(!k)return p;return close_group(s,k,p);}
    const struct rx_node *a=&s->rx->nodes[n];
    switch(a->type){
    case T_BOL: return p==0||au_at(s->t,p-1)=='\n'?match(s,a->next,p,k):-1;
    case T_EOL: return p>=s->length||au_at(s->t,p)=='\n'?match(s,a->next,p,k):-1;
    case T_WORDB: case T_NWORDB:{
        int before=p>0&&word(au_at(s->t,p-1)),after=p<s->length&&word(au_at(s->t,p));
        return (before!=after)==(a->type==T_WORDB)?match(s,a->next,p,k):-1;
    }
    case T_GROUP:
        if(a->min==0&&a->lazy){long r=match(s,a->next,p,k);if(r!=-1)return r;}
        {long r=group_round(s,n,p,k,0);if(r!=-1)return r;}
        if(a->min==0&&!a->lazy)return match(s,a->next,p,k);
        return -1;
    default:{
        /* A single-character atom: count how far it can repeat, then try counts in the preferred order. */
        unsigned int most=0;while(most<a->max&&one(s,a,p+most))most++;
        if(most<a->min)return -1;
        if(a->lazy){for(unsigned int c=a->min;c<=most;c++){long r=match(s,a->next,p+c,k);if(r!=-1)return r;}}
        else{for(unsigned int c=most+1;c-->a->min;){long r=match(s,a->next,p+c,k);if(r!=-1)return r;}}
        return -1;
    }
    }
}
/* One search shares one step budget across all its start positions. */
static long try_at(const struct rx *rx,const struct au_text *t,unsigned int p,unsigned int *end,long *groups,long *steps){
    long g[2*RX_GROUPS];for(int i=0;i<2*RX_GROUPS;i++)g[i]=-1;
    struct state s={rx,t,au_length(t),g,*steps};
    long r=group_round(&s,rx->start,p,0,0);
    *steps=s.steps;
    if(r<0)return -1;
    *end=(unsigned int)r;
    if(groups)for(int i=0;i<2*RX_GROUPS;i++)groups[i]=g[i];
    return p;
}
long rx_search(const struct rx *rx,const struct au_text *t,unsigned int from,unsigned int limit,unsigned int *end,long *groups){
    unsigned int n=au_length(t);if(limit>n)limit=n;
    long steps=0;
    for(unsigned int p=from;p<=limit&&steps<=STEPS;p++){if(try_at(rx,t,p,end,groups,&steps)>=0)return p;if(p==n)break;}
    return -1;
}
long rx_search_back(const struct rx *rx,const struct au_text *t,unsigned int before,unsigned int *end,long *groups){
    unsigned int n=au_length(t);if(before>n+1)before=n+1;
    long steps=0;
    for(unsigned int p=before;p-->0&&steps<=STEPS;)if(try_at(rx,t,p,end,groups,&steps)>=0)return p;
    return -1;
}
unsigned int rx_expand(const struct au_text *t,const char *r,unsigned int bytes,const long *groups,char *out,unsigned int capacity){
    unsigned int o=0;
    for(unsigned int i=0;i<bytes;i++){
        if(r[i]=='$'&&i+1<bytes&&r[i+1]>='0'&&r[i+1]<='9'){
            int g=r[++i]-'0';
            if(groups[2*g]>=0&&groups[2*g+1]>=groups[2*g]){
                unsigned int n=(unsigned int)(groups[2*g+1]-groups[2*g]);if(o+n>capacity)return 0;
                o+=au_read(t,(unsigned int)groups[2*g],n,out+o);
            }
            continue;
        }
        if(r[i]=='$'&&i+1<bytes&&r[i+1]=='$')i++;
        if(o+1>capacity)return 0;
        out[o++]=r[i];
    }
    return o;
}
