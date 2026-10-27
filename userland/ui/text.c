#include "text.h"
static int rectangle(struct zui *u,struct zui_rect r){return r.x>=0&&r.y>=0&&r.w>0&&r.h>0&&(long)r.x+r.w<=u->width&&(long)r.y+r.h<=u->height;}
static void clip_inside(struct zui *u,struct zui_rect saved,struct zui_rect r){
    zui_clip(u,r);
    int x=u->clip.x>saved.x?u->clip.x:saved.x,y=u->clip.y>saved.y?u->clip.y:saved.y;
    int right=u->clip.x+u->clip.w<saved.x+saved.w?u->clip.x+u->clip.w:saved.x+saved.w;
    int bottom=u->clip.y+u->clip.h<saved.y+saved.h?u->clip.y+u->clip.h:saved.y+saved.h;
    zui_clip(u,(struct zui_rect){x,y,right>x?right-x:0,bottom>y?bottom-y:0});
}
static unsigned int previous(const struct zui_input *i,unsigned int p){if(p)p--;while(p&&((unsigned char)i->text[p]&0xc0)==0x80)p--;return p;}
static unsigned int next(const struct zui_input *i,unsigned int p){if(p<i->length)p++;while(p<i->length&&((unsigned char)i->text[p]&0xc0)==0x80)p++;return p;}
int zui_input_init(struct zui_input *i,char *text,unsigned int capacity){
    if(!i||!text||!capacity||capacity>ZUI_MAX_TEXT_BYTES)return 0;
    unsigned int n=0;while(n<capacity&&text[n])n++;if(n==capacity)return 0;
    *i=(struct zui_input){text,capacity,n,n};return 1;
}
int zui_input_event(struct zui_input *i,const struct zenith_gui_event *e){
    if(!i||!e||e->type!=Z_GUI_KEY)return 0;
    if(e->modifiers&6)return 0;
    unsigned int begin=i->cursor,end=begin;
    if(e->extra==5){i->cursor=previous(i,begin);return 1;}
    if(e->extra==6){i->cursor=next(i,begin);return 1;}
    if(e->extra==10){i->cursor=0;return 1;}
    if(e->extra==11){i->cursor=i->length;return 1;}
    if(e->extra==3)begin=previous(i,begin);
    else if(e->extra==9)end=next(i,end);
    else {
        unsigned int c=(unsigned int)e->code;char bytes[4];unsigned int n;
        if(e->extra||c<32||c==127||c>0x10ffff||(c>=0xd800&&c<=0xdfff))return 0;
        if(c<128){bytes[0]=(char)c;n=1;}
        else if(c<2048){bytes[0]=(char)(0xc0|(c>>6));bytes[1]=(char)(0x80|(c&63));n=2;}
        else if(c<65536){bytes[0]=(char)(0xe0|(c>>12));bytes[1]=(char)(0x80|((c>>6)&63));bytes[2]=(char)(0x80|(c&63));n=3;}
        else {bytes[0]=(char)(0xf0|(c>>18));bytes[1]=(char)(0x80|((c>>12)&63));bytes[2]=(char)(0x80|((c>>6)&63));bytes[3]=(char)(0x80|(c&63));n=4;}
        if(i->length+n>=i->capacity)return 0;
        for(unsigned int p=i->length+1;p>begin;p--)i->text[p+n-1]=i->text[p-1];
        for(unsigned int p=0;p<n;p++)i->text[begin+p]=bytes[p];
        i->length+=n;i->cursor+=n;return 1;
    }
    for(unsigned int p=end;p<=i->length;p++)i->text[begin+p-end]=i->text[p];
    i->length-=end-begin;i->cursor=begin;return 1;
}
void zui_input_draw(struct zui *u,struct zui_rect r,const struct zui_input *i,int focused){
    if(!rectangle(u,r)||!i)return;
    struct zui_rect saved=u->clip;
    zui_round(u,r,8,focused?u->theme.accent:u->theme.border);
    zui_round(u,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},7,u->theme.raised);
    clip_inside(u,saved,(struct zui_rect){r.x+8,r.y+3,r.w-16,r.h-6});
    int cursor=zui_measure(i->text,i->cursor,&zui_body),offset=cursor>r.w-24?cursor-r.w+24:0;
    zui_text(u,r.x+8-offset,r.y+(r.h-zui_body.height)/2,i->text,i->length,&zui_body,u->theme.text);
    if(focused)zui_fill(u,(struct zui_rect){r.x+8+cursor-offset,r.y+8,1,r.h-16},u->theme.text);
    u->clip=saved;
}
int zui_paragraph(struct zui *u,struct zui_rect r,const char *s,const struct zui_font *f,unsigned int color,int scroll){
    if(!s||!f||!rectangle(u,r))return 0;
    if(scroll<0)scroll=0;
    if(scroll>ZUI_MAX_TEXT_BYTES)scroll=ZUI_MAX_TEXT_BYTES;
    unsigned int n=0;while(n<ZUI_MAX_TEXT_BYTES&&s[n])n++;
    struct zui_rect saved=u->clip;clip_inside(u,saved,r);int line=0;
    for(unsigned int start=0;start<n;){
        unsigned int end=start,last=start,p=start;
        while(p<n&&s[p]!='\n'){
            unsigned int q=p+1;while(q<n&&((unsigned char)s[q]&0xc0)==0x80)q++;
            if(zui_measure(s+start,q-start,f)>r.w&&p>start)break;
            end=q;if(s[p]==' ')last=p;p=q;
        }
        if(p<n&&s[p]!='\n'&&last>start)end=last;
        if(end==start&&p==start&&s[p]!='\n')end=p+1;
        int y=r.y+(line-scroll)*(f->height+4);
        if(y+f->height>r.y&&y<r.y+r.h)zui_text(u,r.x,y,s+start,end-start,f,color);
        line++;start=end;
        while(start<n&&s[start]==' ')start++;
        if(start<n&&s[start]=='\n')start++;
    }
    u->clip=saved;return line;
}
