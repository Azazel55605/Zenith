/* Aurora's native side: the au_sys_* calls over Zenith's native ABI, and the window loop. The editor itself is
 * editor.c (host-tested by tools/test_ui_library.py). */
#include "aurora.h"
#include "../include/zenith_settings.h"
#include "../include/zenith_files.h"

static unsigned int pixels[640*480];
static struct zui ui;
static long handle;

static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static int starts(const char *s,const char *prefix){while(*prefix)if(*s++!=*prefix++)return 0;return 1;}
static void copy(char *o,const char *s,unsigned long n){unsigned long i=0;for(;i+1<n&&s[i];i++)o[i]=s[i];o[i]=0;}

/* ---------- Files. */
long au_sys_info(const char *path,struct au_info *info){
    struct zenith_stat st;long r=zenith_call(4,(unsigned long)path,length(path),(unsigned long)&st);
    if(r<0)return r;
    *info=(struct au_info){st.size,st.mode,0,(st.mode&0xf000)==0x4000};
    /* The modification time comes from the folder listing (zenith-native-files-abi). */
    char folder[1024];copy(folder,path,sizeof(folder));
    long n=(long)length(folder);while(n>1&&folder[n-1]!='/')n--;
    const char *name=path+n;if(n>1)n--;folder[n]=0;
    static struct zenith_files_list request;static unsigned char page[Z_FILES_PAGE] __attribute__((aligned(16)));
    request=(struct zenith_files_list){1,0,0,(unsigned int)length(folder),{0}};copy(request.path,folder,sizeof(request.path));
    for(int guard=0;guard<64;guard++){
        if(zenith_files_list(&request,page)<0)break;
        const struct zenith_files_page *h=(const struct zenith_files_page *)page;
        unsigned long offset=sizeof(*h);
        for(unsigned int i=0;i<h->count&&offset+sizeof(struct zenith_file_entry)<=Z_FILES_PAGE;i++){
            const struct zenith_file_entry *e=(const struct zenith_file_entry *)(page+offset);
            if(e->record_bytes<sizeof(*e)||offset+e->record_bytes>Z_FILES_PAGE)break;
            if(e->name_bytes==length(name)){int match=1;for(unsigned int k=0;k<e->name_bytes;k++)if(e->name[k]!=name[k]){match=0;break;}
                if(match){info->mtime=e->mtime;return 0;}}
            offset+=e->record_bytes;
        }
        if(!h->next||h->next<=request.offset)break;
        request.offset=h->next;
    }
    return 0;
}
long au_sys_read(const char *path,char *out,unsigned long capacity){
    struct au_info info;long r=au_sys_info(path,&info);
    if(r<0)return r;
    if(info.is_dir)return -21;
    if(info.size>capacity)return -27;
    long fd=zenith_call(2,(unsigned long)path,length(path),Z_OPEN_READ);
    if(fd<0)return fd;
    unsigned long done=0;
    while(done<capacity){
        /* A native read or write moves at most 4096 bytes (ProcessFiles.MaximumIo). */
        unsigned long chunk=capacity-done>4096?4096:capacity-done;
        long n=zenith_call(0,(unsigned long)fd,(unsigned long)(out+done),chunk);
        if(n<0){zenith_call(3,(unsigned long)fd,0,0);return n;}
        if(!n)break;
        done+=(unsigned long)n;
    }
    if(done==capacity){char extra;long n=zenith_call(0,(unsigned long)fd,(unsigned long)&extra,1);if(n>0){zenith_call(3,(unsigned long)fd,0,0);return -27;}}
    zenith_call(3,(unsigned long)fd,0,0);
    return (long)done;
}
long au_sys_create(const char *path){return zenith_call(2,(unsigned long)path,length(path),Z_OPEN_WRITE|Z_OPEN_CREATE|Z_OPEN_TRUNCATE);}
long au_sys_write(long fd,const char *data,unsigned long bytes){
    unsigned long done=0;
    while(done<bytes){unsigned long chunk=bytes-done>4096?4096:bytes-done;
        long n=zenith_call(1,(unsigned long)fd,(unsigned long)(data+done),chunk);if(n<0)return n;if(!n)return -28;done+=(unsigned long)n;}
    return (long)done;
}
long au_sys_close(long fd){
    long flushed=zenith_call(74,(unsigned long)fd,0,0);
    long closed=zenith_call(3,(unsigned long)fd,0,0);
    return flushed<0&&flushed!=-38?flushed:closed;
}
long au_sys_rename(const char *from,const char *to){return zenith_rename(from,length(from),to,length(to));}
long au_sys_unlink(const char *path){return zenith_path(2,path,length(path),0,0,0,0);}
long au_sys_chmod(const char *path,unsigned int mode){return zenith_path(5,path,length(path),0,0,mode&0xfff,0);}
long au_sys_mkdir(const char *path){return zenith_mkdir(path,length(path));}
/* UTC seconds from the files service (its page header carries the time). */
long au_sys_now(void){
    static struct zenith_files_list request;static unsigned char page[Z_FILES_PAGE] __attribute__((aligned(16)));
    request=(struct zenith_files_list){1,0,0,1,{0}};request.path[0]='/';
    if(zenith_files_list(&request,page)<0)return 0;
    return ((const struct zenith_files_page *)page)->now;
}
long au_sys_list(const char *folder,void (*each)(void *,const char *,int),void *context){
    static struct zenith_files_list request;static unsigned char page[Z_FILES_PAGE] __attribute__((aligned(16)));
    request=(struct zenith_files_list){1,0,0,(unsigned int)length(folder),{0}};copy(request.path,folder,sizeof(request.path));
    long total=0;
    for(int guard=0;guard<64;guard++){
        long r=zenith_files_list(&request,page);
        if(r<0)return total?total:r;
        const struct zenith_files_page *h=(const struct zenith_files_page *)page;
        unsigned long offset=sizeof(*h);
        for(unsigned int i=0;i<h->count&&offset+sizeof(struct zenith_file_entry)<=Z_FILES_PAGE;i++){
            const struct zenith_file_entry *e=(const struct zenith_file_entry *)(page+offset);
            if(e->record_bytes<sizeof(*e)||offset+e->record_bytes>Z_FILES_PAGE)break;
            char name[256];unsigned int n=e->name_bytes<255?e->name_bytes:255;for(unsigned int k=0;k<n;k++)name[k]=e->name[k];name[n]=0;
            int folder_entry=(e->mode&0xf000)==0x4000||((e->mode&0xf000)==0xa000&&(e->target_mode&0xf000)==0x4000);
            each(context,name,folder_entry);total++;
            offset+=e->record_bytes;
        }
        if(!h->next||h->next<=request.offset)break;
        request.offset=h->next;
    }
    return total;
}

/* ---------- Window services. */
long au_sys_copy(const char *text,unsigned long bytes){return zenith_call(639,(unsigned long)handle,(unsigned long)text,bytes);}
long au_sys_paste(char *out,unsigned long capacity){return zenith_call(659,(unsigned long)handle,(unsigned long)out,capacity);}
long au_sys_clock(void){return zui_motion_clock();}
void au_sys_log(const char *line){zenith_write(line,length(line));}
void au_sys_title(const char *title){if(handle>0)zenith_gui_set_title(handle,title,length(title));}
void au_sys_unsaved(int unsaved){if(handle>0)zenith_gui_dirty(handle,unsaved);}

static void update_appearance(void){
    struct zenith_appearance state={0};struct zenith_scoped_appearance scoped={0};
    int personal=zenith_scoped_appearance_read(1,&scoped)==0;
    if(personal)state=scoped.appearance;
    if((personal||!zenith_appearance_read(&state))&&state.active_mode<2&&state.active_accent<6){
        const unsigned int *p=state.palette[state.active_mode][state.active_accent];
        ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};
        zui_appearance(&ui,state.active_mode==1,(int)state.active_accent);
        ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};
    }
    /* Whole-app transparency (Meridian > Appearance > Materials), from the person's own scope when they have one. */
    struct zenith_material_settings material_state={0};
    if(personal)ui.full_background=(scoped.materials.saved&5)==5;
    else if(!zenith_material_settings_read(&material_state))ui.full_background=(material_state.saved&5)==5;
    long clock=zui_motion_clock();if(clock>=0)zui_apply_motion(&ui,clock);
    ui.dirty=1;
}

int main(int argc,char **argv,char **envp){
    char home[1024]="/",cwd[1024]="";
    for(char **e=envp;e&&*e;e++){
        if(starts(*e,"HOME=")&&(*e)[5]=='/')copy(home,*e+5,sizeof(home));
        if(starts(*e,"PWD=")&&(*e)[4]=='/')copy(cwd,*e+4,sizeof(cwd));
    }
    if(argc==2&&same(argv[1],"--version")){zenith_write("aurora " SUNBEAM_VERSION "\n",8+length(SUNBEAM_VERSION));return 0;}
    /* Files to open: absolute, or relative to the working folder (else home). */
    static char storage[8][1024];char *paths[8];int count=0;
    for(int i=1;i<argc&&count<8;i++){
        if(argv[i][0]=='-')continue;
        if(argv[i][0]=='/')copy(storage[count],argv[i],sizeof(storage[count]));
        else{const char *base=cwd[0]?cwd:home;unsigned long n=length(base);copy(storage[count],base,sizeof(storage[count]));
             if(n&&n<1022&&storage[count][n-1]!='/'){storage[count][n++]='/';storage[count][n]=0;}copy(storage[count]+n,argv[i],sizeof(storage[count])-n);}
        paths[count]=storage[count];count++;
    }
    zui_icons_use_packages();
    zui_init(&ui,pixels,640,480);update_appearance();
    struct zenith_material_caps caps={0};struct zenith_material_settings settings={0};
    int material=!zenith_material_settings_read(&settings)&&!zenith_gui_material_caps(&caps)&&caps.version==2;
    handle=material?zenith_gui_open_material(640,480,"Aurora",6):zenith_gui_open(640,480,"Aurora",6);
    if(handle<0){zenith_write("aurora: graphical session required\n",36);return 1;}
    ui.foreground=material;
    if(zenith_gui_interactive(handle)<0||zenith_gui_resize_enable(handle)<0){zenith_gui_close(handle);return 1;}
    /* Pointer motion, right-button presses and F1..F12 (F3 finds the next match). */
    ui.hover_supported=zenith_gui_input(handle,7)==0||zenith_gui_pointer_motion(handle)==0;
    aurora_start(&ui,home,count,paths);
    zenith_write(material?(ui.full_background?"AURORA_MATERIAL window\n":"AURORA_MATERIAL navigation\n"):"AURORA_MATERIAL none\n",material?(ui.full_background?23:27):21);
    zenith_write("AURORA_READY\n",13);
    int result=0;long repaired_total=0;
    for(;;){
        aurora_tick(zui_motion_clock());
        if(ui.dirty||ui.animating){
            long now=zui_motion_clock();if(now>=0)zui_motion_frame(&ui,now);
            aurora_draw();
            if(material){
                /* The desktop refuses a frame that breaks its material rule and the program would end: repair
                 * and report instead (tools/test_ui_library.py checks every frame of the editor's flows). */
                int repaired=zui_material_repair(&ui);
                if(repaired&&repaired_total<8){repaired_total++;char line[48]="AURORA_FRAME_REPAIRED ";char *o=line+22;
                    char t[12];int k=0;do{t[k++]=(char)('0'+repaired%10);repaired/=10;}while(repaired);while(k)*o++=t[--k];*o++='\n';zenith_write(line,(unsigned long)(o-line));}
            }
            long r=material?zenith_gui_present_material(handle,ui.pixels,(unsigned long)ui.width*ui.height*4,ui.materials,ui.material_count)
                :zenith_gui_present(handle,ui.pixels,(unsigned long)ui.width*ui.height*4);
            if(r<0){result=zui_window_closed(handle)?0:1;break;}
        }
        struct zenith_gui_event e={0};long r=zenith_gui_poll(handle,&e);if(r<0){result=1;break;}if(!r)continue;
        if(e.type==Z_GUI_RESIZE){if(zui_apply_viewport(&ui,handle,640*480)<0){result=zui_window_closed(handle)?0:1;break;}continue;}
        if(e.type==Z_GUI_CLOSE)break;
        if(e.type==Z_GUI_CLOSE_INTENT){if(aurora_close())break;continue;}
        if(e.type==Z_GUI_RESET){update_appearance();continue;}
        if(e.type==Z_GUI_FOCUS){aurora_focus(e.code);if(e.code)zenith_write("AURORA_FOCUSED\n",15);continue;}
        if(aurora_event(&e))break;
        aurora_after_event();
    }
    zenith_write("AURORA_CLOSED\n",14);
    zenith_gui_close(handle);return result;
}
