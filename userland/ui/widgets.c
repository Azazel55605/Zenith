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
    int over=u->hover_supported&&u->pointer_x>=s->rect.x+s->rect.w-12&&inside(s->rect,u->pointer_x,u->pointer_y);
    if(over||s->dragging){t.x-=2;t.w+=2;}
    if(t.h)zui_round_alpha(u,t,3,u->theme.secondary,s->dragging?220:over?190:140);
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
        /* One notch is a code of 1; a coalesced burst never jumps past a viewport. */
        long d=-(long)e->code*ZUI_WHEEL_STEP,limit=s->rect.h>ZUI_WHEEL_STEP?s->rect.h:ZUI_WHEEL_STEP;
        d=d>limit?limit:d<-limit?-limit:d;s->offset=clampi(s->offset+d,0,max);
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
static int row_h(const struct zui_list *l){return l->row_height>0?l->row_height:40;}
static unsigned long len256(const char *s){unsigned long n=0;while(s&&n<256&&s[n])n++;return n;}
void zui_list_draw(struct zui *u,struct zui_list *l,struct zui_rect r,int count,zui_list_source items,void *ctx){
    int h=row_h(l);l->count=count<0?0:count;
    if(l->selected>=l->count)l->selected=l->count?l->count-1:0;
    if(l->selected<0)l->selected=0;
    int top=zui_scroll_begin(u,&l->scroll,r);
    l->first_control=u->count;l->visible_controls=0;
    for(int i=l->scroll.offset/h;i<l->count;i++){
        int y=top+3+i*h;if(y>=r.y+r.h)break;
        struct zui_list_item it;if(!items(ctx,i,&it))continue;
        struct zui_rect row={r.x+3,y,r.w-15,h-4};
        if(!zui_add(u,l->base_id+i,ZUI_NAVIGATION,row,it.label?it.label:"",i==l->selected,it.enabled))continue;
        struct zui_control *c=&u->controls[u->count-1];
        c->icon=it.icon;c->detail=it.subtitle;c->no_chevron=!it.chevron;l->visible_controls++;
    }
    zui_scroll_end(u,&l->scroll,l->count?(int)((long)l->count*h+6>0x3fffffff?0x3fffffff:(long)l->count*h+6):0);
}
static int enabled_at(zui_list_source items,void *ctx,int i){struct zui_list_item it;return items(ctx,i,&it)&&it.enabled;}
static int lower(int c){return c>='A'&&c<='Z'?c+32:c;}
int zui_list_event(struct zui *u,struct zui_list *l,const struct zenith_gui_event *e,zui_list_source items,void *ctx){
    int h=row_h(l),old=l->selected;
    if(zui_scroll_event(u,&l->scroll,e))return 0;
    if(e->type==Z_GUI_UP){
        for(int i=0;i<l->visible_controls;i++){
            int c=l->first_control+i;if(c>=u->count)break;
            struct zui_rect area=zui_control_area(u,c);
            if(u->controls[c].enabled&&inside(area,e->x,e->y)){l->selected=u->controls[c].id-l->base_id;u->dirty=1;return l->selected+1;}
        }
        return 0;
    }
    if(e->type!=Z_GUI_KEY||(e->modifiers&6)||!l->count)return 0;
    if(e->extra==2)return enabled_at(items,ctx,l->selected)?l->selected+1:0;
    int step=e->extra==8?1:e->extra==7?-1:0,at=l->selected;
    if(e->extra==10){at=-1;step=1;}else if(e->extra==11){at=l->count;step=-1;}
    else if(e->extra==ZUI_KEY_PAGE_UP||e->extra==ZUI_KEY_PAGE_DOWN){
        int rows=l->scroll.rect.h/h-1;if(rows<1)rows=1;
        step=e->extra==ZUI_KEY_PAGE_UP?-1:1;at=l->selected+step*rows;
        at=at<0?0:at>=l->count?l->count-1:at;
        if(!enabled_at(items,ctx,at)){int i=at;while(i>=0&&i<l->count&&!enabled_at(items,ctx,i))i+=step;if(i>=0&&i<l->count)at=i;else at=l->selected;}
        l->selected=at;step=0;
    }
    if(step){
        for(int i=at+step;i>=0&&i<l->count;i+=step)if(enabled_at(items,ctx,i)){l->selected=i;break;}
    }else if(!e->extra&&e->code>32&&e->code<127){
        for(int k=1;k<=l->count;k++){
            int i=(l->selected+k)%l->count;struct zui_list_item it;
            if(items(ctx,i,&it)&&it.enabled&&it.label&&lower((unsigned char)it.label[0])==lower(e->code)){l->selected=i;break;}
        }
    }
    if(l->selected!=old){zui_scroll_reveal(&l->scroll,l->selected*h,h+6);u->dirty=1;}
    return 0;
}
void zui_combo_draw(struct zui *u,struct zui_combo *c,struct zui_rect f,int count,zui_list_source items,void *ctx){
    struct zui_list_item it={0};c->field=f;(void)count;
    const char *label=items(ctx,c->value,&it)&&it.label?it.label:"";
    if(zui_add(u,c->id,ZUI_BUTTON,f,label,0,1))u->controls[u->count-1].icon="chevron-down";
}
void zui_combo_popup(struct zui *u,struct zui_combo *c,int count,zui_list_source items,void *ctx){
    if(!c->open)return;
    struct zui_rect f=c->field;
    int rows=count<8?count:8,h=rows*40+14;
    int y=f.y+f.h+4<=u->height-h?f.y+f.h+4:f.y-4-h;
    if(y<0)y=0;
    c->panel=(struct zui_rect){f.x,y,f.w,h};
    struct zui_rect saved=u->clip;zui_clip(u,(struct zui_rect){0,0,u->width,u->height});
    zui_surface(u,c->panel,10,ZUI_SURFACE_CONTROL);
    zui_list_draw(u,&c->popup,(struct zui_rect){f.x+4,y+4,f.w-8,h-8},count,items,ctx);
    zui_update_hover(u);
    zui_draw_range(u,c->popup.first_control,c->popup.visible_controls);
    u->clip=saved;
}
int zui_combo_event(struct zui *u,struct zui_combo *c,const struct zenith_gui_event *e,zui_list_source items,void *ctx){
    if(!c->open){
        int focused=u->focus>=0&&u->focus<u->count&&u->controls[u->focus].id==c->id;
        if((e->type==Z_GUI_UP&&inside(c->field,e->x,e->y))||(e->type==Z_GUI_KEY&&e->extra==2&&focused)){
            /* Content height is unknown until the next draw; scroll_end clamps this. */
            c->open=1;c->popup.selected=c->value;c->popup.scroll.offset=c->value>3?(c->value-3)*40:0;u->dirty=1;
        }
        return 0;
    }
    if((e->type==Z_GUI_KEY&&e->extra==1)||(e->type==Z_GUI_UP&&!inside(c->panel,e->x,e->y))||e->type==Z_GUI_FOCUS){
        c->open=0;u->dirty=1;return 0;
    }
    int chosen=zui_list_event(u,&c->popup,e,items,ctx);
    if(chosen){c->value=chosen-1;c->open=0;u->dirty=1;return chosen;}
    return 0;
}
void zui_form_begin(struct zui_form *f,struct zui_rect a,int top,int window_width){
    f->area=a;f->top=top;f->y=top;f->narrow=window_width<720;
    f->w=a.w<640?a.w:640;if(f->w<1)f->w=1;
    f->x=a.x+(a.w-f->w)/2;f->label_w=f->narrow?0:f->w*2/5;
}
int zui_form_section(struct zui *u,struct zui_form *f,const char *title){
    if(f->y>f->top)f->y+=16;
    int at=f->y;
    zui_text(u,f->x,f->y,title,len256(title),&zui_label,u->theme.text);
    zui_fill(u,(struct zui_rect){f->x,f->y+26,f->w,1},u->theme.border);
    f->y+=40;return at;
}
struct zui_rect zui_form_row(struct zui *u,struct zui_form *f,const char *label,const char *help,int cw,int ch){
    int width=f->narrow?f->w:f->w-f->label_w-16;
    if(cw>width)cw=width;
    if(cw<1)cw=1;
    if(ch<1)ch=1;
    struct zui_rect r;
    if(f->narrow){
        zui_text(u,f->x,f->y,label,len256(label),&zui_label,u->theme.text);
        r=(struct zui_rect){f->x,f->y+24,cw,ch};f->y+=24+ch;
    }else{
        int lw=zui_measure(label,len256(label),&zui_label);
        zui_text(u,f->x+f->label_w-lw,f->y+(ch-zui_label.height)/2,label,len256(label),&zui_label,u->theme.text);
        r=(struct zui_rect){f->x+f->label_w+16,f->y,cw,ch};f->y+=ch;
    }
    if(help){zui_text(u,r.x,f->y+4,help,len256(help),&zui_caption,u->theme.secondary);f->y+=22;}
    f->y+=12;return r;
}
int zui_form_height(const struct zui_form *f,int top){return f->y-top+12;}
int zui_footer(struct zui *u,struct zui_rect b,int base,int help,int changed,int at_defaults){
    zui_fill(u,(struct zui_rect){b.x,b.y,b.w,1},u->theme.border);
    int y=b.y+(b.h-36)/2,x=b.x+b.w-12;
    x-=112;zui_add(u,base+ZUI_FOOTER_APPLY,ZUI_BUTTON,(struct zui_rect){x,y,112,36},"Apply",changed,changed);
    x-=120;zui_add(u,base+ZUI_FOOTER_RESET,ZUI_BUTTON,(struct zui_rect){x,y,112,36},"Reset",0,changed);
    x-=120;zui_add(u,base+ZUI_FOOTER_DEFAULTS,ZUI_BUTTON,(struct zui_rect){x,y,112,36},"Defaults",0,!at_defaults);
    if(help)zui_add(u,base+ZUI_FOOTER_HELP,ZUI_BUTTON,(struct zui_rect){b.x+12,y,96,36},"Help",0,1);
    return 56;
}
static int dialog_control(const struct zui *u,const struct zui_dialog *d,int index){
    return index>=0&&index<u->count&&u->controls[index].id>d->base_id&&u->controls[index].id<=d->base_id+d->count;
}
void zui_dialog_draw(struct zui *u,const struct zui_dialog *d){
    if(!d->open||d->count<1||d->count>3)return;
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});
    zui_round_alpha(u,(struct zui_rect){0,0,u->width,u->height},0,0x000000,120);
    int w=u->width<440?u->width-24:416,h=168,x=(u->width-w)/2,y=(u->height-h)/2;
    zui_surface(u,(struct zui_rect){x,y,w,h},12,ZUI_SURFACE_CONTROL);
    zui_text(u,x+24,y+20,d->title,len256(d->title),&zui_label,u->theme.text);
    zui_text(u,x+24,y+52,d->message,len256(d->message),&zui_body,u->theme.secondary);
    int bx=x+w-24;
    int first=u->count;
    for(int i=d->count-1;i>=0;i--){
        bx-=104;zui_add(u,d->base_id+i+1,ZUI_BUTTON,(struct zui_rect){bx,y+h-60,96,36},d->labels[i],i==0,1);bx-=8;
    }
    zui_update_hover(u);
    zui_draw_range(u,first,u->count-first);
    if(!dialog_control(u,d,u->focus))
        for(int i=0;i<u->count;i++)if(u->controls[i].id==d->base_id+1){u->focus=i;break;}
}
int zui_dialog_event(struct zui *u,struct zui_dialog *d,const struct zenith_gui_event *e){
    if(!d->open)return 0;
    if(e->type==Z_GUI_KEY&&e->extra==1){d->open=0;u->dirty=1;return d->count;}
    if(e->type==Z_GUI_KEY&&e->extra==4){
        int first=-1;
        for(int i=0;i<u->count;i++)if(dialog_control(u,d,i)){first=i;break;}
        if(first<0)return 0;
        int next=dialog_control(u,d,u->focus)?u->focus+((e->modifiers&1)?-1:1):first;
        if(next<first)next=first+d->count-1;
        if(next>=first+d->count)next=first;
        u->focus=next;u->focus_visible=1;u->dirty=1;return 0;
    }
    int id=zui_event(u,e);
    if(e->type==Z_GUI_DOWN&&!dialog_control(u,d,u->pressed)){
        u->pressed=-1;
        for(int i=0;i<u->count;i++)if(u->controls[i].id==d->base_id+1){u->focus=i;break;}
    }
    if(id>d->base_id&&id<=d->base_id+d->count){d->open=0;u->dirty=1;return id-d->base_id;}
    return 0;
}
