#include "ui.h"
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
void zui_icons_use_packages(void){zui_icon_source(package_icon);}
