#include "ui.h"
static const unsigned int accents[2][6]={{0x8baaff,0xb6a0ff,0xf39db8,0xe7bb70,0x8bcba4,0x7ecac7},
    {0x3157b7,0x7042be,0xae325d,0x886010,0x277347,0x197571}};
static int min(int a,int b){return a<b?a:b;}
static int max(int a,int b){return a>b?a:b;}
static struct zui_rect intersect(struct zui_rect a,struct zui_rect b){
    long x=max(a.x,b.x),y=max(a.y,b.y);
    long right=(long)a.x+max(0,a.w),bottom=(long)a.y+max(0,a.h);
    long br=(long)b.x+max(0,b.w),bb=(long)b.y+max(0,b.h);
    if(br<right)right=br;
    if(bb<bottom)bottom=bb;
    if(right<=x||bottom<=y)return (struct zui_rect){0,0,0,0};
    return (struct zui_rect){(int)x,(int)y,(int)(right-x),(int)(bottom-y)};
}
int zui_init(struct zui *u,unsigned int *p,int w,int h){
    if(!u||!p||w<1||h<1||w>640||h>480)return 0;
    *u=(struct zui){0};u->pixels=p;u->width=w;u->height=h;
    u->clip=(struct zui_rect){0,0,w,h};u->focus=-1;u->pressed=-1;
    zui_appearance(u,0,0);return 1;
}
void zui_appearance(struct zui *u,int light,int accent){
    light=!!light;if(accent<0||accent>5)accent=0;
    u->theme=light?(struct zui_theme){0xfafafb,0xffffff,0xeff0f3,0xcfd1d8,0x202126,0x5c606b,accents[1][accent],0xffffff}:
        (struct zui_theme){0x202125,0x2a2c31,0x25262b,0x45474f,0xf1f2f4,0xb4b7c0,accents[0][accent],0x202125};
    u->dirty=1;
}
void zui_clip(struct zui *u,struct zui_rect r){u->clip=intersect(r,(struct zui_rect){0,0,u->width,u->height});}
void zui_fill(struct zui *u,struct zui_rect r,unsigned int c){
    r=intersect(r,u->clip);
    for(int y=r.y;y<r.y+r.h;y++)for(int x=r.x;x<r.x+r.w;x++)u->pixels[y*u->width+x]=c&0xffffff;
}
void zui_round(struct zui *u,struct zui_rect r,int radius,unsigned int c){
    if(r.w<=0||r.h<=0)return;
    radius=max(0,min(radius,min(r.w,r.h)/2));radius=min(radius,32);
    struct zui_rect b=intersect(r,u->clip);
    for(int y=b.y;y<b.y+b.h;y++)for(int x=b.x;x<b.x+b.w;x++){
        long px=(long)x-r.x,py=(long)y-r.y;
        long dx=px<radius?radius-1-px:(px>=r.w-radius?px-(r.w-radius):0);
        long dy=py<radius?radius-1-py:(py>=r.h-radius?py-(r.h-radius):0);
        if(dx*dx+dy*dy<=(long)radius*radius)u->pixels[y*u->width+x]=c&0xffffff;
    }
}
static unsigned int blend(unsigned int bg,unsigned int fg,unsigned int a){
    unsigned int out=0;
    for(int s=0;s<24;s+=8)out|=((((bg>>s)&255)*(255-a)+((fg>>s)&255)*a+127)/255)<<s;
    return out;
}
/* Consume a bounded UTF-8 sequence; every non-ASCII scalar maps to replacement. */
static unsigned int glyph(const char *s,unsigned long n,unsigned long *i){
    unsigned int c=(unsigned char)s[(*i)++];
    if(c<128)return c>=32&&c<=126?c:'?';
    int count=c>=0xc2&&c<=0xdf?1:(c>=0xe0&&c<=0xef?2:(c>=0xf0&&c<=0xf4?3:0));
    if(!count)return '?';
    unsigned int v=c&((1U<<(6-count))-1);
    if(n-*i<(unsigned long)count)return '?';
    for(int j=0;j<count;j++)if(((unsigned char)s[*i+j]&0xc0)!=0x80)return '?';
    for(int j=0;j<count;j++)v=(v<<6)|((unsigned char)s[(*i)++]&63);
    (void)v;return '?'; /* Current atlas is printable ASCII; do not claim Unicode fonts. */
}
int zui_measure(const char *s,unsigned long n,const struct zui_font *f){
    if(!s||!f)return 0;
    if(n>ZUI_MAX_TEXT_BYTES)n=ZUI_MAX_TEXT_BYTES;
    int width=0;for(unsigned long i=0;i<n&&s[i]&&width<16384;){unsigned int c=glyph(s,n,&i);width+=f->advance[c-32];}
    return width;
}
void zui_text(struct zui *u,int x,int y,const char *s,unsigned long n,const struct zui_font *f,unsigned int color){
    if(!s||!f||x>u->clip.x+u->clip.w||y>u->clip.y+u->clip.h)return;
    if(n>ZUI_MAX_TEXT_BYTES)n=ZUI_MAX_TEXT_BYTES;
    long pen=x;
    for(unsigned long i=0;i<n&&s[i]&&pen<u->clip.x+u->clip.w;){
        unsigned int c=glyph(s,n,&i)-32;
        for(int yy=0;yy<f->height;yy++)for(int xx=0;xx<f->width;xx++){
            long px=pen+xx,py=(long)y+yy;
            if(px<u->clip.x||py<u->clip.y||px>=u->clip.x+u->clip.w||py>=u->clip.y+u->clip.h)continue;
            unsigned int a=f->pixels[(c*f->height+yy)*f->width+xx];
            if(a){unsigned int *p=&u->pixels[py*u->width+px];*p=blend(*p,color,a);}
        }
        pen+=f->advance[c];
    }
}
int zui_add(struct zui *u,int id,int kind,struct zui_rect r,const char *label,int value,int enabled){
    if(u->count>=ZUI_MAX_CONTROLS||id<=0||kind<1||kind>3||!label||r.w<1||r.h<1||r.x<0||r.y<0||
       (long)r.x+r.w>u->width||(long)r.y+r.h>u->height)return 0;
    for(int i=0;i<u->count;i++)if(u->controls[i].id==id)return 0;
    u->controls[u->count++]=(struct zui_control){id,kind,!!enabled,!!value,r,label};u->dirty=1;return 1;
}
static int hit(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x-r.x<r.w&&y-r.y<r.h;}
int zui_event(struct zui *u,const struct zenith_gui_event *e){
    if(!e)return 0;
    if(e->type==Z_GUI_FOCUS){u->window_focused=!!e->code;u->pressed=-1;u->dirty=1;return 0;}
    if(e->type==Z_GUI_RESET||e->type==Z_GUI_CLOSE){u->pressed=-1;u->dirty=1;return 0;}
    if(!u->window_focused)return 0;
    if(e->type==Z_GUI_KEY&&e->extra==4&&!(e->modifiers&6)){
        int cursor=u->focus,found=-1;
        for(int k=0;k<u->count;k++){
            cursor=cursor<0?((e->modifiers&1)?u->count-1:0):(cursor+((e->modifiers&1)?u->count-1:1))%u->count;
            if(u->controls[cursor].enabled){found=cursor;break;}
        }
        u->focus=found;u->dirty=1;return 0;
    }
    if(e->type==Z_GUI_KEY&&!(e->modifiers&6)&&(e->extra==2||e->code==' ')&&u->focus>=0){
        struct zui_control *c=&u->controls[u->focus];return c->enabled?c->id:0;
    }
    if(e->type==Z_GUI_DOWN){
        u->pressed=-1;
        for(int i=u->count-1;i>=0;i--)if(u->controls[i].enabled&&hit(u->controls[i].rect,e->x,e->y)){
            u->focus=i;u->pressed=i;break;
        }
        u->dirty=1;
    }
    if(e->type==Z_GUI_UP){
        int p=u->pressed;u->pressed=-1;u->dirty=1;
        if(p>=0&&u->controls[p].enabled&&hit(u->controls[p].rect,e->x,e->y))return u->controls[p].id;
    }
    return 0;
}
static unsigned long length(const char *s){unsigned long n=0;while(n<256&&s[n])n++;return n;}
void zui_draw_range(struct zui *u,int first,int count){
    struct zui_rect saved=u->clip;
    if(first<0||count<0||first>u->count||count>u->count-first)return;
    for(int i=first;i<first+count;i++){
        struct zui_control *c=&u->controls[i];struct zui_rect r=c->rect;
        unsigned int fill=c->value?u->theme.accent:u->theme.raised;
        unsigned int fg=c->value?u->theme.on_accent:u->theme.text;
        if(!c->enabled){fill=u->theme.subtle;fg=u->theme.secondary;}
        if(u->window_focused&&u->focus==i&&c->enabled){
            zui_round(u,(struct zui_rect){r.x-3,r.y-3,r.w+6,r.h+6},10,u->theme.accent);
            zui_round(u,(struct zui_rect){r.x-2,r.y-2,r.w+4,r.h+4},9,u->theme.surface);
        }
        zui_round(u,r,8,u->theme.border);
        zui_round(u,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},7,u->pressed==i?u->theme.subtle:fill);
        if(u->pressed==i)fg=u->theme.text;
        zui_clip(u,intersect(saved,r));
        int tx=r.x+12;
        if(c->kind==ZUI_TOGGLE){
            zui_round(u,(struct zui_rect){r.x+r.w-48,r.y+(r.h-24)/2,40,24},12,c->value?u->theme.on_accent:u->theme.secondary);
            zui_round(u,(struct zui_rect){r.x+r.w-46+(c->value?16:0),r.y+(r.h-20)/2,20,20},10,fill);
            u->clip=intersect(u->clip,(struct zui_rect){r.x,r.y,r.w-56,r.h});
        }else tx=r.x+(r.w-zui_measure(c->label,length(c->label),&zui_label))/2;
        zui_text(u,tx,r.y+(r.h-zui_label.height)/2,c->label,length(c->label),&zui_label,fg);
        u->clip=saved;
    }
}

void zui_draw_controls(struct zui *u){zui_draw_range(u,0,u->count);}
