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
void zui_begin_material_frame(struct zui *u){
    u->foreground=1;u->material_count=0;
    for(int i=0;i<u->width*u->height;i++)u->pixels[i]=0;
}
int zui_material_surface(struct zui *u,struct zui_rect r,int radius,int role,int effect){
    if(!u->foreground||u->material_count>=8||!zui_backdrop_candidate(role)||effect<0||effect>2||
       radius<0||radius>24||r.x<0||r.y<0||r.w<1||r.h<1||(long)r.x+r.w>u->width||(long)r.y+r.h>u->height||radius>min(r.w,r.h)/2)return 0;
    long area=(long)r.w*r.h;
    for(int i=0;i<u->material_count;i++){
        struct zenith_material_region *p=&u->materials[i];area+=(long)p->w*p->h;
        if(r.x<p->x+p->w&&p->x<r.x+r.w&&r.y<p->y+p->h&&p->y<r.y+r.h)return 0;
    }
    if(area>131072)return 0;
    u->materials[u->material_count++]=(struct zenith_material_region){r.x,r.y,r.w,r.h,radius,role,effect,0};return 1;
}
static unsigned int over(unsigned int bg,unsigned int fg,unsigned int a){
    unsigned int out=(a+((bg>>24)*(255-a)+127)/255)<<24;
    for(int s=0;s<24;s+=8)out|=(((((fg>>s)&255)*a+127)/255)+((((bg>>s)&255)*(255-a)+127)/255))<<s;
    return out;
}
void zui_appearance(struct zui *u,int light,int accent){
    light=!!light;if(accent<0||accent>5)accent=0;
    u->theme=light?(struct zui_theme){0xfafafb,0xffffff,0xeff0f3,0xcfd1d8,0x202126,0x5c606b,accents[1][accent],0xffffff}:
        (struct zui_theme){0x202125,0x2a2c31,0x25262b,0x45474f,0xf1f2f4,0xb4b7c0,accents[0][accent],0x202125};
    for(int i=0;i<6;i++)u->swatches[i]=accents[light][i];
    u->dirty=1;
}
void zui_clip(struct zui *u,struct zui_rect r){u->clip=intersect(r,(struct zui_rect){0,0,u->width,u->height});}
void zui_fill(struct zui *u,struct zui_rect r,unsigned int c){
    r=intersect(r,u->clip);
    for(int y=r.y;y<r.y+r.h;y++)for(int x=r.x;x<r.x+r.w;x++)u->pixels[y*u->width+x]=(c&0xffffff)|(u->foreground?0xff000000:0);
}
static unsigned int blend(unsigned int bg,unsigned int fg,unsigned int a);
/* 4x4 subpixel coverage, integer-only for the freestanding no-SSE client.
 * Only corner pixels need sampling; geometry is symmetric around pixel centers. */
static unsigned int corner_coverage(int x,int y,int radius){
    int hits=0,rr=radius*8;
    for(int sy=1;sy<8;sy+=2)for(int sx=1;sx<8;sx+=2){
        int dx=rr-(x*8+sx),dy=rr-(y*8+sy);
        if(dx*dx+dy*dy<=rr*rr)hits++;
    }
    return (unsigned int)(hits*255+8)/16;
}
void zui_round(struct zui *u,struct zui_rect r,int radius,unsigned int c){
    if(r.w<=0||r.h<=0)return;
    radius=max(0,min(radius,min(r.w,r.h)/2));radius=min(radius,32);
    struct zui_rect b=intersect(r,u->clip);c&=0xffffff;
    for(int y=b.y;y<b.y+b.h;y++)for(int x=b.x;x<b.x+b.w;x++){
        long px=(long)x-r.x,py=(long)y-r.y;
        long cx=px<radius?px:(px>=r.w-radius?r.w-1-px:radius);
        long cy=py<radius?py:(py>=r.h-radius?r.h-1-py:radius);
        unsigned int coverage=cx<radius&&cy<radius?corner_coverage((int)cx,(int)cy,radius):255;
        unsigned int *pixel=&u->pixels[y*u->width+x];
        if(coverage==255)*pixel=c|(u->foreground?0xff000000:0);
        else if(coverage)*pixel=u->foreground?over(*pixel,c,coverage):blend(*pixel,c,coverage);
    }
}
int zui_backdrop_candidate(int role){
    return role==ZUI_SURFACE_NAVIGATION||role==ZUI_SURFACE_PANEL||role==ZUI_SURFACE_POPOVER;
}
int zui_surface(struct zui *u,struct zui_rect r,int radius,int role){
    unsigned int color;
    if(u->foreground&&zui_backdrop_candidate(role)&&zui_material_surface(u,r,radius,role,2)){
        struct zui_rect clear=intersect(r,u->clip);
        for(int y=clear.y;y<clear.y+clear.h;y++)for(int x=clear.x;x<clear.x+clear.w;x++)u->pixels[y*u->width+x]=0;
        return 1;
    }
    switch(role){
        case ZUI_SURFACE_CONTENT:color=u->theme.surface;break;
        case ZUI_SURFACE_CONTROL:case ZUI_SURFACE_PANEL:case ZUI_SURFACE_POPOVER:color=u->theme.raised;break;
        case ZUI_SURFACE_NAVIGATION:color=u->theme.subtle;break;
        default:return 0;
    }
    /* ABI 1 remains flattened opaque RGB. Geometry is supplied independently
     * of future material capability/effective preference and never changes here. */
    zui_round(u,r,radius,color);return 1;
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
            if(a){unsigned int *p=&u->pixels[py*u->width+px];*p=u->foreground?over(*p,color,a):blend(*p,color,a);}
        }
        pen+=f->advance[c];
    }
}
int zui_add(struct zui *u,int id,int kind,struct zui_rect r,const char *label,int value,int enabled){
    if(u->count>=ZUI_MAX_CONTROLS||id<=0||kind<1||kind>ZUI_NAVIGATION||!label||r.w<1||r.h<1||r.x<0||r.y<0||
       (long)r.x+r.w>u->width||(long)r.y+r.h>u->height)return 0;
    for(int i=0;i<u->count;i++)if(u->controls[i].id==id)return 0;
    u->controls[u->count++]=(struct zui_control){id,kind,!!enabled,!!value,0,r,label};u->dirty=1;return 1;
}
int zui_add_swatch(struct zui *u,int id,struct zui_rect r,const char *label,int accent,int selected,int enabled){
    if(accent<0||accent>5||r.w!=r.h||r.w<24||r.w>48)return 0;
    if(!zui_add(u,id,ZUI_SWATCH,r,label,selected,enabled))return 0;
    u->controls[u->count-1].swatch=accent;return 1;
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
    if(e->type==Z_GUI_KEY&&!(e->modifiers&7)&&(e->extra==5||e->extra==6)&&u->focus>=0&&u->focus<u->count){
        int kind=u->controls[u->focus].kind;
        if(kind==ZUI_SEGMENT||kind==ZUI_SWATCH){
            int step=e->extra==5?-1:1,at=u->focus;
            for(int k=0;k<u->count;k++){
                at=(at+step+u->count)%u->count;
                if(u->controls[at].kind==kind&&u->controls[at].enabled){u->focus=at;u->dirty=1;return u->controls[at].id;}
            }
        }
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
unsigned int zui_accent_color(const struct zui *u,int index){
    return u->swatches[index>=0&&index<6?index:0];
}
void zui_swatch_caption(struct zui *u,int x,int y){
    const char *label=0;
    for(int i=0;i<u->count;i++)if(u->controls[i].kind==ZUI_SWATCH&&u->controls[i].value)label=u->controls[i].label;
    if(u->window_focused&&u->focus>=0&&u->focus<u->count&&u->controls[u->focus].kind==ZUI_SWATCH)label=u->controls[u->focus].label;
    if(label)zui_text(u,x,y,label,length(label),&zui_caption,u->theme.secondary);
}
void zui_segment_track(struct zui *u,struct zui_rect r){zui_round(u,r,8,u->theme.subtle);}
void zui_setting_row(struct zui *u,struct zui_rect r,const char *label,const char *help){
    struct zui_rect saved=u->clip;zui_clip(u,intersect(saved,r));
    if(!u->clip.w||!u->clip.h){u->clip=saved;return;}
    zui_text(u,r.x,r.y,label,length(label),&zui_label,u->theme.text);
    if(help)zui_text(u,r.x,r.y+24,help,length(help),&zui_caption,u->theme.secondary);
    u->clip=saved;
}
/* One bounded outline family; checks and chevrons do not depend on Unicode glyphs. */
static void check(struct zui *u,int x,int y,unsigned int c){
    for(int i=0;i<5;i++)zui_fill(u,(struct zui_rect){x+i,y+i,2,2},c);
    for(int i=0;i<9;i++)zui_fill(u,(struct zui_rect){x+4+i,y+4-i,2,2},c);
}
void zui_draw_range(struct zui *u,int first,int count){
    struct zui_rect saved=u->clip;
    if(first<0||count<0||first>u->count||count>u->count-first)return;
    for(int i=first;i<first+count;i++){
        struct zui_control *c=&u->controls[i];struct zui_rect r=c->rect;
        int selected=c->value,pressed=u->pressed==i;
        unsigned int fill=selected?u->theme.accent:u->theme.raised;
        unsigned int fg=selected?u->theme.on_accent:u->theme.text;
        if(!c->enabled){fill=u->theme.subtle;fg=u->theme.secondary;}
        int radius=c->kind==ZUI_SWATCH?r.w/2:8;
        if(u->window_focused&&u->focus==i&&c->enabled){
            zui_round(u,(struct zui_rect){r.x-3,r.y-3,r.w+6,r.h+6},radius+3,u->theme.accent);
            zui_round(u,(struct zui_rect){r.x-2,r.y-2,r.w+4,r.h+4},radius+2,u->theme.surface);
        }
        if(c->kind==ZUI_SWATCH){
            unsigned int color=zui_accent_color(u,c->swatch);
            if(!c->enabled)color=blend(u->theme.surface,color,100);
            zui_round(u,r,radius,pressed?blend(color,u->theme.text,40):color);
            if(selected)check(u,r.x+(r.w-14)/2,r.y+r.h/2,u->theme.on_accent);
            continue;
        }
        if(c->kind==ZUI_SEGMENT){
            fill=selected?u->theme.raised:u->theme.subtle;fg=c->enabled?u->theme.text:u->theme.secondary;
            if(selected){zui_round(u,r,6,u->theme.border);zui_round(u,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},5,fill);}
            else zui_round(u,r,6,pressed?u->theme.raised:fill);
        }else if(c->kind==ZUI_NAVIGATION){
            fill=selected?blend(u->theme.subtle,u->theme.accent,32):u->theme.subtle;
            if(selected||pressed)zui_round(u,r,6,pressed?u->theme.raised:fill);
            fg=c->enabled?u->theme.text:u->theme.secondary;
            if(selected)zui_round(u,(struct zui_rect){r.x+1,r.y+7,3,r.h-14},1,u->theme.accent);
        }else if(c->kind==ZUI_TOGGLE){
            /* A settings row, not a selected accent-filled button. */
            fg=c->enabled?u->theme.text:u->theme.secondary;
            int x=r.x+r.w-48,y=r.y+(r.h-26)/2;
            zui_round(u,(struct zui_rect){x,y,48,26},13,selected&&c->enabled?u->theme.accent:u->theme.border);
            zui_round(u,(struct zui_rect){x+4+(selected?22:0),y+4,18,18},9,selected&&c->enabled?u->theme.on_accent:u->theme.raised);
        }else{
            zui_round(u,r,8,c->enabled?u->theme.border:u->theme.subtle);
            zui_round(u,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},7,pressed?u->theme.subtle:fill);
            if(pressed)fg=u->theme.text;
        }
        zui_clip(u,intersect(saved,r));
        int tx=r.x+(r.w-zui_measure(c->label,length(c->label),&zui_label))/2;
        if(c->kind==ZUI_TOGGLE||c->kind==ZUI_NAVIGATION){
            tx=r.x+(c->kind==ZUI_NAVIGATION?12:0);
            u->clip=intersect(u->clip,(struct zui_rect){r.x,r.y,r.w-(c->kind==ZUI_TOGGLE?60:24),r.h});
        }
        zui_text(u,tx,r.y+(r.h-zui_label.height)/2,c->label,length(c->label),&zui_label,fg);
        u->clip=saved;
        if(c->kind==ZUI_NAVIGATION){
            for(int k=0;k<5;k++){
                zui_fill(u,(struct zui_rect){r.x+r.w-16+k,r.y+r.h/2-4+k,1,1},u->theme.secondary);
                zui_fill(u,(struct zui_rect){r.x+r.w-16+k,r.y+r.h/2+4-k,1,1},u->theme.secondary);
            }
        }
    }
}
void zui_draw_controls(struct zui *u){zui_draw_range(u,0,u->count);}
