#include "styles.h"
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
void styles_init(struct meridian_styles *s){
    long (*catalog)(unsigned int,struct zenith_style_list*)=s->catalog;long (*preview)(unsigned int,unsigned int,int,int,unsigned int*)=s->preview;
    long (*save)(unsigned int,unsigned int,unsigned long)=s->save;long (*dirty)(int)=s->dirty;
    unsigned char *p=(unsigned char*)s;for(unsigned long i=0;i<sizeof(*s)-sizeof(s->thumbs);i++)p[i]=0;
    s->catalog=catalog;s->preview=preview;s->save=save;s->dirty=dirty;
    for(int i=0;i<STYLE_THUMBS;i++)s->thumb_size[i]=0;
}
int styles_page_fields(const char *id,unsigned int *out){
    int n=0;
    if(same(id,"style.wallpaper"))out[n++]=6;
    else if(same(id,"style.cursors")){out[n++]=8;out[n++]=9;}
    else if(same(id,"style.decorations"))out[n++]=10;
    else if(same(id,"style.application")){out[n++]=11;out[n++]=12;}
    else if(same(id,"style.splash"))out[n++]=13;
    else if(same(id,"style.login"))out[n++]=14;
    else if(same(id,"style.boot-splash"))out[n++]=15;
    else if(same(id,"audio.sounds"))out[n++]=16;
    return n;
}
int styles_field_ready(const struct meridian_styles *s,unsigned int f){return f<STYLE_FIELDS&&s->ready[f];}
static int field_changed(const struct meridian_styles *s,unsigned int f){return f<STYLE_FIELDS&&s->ready[f]&&s->draft[f]!=s->list[f].current;}
void styles_refresh(struct meridian_styles *s){
    if(!s->catalog)return;
    for(unsigned int f=6;f<STYLE_FIELDS;f++){
        if(f==7)continue;
        static struct zenith_style_list l;
        int ok=!s->catalog(f,&l)&&l.version==1&&l.field==f&&l.count>0&&l.count<=63&&l.current<l.count;
        for(unsigned int i=0;ok&&i<l.count;i++){l.entries[i].label[31]=0;l.entries[i].name[23]=0;if(!l.entries[i].label[0])ok=0;}
        if(!ok){s->ready[f]=0;continue;}
        int keep=s->ready[f]&&field_changed(s,f)&&s->draft[f]<l.count;
        s->list[f]=l;s->ready[f]=1;if(!keep)s->draft[f]=l.current;
    }
}
int styles_changed(const struct meridian_styles *s,const char *id){
    unsigned int f[4];int n=styles_page_fields(id,f);
    for(int i=0;i<n;i++)if(field_changed(s,f[i]))return 1;
    return 0;
}
int styles_any_changed(const struct meridian_styles *s){for(unsigned int f=0;f<STYLE_FIELDS;f++)if(field_changed(s,f))return 1;return 0;}
int styles_choose(struct meridian_styles *s,unsigned int f,unsigned int choice){
    if(f>=STYLE_FIELDS||!s->ready[f]||choice>=s->list[f].count)return -22;
    unsigned int old=s->draft[f];s->draft[f]=choice;
    if(s->dirty&&s->dirty(styles_any_changed(s))){s->draft[f]=old;s->message="Cannot protect edits; selection kept.";return -5;}
    s->message=0;return 0;
}
long styles_apply(struct meridian_styles *s,const char *id){
    unsigned int f[4];int n=styles_page_fields(id,f);long result=0;
    for(int i=0;i<n;i++){
        if(!field_changed(s,f[i]))continue;
        long r=s->save?s->save(f[i],s->draft[f[i]],s->list[f[i]].revision):-38;
        if(r){s->message=r==-11?"Changed elsewhere. Reset to reload.":r==-13?"Administrator permission required.":"Save failed; edits kept.";result=r;break;}
        /* The revision moved: the next field saves against the new one. */
        styles_refresh(s);
    }
    if(!result){s->message="Saved and applied.";styles_refresh(s);}
    if(s->dirty)s->dirty(styles_any_changed(s));
    return result;
}
void styles_reset(struct meridian_styles *s,const char *id){
    unsigned int f[4];int n=styles_page_fields(id,f);
    for(int i=0;i<n;i++)if(s->ready[f[i]])s->draft[f[i]]=s->list[f[i]].current;
    s->message=0;if(s->dirty)s->dirty(styles_any_changed(s));
}
const unsigned int *styles_thumb(struct meridian_styles *s,unsigned int f,unsigned int index,int w,int h,unsigned int key){
    if(!s->preview||w*h>STYLE_THUMB_PIXELS||w<32||h<20)return 0;
    if(key!=s->thumb_key){s->thumb_key=key;for(int i=0;i<STYLE_THUMBS;i++)s->thumb_size[i]=0;}
    unsigned int size=(unsigned int)(w<<16|h);
    for(int i=0;i<STYLE_THUMBS;i++)if(s->thumb_size[i]==size&&s->thumb_field[i]==f&&s->thumb_index[i]==index)return s->thumbs[i];
    int slot=(int)(s->thumb_next++%STYLE_THUMBS);
    if(s->preview(f,index,w,h,s->thumbs[slot])<0){s->thumb_size[slot]=0;return 0;}
    s->thumb_field[slot]=f;s->thumb_index[slot]=index;s->thumb_size[slot]=size;return s->thumbs[slot];
}
