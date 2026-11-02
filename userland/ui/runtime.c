#include "ui.h"
#include "../include/zenith_settings.h"
#include "../include/zenith_system_settings.h"
/* Compiler-emitted structure initialization/copy helpers for freestanding clients. */
void *memset(void *destination,int value,unsigned long size){
    unsigned char *p=destination;for(unsigned long i=0;i<size;i++)p[i]=(unsigned char)value;return destination;
}
void *memcpy(void *destination,const void *source,unsigned long size){
    unsigned char *d=destination;const unsigned char *s=source;
    for(unsigned long i=0;i<size;i++)d[i]=s[i];
    return destination;
}
/* Package-installed icons: read one bounded root-owned file through the native ABI. */
static long package_icon(const char *name,unsigned char *out,unsigned long capacity){
    char path[80]="/usr/share/zenith/icons/";unsigned long n=24;
    for(unsigned long i=0;name[i];i++)if(n<sizeof(path)-5)path[n++]=name[i];
    path[n++]='.';path[n++]='z';path[n++]='i';path[n++]='c';path[n]=0;
    long fd=zenith_call(2,(unsigned long)path,n,0);
    if(fd<0)return fd;
    unsigned long total=0;long r=0;
    while(total<capacity){
        unsigned long chunk=capacity-total>4096?4096:capacity-total;
        r=zenith_call(0,(unsigned long)fd,(unsigned long)(out+total),chunk);
        if(r<=0)break;
        total+=(unsigned long)r;
    }
    zenith_call(3,(unsigned long)fd,0,0);
    return r<0?r:(long)total;
}
long zui_motion_clock(void){return zenith_call(628,0,0,0);}
void zui_icons_use_packages(void){zui_icon_source(package_icon);}
int zui_apply_appearance(struct zui *u){
    struct zenith_appearance a={0};
    if(zenith_appearance_read(&a)||a.version!=1)return 0;
    unsigned int surface=u->theme.surface,accent=u->theme.accent;
    zui_appearance(u,(int)a.active_mode,(int)a.active_accent);
    if(surface==u->theme.surface&&accent==u->theme.accent)return 0;
    u->dirty=1;return 1;
}
int zui_apply_motion(struct zui *u,long clock){
    static struct zenith_system_settings settings;
    if(clock<0||zenith_system_settings_read(&settings))return 0;
    int percent=zui_motion_percent(settings.version==2?settings.animation:0);
    if(percent==u->motion_percent)return 0;
    u->motion_percent=percent;return 1;
}
int zui_apply_viewport(struct zui *u,long handle,unsigned long capacity){
    struct zenith_viewport v={0};
    if(zenith_gui_viewport(handle,&v)||v.version!=1||!(v.flags&1)||v.width<600||v.height<480||v.width>1920||v.height>1000)return -1;
    if(v.width==(unsigned int)u->width&&v.height==(unsigned int)u->height)return 0;
    long r=zenith_gui_resize_accept(handle,&v);
    if(r==-11)return 0;
    if(r||!zui_resize(u,(int)v.width,(int)v.height,capacity))return -1;
    u->dirty=1;return 1;
}
