#include "widgets.h"
static int clampi(long v,int lo,int hi){return v<lo?lo:v>hi?hi:(int)v;}
static int inside(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x-r.x<r.w&&y-r.y<r.h;}
int zui_scroll_maximum(const struct zui_scroll *s){return s->content>s->rect.h?s->content-s->rect.h:0;}
static struct zui_rect thumb(const struct zui_scroll *s){
    int max=zui_scroll_maximum(s);if(!max)return (struct zui_rect){0,0,0,0};
    int h=clampi((long)s->rect.h*s->rect.h/s->content,24,s->rect.h);
    int y=s->rect.y+(int)((long)(s->rect.h-h)*s->offset/max);
    return (struct zui_rect){s->rect.x+s->rect.w-8,y,6,h};
}
int zui_scroll_begin(struct zui *u,struct zui_scroll *s,struct zui_rect r){
    s->rect=r;s->saved_clip=u->clip;
    long x0=r.x>u->clip.x?r.x:u->clip.x,y0=r.y>u->clip.y?r.y:u->clip.y;
    long x1=(long)r.x+r.w,y1=(long)r.y+r.h,cx1=(long)u->clip.x+u->clip.w,cy1=(long)u->clip.y+u->clip.h;
    if(x1>cx1)x1=cx1;
    if(y1>cy1)y1=cy1;
    u->clip=x1>x0&&y1>y0?(struct zui_rect){(int)x0,(int)y0,(int)(x1-x0),(int)(y1-y0)}:(struct zui_rect){0,0,0,0};
    return r.y-s->offset;
}
void zui_scroll_end(struct zui *u,struct zui_scroll *s,int content){
    s->content=content<0?0:content;s->offset=clampi(s->offset,0,zui_scroll_maximum(s));
    struct zui_rect t=thumb(s);
    if(t.h)zui_round_alpha(u,t,3,u->theme.secondary,s->dragging?220:140);
    u->clip=s->saved_clip;
}
void zui_scroll_reveal(struct zui_scroll *s,int top,int height){
    if(top<s->offset)s->offset=top;
    else if((long)top+height>(long)s->offset+s->rect.h)s->offset=top+height-s->rect.h;
    s->offset=clampi(s->offset,0,zui_scroll_maximum(s));
}
int zui_scroll_event(struct zui *u,struct zui_scroll *s,const struct zenith_gui_event *e){
    int old=s->offset,max=zui_scroll_maximum(s);
    if(e->type==Z_GUI_SCROLL&&inside(s->rect,e->x,e->y)&&e->code){
        long d=-(long)e->code/2;d=d>120?120:d<-120?-120:d;s->offset=clampi(s->offset+d,0,max);
    }else if(e->type==Z_GUI_DOWN&&max){
        struct zui_rect t=thumb(s),track={t.x,s->rect.y,t.w,s->rect.h};
        if(inside(t,e->x,e->y)){s->dragging=1;s->drag_y=e->y;s->drag_offset=s->offset;}
        else if(inside(track,e->x,e->y))s->offset=clampi((long)s->offset+(e->y<t.y?-s->rect.h:s->rect.h),0,max);
    }else if(e->type==Z_GUI_DRAG&&s->dragging){
        struct zui_rect t=thumb(s);int travel=s->rect.h-t.h;
        if(travel>0)s->offset=clampi(s->drag_offset+(long)(e->y-s->drag_y)*max/travel,0,max);
    }else if(e->type==Z_GUI_UP||e->type==Z_GUI_RESET||e->type==Z_GUI_FOCUS)s->dragging=0;
    if(s->offset!=old)u->dirty=1;
    return s->offset!=old;
}
