/* Host tests for Aurora (tools/test_aurora.py builds this with sanitizers): the text engine against a
 * reference model, then the real editor driven through events over a temporary directory, with every
 * frame checked against the desktop's material rule (ProcessMaterials.ValidForeground). */
#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>
#include "aurora.h"

static int failures;
#define CHECK(c) do{if(!(c)){fprintf(stderr,"%s:%d: CHECK(%s)\n",__FILE__,__LINE__,#c);failures++;}}while(0)

/* ---------- Text engine against a plain string. */
static char model[1<<16];static unsigned int model_n;
static char tdata[1<<16];static unsigned int tlines[4096];
static struct au_change tchanges[64];static char tstore[2048];
static void check_same(const struct au_text *t){
    CHECK(au_length(t)==model_n);
    static char out[1<<16];unsigned int n=au_read(t,0,au_length(t),out);
    CHECK(n==model_n&&!memcmp(out,model,n));
    unsigned int lines=1;for(unsigned int i=0;i<model_n;i++)lines+=model[i]=='\n';
    CHECK(t->line_count==lines);
    unsigned int l=1;for(unsigned int i=0;i<model_n&&l<t->line_count;i++)if(model[i]=='\n'){CHECK(t->lines[l]==i+1);l++;}
}
static void engine_tests(void){
    struct au_text t;struct au_history h;
    au_text_init(&t,tdata,sizeof(tdata),tlines,4096);au_history_init(&h,tchanges,64,tstore,sizeof(tstore));
    srand(7);model_n=0;
    /* states[k]: the text before undo step k (no-op edits make no step). */
    static char states[301][1<<12];static unsigned int sizes[301];int steps=0;
    for(int i=0;i<300;i++){
        unsigned int p=model_n?(unsigned int)rand()%(model_n+1):0,count=p<model_n?(unsigned int)rand()%((model_n-p)<8?(model_n-p)+1:8):0;
        char ins[12];unsigned int bytes=(unsigned int)rand()%6;
        for(unsigned int k=0;k<bytes;k++)ins[k]="ab\nc d\t"[rand()%7];
        if((count||bytes)&&model_n<4000){memcpy(states[steps],model,model_n);sizes[steps]=model_n;steps++;}
        au_history_break(&h);
        CHECK(au_edit(&t,&h,p,count,ins,bytes,p,p+bytes,0)==0);
        memmove(model+p+bytes,model+p+count,model_n-p-count);memcpy(model+p,ins,bytes);model_n=model_n-count+bytes;
        check_same(&t);
    }
    /* Undo walks back exactly as far as the bounded history reaches, and redo returns. */
    unsigned int caret,undone=0;
    while(au_undo(&t,&h,&caret)){undone++;unsigned int k=(unsigned int)steps-undone;memcpy(model,states[k],sizes[k]);model_n=sizes[k];check_same(&t);}
    CHECK(undone>10&&undone<=64);
    while(au_redo(&t,&h,&caret)){}
    /* Typing merges into one undo step; a newline starts a new one. */
    au_text_init(&t,tdata,sizeof(tdata),tlines,4096);au_history_init(&h,tchanges,64,tstore,sizeof(tstore));
    au_mark_saved(&h);
    for(const char *s="hello";*s;s++){unsigned int p=au_length(&t);au_edit(&t,&h,p,0,s,1,p,p+1,1);}
    CHECK(au_modified(&h));
    au_edit(&t,&h,5,0,"\n",1,5,6,1);
    au_undo(&t,&h,&caret);CHECK(au_length(&t)==5);
    au_undo(&t,&h,&caret);CHECK(au_length(&t)==0&&!au_modified(&h));
    au_redo(&t,&h,&caret);CHECK(au_length(&t)==5);
    /* Search, words, columns. */
    au_load(&t,"Find find FIND findings\n\tx",26);
    CHECK(au_find(&t,"find",4,0,1,0)==0);
    CHECK(au_find(&t,"find",4,1,1,AU_CASE)==5);
    CHECK(au_count(&t,"find",4,AU_WORD,100,0,0)==3);
    CHECK(au_count(&t,"find",4,0,100,0,0)==4);
    CHECK(au_find(&t,"find",4,25,0,AU_WORD)==10);
    CHECK(au_column(&t,25,4)==4&&au_at_column(&t,1,4,4)==25);
    CHECK(au_word_right(&t,0)==5&&au_word_left(&t,9)==5);
    /* Loading and saving. */
    CHECK(au_check("ok \xc3\xa4\xe2\x82\xac",8)==0);
    CHECK(au_check("a\0b",3)==AU_NOT_TEXT);
    CHECK(au_check("\xc3",1)==AU_NOT_UTF8&&au_check("\xc0\x80",2)==AU_NOT_UTF8&&au_check("\xed\xa0\x80",3)==AU_NOT_UTF8);
    char raw[]="\xef\xbb\xbf" "a\r\nb\r\nc";int crlf,bom;
    unsigned int n=au_normalize(raw,(unsigned int)strlen(raw),&crlf,&bom);
    CHECK(n==5&&crlf&&bom&&!memcmp(raw,"a\nb\nc",5));
    au_load(&t,raw,n);char out[32];unsigned int position=0,w=au_encode(&t,&position,1,out,sizeof(out));
    CHECK(w==7&&!memcmp(out,"a\r\nb\r\nc",7)&&position==5);
}

/* ---------- Platform over a temporary directory. */
static char logbuf[1<<16];static unsigned long logged;static long now_ms=1000;
static char clipboard[1<<16];static long clip_n=-1;static int fail_space;
long au_sys_read(const char *path,char *out,unsigned long capacity){
    int fd=open(path,O_RDONLY);if(fd<0)return -errno;
    struct stat st;fstat(fd,&st);if(S_ISDIR(st.st_mode)){close(fd);return -21;}
    long n=read(fd,out,capacity),extra=0;char c;if(n==(long)capacity)extra=read(fd,&c,1);
    close(fd);return extra>0?-27:n;
}
long au_sys_info(const char *path,struct au_info *info){
    struct stat st;if(stat(path,&st))return -errno;
    info->size=(unsigned long)st.st_size;info->mode=st.st_mode;info->mtime=st.st_mtim.tv_sec*1000+st.st_mtim.tv_nsec/1000000;info->is_dir=S_ISDIR(st.st_mode);
    return 0;
}
static unsigned long space_left;
long au_sys_create(const char *path){int fd=open(path,O_WRONLY|O_CREAT|O_TRUNC,0644);space_left=fail_space?16:~0UL;return fd<0?-errno:fd;}
long au_sys_write(long fd,const char *data,unsigned long bytes){
    if(bytes>space_left){long w=write((int)fd,data,space_left);space_left=0;(void)w;return -28;}
    space_left-=bytes;long w=write((int)fd,data,bytes);return w<0?-errno:w;
}
long au_sys_close(long fd){return close((int)fd)?-errno:0;}
long au_sys_rename(const char *from,const char *to){return rename(from,to)?-errno:0;}
long au_sys_unlink(const char *path){return unlink(path)?-errno:0;}
long au_sys_chmod(const char *path,unsigned int mode){return chmod(path,mode)?-errno:0;}
long au_sys_mkdir(const char *path){return mkdir(path,0755)?-errno:0;}
long au_sys_now(void){return (long)time(0);}
long au_sys_list(const char *folder,void (*each)(void *,const char *,int),void *context){
    DIR *d=opendir(folder);if(!d)return -errno;long n=0;struct dirent *e;
    while((e=readdir(d))){if(!strcmp(e->d_name,".")||!strcmp(e->d_name,".."))continue;each(context,e->d_name,e->d_type==DT_DIR);n++;}
    closedir(d);return n;
}
long au_sys_copy(const char *text,unsigned long bytes){memcpy(clipboard,text,bytes);clip_n=(long)bytes;return 0;}
long au_sys_paste(char *out,unsigned long capacity){if(clip_n<0)return -61;if((unsigned long)clip_n>capacity)return -27;memcpy(out,clipboard,(size_t)clip_n);return clip_n;}
long au_sys_clock(void){return now_ms;}
void au_sys_log(const char *line){unsigned long n=strlen(line);if(logged+n<sizeof(logbuf)){memcpy(logbuf+logged,line,n);logged+=n;logbuf[logged]=0;}}
static char title[200];static int unsaved;
void au_sys_title(const char *t){snprintf(title,sizeof(title),"%s",t);}
void au_sys_unsaved(int u){unsaved=u;}

/* ---------- Driving the editor. */
static unsigned int pixels[1400*900];static struct zui ui;static int frames,mode;
/* The desktop's rule, as ProcessMaterials.ValidForeground applies it. */
static int valid_frame(void){
    if(!ui.foreground){for(int i=0;i<ui.width*ui.height;i++)if(pixels[i]>>24)return 0;return 1;}
    for(int y=0;y<ui.height;y++)for(int x=0;x<ui.width;x++){
        unsigned int p=pixels[y*ui.width+x];if(p>=0xff000000u)continue;
        unsigned int a=p>>24;if((p&255)>a||(p>>8&255)>a||(p>>16&255)>a){fprintf(stderr,"premultiplied broken at %d,%d: %08x\n",x,y,p);return 0;}
        int in=0;for(int i=0;i<ui.material_count;i++){struct zenith_material_region *m=&ui.materials[i];in|=x>=m->x&&y>=m->y&&x<m->x+m->w&&y<m->y+m->h;}
        if(!in){fprintf(stderr,"translucent outside a material at %d,%d: %08x\n",x,y,p);return 0;}
    }
    return 1;
}
static void frame(void){
    aurora_draw();frames++;
    CHECK(valid_frame());
    CHECK(zui_material_repair(&ui)==0);
}
static int send(int type,int code,int x,int y,int modifiers,int extra){
    struct zenith_gui_event e={type,code,x,y,modifiers,extra};now_ms+=40;
    int end=aurora_event(&e);aurora_after_event();frame();return end;
}
static void key(int extra,int modifiers){send(Z_GUI_KEY,0,0,0,modifiers,extra);}
static void ctrl(char c){send(Z_GUI_KEY,c,0,0,2,0);}
static void type(const char *s){for(;*s;s++){if(*s=='\n')key(2,0);else if(*s=='\t')key(4,0);else send(Z_GUI_KEY,(unsigned char)*s,0,0,0,0);}}
static int logged_has(const char *s){return strstr(logbuf,s)!=0;}
/* Frames to look at: AURORA_SNAPSHOTS=DIR writes PPM files (opaque view of the frame). */
static int snapshot_mode;
static void snapshot(const char *name){
    const char *dir=getenv("AURORA_SNAPSHOTS");if(!dir)return;
    char path[600];snprintf(path,sizeof(path),"%s/%s-%dx%d-%d.ppm",dir,name,ui.width,ui.height,snapshot_mode);
    FILE *f=fopen(path,"wb");if(!f)return;fprintf(f,"P6 %d %d 255\n",ui.width,ui.height);
    for(int i=0;i<ui.width*ui.height;i++){
        unsigned int p=pixels[i],a=ui.foreground?p>>24:255;
        /* Material areas over a checker so they are visible. */
        int x=i%ui.width,y=i/ui.width;unsigned int bg=((x/16+y/16)&1)?0x707888:0x8890a0;
        unsigned char c[3];for(int k=0;k<3;k++){unsigned int s=16-8*k,v=p>>s&255,b=bg>>s&255;c[k]=(unsigned char)(ui.foreground?v+b*(255-a)/255:v);}
        fwrite(c,1,3,f);
    }
    fclose(f);
}
static char *slurp(const char *path,long *n){static char b[1<<16];FILE *f=fopen(path,"rb");if(!f){*n=-1;return b;}*n=(long)fread(b,1,sizeof(b),f);fclose(f);return b;}
static void put_file(const char *path,const char *data,long n){FILE *f=fopen(path,"wb");fwrite(data,1,(size_t)n,f);fclose(f);}

static void editor_tests(const char *dir,int width,int height,int foreground,int full){
    char src[512],bin[512],out[512],sub[512];
    snprintf(src,sizeof(src),"%s/session-store.cs",dir);snprintf(bin,sizeof(bin),"%s/image.bin",dir);
    snprintf(out,sizeof(out),"%s/notes.txt",dir);snprintf(sub,sizeof(sub),"%s/folder",dir);
    const char *code="// Bounded session restore.\r\ninternal sealed class SessionStore\r\n{\r\n\tprivate const int Maximum = 128; /* entries\r\n\t   per file */\r\n\tpublic bool TryRestore(string path) { return \"x\" != null; }\r\n}\r\n";
    put_file(src,code,(long)strlen(code));put_file(bin,"\x7f" "ELF\0\0\1",7);unlink(out);mkdir(sub,0755);
    logged=0;logbuf[0]=0;
    zui_init(&ui,pixels,width,height);ui.foreground=foreground;ui.full_background=full;ui.window_focused=1;
    char *paths[]={src};
    aurora_start(&ui,dir,1,paths);frame();
    CHECK(logged_has("AURORA_OPENED")&&logged_has("crlf"));
    /* AU2: the outline lists the class and its method (the sidebar shows from 820 pixels). */
    CHECK(width<820||logged_has("AURORA_OUTLINE 2 SessionStore TryRestore"));
    CHECK(width<820||logged_has("AURORA_PROJECT"));
    /* Edit, undo, redo. */
    ctrl('G');type("3");key(2,0);
    key(11,0);type("\n");type("// added");
    long n;CHECK(strstr(title,"\xE2\x80\xA2")!=0&&unsaved);
    ctrl('z');ctrl('y'); /* typed text is one step: undo and redo it */
    /* Select all, copy, paste at the end: the text doubles. */
    ctrl('a');ctrl('c');CHECK(clip_n>100);key(11,2);ctrl('v');CHECK(logged_has("AURORA_PASTED"));
    ctrl('z');
    /* Find and replace. */
    ctrl('f');type("SessionStore");CHECK(logged_has("AURORA_FIND 1 SessionStore"));
    snapshot("find");
    ctrl('h');type("Store2");key(2,0); /* Enter in the replace field replaces the selected match */
    CHECK(logged_has("AURORA_REPLACED 1"));
    key(1,0);key(1,0); /* Esc leaves the field, then closes the bar */
    snapshot("edited");
    /* Save: CRLF comes back, the edit is there. */
    ctrl('s');CHECK(logged_has("AURORA_SAVED"));
    char *s=slurp(src,&n);CHECK(n>0&&strstr(s,"// added\r\n")!=0&&strstr(s,"class Store2\r\n")!=0);
    CHECK(!unsaved);
    /* AU2: a regular expression with a group in the replacement (Alt+R turns it on). */
    ctrl('f');send(Z_GUI_KEY,'r',0,0,4,0);CHECK(logged_has("AURORA_REGEX on"));
    for(int i=0;i<40;i++)key(3,0);
    type("Try(\\w+)\\(");CHECK(logged_has("AURORA_FIND 1 Try(\\w+)\\("));
    ctrl('h');type("Do$1(");key(2,0);CHECK(logged_has("AURORA_REPLACED 1"));
    key(1,0);key(1,0);
    send(Z_GUI_KEY,0,0,0,0,26);CHECK(logged_has("AURORA_OVERWRITE on"));   /* Insert: overwrite */
    ctrl('G');type("1");key(2,0);type("##");send(Z_GUI_KEY,0,0,0,0,26);
    ctrl('s');s=slurp(src,&n);CHECK(strstr(s,"DoRestore(string path)")!=0);CHECK(strstr(s,"## Bounded")!=0&&strstr(s,"// Bounded")==0);
    /* A save that runs out of space leaves the file unchanged and keeps the text. */
    type("x");CHECK(unsaved&&strstr(title,"\xE2\x80\xA2"));fail_space=1;ctrl('s');fail_space=0;
    CHECK(logged_has("AURORA_SAVE_FAILED"));
    char *again=slurp(src,&n);CHECK(strstr(again,"// addedx")==0);
    char temporary[600];snprintf(temporary,sizeof(temporary),"%s/.session-store.cs.aurora-save",dir);CHECK(access(temporary,F_OK)!=0);
    key(2,0); /* OK */
    /* Another program changes the file: the banner appears when the window gets focus back. */
    put_file(src,"changed\n",8);
    aurora_focus(1);frame();CHECK(logged_has("AURORA_CHANGED_ON_DISK"));snapshot("banner");
    /* Saving now asks first; Cancel keeps the other program's version. */
    ctrl('s');CHECK(logged_has("AURORA_DIALOG The file changed on disk"));
    key(1,0);s=slurp(src,&n);CHECK(n==8);
    /* Binary files are refused. */
    ctrl('o');CHECK(logged_has("AURORA_FILE_DIALOG open"));
    for(const char *p=bin;*p;p++)send(Z_GUI_KEY,(unsigned char)*p,0,0,0,0);
    key(2,0);CHECK(logged_has("AURORA_REFUSED"));key(2,0);
    /* A new document saved through Save as. */
    ctrl('n');type("one\ntwo\n\tthree");
    ctrl('s');CHECK(logged_has("AURORA_FILE_DIALOG save"));snapshot("save-as");
    for(int i=0;i<40;i++)key(3,0);
    for(const char *p=out;*p;p++)send(Z_GUI_KEY,(unsigned char)*p,0,0,0,0);
    key(2,0);s=slurp(out,&n);CHECK(n==17&&!memcmp(s,"one\ntwo\n    three",17)); /* new documents indent with spaces */
    /* Quitting with unsaved changes asks; Don't save ends Aurora. */
    type("!");
    CHECK(aurora_close()==0);frame();
    /* zui lays dialog buttons out right to left: Shift+Tab from Save reaches Don't save. */
    key(4,1);int end=send(Z_GUI_KEY,0,0,0,0,2);
    if(!end){key(4,1);end=send(Z_GUI_KEY,0,0,0,0,2);}
    CHECK(end);
    s=slurp(out,&n);CHECK(n==17);
    /* AU3: a recovery copy of unsaved work, offered after a "crash" (a restart without closing). */
    char rdir[700],rfile[1200];snprintf(rdir,sizeof(rdir),"%s/.local/share/aurora/recovery",dir);
    logged=0;logbuf[0]=0;aurora_start(&ui,dir,0,0);frame();
    type("unsaved words");now_ms+=3000;aurora_tick(now_ms);frame();
    CHECK(logged_has("AURORA_RECOVERY_SAVED"));
    int copies=0;DIR *rd=opendir(rdir);struct dirent *re;
    while(rd&&(re=readdir(rd))){if(re->d_name[0]=='r'){copies++;snprintf(rfile,sizeof(rfile),"%s/%s",rdir,re->d_name);}}
    if(rd)closedir(rd);
    struct stat st;CHECK(copies==1&&stat(rfile,&st)==0&&(st.st_mode&0777)==0600);
    CHECK(stat(rdir,&st)==0&&(st.st_mode&0777)==0700);
    logged=0;logbuf[0]=0;aurora_start(&ui,dir,0,0);frame();
    CHECK(logged_has("AURORA_RECOVERY_FOUND"));
    key(2,0);CHECK(logged_has("AURORA_RECOVERED"));CHECK(unsaved);
    CHECK(access(rfile,F_OK)==0);   /* kept until the document is saved or closed */
    ctrl('2');ctrl('w');key(4,1);end=send(Z_GUI_KEY,0,0,0,0,2);   /* close it without saving: the copy goes */
    CHECK(access(rfile,F_OK)!=0);
    /* AU3: another program's change is noticed within three seconds, without a focus change. */
    char *again_paths[]={out};logged=0;logbuf[0]=0;aurora_start(&ui,dir,1,again_paths);frame();
    put_file(out,"changed elsewhere",17);now_ms+=3500;aurora_tick(now_ms);frame();
    CHECK(logged_has("AURORA_CHANGED_ON_DISK"));
}

int main(int argc,char **argv){
    engine_tests();
    const char *dir=argc>1?argv[1]:"/tmp";
    int sizes[][2]={{640,480},{1200,800}};
    for(int s=0;s<2;s++)for(int m=0;m<3;m++){mode=m;snapshot_mode=m;editor_tests(dir,sizes[s][0],sizes[s][1],m>0,m==2);}
    printf("aurora host tests: %d frames, %s\n",frames,failures?"FAILED":"passed");
    return failures?1:0;
}
