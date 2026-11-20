#include "styles.h"
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
void styles_init(struct meridian_styles *s){
    long (*catalog)(unsigned int,struct zenith_style_list*)=s->catalog;long (*preview)(unsigned int,unsigned int,int,int,unsigned int*)=s->preview;
    long (*save)(unsigned int,unsigned int,unsigned long)=s->save;long (*dirty)(int)=s->dirty;
    long (*sounds)(struct zenith_sound_settings*)=s->sounds;long (*sound_save)(unsigned int,unsigned int,unsigned long)=s->sound_save;long (*sound_play)(unsigned int)=s->sound_play;
    long (*notify_read)(struct zenith_notify_settings*)=s->notify_read;long (*notify_save)(unsigned int,unsigned int,unsigned long)=s->notify_save;long (*notify_post)(const char*,const char*,const char*)=s->notify_post;
    unsigned char *p=(unsigned char*)s;for(unsigned long i=0;i<sizeof(*s)-sizeof(s->thumbs);i++)p[i]=0;
    s->catalog=catalog;s->preview=preview;s->save=save;s->dirty=dirty;s->sounds=sounds;s->sound_save=sound_save;s->sound_play=sound_play;s->notify_read=notify_read;s->notify_save=notify_save;s->notify_post=notify_post;
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
    else if(same(id,"style.icons"))out[n++]=17;
    return n;
}
int styles_field_ready(const struct meridian_styles *s,unsigned int f){return f<STYLE_FIELDS&&s->ready[f];}
static int field_changed(const struct meridian_styles *s,unsigned int f){return f<STYLE_FIELDS&&s->ready[f]&&s->draft[f]!=s->list[f].current;}
static int notify_changed(const struct meridian_styles *s){return s->notify_ready&&(s->notify_dnd!=s->notify.dnd||s->notify_popup!=s->notify.popup);}
static int sound_changed(const struct meridian_styles *s){return s->sound_ready&&(s->sound_level!=s->sound.level||s->sound_mask!=s->sound.mask);}
void styles_refresh(struct meridian_styles *s){
    if(s->notify_read){
        static struct zenith_notify_settings n;
        int keep=notify_changed(s);
        if(s->notify_read(&n)>=0&&n.version==1&&n.dnd<=1&&n.popup<n.choices&&n.choices<=3){s->notify=n;if(!s->notify_ready||!keep){s->notify_dnd=n.dnd;s->notify_popup=n.popup;}s->notify_ready=1;}
        else s->notify_ready=0;
    }
    if(s->sounds){
        static struct zenith_sound_settings snd;
        int keep=sound_changed(s);
        if(s->sounds(&snd)>=0&&snd.version==1&&snd.level<=100&&snd.count<=15){s->sound=snd;if(!s->sound_ready||!keep){s->sound_level=snd.level;s->sound_mask=snd.mask;}s->sound_ready=1;}
        else s->sound_ready=0;
    }
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
    return (same(id,"audio.sounds")&&sound_changed(s))||(same(id,"windows.notifications")&&notify_changed(s));
}
int styles_any_changed(const struct meridian_styles *s){for(unsigned int f=0;f<STYLE_FIELDS;f++)if(field_changed(s,f))return 1;return sound_changed(s)||notify_changed(s);}
int styles_notify_set(struct meridian_styles *s,unsigned int dnd,unsigned int popup){
    if(!s->notify_ready||dnd>1||popup>=s->notify.choices)return -22;
    s->notify_dnd=dnd;s->notify_popup=popup;s->message=0;
    if(s->dirty)s->dirty(styles_any_changed(s));
    return 0;
}
int styles_sound_set(struct meridian_styles *s,unsigned int level,unsigned int mask){
    if(!s->sound_ready||level>100||mask>0xFFF)return -22;
    s->sound_level=level;s->sound_mask=mask;s->message=0;
    if(s->dirty)s->dirty(styles_any_changed(s));
    return 0;
}
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
    if(!result&&same(id,"audio.sounds")&&sound_changed(s)){
        long r=s->sound_save?s->sound_save(s->sound_level,s->sound_mask,s->sound.revision):-38;
        if(r){s->message=r==-11?"Changed elsewhere. Reset to reload.":r==-13?"Administrator permission required.":"Save failed; edits kept.";result=r;}
    }
    if(!result&&same(id,"windows.notifications")&&notify_changed(s)){
        long r=s->notify_save?s->notify_save(s->notify_dnd,s->notify_popup,s->notify.revision):-38;
        if(r){s->message=r==-11?"Changed elsewhere. Reset to reload.":r==-13?"Administrator permission required.":"Save failed; edits kept.";result=r;}
    }
    if(!result){s->message="Saved and applied.";styles_refresh(s);}
    if(s->dirty)s->dirty(styles_any_changed(s));
    return result;
}
void styles_reset(struct meridian_styles *s,const char *id){
    unsigned int f[4];int n=styles_page_fields(id,f);
    for(int i=0;i<n;i++)if(s->ready[f[i]])s->draft[f[i]]=s->list[f[i]].current;
    if(same(id,"audio.sounds")&&s->sound_ready){s->sound_level=s->sound.level;s->sound_mask=s->sound.mask;}
    if(same(id,"windows.notifications")&&s->notify_ready){s->notify_dnd=s->notify.dnd;s->notify_popup=s->notify.popup;}
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
