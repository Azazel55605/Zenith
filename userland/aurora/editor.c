/* Aurora: the text editor (docs/components/AURORA.md, concept docs/design/editor-concept.svg). Documents with
 * tabs and a sidebar list, a code view with line numbers, syntax colours from the accent palette, selection,
 * the system clipboard, undo/redo, find and replace docked above the status bar, open/save dialogs, staged
 * saving that never truncates the file it replaces, unsaved-change prompts and changes on disk.
 * Everything system-specific goes through the au_sys_* calls (aurora.h). */
#include "aurora.h"
#include "../ui/widgets.h"
#include "../ui/text.h"
#include "regex.h"

#define MAX_DOCS 8
#define TEXT_BYTES (1u<<20)       /* a document holds up to 1 MiB of text */
#define LINE_SLOTS 65536u
#define CHANGE_SLOTS 4096u
#define HISTORY_BYTES (256u<<10)
#define PATH_BYTES 1024
#define LINE_DRAW 4096           /* bytes of one line that are drawn */
#define DIALOG_ENTRIES 512

static char text_store[MAX_DOCS][TEXT_BYTES];
static unsigned int line_store[MAX_DOCS][LINE_SLOTS];
static struct au_change change_store[MAX_DOCS][CHANGE_SLOTS];
static char history_store[MAX_DOCS][HISTORY_BYTES];
static char staging[TEXT_BYTES+1];
static char line_bytes[LINE_DRAW];
static unsigned char line_class[LINE_DRAW];

enum { LANG_PLAIN,LANG_C,LANG_HASH,LANG_MARKDOWN,LANG_JSON };
struct doc {
    int used,number,language,crlf,bom,tab,spaces,disk_known,disk_state; /* disk_state: 1 changed, 2 missing */
    char path[PATH_BYTES],name[256];
    struct au_text text; struct au_history history;
    unsigned int caret,anchor,goal; int goal_valid,scroll_x,scroll_y;
    unsigned long disk_size; long disk_mtime; unsigned int disk_mode;
    unsigned int recovery_id; int has_recovery; long last_change,last_snapshot; unsigned long snapshot_stamp;
    int state_line,state_value; /* syntax state at the start of state_line */
};
static struct doc docs[MAX_DOCS];
static int order[MAX_DOCS],doc_count,current=-1,untitled;
static struct zui *ui;
static char home[PATH_BYTES]="/";

/* ---------- Small helpers: no libc. */
static unsigned long length(const char *s){unsigned long n=0;while(s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static char *put(char *o,const char *s){while(*s)*o++=*s++;*o=0;return o;}
static char *num(char *o,unsigned long v){char t[24];int n=0;do{t[n++]=(char)('0'+v%10);v/=10;}while(v);while(n)*o++=t[--n];*o=0;return o;}
static void copy(char *o,const char *s,unsigned long n){unsigned long i=0;for(;i+1<n&&s[i];i++)o[i]=s[i];o[i]=0;}
static char lower(char c){return c>='A'&&c<='Z'?(char)(c+32):c;}
static int inside(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x<r.x+r.w&&y<r.y+r.h;}
static int min(int a,int b){return a<b?a:b;}
static int max(int a,int b){return a>b?a:b;}
static void text(int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(ui,x,y,s,length(s),f,c);}
static int measure(const char *s,const struct zui_font *f){return zui_measure(s,length(s),f);}
static void text_right(int right,int y,const char *s,const struct zui_font *f,unsigned int c){text(right-measure(s,f),y,s,f,c);}
static void fit(int x,int y,const char *s,int width,const struct zui_font *f,unsigned int c){
    unsigned long n=length(s);
    if(zui_measure(s,n,f)<=width){zui_text(ui,x,y,s,n,f,c);return;}
    int dots=zui_measure("...",3,f);
    while(n&&zui_measure(s,n,f)+dots>width){n--;while(n&&((unsigned char)s[n]&0xc0)==0x80)n--;}
    zui_text(ui,x,y,s,n,f,c);zui_text(ui,x+zui_measure(s,n,f),y,"...",3,f,c);
}
static unsigned int mix(unsigned int a,unsigned int b,int percent){
    unsigned int r=0;
    for(int shift=0;shift<24;shift+=8){unsigned int x=a>>shift&255,y=b>>shift&255;r|=((x*(unsigned int)(100-percent)+y*(unsigned int)percent)/100)<<shift;}
    return r;
}
static int light(void){unsigned int s=ui->theme.surface;return ((s>>16&255)+(s>>8&255)+(s&255))>384;}
static unsigned int bad(void){return light()?0xAE325D:0xF29BAE;}
static unsigned int warn(void){return light()?0x886010:0xE6C47A;}
static void mark(const char *a,const char *b){char line[PATH_BYTES+160],*o=put(line,a);if(b){o=put(o," ");copy(o,b,PATH_BYTES+40);}o=line+length(line);put(o,"\n");au_sys_log(line);}
static const char *leaf(const char *path){const char *l=path;for(const char *p=path;*p;p++)if(*p=='/'&&p[1])l=p+1;return l;}
static void parent(char *o,const char *path){
    copy(o,path,PATH_BYTES);long n=(long)length(o);
    while(n>1&&o[n-1]=='/')o[--n]=0;
    while(n>0&&o[n-1]!='/')o[--n]=0;
    while(n>1&&o[n-1]=='/')o[--n]=0;
    if(!o[0])put(o,"/");
}
static void join(char *o,const char *folder,const char *name){
    char t[PATH_BYTES];copy(t,folder,sizeof(t));unsigned long n=length(t);
    if(n&&t[n-1]!='/'&&n+1<sizeof(t)){t[n++]='/';t[n]=0;}
    copy(t+n,name,sizeof(t)-n);copy(o,t,PATH_BYTES);
}
static const char *error_text(long e){
    switch(e){
    case -2:return "it does not exist";
    case -13:case -1:return "you do not have permission";
    case -21:return "it is a folder";
    case -28:return "there is not enough space";
    case -30:return "the disk is read-only";
    case -27:return "it is larger than 1 MiB";
    case -36:return "the name is too long";
    default:return "the system refused it";
    }
}

/* ---------- Documents. */
static struct doc *D(void){return current>=0?&docs[current]:0;}
static unsigned int selection_start(const struct doc *d){return d->caret<d->anchor?d->caret:d->anchor;}
static unsigned int selection_end(const struct doc *d){return d->caret>d->anchor?d->caret:d->anchor;}
static int has_selection(const struct doc *d){return d->caret!=d->anchor;}
static int language_of(const char *name){
    const char *dot=0;for(const char *p=name;*p;p++)if(*p=='.')dot=p+1;
    if(!dot)return same(name,"Makefile")?LANG_HASH:LANG_PLAIN;
    char e[12];int i=0;while(dot[i]&&i<11){e[i]=lower(dot[i]);i++;}e[i]=0;
    static const char *const c[]={"c","h","cc","cpp","hpp","cs","java","js","ts","rs","go","kt","swift","css",0};
    static const char *const h[]={"py","sh","bash","conf","cfg","ini","toml","yaml","yml","app","tsv","mk",0};
    for(int k=0;c[k];k++)if(same(e,c[k]))return LANG_C;
    for(int k=0;h[k];k++)if(same(e,h[k]))return LANG_HASH;
    if(same(e,"md"))return LANG_MARKDOWN;
    if(same(e,"json"))return LANG_JSON;
    return LANG_PLAIN;
}
static const char *language_name(const struct doc *d){
    const char *dot=0;for(const char *p=d->name;*p;p++)if(*p=='.')dot=p+1;
    if(d->language==LANG_C&&dot){
        if(same(dot,"c")||same(dot,"h"))return "C";
        if(same(dot,"cs"))return "C#";
        if(same(dot,"cpp")||same(dot,"cc")||same(dot,"hpp"))return "C++";
        if(same(dot,"js"))return "JavaScript";
        if(same(dot,"ts"))return "TypeScript";
        if(same(dot,"rs"))return "Rust";
        if(same(dot,"go"))return "Go";
        if(same(dot,"java"))return "Java";
        return "Code";
    }
    if(d->language==LANG_HASH&&dot){
        if(same(dot,"py"))return "Python";
        if(same(dot,"sh")||same(dot,"bash"))return "Shell";
        if(same(dot,"yaml")||same(dot,"yml"))return "YAML";
        return "Config";
    }
    return d->language==LANG_HASH?"Config":d->language==LANG_MARKDOWN?"Markdown":d->language==LANG_JSON?"JSON":"Plain text";
}
static int modified(const struct doc *d){return au_modified(&d->history);}
static void update_title(void){
    struct doc *d=D();char t[120],*o=t;
    /* "name — Aurora •": the form Sunset and the taskbar read (the dot while there are unsaved changes). */
    if(d){char n[64];copy(n,d->name,sizeof(n));o=put(o,n);o=put(o," \xE2\x80\x94 Aurora");if(modified(d))put(o," \xE2\x80\xA2");}
    else put(t,"Aurora");
    au_sys_title(t);
    int any=0;for(int i=0;i<doc_count;i++)any|=modified(&docs[order[i]]);
    au_sys_unsaved(any);
}
static int new_doc(void){
    if(doc_count>=MAX_DOCS)return -1;
    int slot=-1;for(int i=0;i<MAX_DOCS;i++)if(!docs[i].used){slot=i;break;}
    if(slot<0)return -1;
    struct doc *d=&docs[slot];*d=(struct doc){0};d->used=1;d->tab=4;d->spaces=1;
    static unsigned int serial;d->recovery_id=(unsigned int)(au_sys_now()*2654435761u)^(unsigned int)(au_sys_clock()<<8)^(++serial*40503u);
    au_text_init(&d->text,text_store[slot],TEXT_BYTES,line_store[slot],LINE_SLOTS);
    au_history_init(&d->history,change_store[slot],CHANGE_SLOTS,history_store[slot],HISTORY_BYTES);
    order[doc_count++]=slot;return slot;
}
static void name_untitled(struct doc *d){
    d->number=++untitled;char *o=put(d->name,"Untitled");if(d->number>1){o=put(o," ");num(o,(unsigned long)d->number);}
    d->path[0]=0;d->language=LANG_PLAIN;
}
static int position_of(int slot){for(int i=0;i<doc_count;i++)if(order[i]==slot)return i;return -1;}
static struct zui_scroll view;static int focus_find; /* 0 the text, 1 the find field, 2 the replace field */
static void switch_doc(int slot){
    if(current>=0)docs[current].scroll_y=view.offset;
    focus_find=0; /* a document that appears takes the keyboard */
    current=slot;
    if(slot>=0)zui_scroll_jump(&view,docs[slot].scroll_y);
    update_title();ui->dirty=1;
}
static void recovery_drop(struct doc *d);static void recovery_answer(int choice);
static void remove_doc(int slot){
    int p=position_of(slot);if(p<0)return;
    recovery_drop(&docs[slot]);   /* closed (saved, or the person chose not to): its recovery copy goes too */
    for(int i=p;i+1<doc_count;i++)order[i]=order[i+1];
    doc_count--;docs[slot].used=0;
    if(current==slot){current=-1;switch_doc(doc_count?order[p<doc_count?p:doc_count-1]:-1);}
    update_title();ui->dirty=1;
}
static void remember_disk(struct doc *d){
    struct au_info info;
    if(d->path[0]&&au_sys_info(d->path,&info)==0){d->disk_known=1;d->disk_size=info.size;d->disk_mtime=info.mtime;d->disk_mode=info.mode;}
    else d->disk_known=0;
    d->disk_state=0;
}

/* Notes in the status bar and modal messages. */
static char status_note[200];static int note_error;
static void note(const char *s,int error){copy(status_note,s,sizeof(status_note));note_error=error;ui->dirty=1;}
static struct zui_dialog dialog;static char dialog_title[160],dialog_message[400];
enum { ASK_NONE,ASK_CLOSE,ASK_QUIT,ASK_OVERWRITE,ASK_REPLACE_FILE,ASK_ERROR,ASK_RELOAD,ASK_RECOVER };
static int asking,asking_doc,quitting;
static void ask(int kind,int slot,const char *title,const char *message,const char *a,const char *b,const char *c){
    asking=kind;asking_doc=slot;copy(dialog_title,title,sizeof(dialog_title));copy(dialog_message,message,sizeof(dialog_message));
    dialog=(struct zui_dialog){1,900,dialog_title,dialog_message,{a,b,c},c?3:b?2:1};
    mark("AURORA_DIALOG",title);ui->dirty=1;
}
static void fail(const char *title,const char *message){ask(ASK_ERROR,-1,title,message,"OK",0,0);}

/* Loads path into a new document, or switches to it when it is open. 1 on success. */
static int open_path(const char *path){
    for(int i=0;i<doc_count;i++)if(same(docs[order[i]].path,path)){switch_doc(order[i]);return 1;}
    char m[PATH_BYTES+200],*o;
    long n=au_sys_read(path,staging,TEXT_BYTES);
    if(n<0){
        o=put(m,"\"");o=put(o,leaf(path));o=put(o,"\" cannot be opened: ");o=put(o,error_text(n));put(o,".");
        if(n==-27)put(o," Aurora opens files up to 1 MiB.");
        char line[PATH_BYTES+24];o=put(line,path);o=put(o," error=-");num(o,(unsigned long)-n);mark("AURORA_REFUSED",line);fail("Cannot open the file",m);return 0;
    }
    int check=au_check(staging,(unsigned int)n);
    if(check){
        o=put(m,"\"");o=put(o,leaf(path));put(o,check==AU_NOT_TEXT?"\" is a binary file. Aurora opens UTF-8 text only.":"\" is not UTF-8 text. Aurora opens UTF-8 text only.");
        mark("AURORA_REFUSED",path);fail("Cannot open the file",m);return 0;
    }
    /* Replace an empty, unchanged new document instead of keeping it beside this one. */
    struct doc *d=D();
    int reuse=d&&!d->path[0]&&!modified(d)&&!au_length(&d->text)&&!d->history.count?current:-1;
    int slot=reuse>=0?reuse:new_doc();
    if(slot<0){fail("Too many documents","Aurora keeps up to 8 documents open. Close one, then open the file again.");return 0;}
    d=&docs[slot];
    int crlf,bom;unsigned int bytes=au_normalize(staging,(unsigned int)n,&crlf,&bom);
    if(au_load(&d->text,staging,bytes)){
        if(reuse<0)remove_doc(slot);
        fail("Cannot open the file","The file has more lines than Aurora can hold (65,536).");return 0;
    }
    au_history_clear(&d->history);au_mark_saved(&d->history);
    copy(d->path,path,sizeof(d->path));copy(d->name,leaf(path),sizeof(d->name));
    d->language=language_of(d->name);d->crlf=crlf;d->bom=bom;d->caret=d->anchor=0;d->goal_valid=0;d->scroll_x=0;d->scroll_y=0;
    d->state_line=0;d->state_value=0;
    /* Indentation: tabs when the file indents with tabs. */
    int tabs=0,spaces=0;for(unsigned int l=0;l<d->text.line_count&&l<2000;l++){char c=au_at(&d->text,au_line_start(&d->text,l));tabs+=c=='\t';spaces+=c==' ';}
    d->spaces=!(tabs>spaces);
    remember_disk(d);
    current=-1;switch_doc(slot);
    char line[PATH_BYTES+80];o=put(line,path);o=put(o," bytes=");o=num(o,bytes);o=put(o," lines=");o=num(o,d->text.line_count);put(o,crlf?" crlf":" lf");
    mark("AURORA_OPENED",line);
    return 1;
}
static void new_document(void){
    int slot=new_doc();
    if(slot<0){fail("Too many documents","Aurora keeps up to 8 documents open. Close one first.");return;}
    name_untitled(&docs[slot]);current=-1;switch_doc(slot);mark("AURORA_NEW",docs[slot].name);
}

/* Saving: write a temporary file beside the target, then rename it over the target, so a failure (no space,
 * permission, a full disk mid-write) leaves the old file as it was. */
static int save_to(struct doc *d,const char *path){
    char folder[PATH_BYTES],temporary[PATH_BYTES],name[300],*o;
    parent(folder,path);o=put(name,".");copy(o,leaf(path),200);o=name+length(name);put(o,".aurora-save");
    join(temporary,folder,name);
    struct au_info old;int existed=au_sys_info(path,&old)==0;
    long fd=au_sys_create(temporary),result=fd<0?fd:0;
    unsigned long written=0;
    if(fd>=0){
        if(d->bom){static const char bom[3]={(char)0xef,(char)0xbb,(char)0xbf};long w=au_sys_write(fd,bom,3);if(w!=3)result=w<0?w:-28;else written+=3;}
        unsigned int position=0,end=au_length(&d->text);
        while(!result&&position<end){
            unsigned int n=au_encode(&d->text,&position,d->crlf,staging,65536);
            long w=au_sys_write(fd,staging,n);
            if(w!=(long)n){result=w<0?w:-28;break;}
            written+=n;
        }
        long c=au_sys_close(fd);if(!result&&c<0)result=c;
        if(!result&&existed)au_sys_chmod(temporary,old.mode&0xfff);
        if(!result)result=au_sys_rename(temporary,path);
        if(result)au_sys_unlink(temporary);
    }
    if(result){
        char m[PATH_BYTES+200];o=put(m,"\"");o=put(o,leaf(path));o=put(o,"\" was not saved: ");o=put(o,error_text(result));
        put(o,". Your text is still here, and the file on disk is unchanged.");
        char line[PATH_BYTES+24];o=put(line,path);o=put(o," error=-");num(o,(unsigned long)-result);mark("AURORA_SAVE_FAILED",line);fail("Cannot save",m);return 0;
    }
    copy(d->path,path,sizeof(d->path));copy(d->name,leaf(path),sizeof(d->name));d->language=language_of(d->name);
    au_mark_saved(&d->history);au_history_break(&d->history);remember_disk(d);recovery_drop(d);
    char line[PATH_BYTES+40];o=put(line,path);o=put(o," bytes=");num(o,written);mark("AURORA_SAVED",line);
    note("Saved",0);update_title();return 1;
}
static int changed_on_disk(struct doc *d){
    if(!d->path[0]||!d->disk_known)return 0;
    struct au_info info;
    if(au_sys_info(d->path,&info))return 2;
    return info.size!=d->disk_size||info.mtime!=d->disk_mtime;
}
static void open_file_dialog(int save);
static void saved_then(int slot);static void stop_closing(void);
static int save_doc(int slot,int force){
    struct doc *d=&docs[slot];
    if(!d->path[0]){switch_doc(slot);open_file_dialog(1);return 0;}
    if(!force&&changed_on_disk(d)==1){
        char m[400],*o=put(m,"\"");o=put(o,d->name);put(o,"\" was changed by another program since Aurora opened it. Saving replaces those changes.");
        ask(ASK_OVERWRITE,slot,"The file changed on disk",m,"Save anyway","Cancel",0);return 0;
    }
    return save_to(d,d->path);
}
static void reload(int slot){
    struct doc *d=&docs[slot];char path[PATH_BYTES];copy(path,d->path,sizeof(path));
    long n=au_sys_read(path,staging,TEXT_BYTES);
    if(n<0||au_check(staging,(unsigned int)n)){fail("Cannot reload","The file on disk cannot be read as UTF-8 text now. Your text is still here.");return;}
    int crlf,bom;unsigned int bytes=au_normalize(staging,(unsigned int)n,&crlf,&bom);
    if(au_load(&d->text,staging,bytes)){fail("Cannot reload","The file on disk is too large now. Your text is still here.");return;}
    au_history_clear(&d->history);au_mark_saved(&d->history);d->crlf=crlf;d->bom=bom;
    unsigned int len=au_length(&d->text);if(d->caret>len)d->caret=len;d->anchor=d->caret;d->state_line=0;d->state_value=0;
    remember_disk(d);mark("AURORA_RELOADED",path);update_title();ui->dirty=1;
}

/* Closing: ask about unsaved changes first. */
static int end_requested;
/* A document the person closed: the last one closing ends Aurora, like Horizon's last tab. */
static void closed(int slot){mark("AURORA_CLOSED_DOC",docs[slot].name);remove_doc(slot);if(!doc_count)end_requested=1;}
static void close_doc(int slot,int ask_first){
    struct doc *d=&docs[slot];
    if(ask_first&&modified(d)){
        char m[400],*o=put(m,"Your changes to \"");o=put(o,d->name);put(o,"\" are lost if you do not save them.");
        char t[200];o=put(t,"Save changes to \"");char n[64];copy(n,d->name,sizeof(n));o=put(o,n);put(o,"\"?");
        switch_doc(slot);ask(quitting?ASK_QUIT:ASK_CLOSE,slot,t,m,"Save","Don't save","Cancel");return;
    }
    closed(slot);
}
/* The next unsaved document while quitting, or 1 when Aurora may end. */
static int continue_quit(void){
    for(int i=0;i<doc_count;i++)if(modified(&docs[order[i]])){close_doc(order[i],1);return 0;}
    return 1;
}
static int close_after_save=-1;
/* After a successful save: finish a close or quit that asked for it. */
static void saved_then(int slot){
    if(close_after_save==slot){close_after_save=-1;closed(slot);}
    if(quitting&&continue_quit())end_requested=1;
}
static void stop_closing(void){close_after_save=-1;quitting=0;}
int aurora_close(void){
    quitting=1;
    if(continue_quit()){end_requested=1;return 1;}
    return 0;
}

/* ---------- Editing. */
static void touched(struct doc *d,unsigned int position){
    d->last_change=au_sys_clock();
    unsigned int line=au_line_of(&d->text,position);
    if((int)line<d->state_line){d->state_line=0;d->state_value=0;}
    update_title();ui->dirty=1;
}
static int reveal_caret,overwrite;   /* overwrite: Insert toggles typing over the next character */
static void replace_selection(struct doc *d,const char *s,unsigned int n,int typing){
    unsigned int a=selection_start(d),b=selection_end(d);
    if(a!=b)typing=0;
    if(!typing)au_history_break(&d->history);
    if(au_edit(&d->text,&d->history,a,b-a,s,n,d->caret,a+n,typing)){
        note("The document is full: Aurora holds up to 1 MiB and 65,536 lines.",1);return;
    }
    d->caret=d->anchor=a+n;d->goal_valid=0;touched(d,a);reveal_caret=1;
}
static void delete_range(struct doc *d,unsigned int a,unsigned int b){
    if(a>=b)return;
    au_history_break(&d->history);
    au_edit(&d->text,&d->history,a,b-a,"",0,d->caret,a,0);
    d->caret=d->anchor=a;d->goal_valid=0;touched(d,a);reveal_caret=1;
}
static unsigned int line_indent_end(const struct au_text *t,unsigned int line){
    unsigned int p=au_line_start(t,line),e=au_line_end(t,line);
    while(p<e&&(au_at(t,p)==' '||au_at(t,p)=='\t'))p++;
    return p;
}
static void newline(struct doc *d){
    unsigned int line=au_line_of(&d->text,selection_start(d)),start=au_line_start(&d->text,line),indent=line_indent_end(&d->text,line);
    if(indent>selection_start(d))indent=selection_start(d);
    char s[256];s[0]='\n';unsigned int n=1;
    for(unsigned int p=start;p<indent&&n<sizeof(s);p++)s[n++]=au_at(&d->text,p);
    replace_selection(d,s,n,0);
}
static void indent_lines(struct doc *d,int out){
    unsigned int a=selection_start(d),b=selection_end(d);
    unsigned int first=au_line_of(&d->text,a),last=au_line_of(&d->text,b);
    if(last>first&&b==au_line_start(&d->text,last))last--;
    au_history_break(&d->history);
    char unit[9];unsigned int units=d->spaces?(unsigned int)d->tab:1;
    for(unsigned int i=0;i<units;i++)unit[i]=d->spaces?' ':'\t';
    for(unsigned int l=first;l<=last;l++){
        unsigned int s=au_line_start(&d->text,l);
        if(out){
            unsigned int n=0;while(n<units&&au_at(&d->text,s+n)==' ')n++;
            if(!n&&au_at(&d->text,s)=='\t')n=1;
            if(n)au_edit(&d->text,&d->history,s,n,"",0,d->caret,d->caret,0);
        }else if(au_line_end(&d->text,l)>s||first==last)au_edit(&d->text,&d->history,s,0,unit,units,d->caret,d->caret,0);
    }
    d->anchor=au_line_start(&d->text,first);d->caret=au_line_end(&d->text,last);
    touched(d,d->anchor);
}
static void insert_tab(struct doc *d){
    if(has_selection(d)&&au_line_of(&d->text,selection_start(d))!=au_line_of(&d->text,selection_end(d))){indent_lines(d,0);return;}
    if(!d->spaces){replace_selection(d,"\t",1,1);return;}
    unsigned int column=au_column(&d->text,selection_start(d),(unsigned int)d->tab),n=(unsigned int)d->tab-column%(unsigned int)d->tab;
    replace_selection(d,"        ",n,1);
}
static int clipboard_copy(struct doc *d,int cut){
    if(!has_selection(d))return 0;
    unsigned int a=selection_start(d),b=selection_end(d);
    if(b-a>TEXT_BYTES)return 0;
    au_read(&d->text,a,b-a,staging);
    long r=au_sys_copy(staging,b-a);
    if(r<0){note(r==-38?"Copying needs a newer Zenith.":"The text was not copied.",1);return 0;}
    char n[24];num(n,b-a);mark(cut?"AURORA_CUT":"AURORA_COPIED",n);
    if(cut)delete_range(d,a,b);
    return 1;
}
static void clipboard_paste(struct doc *d){
    long n=au_sys_paste(staging,TEXT_BYTES);
    if(n<0){note(n==-38?"Pasting needs a newer Zenith.":n==-27?"The clipboard holds more than 1 MiB.":"Nothing to paste.",1);return;}
    if(au_check(staging,(unsigned int)n)){note("The clipboard does not hold UTF-8 text.",1);return;}
    int crlf,bom;unsigned int bytes=au_normalize(staging,(unsigned int)n,&crlf,&bom);
    replace_selection(d,staging,bytes,0);
    char t[24];num(t,bytes);mark("AURORA_PASTED",t);
}
static void undo(struct doc *d,int redo){
    unsigned int caret;
    if(redo?au_redo(&d->text,&d->history,&caret):au_undo(&d->text,&d->history,&caret)){
        unsigned int n=au_length(&d->text);d->caret=d->anchor=caret>n?n:caret;d->goal_valid=0;
        d->state_line=0;d->state_value=0;touched(d,0);reveal_caret=1;mark(redo?"AURORA_REDO":"AURORA_UNDO",0);
    }else note(redo?"Nothing to redo.":"Nothing to undo.",0);
}

/* ---------- Find and replace: plain text (Aa case, W whole word) or a regular expression (.*, regex.h) with $1..$9
 * in the replacement. Regex matches never cross a line end. */
#define AU_REGEX 4
static int find_open,replace_open,find_flags;
static char find_buffer[256],replace_buffer[256];
static struct zui_input find_input,replace_input;
static unsigned int find_total,find_index;
static struct rx find_rx;static char find_compiled[256];static int find_compiled_flags=-1;static const char *find_error;
/* Compiles the pattern when it or the flags changed; 0 when the expression is not valid. */
static int find_ready(void){
    if(!(find_flags&AU_REGEX)){find_error=0;return find_input.length>0;}
    if(!find_input.length)return 0;
    if(find_compiled_flags!=find_flags||!same(find_compiled,find_buffer)){
        copy(find_compiled,find_buffer,sizeof(find_compiled));find_compiled_flags=find_flags;
        find_error=rx_compile(&find_rx,find_buffer,find_input.length,find_flags&AU_CASE?0:RX_ICASE)?find_rx.error:0;
    }
    return !find_error;
}
/* The next match from `from` (forward) or before it, wrapping once; its start, or -1. */
static long find_match(struct doc *d,unsigned int from,int forward,unsigned int *end,long *groups,int wrap){
    unsigned int n=au_length(&d->text);long p;
    if(find_flags&AU_REGEX){
        p=forward?rx_search(&find_rx,&d->text,from,n,end,groups):rx_search_back(&find_rx,&d->text,from,end,groups);
        if(p<0&&wrap)p=forward?rx_search(&find_rx,&d->text,0,n,end,groups):rx_search_back(&find_rx,&d->text,n+1,end,groups);
        return p;
    }
    p=au_find(&d->text,find_buffer,find_input.length,from,forward,find_flags);
    if(p<0&&wrap)p=au_find(&d->text,find_buffer,find_input.length,forward?0:n,forward,find_flags);
    if(p>=0)*end=(unsigned int)p+find_input.length;
    return p;
}
static void count_matches(void){
    struct doc *d=D();find_total=find_index=0;
    if(!d||!find_ready())return;
    if(!(find_flags&AU_REGEX)){
        find_total=au_count(&d->text,find_buffer,find_input.length,find_flags,99999,selection_start(d),&find_index);
        if(!has_selection(d)||selection_end(d)-selection_start(d)!=find_input.length)find_index=0;
        return;
    }
    unsigned int position=0,end,n=au_length(&d->text);long p;
    while(find_total<9999&&position<=n&&(p=rx_search(&find_rx,&d->text,position,n,&end,0))>=0){
        find_total++;
        if((unsigned int)p==selection_start(d)&&end==selection_end(d))find_index=find_total;
        position=end>(unsigned int)p?end:(unsigned int)p+1;
    }
}
static int find_step(int forward,int from_selection_start){
    struct doc *d=D();if(!d||!find_ready())return 0;
    unsigned int from=forward?(from_selection_start?selection_start(d):selection_end(d)):selection_start(d),end;
    /* An empty regex match at the caret would repeat forever: step past it. */
    if(forward&&!from_selection_start&&!has_selection(d)&&(find_flags&AU_REGEX))from++;
    long p=find_match(d,from,forward,&end,0,1);
    if(p<0){count_matches();note("No results",0);return 0;}
    d->anchor=(unsigned int)p;d->caret=end;d->goal_valid=0;reveal_caret=1;
    count_matches();ui->dirty=1;
    char n[300],*o=num(n,find_total);*o++=' ';copy(o,find_buffer,sizeof(n)-(unsigned long)(o-n));mark("AURORA_FIND",n);
    return 1;
}
static void open_find(int replace){
    struct doc *d=D();if(!d)return;
    int was_open=find_open;
    find_open=1;replace_open|=replace;focus_find=replace&&find_input.length?2:1;
    /* Opening the bar takes the selected text as the search; Ctrl+H on an open bar keeps the pattern. */
    if(!was_open&&has_selection(d)&&selection_end(d)-selection_start(d)<sizeof(find_buffer)&&au_line_of(&d->text,selection_start(d))==au_line_of(&d->text,selection_end(d))){
        unsigned int n=au_read(&d->text,selection_start(d),selection_end(d)-selection_start(d),find_buffer);find_buffer[n]=0;
        zui_input_init(&find_input,find_buffer,sizeof(find_buffer));
    }
    count_matches();ui->dirty=1;
}
static char replacement[4096];
/* The replacement text for the match [a,end): literal, or with $0..$9 expanded for a regex. */
static unsigned int replacement_for(struct doc *d,const long *groups){
    if(!(find_flags&AU_REGEX)){copy(replacement,replace_buffer,sizeof(replacement));return replace_input.length;}
    return rx_expand(&d->text,replace_buffer,replace_input.length,groups,replacement,sizeof(replacement));
}
static void replace_one(void){
    struct doc *d=D();if(!d||!find_ready())return;
    unsigned int a=selection_start(d),b=selection_end(d),end;long groups[2*RX_GROUPS];
    long p=find_flags&AU_REGEX?rx_search(&find_rx,&d->text,a,a,&end,groups):au_find(&d->text,find_buffer,find_input.length,a,1,find_flags);
    if(!(find_flags&AU_REGEX))end=(unsigned int)p+find_input.length;
    if(p==(long)a&&end==b&&b>a){
        unsigned int n=replacement_for(d,groups);
        replace_selection(d,replacement,n,0);
        mark("AURORA_REPLACED","1");
    }
    find_step(1,0);
}
static void replace_all(void){
    struct doc *d=D();if(!d||!find_ready())return;
    au_history_break(&d->history);
    unsigned int position=0,done=0,end;long p,groups[2*RX_GROUPS];
    while(position<=au_length(&d->text)&&(p=find_match(d,position,1,&end,groups,0))>=0&&done<100000){
        unsigned int n=replacement_for(d,groups);
        if(au_edit(&d->text,&d->history,(unsigned int)p,end-(unsigned int)p,replacement,n,d->caret,(unsigned int)p,0)){note("The document is full.",1);break;}
        position=(unsigned int)p+n+(end==(unsigned int)p?1:0);done++;
    }
    unsigned int n=au_length(&d->text);if(d->caret>n)d->caret=n;d->anchor=d->caret;
    d->state_line=0;d->state_value=0;touched(d,0);count_matches();
    char m[80],*o=num(m,done);put(o,done==1?" replacement":" replacements");note(m,0);
    char c[24];num(c,done);mark("AURORA_REPLACED",c);
}

/* ---------- Go to line. */
static int goto_open;static char goto_buffer[16];static struct zui_input goto_input;
static void go_to_line(void){
    struct doc *d=D();if(!d)return;
    unsigned long line=0;for(unsigned int i=0;i<goto_input.length;i++){char c=goto_buffer[i];if(c<'0'||c>'9')return;line=line*10+(unsigned long)(c-'0');if(line>LINE_SLOTS)break;}
    if(!line)return;
    if(line>d->text.line_count)line=d->text.line_count;
    d->caret=d->anchor=au_line_start(&d->text,(unsigned int)line-1);d->goal_valid=0;reveal_caret=1;goto_open=0;ui->dirty=1;
    char n[24];num(n,line);mark("AURORA_GOTO",n);
}

/* ---------- File dialog (open and save as). */
struct entry { unsigned short name; unsigned char folder; };
static int file_dialog,dialog_save;static char dialog_folder[PATH_BYTES],dialog_error[160];
static struct entry entries[DIALOG_ENTRIES];static char names[65536];static unsigned int names_used;static int entry_count,entry_selected;
static char dialog_name_buffer[256];static struct zui_input dialog_name;static struct zui_scroll dialog_scroll;
static long last_click_time=-1000;static int last_click_id=-1;
static void add_entry(void *context,const char *name,int folder){
    (void)context;unsigned long n=length(name);
    if(name[0]=='.'||entry_count>=DIALOG_ENTRIES||names_used+n+1>sizeof(names))return;
    entries[entry_count].name=(unsigned short)names_used;entries[entry_count].folder=(unsigned char)!!folder;entry_count++;
    copy(names+names_used,name,n+1);names_used+=(unsigned int)n+1;
}
static const char *entry_name(int i){return names+entries[i].name;}
static int entry_before(int a,int b){
    if(entries[a].folder!=entries[b].folder)return entries[a].folder>entries[b].folder;
    const char *x=entry_name(a),*y=entry_name(b);
    while(*x&&lower(*x)==lower(*y)){x++;y++;}
    return lower(*x)<lower(*y);
}
static void list_folder(const char *folder){
    entry_count=0;names_used=0;entry_selected=-1;dialog_error[0]=0;
    long r=au_sys_list(folder,add_entry,0);
    if(r<0){put(put(dialog_error,"This folder cannot be read: "),error_text(r));}
    for(int i=1;i<entry_count;i++){struct entry e=entries[i];int j=i;while(j>0&&entry_before(j,j-1)){entries[j]=entries[j-1];entries[j-1]=e;j--;}}
    copy(dialog_folder,folder,sizeof(dialog_folder));zui_scroll_jump(&dialog_scroll,0);ui->dirty=1;
}
static void open_file_dialog(int save){
    struct doc *d=D();
    file_dialog=1;dialog_save=save;
    char folder[PATH_BYTES];
    if(d&&d->path[0])parent(folder,d->path);else copy(folder,home,sizeof(folder));
    dialog_name_buffer[0]=0;if(save&&d)copy(dialog_name_buffer,d->path[0]?d->name:"untitled.txt",sizeof(dialog_name_buffer));
    zui_input_init(&dialog_name,dialog_name_buffer,sizeof(dialog_name_buffer));
    list_folder(folder);mark("AURORA_FILE_DIALOG",save?"save":"open");
}
static char pending_save_path[PATH_BYTES];
static void dialog_choose(void){
    char path[PATH_BYTES];
    if(dialog_name.length&&dialog_name_buffer[0]=='/')copy(path,dialog_name_buffer,sizeof(path));
    else if(dialog_name.length)join(path,dialog_folder,dialog_name_buffer);
    else if(entry_selected>=0)join(path,dialog_folder,entry_name(entry_selected));
    else return;
    struct au_info info;int exists=au_sys_info(path,&info)==0;
    if(exists&&info.is_dir){dialog_name_buffer[0]=0;zui_input_init(&dialog_name,dialog_name_buffer,sizeof(dialog_name_buffer));list_folder(path);return;}
    if(!dialog_save){
        if(!exists){copy(dialog_error,"There is no such file.",sizeof(dialog_error));ui->dirty=1;return;}
        file_dialog=0;open_path(path);ui->dirty=1;return;
    }
    struct doc *d=D();if(!d){file_dialog=0;return;}
    copy(pending_save_path,path,sizeof(pending_save_path));
    if(exists&&!same(path,d->path)){
        char m[400],*o=put(m,"\"");o=put(o,leaf(path));put(o,"\" already exists. Replacing it overwrites its contents.");
        file_dialog=0;ask(ASK_REPLACE_FILE,current,"Replace the file?",m,"Replace","Cancel",0);return;
    }
    file_dialog=0;
    if(save_to(d,path))saved_then(current);else stop_closing();
}

/* ---------- Layout. */
#define TAB_Y 8
#define TAB_H 36
#define CRUMB_H 26
#define STATUS_H 30
#define FIND_H 50
#define BANNER_H 40
enum { H_NONE,H_TEXT,H_GUTTER,H_MINIMAP,H_HSCROLL,H_REGEX,H_OVERWRITE,H_NEW,H_OPEN,H_MENU_BUTTON,H_FIND_FIELD,H_REPLACE_FIELD,H_CASE,H_WORD,H_PREVIOUS,H_NEXT,
       H_REPLACE,H_ALL,H_FIND_CLOSE,H_EOL,H_INDENT,H_RELOAD,H_KEEP,H_GOTO_FIELD,H_DIALOG_UP,H_DIALOG_FIELD,H_DIALOG_OK,
       H_DIALOG_CANCEL,H_DIALOG_LIST,H_MODAL,H_DOC=100,H_ENTRY=200,H_MENU=1000,H_TREE=1200,H_SYMBOL=1500 };
struct hit_area { struct zui_rect r; int id; };
static struct hit_area hits[400];static int hit_count,hover=H_NONE;
static void hit(struct zui_rect r,int id){if(hit_count<400){hits[hit_count].r=r;hits[hit_count].id=id;hit_count++;}}
static int hit_at(int x,int y){for(int i=hit_count-1;i>=0;i--)if(inside(hits[i].r,x,y))return hits[i].id;return H_NONE;}
static int sidebar_w(void){return ui->width>=820?(ui->width>=1100?240:208):0;}
static struct zui_rect editor_area,text_area;static int gutter_w,cell_w,line_h;
static struct zui_tabs strip;static struct zui_rect tabs_rect;

static int icon(const char *name,int x,int y,int size,int role){
    if(zui_icon(ui,name,x,y,size,role))return 1;
    unsigned int c=role==ZUI_ICON_DISABLED?ui->theme.secondary:role==ZUI_ICON_TEXT?ui->theme.text:ui->theme.accent;
    int u=size/8;
    if(same(name,"plus")){zui_fill(ui,(struct zui_rect){x+size/2-1,y+u,2,size-2*u},c);zui_fill(ui,(struct zui_rect){x+u,y+size/2-1,size-2*u,2},c);return 1;}
    if(same(name,"menu")){for(int i=0;i<3;i++)zui_fill(ui,(struct zui_rect){x+u,y+3*u+i*u*2-1,size-2*u,2},c);return 1;}
    if(same(name,"x")){for(int i=u;i<size-u;i++){zui_fill(ui,(struct zui_rect){x+i,y+i,2,2},c);zui_fill(ui,(struct zui_rect){x+size-2-i,y+i,2,2},c);}return 1;}
    if(same(name,"arrow-up")||same(name,"chevron-up")){for(int i=0;i<size/3;i++)zui_fill(ui,(struct zui_rect){x+size/2-i,y+size/3+i,2*i+1,1},c);return 1;}
    if(same(name,"arrow-down")||same(name,"chevron-down")){for(int i=0;i<size/3;i++)zui_fill(ui,(struct zui_rect){x+size/2-i,y+size*2/3-i,2*i+1,1},c);return 1;}
    if(same(name,"folder")||same(name,"folder-open")){zui_round(ui,(struct zui_rect){x+u,y+2*u,size*3/8,2*u},u,c);zui_round(ui,(struct zui_rect){x+u,y+3*u,size-2*u,size-5*u},u,c);return 1;}
    if(same(name,"file-text")||same(name,"file")){zui_round_outline(ui,(struct zui_rect){x+size/5,y+u,size*3/5,size-2*u},2,c);return 1;}
    if(same(name,"search")){zui_round_outline(ui,(struct zui_rect){x+u,y+u,size/2+u,size/2+u},size/4,c);zui_fill(ui,(struct zui_rect){x+size*5/8,y+size*5/8,size/4,2},c);return 1;}
    return 0;
}
static void icon_button(struct zui_rect r,const char *name,int id,int enabled,int active){
    if(active)zui_round(ui,r,8,mix(ui->theme.raised,ui->theme.accent,22));
    else if(enabled&&hover==id)zui_round(ui,r,8,mix(ui->theme.raised,ui->theme.text,8));
    icon(name,r.x+(r.w-16)/2,r.y+(r.h-16)/2,16,!enabled?ZUI_ICON_DISABLED:active?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
    if(enabled)hit(r,id);
}
static void text_button(struct zui_rect r,const char *label,int id,int primary,int enabled,int active){
    unsigned int fill=primary?ui->theme.accent:active?mix(ui->theme.raised,ui->theme.accent,22):hover==id&&enabled?mix(ui->theme.raised,ui->theme.text,8):ui->theme.raised;
    zui_round(ui,r,8,primary?fill:active?ui->theme.accent:ui->theme.border);
    if(!primary)zui_round(ui,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},7,fill);
    unsigned int color=!enabled?ui->theme.secondary:primary?ui->theme.on_accent:ui->theme.text;
    const struct zui_font *f=primary||active?&zui_label:&zui_body;
    int w=measure(label,f);text(r.x+(r.w-w)/2,r.y+(r.h-f->height)/2,label,f,color);
    if(enabled)hit(r,id);
}
static void field_frame(struct zui_rect r,int focused){
    zui_round(ui,r,8,focused?ui->theme.accent:ui->theme.border);
    zui_round(ui,(struct zui_rect){r.x+1,r.y+1,r.w-2,r.h-2},7,ui->theme.surface);
}
static void input(struct zui_rect r,struct zui_input *in,int focused,int id,const char *placeholder){
    field_frame(r,focused);
    if(!in->length&&placeholder)text(r.x+10,r.y+(r.h-zui_body.height)/2,placeholder,&zui_body,ui->theme.secondary);
    zui_input_draw(ui,(struct zui_rect){r.x+2,r.y+1,r.w-4,r.h-2},in,focused);
    hit(r,id);
}
/* The window's material (the desktop's blurred backdrop) is used where the concept puts chrome: the whole
 * window when the person chose transparent app backgrounds, else the sidebar. Code is always on an opaque
 * surface ("effects stay off over code"). */
static int see_through(void){return ui->foreground&&ui->full_background&&!ui->theme_preview;}
static void panel(struct zui_rect r,int radius){
    if(see_through())zui_round_alpha(ui,r,radius,ui->theme.surface,40);
    else zui_round(ui,r,radius,ui->theme.surface);
}

/* ---------- Syntax colours, from the accent palette only. */
enum { K_TEXT,K_KEYWORD,K_TYPE,K_STRING,K_NUMBER,K_COMMENT,K_MARK };
static const char *const c_words[]={"if","else","for","while","do","switch","case","default","break","continue","return","goto",
    "sizeof","typedef","struct","union","enum","static","const","extern","inline","volatile","void","char","short","int","long",
    "float","double","signed","unsigned","bool","true","false","null","NULL","nullptr","class","public","private","protected",
    "internal","sealed","abstract","override","virtual","new","delete","this","base","using","namespace","var","let","fn","func",
    "package","import","from","export","function","async","await","yield","try","catch","finally","throw","throws","readonly",
    "ref","out","in","is","as","interface","impl","trait","pub","mut","match","loop","where","self","mod","crate","type","defer",
    "go","chan","select","range","string","object","uint","ulong","ushort","byte","sbyte","decimal","get","set","init","record",0};
static const char *const hash_words[]={"def","class","if","elif","else","for","while","in","not","and","or","is","return","import",
    "from","as","with","try","except","finally","raise","pass","break","continue","lambda","yield","global","nonlocal","None","True",
    "False","async","await","then","fi","do","done","case","esac","function","local","export","true","false","null",0};
static const char *const json_words[]={"true","false","null",0};
static int word_in(const char *s,int n,const char *const *list){
    for(int k=0;list[k];k++){int i=0;while(i<n&&list[k][i]==s[i])i++;if(i==n&&!list[k][n])return 1;}
    return 0;
}
static int ident_start(char c){return (c>='a'&&c<='z')||(c>='A'&&c<='Z')||c=='_';}
static int ident_char(char c){return ident_start(c)||(c>='0'&&c<='9');}
/* Classifies one line's bytes. state: 1 inside a block comment (C), 2 inside a fenced block (Markdown). */
static int classify(int language,const char *s,int n,int state,unsigned char *k){
    for(int i=0;i<n;i++)k[i]=K_TEXT;
    if(language==LANG_PLAIN)return 0;
    if(language==LANG_MARKDOWN){
        int fence=n>=3&&s[0]=='`'&&s[1]=='`'&&s[2]=='`';
        if(state==2||fence){for(int i=0;i<n;i++)k[i]=K_STRING;return fence?(state==2?0:2):2;}
        if(n&&s[0]=='#'){for(int i=0;i<n;i++)k[i]=K_MARK;return 0;}
        int code=0;for(int i=0;i<n;i++){if(s[i]=='`')code=!code;if(code||s[i]=='`')k[i]=K_STRING;}
        if(n>1&&(s[0]=='-'||s[0]=='*')&&s[1]==' ')k[0]=K_KEYWORD;
        return 0;
    }
    int i=0;
    if(language==LANG_C&&state==1){
        while(i<n){k[i]=K_COMMENT;if(s[i]=='*'&&i+1<n&&s[i+1]=='/'){k[i+1]=K_COMMENT;i+=2;state=0;break;}i++;}
        if(state)return 1;
    }
    int first=1;
    while(i<n){
        char c=s[i];
        if(c==' '||c=='\t'){i++;continue;}
        if(language==LANG_C&&c=='#'&&first){for(;i<n;i++)k[i]=K_KEYWORD;break;}
        first=0;
        if((language==LANG_C||language==LANG_JSON)&&c=='/'&&i+1<n&&s[i+1]=='/'){for(;i<n;i++)k[i]=K_COMMENT;break;}
        if(language==LANG_C&&c=='/'&&i+1<n&&s[i+1]=='*'){
            k[i]=k[i+1]=K_COMMENT;i+=2;state=1;
            while(i<n){k[i]=K_COMMENT;if(s[i]=='*'&&i+1<n&&s[i+1]=='/'){k[i+1]=K_COMMENT;i+=2;state=0;break;}i++;}
            continue;
        }
        if(language==LANG_HASH&&c=='#'){for(;i<n;i++)k[i]=K_COMMENT;break;}
        if(c=='"'||c=='\''||(c=='`'&&language==LANG_C)){
            int j=i+1;
            while(j<n&&s[j]!=c){if(s[j]=='\\')j++;j++;}
            if(j<n){j++;}
            if(j>n){j=n;}
            for(int q=i;q<j;q++){k[q]=K_STRING;}
            i=j;continue;
        }
        if(c>='0'&&c<='9'&&(i==0||!ident_char(s[i-1]))){
            int j=i;
            while(j<n&&(ident_char(s[j])||s[j]=='.'))j++;
            for(int q=i;q<j;q++){k[q]=K_NUMBER;}
            i=j;continue;
        }
        if(ident_start(c)){
            int j=i;
            while(j<n&&ident_char(s[j]))j++;
            const char *const *list=language==LANG_C?c_words:language==LANG_HASH?hash_words:json_words;
            int kind=word_in(s+i,j-i,list)?K_KEYWORD:language==LANG_C&&c>='A'&&c<='Z'&&j-i>1?K_TYPE:K_TEXT;
            for(int q=i;q<j;q++){k[q]=(unsigned char)kind;}
            i=j;continue;
        }
        i++;
    }
    return language==LANG_C?state:0;
}
static unsigned int kind_color(int kind){
    switch(kind){
    case K_KEYWORD:return zui_accent_color(ui,1);
    case K_TYPE:return zui_accent_color(ui,5);
    case K_STRING:return zui_accent_color(ui,4);
    case K_NUMBER:return zui_accent_color(ui,3);
    case K_COMMENT:return ui->theme.secondary;
    case K_MARK:return ui->theme.accent;
    default:return ui->theme.text;
    }
}
/* Syntax state at the start of a line, continuing from the last one computed when possible. */
static int state_at(struct doc *d,unsigned int line){
    if(d->language!=LANG_C&&d->language!=LANG_MARKDOWN)return 0;
    if((unsigned int)d->state_line>line){d->state_line=0;d->state_value=0;}
    int state=d->state_value;
    for(unsigned int l=(unsigned int)d->state_line;l<line;l++){
        unsigned int s=au_line_start(&d->text,l),e=au_line_end(&d->text,l);
        unsigned int n=au_read(&d->text,s,min((int)(e-s),LINE_DRAW),line_bytes);
        state=classify(d->language,line_bytes,(int)n,state,line_class);
    }
    d->state_line=(int)line;d->state_value=state;return state;
}

/* ---------- Drawing. */
static void draw_tabs(struct zui_rect r){
    static struct zui_tab items[MAX_DOCS];
    int active=0;
    for(int i=0;i<doc_count;i++){struct doc *d=&docs[order[i]];items[i]=(struct zui_tab){d->name,modified(d),"file-text"};if(order[i]==current)active=i;}
    zui_tabs_draw(ui,&strip,(struct zui_rect){r.x,r.y,r.w-44,r.h},items,doc_count,active,doc_count<MAX_DOCS);
    icon_button((struct zui_rect){r.x+r.w-36,r.y+2,36,32},"menu",H_MENU_BUTTON,1,0);
}
/* ---------- Project (the document's folder as a tree) and Outline (its classes, functions and headings). */
#define TREE_ROWS 256
#define TREE_OPEN 16
struct tree_row { char name[96]; unsigned char depth,folder,open; };
static struct tree_row tree[TREE_ROWS];static char tree_paths[TREE_ROWS][256];static int tree_count,tree_truncated,tree_dirty=1;
static char tree_root[PATH_BYTES],tree_opened[TREE_OPEN][256];static int tree_open_count;
static struct zui_scroll side_scroll;static struct zui_rect side_rect;
static char list_names[6][128][96];static unsigned char list_folders[6][128];static int list_count[6],list_depth;
static void collect(void *context,const char *name,int folder){
    (void)context;int d=list_depth;
    if(name[0]=='.'||list_count[d]>=128)return;
    copy(list_names[d][list_count[d]],name,96);list_folders[d][list_count[d]]=(unsigned char)!!folder;list_count[d]++;
}
static int folder_open(const char *rel){for(int i=0;i<tree_open_count;i++)if(same(tree_opened[i],rel))return 1;return 0;}
static void add_children(const char *rel,int depth){
    if(depth>5)return;
    char folder[PATH_BYTES];if(rel[0])join(folder,tree_root,rel);else copy(folder,tree_root,sizeof(folder));
    list_depth=depth;list_count[depth]=0;
    if(au_sys_list(folder,collect,0)<0)return;
    int n=list_count[depth];
    /* Folders first, then names, case-insensitive. */
    for(int i=1;i<n;i++)for(int j=i;j>0;j--){
        int a=j-1,b=j,swap=list_folders[depth][a]!=list_folders[depth][b]?list_folders[depth][b]>list_folders[depth][a]:0;
        if(list_folders[depth][a]==list_folders[depth][b]){const char *x=list_names[depth][a],*y=list_names[depth][b];while(*x&&lower(*x)==lower(*y)){x++;y++;}swap=lower(*x)>lower(*y);}
        if(!swap)break;
        char tmp[96];copy(tmp,list_names[depth][a],96);copy(list_names[depth][a],list_names[depth][b],96);copy(list_names[depth][b],tmp,96);
        unsigned char f=list_folders[depth][a];list_folders[depth][a]=list_folders[depth][b];list_folders[depth][b]=f;
    }
    for(int i=0;i<n;i++){
        if(tree_count>=TREE_ROWS){tree_truncated=1;return;}
        struct tree_row *row=&tree[tree_count];copy(row->name,list_names[depth][i],sizeof(row->name));row->depth=(unsigned char)depth;row->folder=list_folders[depth][i];
        char path[256];if(rel[0]){copy(path,rel,sizeof(path));unsigned long k=length(path);if(k+1<sizeof(path)){path[k]='/';copy(path+k+1,row->name,sizeof(path)-k-1);}}else copy(path,row->name,sizeof(path));
        copy(tree_paths[tree_count],path,256);row->open=row->folder&&folder_open(path);tree_count++;
        if(row->open){add_children(path,depth+1);list_depth=depth;}
    }
}
static void project_refresh(void){
    char root[PATH_BYTES];struct doc *d=D();
    if(d&&d->path[0])parent(root,d->path);else copy(root,home,sizeof(root));
    if(!same(root,tree_root)){copy(tree_root,root,sizeof(tree_root));tree_open_count=0;tree_dirty=1;}
    if(!tree_dirty)return;
    tree_dirty=0;tree_count=0;tree_truncated=0;add_children("",0);
    char n[24];num(n,(unsigned long)tree_count);mark("AURORA_PROJECT",n);
}
static void toggle_folder(int i){
    if(i<0||i>=tree_count||!tree[i].folder)return;
    if(tree[i].open){int k=0;for(int j=0;j<tree_open_count;j++)if(!same(tree_opened[j],tree_paths[i]))copy(tree_opened[k++],tree_opened[j],256);tree_open_count=k;}
    else if(tree_open_count<TREE_OPEN)copy(tree_opened[tree_open_count++],tree_paths[i],256);
    tree_dirty=1;ui->dirty=1;
}
#define SYMBOLS 200
struct symbol { char name[64]; unsigned int line; unsigned char depth,kind; };
static struct symbol symbols[SYMBOLS];static int symbol_count,outline_slot=-1;static unsigned long outline_stamp;
static int ident(char c){return (c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||c=='$';}
static int word_is(const char *s,int n,const char *w){int i=0;while(i<n&&w[i]&&s[i]==w[i])i++;return i==n&&!w[i];}
static void add_symbol(const char *s,int n,unsigned int line,int depth,int kind){
    if(symbol_count>=SYMBOLS||n<=0)return;
    struct symbol *y=&symbols[symbol_count++];if(n>63)n=63;for(int i=0;i<n;i++)y->name[i]=s[i];y->name[n]=0;
    y->line=line;y->depth=(unsigned char)(depth>4?4:depth);y->kind=(unsigned char)kind;
}
/* One line: a type (kind 1) or a function (kind 0), by language; heuristics, not a parser. */
static void outline_line(int language,const char *s,int n,unsigned int line){
    int indent=0,i=0;while(i<n&&(s[i]==' '||s[i]=='\t')){indent+=s[i]=='\t'?4:1;i++;}
    if(i>=n)return;
    int depth=indent/4;
    if(language==LANG_MARKDOWN){if(s[0]!='#')return;int h=0;while(h<n&&s[h]=='#')h++;int a=h;while(a<n&&s[a]==' ')a++;add_symbol(s+a,n-a,line,h-1,h==1);return;}
    if(language==LANG_HASH){
        for(int k=0;k<3;k++){static const char *const kw[]={"def ","class ","function "};int l=(int)length(kw[k]);
            if(n-i>l&&word_is(s+i,l,kw[k])){int a=i+l,b=a;while(b<n&&ident(s[b]))b++;add_symbol(s+a,b-a,line,depth,k==1);return;}}
        if(!indent){int b=i;while(b<n&&ident(s[b]))b++;if(b+1<n&&s[b]=='('&&s[b+1]==')'&&b>i)add_symbol(s+i,b-i,line,0,0);}
        return;
    }
    if(language!=LANG_C)return;
    if(s[i]=='#'||s[i]=='/'||s[i]=='*'||s[i]=='.'||s[i]=='}')return;
    /* Types: modifiers, then class/struct/interface/enum/namespace/record/trait/impl/union and a name. */
    static const char *const mods[]={"public","private","protected","internal","static","sealed","abstract","partial","readonly","export","default","pub","unsafe","extern","async","virtual","override","const","inline","final","data",0};
    static const char *const types[]={"class","struct","interface","enum","namespace","record","trait","impl","union",0};
    int p=i;
    for(int guard=0;guard<6&&p<n;guard++){
        int b=p;while(b<n&&ident(s[b]))b++;
        int is_mod=0;for(int k=0;mods[k];k++)if(word_is(s+p,b-p,mods[k]))is_mod=1;
        int is_type=0;for(int k=0;types[k];k++)if(word_is(s+p,b-p,types[k]))is_type=1;
        if(is_type){int a=b;while(a<n&&s[a]==' ')a++;int e=a;while(e<n&&ident(s[e]))e++;if(e>a)add_symbol(s+a,e-a,line,depth,1);return;}
        if(!is_mod||b==p)break;
        p=b;while(p<n&&s[p]==' ')p++;
    }
    /* Functions: "type name(" or "fn/func/function name(", not a call or a control statement, not ending in ";". */
    int end=n;while(end>i&&(s[end-1]==' '||s[end-1]=='\r'))end--;
    if(end>i&&s[end-1]==';')return;
    for(int q=i;q<end;q++){
        if(s[q]=='='&&(q+1>=end||s[q+1]!='>'))return;
        if(s[q]!='(')continue;
        int b=q;while(b>i&&s[b-1]==' ')b--;
        int a=b;while(a>i&&ident(s[a-1]))a--;
        if(a==b)return;
        static const char *const skip[]={"if","for","while","switch","return","catch","foreach","using","lock","sizeof","new","else","do","when","typeof","nameof","throw","await","fixed","checked","base","this",0};
        for(int k=0;skip[k];k++)if(word_is(s+a,b-a,skip[k]))return;
        if(a==i&&!(word_is(s+i,b-a,"function")))return;   /* a bare call: no return type before the name */
        add_symbol(s+a,b-a,line,depth,0);return;
    }
}
static void outline_refresh(struct doc *d){
    unsigned long stamp=(unsigned long)au_length(&d->text)*31+d->history.current*7+d->text.line_count+(unsigned long)d->language*101;
    if(outline_slot==current&&outline_stamp==stamp)return;
    outline_slot=current;outline_stamp=stamp;symbol_count=0;
    if(d->language==LANG_PLAIN||d->language==LANG_JSON)return;
    static char s[256];
    for(unsigned int l=0;l<d->text.line_count&&l<50000&&symbol_count<SYMBOLS;l++){
        unsigned int a=au_line_start(&d->text,l),e=au_line_end(&d->text,l);
        int n=(int)au_read(&d->text,a,min((int)(e-a),255),s);
        outline_line(d->language,s,n,l);
    }
    char line[400],*o=num(line,(unsigned long)symbol_count);
    for(int i=0;i<symbol_count&&i<6;i++){*o++=' ';o=put(o,symbols[i].name);}
    mark("AURORA_OUTLINE",line);
}
static void draw_sidebar(struct zui_rect r){
    int y=r.y+8;
    text(r.x+12,y+6,"Documents",&zui_label,ui->theme.text);
    icon_button((struct zui_rect){r.x+r.w-72,y,32,30},"folder-open",H_OPEN,1,0);
    icon_button((struct zui_rect){r.x+r.w-36,y,32,30},"plus",H_NEW,doc_count<MAX_DOCS,0);
    y+=42;
    for(int i=0;i<doc_count;i++){
        struct doc *d=&docs[order[i]];struct zui_rect row={r.x+4,y,r.w-8,34};
        int on=order[i]==current;
        if(on)zui_round(ui,row,8,mix(ui->theme.raised,ui->theme.accent,16));
        else if(hover==H_DOC+i)zui_round(ui,row,8,mix(ui->theme.raised,ui->theme.text,6));
        if(on)zui_round(ui,(struct zui_rect){row.x,row.y+8,3,18},1,ui->theme.accent);
        icon("file-text",row.x+12,row.y+9,16,on?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
        fit(row.x+38,row.y+(34-zui_body.height)/2,d->name,row.w-70,on?&zui_label:&zui_body,ui->theme.text);
        if(modified(d))zui_round(ui,(struct zui_rect){row.x+row.w-20,row.y+13,8,8},4,ui->theme.accent);
        hit(row,H_DOC+i);y+=38;
    }
    /* Project and Outline scroll together below the documents. */
    struct zui_rect area={r.x,y+4,r.w,r.y+r.h-y-8};
    if(area.h<60)return;
    side_rect=area;
    int top=zui_scroll_begin(ui,&side_scroll,area),yy=top;
    if(current>=0){project_refresh();outline_refresh(&docs[current]);}
    text(r.x+12,yy+6,"Project",&zui_label,ui->theme.text);
    fit(r.x+80,yy+8,leaf(tree_root),r.w-92,&zui_caption,ui->theme.secondary);yy+=32;
    for(int i=0;i<tree_count;i++,yy+=28){
        if(yy+28<area.y||yy>area.y+area.h)continue;
        struct zui_rect row={r.x+4,yy,r.w-8,26};int x=r.x+12+tree[i].depth*14;
        if(hover==H_TREE+i)zui_round(ui,row,6,mix(ui->theme.raised,ui->theme.text,6));
        char full[PATH_BYTES];join(full,tree_root,tree_paths[i]);
        int open_doc=current>=0&&same(docs[current].path,full);
        if(tree[i].folder)icon(tree[i].open?"chevron-down":"folder",x,yy+5,16,ZUI_ICON_NORMAL);else icon("file-text",x,yy+5,16,open_doc?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
        fit(x+22,yy+(26-zui_body.height)/2,tree[i].name,r.x+r.w-x-34,open_doc?&zui_label:&zui_body,ui->theme.text);
        hit(row,H_TREE+i);
    }
    if(tree_truncated){text(r.x+12,yy+4,"...",&zui_caption,ui->theme.secondary);yy+=24;}
    yy+=10;text(r.x+12,yy+6,"Outline",&zui_label,ui->theme.text);yy+=32;
    if(!symbol_count){text(r.x+12,yy+2,current>=0&&docs[current].language!=LANG_PLAIN?"No symbols found.":"No outline for plain text.",&zui_caption,ui->theme.secondary);yy+=24;}
    for(int i=0;i<symbol_count;i++,yy+=26){
        if(yy+26<area.y||yy>area.y+area.h)continue;
        struct zui_rect row={r.x+4,yy,r.w-8,24};
        if(hover==H_SYMBOL+i)zui_round(ui,row,6,mix(ui->theme.raised,ui->theme.text,6));
        fit(r.x+12+symbols[i].depth*12,yy+(24-zui_mono.height)/2,symbols[i].name,r.w-24-symbols[i].depth*12,&zui_mono,symbols[i].kind?zui_accent_color(ui,1):ui->theme.secondary);
        hit(row,H_SYMBOL+i);
    }
    zui_scroll_end(ui,&side_scroll,yy-top+8);
}
static void draw_crumbs(struct zui_rect r,struct doc *d){
    char t[PATH_BYTES+8];
    if(d->path[0]){char f[PATH_BYTES];parent(f,d->path);copy(t,f,sizeof(t));}
    else copy(t,"Not saved yet",sizeof(t));
    fit(r.x+8,r.y+(r.h-zui_caption.height)/2,t,r.w-16,&zui_caption,ui->theme.secondary);
}
/* A find match at byte i of the drawn line (n bytes): the same rule as au_find, within the line. */
static int line_match(unsigned int i,unsigned int n){
    unsigned int k=find_input.length;
    for(unsigned int q=0;q<k;q++){char a=line_bytes[i+q],b=find_buffer[q];if(find_flags&AU_CASE?a!=b:lower(a)!=lower(b))return 0;}
    if(find_flags&AU_WORD){if(i&&au_word_char(line_bytes[i-1]))return 0;if(i+k<n&&au_word_char(line_bytes[i+k]))return 0;}
    return 1;
}
/* The bracket beside the caret and its partner (scanning at most 20000 bytes), or 0. */
static int bracket_pair(struct doc *d,unsigned int *a,unsigned int *b){
    static const char open[]="([{",close[]=")]}";
    unsigned int n=au_length(&d->text),c=d->caret;
    for(int side=0;side<2;side++){
        if(side==0&&c>=n)continue;
        if(side==1&&!c)continue;
        unsigned int at=side==0?c:c-1;char ch=au_at(&d->text,at);
        for(int k=0;k<3;k++){
            if(ch==open[k]){int depth=0;for(unsigned int p=at;p<n&&p<at+20000;p++){char x=au_at(&d->text,p);if(x==open[k])depth++;else if(x==close[k]&&--depth==0){*a=at;*b=p;return 1;}}return 0;}
            if(ch==close[k]){int depth=0;for(unsigned int p=at+1;p-->0&&p+20000>at;){char x=au_at(&d->text,p);if(x==close[k])depth++;else if(x==open[k]&&--depth==0){*a=p;*b=at;return 1;}}return 0;}
        }
    }
    return 0;
}
static struct zui_rect minimap_rect,hbar_rect;static int hscroll_range,hscroll_visible;
static void outline_at(struct doc *d,unsigned int position,int top,int text_x,unsigned int color){
    unsigned int line=au_line_of(&d->text,position);
    int y=top+(int)line*line_h,x=text_x+(int)au_column(&d->text,position,(unsigned int)d->tab)*cell_w;
    zui_round_outline(ui,(struct zui_rect){x-1,y,cell_w+2,line_h},3,color);
}
static void draw_minimap(struct doc *d,struct zui_rect m){
    zui_fill(ui,m,mix(ui->theme.surface,ui->theme.subtle,60));
    unsigned int lines=d->text.line_count;int dense=(long)lines*2>m.h;
    for(int row=0;row<m.h;row+=2){
        unsigned int l=dense?(unsigned int)((long)row*lines/m.h):(unsigned int)(row/2);
        if(l>=lines)break;
        unsigned int s=au_line_start(&d->text,l),e=au_line_end(&d->text,l),indent=0,length=0;char first=0;
        for(unsigned int q=s;q<e&&length<120;q++){char c=au_at(&d->text,q);if(!first&&(c==' '||c=='\t')){indent+=c=='\t'?4:1;continue;}if(!first)first=c;length++;}
        if(!length)continue;
        int x=m.x+4+min((int)indent,30),w=min((int)length,m.w-8-min((int)indent,30));
        unsigned int color=first=='/'||first=='#'?mix(ui->theme.surface,ui->theme.secondary,45):mix(ui->theme.surface,ui->theme.text,38);
        if(w>0)zui_fill(ui,(struct zui_rect){x,m.y+row,w,1},color);
    }
    /* The visible part. */
    long content=(long)lines*line_h;if(content<1)content=1;
    int y0=dense?(int)((long)view.visual*m.h/content):view.visual/line_h*2,h=dense?(int)((long)view.rect.h*m.h/content):view.rect.h/line_h*2;
    if(h<6)h=6;
    zui_round_alpha(ui,(struct zui_rect){m.x,m.y+y0,m.w,min(h,m.h-y0)},4,ui->theme.accent,50);
    hit(m,H_MINIMAP);
}
static void draw_text_area(struct doc *d){
    struct zui_rect a=text_area;
    zui_round(ui,a,10,ui->theme.surface);
    int mm=a.w>=700?78:0;
    struct zui_rect scroll_rect={a.x,a.y+6,a.w-(mm?mm+8:0),a.h-12-12};
    int top=zui_scroll_begin(ui,&view,scroll_rect);
    unsigned int lines=d->text.line_count;
    int first=view.visual/line_h;if(first<0)first=0;
    unsigned int caret_line=au_line_of(&d->text,d->caret);
    unsigned int s0=selection_start(d),s1=selection_end(d);
    int focused=ui->window_focused&&!focus_find&&!file_dialog&&!dialog.open&&!goto_open;
    int text_x=a.x+gutter_w-d->scroll_x;
    struct zui_rect code_clip={a.x+gutter_w-4,scroll_rect.y,scroll_rect.w-gutter_w,scroll_rect.h};
    struct zui_rect area_clip={a.x,scroll_rect.y,scroll_rect.w,scroll_rect.h};
    int state=state_at(d,(unsigned int)first);
    unsigned int match_color=zui_accent_color(ui,3);
    int regex=find_open&&(find_flags&AU_REGEX)&&find_ready(),widest=0;
    unsigned int ba=0,bb=0;int brackets=bracket_pair(d,&ba,&bb);
    for(unsigned int l=(unsigned int)first;l<lines;l++){
        int y=top+(int)l*line_h;if(y>=scroll_rect.y+scroll_rect.h)break;
        unsigned int ls=au_line_start(&d->text,l),le=au_line_end(&d->text,l);
        unsigned int n=au_read(&d->text,ls,min((int)(le-ls),LINE_DRAW),line_bytes);
        int next=classify(d->language,line_bytes,(int)n,state,line_class);
        if(l==caret_line&&!has_selection(d)){zui_clip(ui,area_clip);zui_round_alpha(ui,(struct zui_rect){a.x+4,y,scroll_rect.w-8,line_h},6,ui->theme.accent,light()?26:30);}
        /* Line number. */
        zui_clip(ui,(struct zui_rect){a.x,scroll_rect.y,gutter_w-6,scroll_rect.h});
        char number[16];num(number,l+1);
        text_right(a.x+gutter_w-14,y+(line_h-zui_mono.height)/2,number,&zui_mono,l==caret_line?ui->theme.text:ui->theme.secondary);
        zui_clip(ui,code_clip);
        /* Columns of the selection and of find matches on this line. */
        if(s0!=s1&&s1>=ls&&s0<=le){
            unsigned int from=s0>ls?s0:ls,to=s1<le?s1:le;
            int x0=text_x+(int)au_column(&d->text,from,(unsigned int)d->tab)*cell_w,x1=text_x+(int)au_column(&d->text,to,(unsigned int)d->tab)*cell_w;
            if(s1>le)x1+=cell_w/2;
            if(x1>x0)zui_round_alpha(ui,(struct zui_rect){x0,y+1,x1-x0,line_h-2},3,ui->theme.accent,light()?70:90);
        }
        if(regex){
            unsigned int position=ls,end;long m;int guard=0;
            while(position<=le&&guard++<200&&(m=rx_search(&find_rx,&d->text,position,le,&end,0))>=0){
                if(end>(unsigned int)m){
                    int x0=text_x+(int)au_column(&d->text,(unsigned int)m,(unsigned int)d->tab)*cell_w,x1=text_x+(int)au_column(&d->text,end,(unsigned int)d->tab)*cell_w;
                    zui_round_outline(ui,(struct zui_rect){x0-1,y,x1-x0+2,line_h},4,match_color);
                }
                position=end>(unsigned int)m?end:(unsigned int)m+1;
            }
        }else if(find_open&&find_input.length&&find_input.length<=n){
            for(unsigned int p=0;p+find_input.length<=n;p++){
                if(!line_match(p,n))continue;
                int x0=text_x+(int)au_column(&d->text,ls+p,(unsigned int)d->tab)*cell_w,cols=0;
                for(unsigned int q=p;q<p+find_input.length;q++)cols+=((unsigned char)line_bytes[q]&0xc0)!=0x80;
                zui_round_outline(ui,(struct zui_rect){x0-1,y,cols*cell_w+2,line_h},4,match_color);
                p+=find_input.length-1;
            }
        }
        /* The text, a run of one colour at a time; tabs move to the next stop. */
        unsigned int column=0;int ty=y+(line_h-zui_mono.height)/2;
        for(unsigned int i=0;i<n;){
            if(line_bytes[i]=='\t'){column=(column/(unsigned int)d->tab+1)*(unsigned int)d->tab;i++;continue;}
            unsigned int j=i,cols=0;int kind=line_class[i];
            while(j<n&&line_bytes[j]!='\t'&&line_class[j]==kind){if(((unsigned char)line_bytes[j]&0xc0)!=0x80)cols++;j++;}
            int x=text_x+(int)column*cell_w;
            if(x<code_clip.x+code_clip.w&&x+(int)cols*cell_w>code_clip.x)zui_text(ui,x,ty,line_bytes+i,j-i,&zui_mono,kind_color(kind));
            column+=cols;i=j;
        }
        if((int)column>widest)widest=(int)column;
        if(brackets&&(au_line_of(&d->text,ba)==l||au_line_of(&d->text,bb)==l)){
            if(au_line_of(&d->text,ba)==l)outline_at(d,ba,top,text_x,ui->theme.accent);
            if(au_line_of(&d->text,bb)==l)outline_at(d,bb,top,text_x,ui->theme.accent);
        }
        if(focused&&l==caret_line){
            int cx=text_x+(int)au_column(&d->text,d->caret,(unsigned int)d->tab)*cell_w;
            if(overwrite)zui_round_alpha(ui,(struct zui_rect){cx,y+2,cell_w,line_h-4},2,ui->theme.text,110);
            else zui_fill(ui,(struct zui_rect){cx,y+2,2,line_h-4},ui->theme.text);
        }
        state=next;
        zui_clip(ui,area_clip);
    }
    zui_clip(ui,area_clip);
    zui_scroll_end(ui,&view,(int)((long)lines*line_h+line_h*2));
    zui_clip(ui,(struct zui_rect){0,0,ui->width,ui->height});
    /* Horizontal scroll bar: shown while the widest visible line or the scroll position runs past the view. */
    hscroll_visible=code_clip.w-cell_w;hscroll_range=widest*cell_w+cell_w*4;
    if(hscroll_range<d->scroll_x+hscroll_visible)hscroll_range=d->scroll_x+hscroll_visible;
    hbar_rect=(struct zui_rect){0,0,0,0};
    if(hscroll_range>hscroll_visible+cell_w){
        struct zui_rect track={code_clip.x,scroll_rect.y+scroll_rect.h+3,code_clip.w-8,6};
        int w=max(24,(int)((long)track.w*hscroll_visible/hscroll_range)),x=track.x+(int)((long)(track.w-w)*d->scroll_x/max(1,hscroll_range-hscroll_visible));
        zui_round_alpha(ui,(struct zui_rect){x,track.y,w,6},3,ui->theme.secondary,hover==H_HSCROLL?200:140);
        hbar_rect=track;hit((struct zui_rect){track.x,track.y-3,track.w,12},H_HSCROLL);
    }
    minimap_rect=(struct zui_rect){0,0,0,0};
    if(mm){minimap_rect=(struct zui_rect){a.x+a.w-mm-6,a.y+6,mm,a.h-12};draw_minimap(d,minimap_rect);}
    hit((struct zui_rect){a.x,a.y,gutter_w,scroll_rect.h+6},H_GUTTER);
    hit((struct zui_rect){a.x+gutter_w,a.y,scroll_rect.w-gutter_w,scroll_rect.h+6},H_TEXT);
}
static void draw_banner(struct zui_rect r,struct doc *d){
    zui_round(ui,r,10,mix(ui->theme.surface,warn(),14));
    const char *m=d->disk_state==2?"This file was deleted or moved. Saving creates it again.":
        modified(d)?"This file changed on disk. Reload discards your changes.":"This file changed on disk.";
    fit(r.x+14,r.y+(r.h-zui_body.height)/2,m,r.w-220,&zui_body,ui->theme.text);
    if(d->disk_state==1)text_button((struct zui_rect){r.x+r.w-196,r.y+5,96,30},"Reload",H_RELOAD,0,1,0);
    text_button((struct zui_rect){r.x+r.w-92,r.y+5,84,30},"Keep",H_KEEP,0,1,0);
}
static int find_h(void){int w=ui->width-(sidebar_w()?sidebar_w()+12:8)-8;return replace_open&&w<760?FIND_H+42:FIND_H;}
static void draw_find(struct zui_rect r){
    panel(r,10);
    int x=r.x+10,y=r.y+8,h=34,right=r.x+r.w-10;
    icon_button((struct zui_rect){right-32,y,32,h},"x",H_FIND_CLOSE,1,0);right-=40;
    int wide=r.w>=760;
    int field=wide?(r.w-560)/2+120:r.w-330;if(field<120)field=120;
    input((struct zui_rect){x,y,field,h},&find_input,focus_find==1,H_FIND_FIELD,"Find");x+=field+8;
    char c[40];
    if(!find_input.length)c[0]=0;
    else if(find_error)copy(c,find_error,sizeof(c));
    else if(!find_total)put(c,"No results");
    else{char *o=c;if(find_index){o=num(o,find_index);o=put(o," of ");}o=num(o,find_total);if(!find_index)put(o,find_total==1?" match":" matches");}
    fit(x,y+(h-zui_caption.height)/2,c,82,&zui_caption,find_input.length&&!find_total?bad():ui->theme.secondary);x+=86;
    text_button((struct zui_rect){x,y,34,h},"Aa",H_CASE,0,1,find_flags&AU_CASE);x+=38;
    text_button((struct zui_rect){x,y,34,h},"W",H_WORD,0,1,find_flags&AU_WORD);x+=38;
    text_button((struct zui_rect){x,y,34,h},".*",H_REGEX,0,1,find_flags&AU_REGEX);x+=40;
    icon_button((struct zui_rect){x,y,30,h},"chevron-up",H_PREVIOUS,find_input.length>0,0);x+=32;
    icon_button((struct zui_rect){x,y,30,h},"chevron-down",H_NEXT,find_input.length>0,0);x+=38;
    if(replace_open&&!wide){
        int ry=y+h+8,rx=r.x+10,rw=r.w-20-84-60;
        input((struct zui_rect){rx,ry,rw,h},&replace_input,focus_find==2,H_REPLACE_FIELD,"Replace with");rx+=rw+8;
        text_button((struct zui_rect){rx,ry,80,h},"Replace",H_REPLACE,0,find_input.length>0,0);rx+=84;
        text_button((struct zui_rect){rx,ry,52,h},"All",H_ALL,0,find_input.length>0,0);
    }
    if(replace_open&&wide){
        int rw=right-x-8-84-60;if(rw<100)rw=100;
        input((struct zui_rect){x,y,rw,h},&replace_input,focus_find==2,H_REPLACE_FIELD,"Replace with");x+=rw+8;
        text_button((struct zui_rect){x,y,80,h},"Replace",H_REPLACE,0,find_input.length>0,0);x+=84;
        text_button((struct zui_rect){x,y,52,h},"All",H_ALL,0,find_input.length>0,0);
    }else if(!replace_open&&x+90<right)text_button((struct zui_rect){x,y,86,h},"Replace...",H_REPLACE,0,1,0);
}
static void draw_status(struct zui_rect r,struct doc *d){
    int ty=r.y+(r.h-zui_caption.height)/2,x=r.x+12;
    char t[200],*o;
    if(d){
        unsigned int line=au_line_of(&d->text,d->caret);
        o=put(t,"Ln ");o=num(o,line+1);o=put(o,", Col ");num(o,au_column(&d->text,d->caret,(unsigned int)d->tab)+1);
        text(x,ty,t,&zui_caption,ui->theme.secondary);x+=measure(t,&zui_caption)+24;
        if(has_selection(d)){
            unsigned int chars=0,a=selection_start(d),b=selection_end(d);
            for(unsigned int p=a;p<b&&p<a+200000;p++)if(((unsigned char)au_at(&d->text,p)&0xc0)!=0x80)chars++;
            o=num(t,chars);put(o,b-a>200000?"+ selected":" selected");text(x,ty,t,&zui_caption,ui->theme.secondary);x+=measure(t,&zui_caption)+24;
        }
    }
    int right=r.x+r.w-12;
    if(d){
        o=put(t,d->spaces?"Spaces: ":"Tabs: ");num(o,(unsigned long)d->tab);
        int w=measure(t,&zui_caption)+16;struct zui_rect b={right-w,r.y+3,w,r.h-6};
        if(hover==H_INDENT)zui_round(ui,b,6,mix(ui->theme.subtle,ui->theme.text,8));
        text(b.x+8,ty,t,&zui_caption,ui->theme.secondary);hit(b,H_INDENT);right-=w+8;
        const char *mode=overwrite?"OVR":"INS";w=measure(mode,&zui_caption)+16;b=(struct zui_rect){right-w,r.y+3,w,r.h-6};
        if(hover==H_OVERWRITE)zui_round(ui,b,6,mix(ui->theme.subtle,ui->theme.text,8));
        text(b.x+8,ty,mode,&zui_caption,overwrite?ui->theme.accent:ui->theme.secondary);hit(b,H_OVERWRITE);right-=w+8;
        const char *enc=d->bom?"UTF-8 BOM":"UTF-8";text_right(right-8,ty,enc,&zui_caption,ui->theme.secondary);right-=measure(enc,&zui_caption)+24;
        const char *eol=d->crlf?"CRLF":"LF";w=measure(eol,&zui_caption)+16;b=(struct zui_rect){right-w,r.y+3,w,r.h-6};
        if(hover==H_EOL)zui_round(ui,b,6,mix(ui->theme.subtle,ui->theme.text,8));
        text(b.x+8,ty,eol,&zui_caption,ui->theme.secondary);hit(b,H_EOL);right-=w+8;
        const char *lang=language_name(d);text_right(right-8,ty,lang,&zui_caption,ui->theme.secondary);right-=measure(lang,&zui_caption)+24;
    }
    if(status_note[0])fit(x,ty,status_note,right-x,&zui_caption,note_error?bad():ui->theme.secondary);
    else if(d){
        const char *s=modified(d)?"Unsaved changes":d->path[0]?"Saved":"New document";
        fit(x,ty,s,right-x,&zui_caption,modified(d)?warn():ui->theme.secondary);
    }
}
static void dim(void){
    /* Through zui: on a material frame pixels are premultiplied, and a plain RGB mix would put colour above alpha. */
    zui_clip(ui,(struct zui_rect){0,0,ui->width,ui->height});
    zui_round_alpha(ui,(struct zui_rect){0,0,ui->width,ui->height},0,0x000000,(light()?30:45)*255/100);
}
static struct zui_rect card(int w,int h){
    if(w>ui->width-24)w=ui->width-24;
    if(h>ui->height-24)h=ui->height-24;
    struct zui_rect r={(ui->width-w)/2,(ui->height-h)/2,w,h};
    zui_round(ui,(struct zui_rect){r.x-1,r.y-1,r.w+2,r.h+2},13,ui->theme.border);zui_round(ui,r,12,ui->theme.raised);
    hit((struct zui_rect){0,0,ui->width,ui->height},H_MODAL);hit(r,H_MODAL);return r;
}
static void draw_file_dialog(void){
    dim();
    struct zui_rect r=card(560,440);
    int x=r.x+20,w=r.w-40,y=r.y+18;
    text(x,y,dialog_save?"Save as":"Open",&zui_heading,ui->theme.text);y+=40;
    icon_button((struct zui_rect){x,y,32,28},"arrow-up",H_DIALOG_UP,!same(dialog_folder,"/"),0);
    fit(x+40,y+(28-zui_caption.height)/2,dialog_folder,w-40,&zui_caption,ui->theme.secondary);y+=36;
    int list_h=r.y+r.h-y-120;
    struct zui_rect list={x,y,w,list_h};
    zui_round(ui,list,8,ui->theme.border);zui_round(ui,(struct zui_rect){list.x+1,list.y+1,list.w-2,list.h-2},7,ui->theme.surface);
    int top=zui_scroll_begin(ui,&dialog_scroll,(struct zui_rect){list.x+1,list.y+4,list.w-2,list.h-8});
    for(int i=dialog_scroll.visual/30;i<entry_count;i++){
        int ry=top+i*30;if(ry>=list.y+list.h)break;
        struct zui_rect row={list.x+5,ry,list.w-18,28};
        if(i==entry_selected)zui_round(ui,row,6,mix(ui->theme.surface,ui->theme.accent,20));
        else if(hover==H_ENTRY+i)zui_round(ui,row,6,mix(ui->theme.surface,ui->theme.text,6));
        icon(entries[i].folder?"folder":"file-text",row.x+8,row.y+6,16,i==entry_selected?ZUI_ICON_NORMAL:ZUI_ICON_TEXT);
        fit(row.x+34,row.y+(28-zui_body.height)/2,entry_name(i),row.w-44,&zui_body,ui->theme.text);
        if(i<100)hit(row,H_ENTRY+i);
    }
    if(!entry_count)text(list.x+16,list.y+14,dialog_error[0]?dialog_error:"This folder is empty.",&zui_body,dialog_error[0]?bad():ui->theme.secondary);
    zui_scroll_end(ui,&dialog_scroll,entry_count*30+8);
    y+=list_h+12;
    text(x,y,dialog_save?"File name":"File name or path",&zui_caption,ui->theme.secondary);y+=20;
    input((struct zui_rect){x,y,w,36},&dialog_name,1,H_DIALOG_FIELD,dialog_save?0:"Choose a file");y+=46;
    if(dialog_error[0]&&entry_count)fit(x,y+8,dialog_error,w-220,&zui_caption,bad());
    text_button((struct zui_rect){x+w-208,y,100,36},"Cancel",H_DIALOG_CANCEL,0,1,0);
    text_button((struct zui_rect){x+w-100,y,100,36},dialog_save?"Save":"Open",H_DIALOG_OK,1,dialog_name.length>0||entry_selected>=0,0);
}
static void draw_goto(void){
    dim();
    struct zui_rect r=card(360,170);int x=r.x+20,w=r.w-40,y=r.y+18;
    text(x,y,"Go to line",&zui_heading,ui->theme.text);y+=42;
    input((struct zui_rect){x,y,w,36},&goto_input,1,H_GOTO_FIELD,0);y+=50;
    text_button((struct zui_rect){x+w-208,y,100,36},"Cancel",H_DIALOG_CANCEL,0,1,0);
    text_button((struct zui_rect){x+w-100,y,100,36},"Go",H_DIALOG_OK,1,goto_input.length>0,0);
}
/* Menus: the main menu (menu button) and the text context menu (right click). */
enum { M_NEW=1,M_OPEN,M_SAVE,M_SAVE_AS,M_CLOSE,M_UNDO,M_REDO,M_CUT,M_COPY,M_PASTE,M_SELECT_ALL,M_FIND,M_REPLACE,M_GOTO };
static int menu_open,menu_x,menu_y,menu_count,menu_items[16],menu_focus;
static const char *menu_label(int m){
    static const char *const l[]={"","New","Open...","Save","Save as...","Close","Undo","Redo","Cut","Copy","Paste","Select all","Find","Replace","Go to line..."};
    return l[m];
}
static const char *menu_keys(int m){
    static const char *const k[]={"","Ctrl+N","Ctrl+O","Ctrl+S","Ctrl+Shift+S","Ctrl+W","Ctrl+Z","Ctrl+Y","Ctrl+X","Ctrl+C","Ctrl+V","Ctrl+A","Ctrl+F","Ctrl+H","Ctrl+G"};
    return k[m];
}
static int menu_enabled(int m){
    struct doc *d=D();
    if(m==M_NEW)return doc_count<MAX_DOCS;
    if(m==M_OPEN)return 1;
    if(!d)return 0;
    if(m==M_UNDO)return d->history.current>0;
    if(m==M_REDO)return d->history.current<d->history.count;
    if(m==M_CUT||m==M_COPY)return has_selection(d);
    return 1;
}
static void open_menu(int x,int y,int context){
    static const int main_items[]={M_NEW,M_OPEN,M_SAVE,M_SAVE_AS,M_CLOSE,M_UNDO,M_REDO,M_FIND,M_REPLACE,M_GOTO};
    static const int text_items[]={M_UNDO,M_REDO,M_CUT,M_COPY,M_PASTE,M_SELECT_ALL};
    const int *items=context?text_items:main_items;menu_count=context?6:10;
    for(int i=0;i<menu_count;i++)menu_items[i]=items[i];
    menu_open=1;menu_x=x;menu_y=y;menu_focus=-1;ui->dirty=1;
}
static void draw_menu(void){
    int w=260,h=menu_count*32+12,x=menu_x,y=menu_y;
    if(x+w>ui->width-8)x=ui->width-8-w;
    if(y+h>ui->height-8)y=ui->height-8-h;
    if(x<8)x=8;
    if(y<8)y=8;
    struct zui_rect r={x,y,w,h};
    zui_round(ui,(struct zui_rect){r.x-1,r.y-1,r.w+2,r.h+2},11,ui->theme.border);zui_round(ui,r,10,ui->theme.raised);
    hit(r,H_MENU+99);
    for(int i=0;i<menu_count;i++){
        struct zui_rect row={x+6,y+6+i*32,w-12,30};int m=menu_items[i],on=menu_enabled(m);
        if(on&&(hover==H_MENU+i||menu_focus==i))zui_round(ui,row,6,mix(ui->theme.raised,ui->theme.accent,18));
        text(row.x+12,row.y+(30-zui_body.height)/2,menu_label(m),&zui_body,on?ui->theme.text:ui->theme.secondary);
        text_right(row.x+row.w-10,row.y+(30-zui_caption.height)/2,menu_keys(m),&zui_caption,ui->theme.secondary);
        if(on)hit(row,H_MENU+i);
    }
}

void aurora_draw(void){
    hit_count=0;ui->count=0;ui->focus=-1;ui->pressed=-1;
    zui_clip(ui,(struct zui_rect){0,0,ui->width,ui->height});
    struct doc *d=D();
    int sw=sidebar_w();
    if(ui->foreground){
        /* A material frame starts transparent: the desktop fills what the regions name with its blurred backdrop. */
        zui_begin_material_frame(ui);
        if(see_through())zui_material_surface(ui,(struct zui_rect){0,0,ui->width,ui->height},0,ZUI_SURFACE_CONTENT,2);
        else{
            zui_fill(ui,ui->clip,ui->theme.subtle);
            /* The sidebar is navigation material: its area goes back to transparent (zero is valid premultiplied). */
            struct zui_rect nav={0,0,sw+12,ui->height-STATUS_H};
            if(sw&&zui_material_surface(ui,nav,0,ZUI_SURFACE_NAVIGATION,2))
                for(int y=nav.y;y<nav.y+nav.h;y++)for(int x=nav.x;x<nav.x+nav.w;x++)ui->pixels[y*ui->width+x]=0;
        }
    }else zui_fill(ui,ui->clip,ui->theme.subtle);
    if(sw)draw_sidebar((struct zui_rect){0,0,sw+12,ui->height-STATUS_H});
    int x0=sw?sw+12:8,w=ui->width-x0-8;
    struct zui_rect tabs={x0,TAB_Y,w,TAB_H};
    tabs_rect=(struct zui_rect){x0,TAB_Y,w-44,TAB_H};
    draw_tabs(tabs);
    int y=TAB_Y+TAB_H+4,bottom=ui->height-STATUS_H-(find_open?find_h()+4:0)-4;
    if(d){
        draw_crumbs((struct zui_rect){x0,y,w,CRUMB_H},d);y+=CRUMB_H;
        if(d->disk_state){draw_banner((struct zui_rect){x0,y,w,BANNER_H},d);y+=BANNER_H+6;}
        cell_w=zui_mono.advance[0];if(cell_w<6)cell_w=8;
        line_h=zui_mono.height+5;
        char digits[16];num(digits,d->text.line_count);gutter_w=(int)length(digits)*cell_w+36;if(gutter_w<56)gutter_w=56;
        editor_area=(struct zui_rect){x0,y,w,bottom-y};text_area=editor_area;
        draw_text_area(d);
    }else{
        struct zui_rect e={x0,y,w,bottom-y};panel(e,10);
        text(e.x+24,e.y+24,"No documents are open.",&zui_label,ui->theme.text);
        text(e.x+24,e.y+50,"Press Ctrl+N for a new document or Ctrl+O to open a file.",&zui_body,ui->theme.secondary);
    }
    if(find_open&&d)draw_find((struct zui_rect){x0,ui->height-STATUS_H-find_h()-4,w,find_h()});
    zui_fill(ui,(struct zui_rect){0,ui->height-STATUS_H,ui->width,1},ui->theme.border);
    zui_fill(ui,(struct zui_rect){0,ui->height-STATUS_H+1,ui->width,STATUS_H-1},ui->theme.subtle);
    draw_status((struct zui_rect){0,ui->height-STATUS_H,ui->width,STATUS_H},d);
    if(menu_open)draw_menu();
    if(file_dialog)draw_file_dialog();
    else if(goto_open)draw_goto();
    zui_draw_controls(ui);
    if(dialog.open)zui_dialog_draw(ui,&dialog);
    ui->dirty=0;
}

/* ---------- Caret movement. */
static void reveal(struct doc *d){
    unsigned int line=au_line_of(&d->text,d->caret);
    zui_scroll_reveal(&view,(int)line*line_h,line_h*2);
    int visible=text_area.w-gutter_w-16,cx=(int)au_column(&d->text,d->caret,(unsigned int)d->tab)*cell_w;
    if(cx<d->scroll_x)d->scroll_x=max(0,cx-cell_w*8);
    else if(cx>d->scroll_x+visible)d->scroll_x=cx-visible+cell_w*8;
}
static void move(struct doc *d,unsigned int to,int extend){
    d->caret=to;if(!extend)d->anchor=to;reveal_caret=1;ui->dirty=1;
}
static void vertical(struct doc *d,int lines,int extend){
    if(!d->goal_valid){d->goal=au_column(&d->text,d->caret,(unsigned int)d->tab);d->goal_valid=1;}
    long line=(long)au_line_of(&d->text,d->caret)+lines;
    if(line<0){move(d,0,extend);return;}
    if(line>=(long)d->text.line_count){move(d,au_length(&d->text),extend);return;}
    d->caret=au_at_column(&d->text,(unsigned int)line,d->goal,(unsigned int)d->tab);if(!extend)d->anchor=d->caret;reveal_caret=1;ui->dirty=1;
}
static unsigned int position_at(struct doc *d,int x,int y){
    int top=text_area.y+6-view.visual;
    long line=(y-top)/line_h;if(y<top)line=0;
    if(line>=(long)d->text.line_count)return au_length(&d->text);
    int column=(x-(text_area.x+gutter_w-d->scroll_x)+cell_w/2)/cell_w;if(column<0)column=0;
    return au_at_column(&d->text,(unsigned int)line,(unsigned int)column,(unsigned int)d->tab);
}

/* ---------- Events. */
static void menu_action(int m){
    struct doc *d=D();menu_open=0;ui->dirty=1;
    switch(m){
    case M_NEW:new_document();break;
    case M_OPEN:open_file_dialog(0);break;
    case M_SAVE:if(d)save_doc(current,0);break;
    case M_SAVE_AS:if(d)open_file_dialog(1);break;
    case M_CLOSE:if(d)close_doc(current,1);break;
    case M_UNDO:if(d)undo(d,0);break;
    case M_REDO:if(d)undo(d,1);break;
    case M_CUT:if(d)clipboard_copy(d,1);break;
    case M_COPY:if(d)clipboard_copy(d,0);break;
    case M_PASTE:if(d)clipboard_paste(d);break;
    case M_SELECT_ALL:if(d){d->anchor=0;d->caret=au_length(&d->text);}break;
    case M_FIND:open_find(0);break;
    case M_REPLACE:open_find(1);break;
    case M_GOTO:if(d){goto_open=1;goto_buffer[0]=0;zui_input_init(&goto_input,goto_buffer,sizeof(goto_buffer));}break;
    }
}
static void dialog_answer(int choice){
    int kind=asking,slot=asking_doc;asking=ASK_NONE;ui->dirty=1;
    if(kind==ASK_RECOVER){recovery_answer(choice);return;}
    if(kind==ASK_CLOSE||kind==ASK_QUIT){
        if(choice==1){
            close_after_save=slot;
            if(!docs[slot].path[0]){switch_doc(slot);open_file_dialog(1);return;}
            if(save_doc(slot,0))saved_then(slot);
            else if(!asking)stop_closing(); /* failed; an overwrite question keeps the close pending */
            return;
        }
        if(choice==2){closed(slot);if(quitting&&continue_quit())end_requested=1;return;}
        stop_closing();return;
    }
    if(kind==ASK_OVERWRITE&&choice==1&&docs[slot].used){if(save_doc(slot,1))saved_then(slot);else stop_closing();return;}
    if(kind==ASK_REPLACE_FILE&&choice==1&&docs[slot].used){if(save_to(&docs[slot],pending_save_path))saved_then(slot);else stop_closing();return;}
    if(kind==ASK_OVERWRITE||kind==ASK_REPLACE_FILE)stop_closing();
}
static void click_text(struct doc *d,const struct zenith_gui_event *e,int gutter){
    long now=au_sys_clock();
    unsigned int p=position_at(d,gutter?text_area.x+gutter_w:e->x,e->y);
    static long last_time=-1000;static unsigned int last_position;static int clicks;
    clicks=now-last_time<450&&p==last_position?clicks+1:1;last_time=now;last_position=p;
    if(gutter||clicks>=3){
        unsigned int l=au_line_of(&d->text,p);d->anchor=au_line_start(&d->text,l);
        d->caret=l+1<d->text.line_count?au_line_start(&d->text,l+1):au_line_end(&d->text,l);
    }else if(clicks==2){
        unsigned int a=p,b=p;
        while(a&&au_word_char(au_at(&d->text,a-1)))a--;
        while(b<au_length(&d->text)&&au_word_char(au_at(&d->text,b)))b++;
        d->anchor=a;d->caret=b;
    }else{d->caret=p;if(!(e->modifiers&1))d->anchor=p;}
    d->goal_valid=0;ui->dirty=1;
}
static int dragging_text,dragging_bar;   /* dragging_bar: 1 minimap, 2 horizontal scroll bar */
static int key_text(struct doc *d,const struct zenith_gui_event *e){
    int shift=e->modifiers&1,control=(e->modifiers&6)==2;
    unsigned int n=au_length(&d->text);
    switch(e->extra){
    case 2:newline(d);return 1;
    case 3:
        if(has_selection(d))delete_range(d,selection_start(d),selection_end(d));
        else if(d->caret)delete_range(d,control?au_word_left(&d->text,d->caret):au_previous(&d->text,d->caret),d->caret);
        return 1;
    case 9:
        if(has_selection(d))delete_range(d,selection_start(d),selection_end(d));
        else if(d->caret<n)delete_range(d,d->caret,control?au_word_right(&d->text,d->caret):au_next(&d->text,d->caret));
        return 1;
    case 4:
        if(control){int p=position_of(current);if(doc_count>1)switch_doc(order[(p+(shift?doc_count-1:1))%doc_count]);return 1;}
        if(shift)indent_lines(d,1);else insert_tab(d);
        return 1;
    case 5:
        if(!shift&&has_selection(d)){move(d,selection_start(d),0);return 1;}
        move(d,control?au_word_left(&d->text,d->caret):au_previous(&d->text,d->caret),shift);d->goal_valid=0;return 1;
    case 6:
        if(!shift&&has_selection(d)){move(d,selection_end(d),0);return 1;}
        move(d,control?au_word_right(&d->text,d->caret):au_next(&d->text,d->caret),shift);d->goal_valid=0;return 1;
    case 7:vertical(d,-1,shift);return 1;
    case 8:vertical(d,1,shift);return 1;
    case 12:case 13:{int page=max(1,(text_area.h-12)/line_h-1);vertical(d,e->extra==12?-page:page,shift);view.offset+=e->extra==12?-page*line_h:page*line_h;return 1;}
    case 10:{
        if(control){move(d,0,shift);d->goal_valid=0;return 1;}
        unsigned int l=au_line_of(&d->text,d->caret),s=au_line_start(&d->text,l),i=line_indent_end(&d->text,l);
        move(d,d->caret==i?s:i,shift);d->goal_valid=0;return 1;
    }
    case 11:
        move(d,control?n:au_line_end(&d->text,au_line_of(&d->text,d->caret)),shift);d->goal_valid=0;return 1;
    }
    return 0;
}
static int shortcut(int c,int shift){
    struct doc *d=D();
    switch(c){
    case 'n':new_document();return 1;
    case 'o':open_file_dialog(0);return 1;
    case 's':if(d){if(shift)open_file_dialog(1);else save_doc(current,0);}return 1;
    case 'w':if(d)close_doc(current,1);return 1;
    case 'f':open_find(0);return 1;
    case 'h':open_find(1);return 1;
    case 'g':menu_action(M_GOTO);return 1;
    }
    if(!d)return 0;
    switch(c){
    case 'a':d->anchor=0;d->caret=au_length(&d->text);ui->dirty=1;return 1;
    case 'c':clipboard_copy(d,0);return 1;
    case 'x':clipboard_copy(d,1);return 1;
    case 'v':clipboard_paste(d);return 1;
    case 'z':undo(d,shift);return 1;
    case 'y':undo(d,1);return 1;
    }
    if(c>='1'&&c<='8'&&c-'1'<doc_count){switch_doc(order[c-'1']);return 1;}
    return 0;
}
static int typed(const struct zenith_gui_event *e){
    unsigned int c=(unsigned int)e->code;
    if(e->extra||c<32||c==127||c>0x10ffff||(c>=0xd800&&c<=0xdfff))return 0;
    /* Ctrl alone is a shortcut; AltGr (Ctrl+Alt) types text such as { [ ] } on many layouts. */
    int m=e->modifiers&6;return m==0||m==6;
}
static int utf8(unsigned int c,char *b){
    if(c<128){b[0]=(char)c;return 1;}
    if(c<2048){b[0]=(char)(0xc0|(c>>6));b[1]=(char)(0x80|(c&63));return 2;}
    if(c<65536){b[0]=(char)(0xe0|(c>>12));b[1]=(char)(0x80|((c>>6)&63));b[2]=(char)(0x80|(c&63));return 3;}
    b[0]=(char)(0xf0|(c>>18));b[1]=(char)(0x80|((c>>12)&63));b[2]=(char)(0x80|((c>>6)&63));b[3]=(char)(0x80|(c&63));return 4;
}
static void dialog_entry_activate(int i){
    if(i<0||i>=entry_count)return;
    if(entries[i].folder){char p[PATH_BYTES];join(p,dialog_folder,entry_name(i));dialog_name_buffer[0]=0;zui_input_init(&dialog_name,dialog_name_buffer,sizeof(dialog_name_buffer));list_folder(p);return;}
    copy(dialog_name_buffer,entry_name(i),sizeof(dialog_name_buffer));zui_input_init(&dialog_name,dialog_name_buffer,sizeof(dialog_name_buffer));
    dialog_choose();
}
static int file_dialog_event(const struct zenith_gui_event *e){
    if(zui_scroll_event(ui,&dialog_scroll,e)){ui->dirty=1;return 1;}
    if(e->type==Z_GUI_UP){
        int id=hit_at(e->x,e->y);
        if(id==H_DIALOG_CANCEL){file_dialog=0;stop_closing();ui->dirty=1;}
        else if(id==H_DIALOG_OK)dialog_choose();
        else if(id==H_DIALOG_UP){char p[PATH_BYTES];parent(p,dialog_folder);list_folder(p);}
        else if(id>=H_ENTRY&&id<H_ENTRY+100){
            int i=id-H_ENTRY;long now=au_sys_clock();
            if(i==last_click_id&&now-last_click_time<450){dialog_entry_activate(i);last_click_id=-1;return 1;}
            last_click_id=i;last_click_time=now;entry_selected=i;
            if(!entries[i].folder){copy(dialog_name_buffer,entry_name(i),sizeof(dialog_name_buffer));zui_input_init(&dialog_name,dialog_name_buffer,sizeof(dialog_name_buffer));}
            ui->dirty=1;
        }
        return 1;
    }
    if(e->type!=Z_GUI_KEY)return 1;
    if(e->extra==1){file_dialog=0;stop_closing();ui->dirty=1;return 1;}
    if(e->extra==2){if(entry_selected>=0&&entries[entry_selected].folder&&(!dialog_name.length||same(dialog_name_buffer,entry_name(entry_selected))))dialog_entry_activate(entry_selected);else dialog_choose();return 1;}
    if(e->extra==7||e->extra==8){
        if(!entry_count)return 1;
        entry_selected=entry_selected<0?0:max(0,min(entry_count-1,entry_selected+(e->extra==7?-1:1)));
        zui_scroll_reveal(&dialog_scroll,entry_selected*30,30);
        copy(dialog_name_buffer,entries[entry_selected].folder&&dialog_save?dialog_name_buffer:entry_name(entry_selected),sizeof(dialog_name_buffer));
        zui_input_init(&dialog_name,dialog_name_buffer,sizeof(dialog_name_buffer));
        ui->dirty=1;return 1;
    }
    if(zui_input_event(&dialog_name,e)){dialog_error[0]=0;ui->dirty=1;}
    return 1;
}
static int find_event(const struct zenith_gui_event *e){
    int shift=e->modifiers&1;
    /* Alt+C case, Alt+W whole word, Alt+R regular expression (as in other editors). */
    if((e->modifiers&6)==4&&!e->extra){
        int c=e->code>='A'&&e->code<='Z'?e->code+32:e->code,flag=c=='c'?AU_CASE:c=='w'?AU_WORD:c=='r'?AU_REGEX:0;
        if(flag){find_flags^=flag;if(flag==AU_REGEX)mark("AURORA_REGEX",find_flags&AU_REGEX?"on":"off");count_matches();ui->dirty=1;return 1;}
    }
    if(e->extra==1){find_open=0;replace_open=0;focus_find=0;ui->dirty=1;return 1;}
    if(e->extra==2){if(focus_find==2)replace_one();else find_step(!shift,0);return 1;}
    if(e->extra==4){focus_find=replace_open&&focus_find==1?2:replace_open&&focus_find==2?1:focus_find;ui->dirty=1;return 1;}
    if(e->extra==7||e->extra==8){focus_find=0;ui->dirty=1;return 0;}
    struct zui_input *in=focus_find==2?&replace_input:&find_input;
    if(typed(e)||(e->extra&&e->extra!=4)){
        if(zui_input_event(in,e)){if(in==&find_input){if(find_input.length)find_step(1,1);else count_matches();}ui->dirty=1;}
        return 1;
    }
    return 0;
}
static int shortcut_key(const struct zenith_gui_event *e){
    struct doc *d=D();
    if((e->modifiers&6)==2&&e->code>=32&&!e->extra){
        int c=e->code>='A'&&e->code<='Z'?e->code+32:e->code;
        return shortcut(c,e->modifiers&1);
    }
    if(e->extra==16){if(find_input.length)find_step(!(e->modifiers&1),0);else open_find(0);return 1;} /* F3 */
    (void)d;return 0;
}
int aurora_event(const struct zenith_gui_event *e){
    struct doc *d=D();
    if(e->type==Z_GUI_MOVE){
        ui->pointer_x=e->x;ui->pointer_y=e->y;
        int h=e->x<0?H_NONE:hit_at(e->x,e->y);if(h!=hover){hover=h;ui->dirty=1;}
        if(e->x>=0)zui_update_hover(ui);
        return end_requested;
    }
    if(dialog.open){
        int choice=zui_dialog_event(ui,&dialog,e);
        if(choice)dialog_answer(choice);
        ui->dirty=1;return end_requested;
    }
    if(file_dialog){file_dialog_event(e);return end_requested;}
    if(goto_open){
        if(e->type==Z_GUI_UP){int id=hit_at(e->x,e->y);if(id==H_DIALOG_CANCEL){goto_open=0;ui->dirty=1;}else if(id==H_DIALOG_OK)go_to_line();}
        if(e->type==Z_GUI_KEY){if(e->extra==1){goto_open=0;ui->dirty=1;}else if(e->extra==2)go_to_line();else if(zui_input_event(&goto_input,e))ui->dirty=1;}
        return end_requested;
    }
    if(menu_open){
        if(e->type==Z_GUI_UP){int id=hit_at(e->x,e->y);if(id>=H_MENU&&id<H_MENU+menu_count){menu_action(menu_items[id-H_MENU]);return end_requested;}if(id!=H_MENU+99){menu_open=0;ui->dirty=1;}return end_requested;}
        if(e->type==Z_GUI_DOWN||e->type==Z_GUI_DRAG)return end_requested;
        if(e->type==Z_GUI_KEY){
            if(e->extra==1){menu_open=0;ui->dirty=1;}
            else if(e->extra==7||e->extra==8){int step=e->extra==7?-1:1;for(int i=0;i<menu_count;i++){menu_focus=(menu_focus+step+menu_count)%menu_count;if(menu_enabled(menu_items[menu_focus]))break;}ui->dirty=1;}
            else if(e->extra==2&&menu_focus>=0&&menu_enabled(menu_items[menu_focus]))menu_action(menu_items[menu_focus]);
            return end_requested;
        }
        if(e->type!=Z_GUI_SECONDARY)return end_requested;
        menu_open=0;
    }
    if(e->type==Z_GUI_SECONDARY){
        int id=hit_at(e->x,e->y);
        if(d&&id==H_TEXT){unsigned int p=position_at(d,e->x,e->y);if(p<selection_start(d)||p>selection_end(d)){d->caret=d->anchor=p;}open_menu(e->x,e->y,1);}
        return end_requested;
    }
    if(e->type==Z_GUI_SCROLL){
        if(side_rect.h>0&&inside(side_rect,e->x,e->y)){if(zui_scroll_event(ui,&side_scroll,e))ui->dirty=1;return end_requested;}
        if(d&&(e->modifiers&1)){d->scroll_x=max(0,d->scroll_x-e->code*cell_w*6);ui->dirty=1;return end_requested;}
        if(d&&zui_scroll_event(ui,&view,e))ui->dirty=1;
        return end_requested;
    }
    if(e->type==Z_GUI_DOWN||e->type==Z_GUI_UP||e->type==Z_GUI_DRAG){
        if(d&&e->type!=Z_GUI_UP&&zui_scroll_event(ui,&view,e)){ui->dirty=1;if(view.dragging)return end_requested;}
        if(d&&view.dragging){if(e->type==Z_GUI_UP)zui_scroll_event(ui,&view,e);ui->dirty=1;return end_requested;}
        if(inside(tabs_rect,e->x,e->y)){
            int a=zui_tabs_event(&strip,e);
            if(a==ZUI_TABS_ADD)new_document();
            else if(a>0&&a<=doc_count)switch_doc(order[a-1]);
            else if(a<0&&-a<=doc_count)close_doc(order[-a-1],1);
            if(a)return end_requested;
        }
        int id=hit_at(e->x,e->y);
        if(d&&(e->type==Z_GUI_DOWN||e->type==Z_GUI_DRAG)&&(id==H_MINIMAP||dragging_bar==1)&&minimap_rect.h>0&&!dragging_text){
            dragging_bar=1;long content=(long)d->text.line_count*line_h,dense=(long)d->text.line_count*2>minimap_rect.h;
            long y=e->y-minimap_rect.y,target=dense?y*content/minimap_rect.h:y/2*line_h;
            zui_scroll_jump(&view,(int)max(0,(int)(target-view.rect.h/2)));view.offset=min(view.offset,zui_scroll_maximum(&view));ui->dirty=1;return end_requested;
        }
        if(d&&(e->type==Z_GUI_DOWN||e->type==Z_GUI_DRAG)&&(id==H_HSCROLL||dragging_bar==2)&&hbar_rect.w>0&&!dragging_text){
            dragging_bar=2;long x=e->x-hbar_rect.x;d->scroll_x=(int)max(0,(int)(x*hscroll_range/max(1,hbar_rect.w)-hscroll_visible/2));ui->dirty=1;return end_requested;
        }
        if(e->type==Z_GUI_UP&&dragging_bar){dragging_bar=0;return end_requested;}
        if(e->type==Z_GUI_DOWN&&d&&(id==H_TEXT||id==H_GUTTER)){focus_find=0;click_text(d,e,id==H_GUTTER);dragging_text=1;return end_requested;}
        if(e->type==Z_GUI_DRAG&&d&&dragging_text){
            d->caret=position_at(d,e->x,e->y);d->goal_valid=0;
            if(e->y<text_area.y+8)view.offset=max(0,view.offset-line_h);
            else if(e->y>text_area.y+text_area.h-8)view.offset+=line_h;
            ui->dirty=1;return end_requested;
        }
        if(e->type==Z_GUI_UP){
            dragging_text=0;
            if(id==H_FIND_FIELD){focus_find=1;ui->dirty=1;}
            else if(id==H_REPLACE_FIELD){focus_find=2;ui->dirty=1;}
            else if(id==H_CASE){find_flags^=AU_CASE;count_matches();ui->dirty=1;}
            else if(id==H_WORD){find_flags^=AU_WORD;count_matches();ui->dirty=1;}
            else if(id==H_REGEX){find_flags^=AU_REGEX;count_matches();ui->dirty=1;mark("AURORA_REGEX",find_flags&AU_REGEX?"on":"off");}
            else if(id==H_OVERWRITE){overwrite=!overwrite;ui->dirty=1;}
            else if(id==H_PREVIOUS)find_step(0,0);
            else if(id==H_NEXT)find_step(1,0);
            else if(id==H_REPLACE){if(!replace_open){replace_open=1;focus_find=2;ui->dirty=1;}else replace_one();}
            else if(id==H_ALL)replace_all();
            else if(id==H_FIND_CLOSE){find_open=replace_open=0;focus_find=0;ui->dirty=1;}
            else if(id==H_NEW)new_document();
            else if(id==H_OPEN)open_file_dialog(0);
            else if(id==H_MENU_BUTTON)open_menu(ui->width-276,TAB_Y+TAB_H,0);
            else if(id>=H_DOC&&id<H_DOC+MAX_DOCS&&id-H_DOC<doc_count)switch_doc(order[id-H_DOC]);
            else if(id>=H_TREE&&id<H_TREE+TREE_ROWS&&id-H_TREE<tree_count){
                int i=id-H_TREE;
                if(tree[i].folder){toggle_folder(i);mark(tree[i].open?"AURORA_FOLDER closed":"AURORA_FOLDER opened",tree[i].name);}
                else{char full[PATH_BYTES];join(full,tree_root,tree_paths[i]);open_path(full);}
            }
            else if(id>=H_SYMBOL&&id<H_SYMBOL+SYMBOLS&&id-H_SYMBOL<symbol_count&&d){
                struct symbol *s=&symbols[id-H_SYMBOL];
                d->caret=d->anchor=au_line_start(&d->text,s->line);d->goal_valid=0;reveal_caret=1;view.offset=max(0,(int)s->line*line_h-line_h*3);
                mark("AURORA_SYMBOL",s->name);ui->dirty=1;
            }
            else if(id==H_EOL&&d){d->crlf=!d->crlf;au_history_break(&d->history);if(!modified(d))d->history.saved=-1;update_title();note(d->crlf?"Line endings: CRLF (Windows) when saved":"Line endings: LF when saved",0);}
            else if(id==H_INDENT&&d){
                if(d->spaces&&d->tab==4)d->tab=2;else if(d->spaces&&d->tab==2){d->spaces=0;d->tab=4;}else{d->spaces=1;d->tab=4;}
                d->scroll_x=0;ui->dirty=1;
            }
            else if(id==H_RELOAD&&d)reload(current);
            else if(id==H_KEEP&&d){remember_disk(d);ui->dirty=1;}
        }
        return end_requested;
    }
    if(e->type!=Z_GUI_KEY)return end_requested;
    if(status_note[0]){status_note[0]=0;ui->dirty=1;} /* a note answers the last action only */
    if(shortcut_key(e))return end_requested;
    if(find_open&&focus_find&&find_event(e))return end_requested;
    if(!d)return end_requested;
    if(e->extra==1){if(find_open){find_open=replace_open=0;}else d->anchor=d->caret;ui->dirty=1;return end_requested;}
    if(e->extra==26){overwrite=!overwrite;mark("AURORA_OVERWRITE",overwrite?"on":"off");ui->dirty=1;return end_requested;}
    if(typed(e)){
        char b[4];int n=utf8((unsigned int)e->code,b);
        if(overwrite&&!has_selection(d)&&d->caret<au_length(&d->text)&&au_at(&d->text,d->caret)!='\n')d->anchor=au_next(&d->text,d->caret);
        replace_selection(d,b,(unsigned int)n,!overwrite);
    }
    else key_text(d,e);
    if(find_open)count_matches();
    return end_requested;
}
/* ---------- Recovery copies (AU3): while a document has unsaved changes, Aurora writes its text every 15 seconds
 * to ~/.local/share/aurora/recovery/rID.txt (folders 0700, files 0600: the person's own) and removes it when the
 * document is saved or closed. After a crash or power loss, the next start offers each copy back (Restore,
 * Discard or Later); copies older than 14 days are deleted. They survive reboots: that is their purpose. */
#define RECOVERY_SECONDS (14L*86400)
static char recovery_dir[PATH_BYTES];
static void recovery_file(char *o,unsigned int id){char n[32],*q=put(n,"r");q=num(q,id);put(q,".txt");join(o,recovery_dir,n);}
static int recovery_folders(void){
    char a[PATH_BYTES],b[PATH_BYTES];join(a,home,".local");join(b,a,"share");join(a,b,"aurora");
    const char *chain[4]={0,0,a,recovery_dir};char c1[PATH_BYTES],c2[PATH_BYTES];join(c1,home,".local");join(c2,c1,"share");chain[0]=c1;chain[1]=c2;
    for(int i=0;i<4;i++){struct au_info info;if(au_sys_info(chain[i],&info)){if(au_sys_mkdir(chain[i])<0)return 0;if(i>=2)au_sys_chmod(chain[i],0700);}}
    return 1;
}
static void recovery_drop(struct doc *d){
    if(!d->has_recovery||!recovery_dir[0])return;
    char f[PATH_BYTES];recovery_file(f,d->recovery_id);au_sys_unlink(f);d->has_recovery=0;d->snapshot_stamp=0;
}
static void recovery_write(struct doc *d,long now){
    if(!recovery_dir[0]||!recovery_folders())return;
    char f[PATH_BYTES];recovery_file(f,d->recovery_id);
    long fd=au_sys_create(f);if(fd<0)return;
    au_sys_chmod(f,0600);
    char head[PATH_BYTES+400],*o=put(head,"aurora-recovery 1\npath=");o=put(o,d->path);o=put(o,"\nname=");o=put(o,d->name);
    o=put(o,"\ncrlf=");o=num(o,(unsigned long)d->crlf);o=put(o,"\ntime=");o=num(o,(unsigned long)au_sys_now());put(o,"\n\n");
    long r=au_sys_write(fd,head,length(head));
    unsigned int position=0,end=au_length(&d->text);
    while(r>=0&&position<end){unsigned int n=au_encode(&d->text,&position,0,staging,65536);r=au_sys_write(fd,staging,n);}
    if(au_sys_close(fd)<0)r=-5;
    if(r<0){au_sys_unlink(f);return;}
    d->has_recovery=1;d->last_snapshot=now;
    if(!d->snapshot_stamp)mark("AURORA_RECOVERY_SAVED",d->name);
    d->snapshot_stamp=(unsigned long)d->history.current*131+au_length(&d->text)+1;
}
/* Copies left by an earlier run: the ones to offer, oldest first. */
#define RECOVERIES 8
static char recover_names[RECOVERIES][32];static int recover_count,recover_next;
static void found(void *context,const char *name,int folder){
    (void)context;
    if(folder||name[0]!='r'||recover_count>=RECOVERIES)return;
    unsigned long n=length(name);if(n<6||n>30||!same(name+n-4,".txt"))return;
    char f[PATH_BYTES];join(f,recovery_dir,name);struct au_info info;
    if(au_sys_info(f,&info))return;
    long now=au_sys_now();
    if(now>0&&info.mtime>0&&now-info.mtime>RECOVERY_SECONDS){au_sys_unlink(f);return;}
    /* A copy this run owns is not "left over". */
    for(int i=0;i<MAX_DOCS;i++){if(!docs[i].used)continue;char mine[PATH_BYTES];recovery_file(mine,docs[i].recovery_id);if(same(mine,f))return;}
    copy(recover_names[recover_count++],name,32);
}
static char recover_text[TEXT_BYTES+PATH_BYTES+512];
/* Reads a copy: its header fields and where its text starts (0 when it is not a recovery copy). */
static long recovery_read(const char *name,char *path,char *title,int *crlf,long *when){
    char f[PATH_BYTES];join(f,recovery_dir,name);
    long n=au_sys_read(f,recover_text,sizeof(recover_text)-1);
    static const char magic[]="aurora-recovery 1\n";
    if(n<20)return 0;
    for(int i=0;magic[i];i++)if(recover_text[i]!=magic[i])return 0;
    recover_text[n]=0;path[0]=title[0]=0;*crlf=0;*when=0;
    char *l=recover_text;
    while(*l&&*l!='\n'){char *e=l;while(*e&&*e!='\n')e++;
        char c=*e;*e=0;
        if(l[0]=='p'&&l[1]=='a'&&l[2]=='t'&&l[3]=='h'&&l[4]=='=')copy(path,l+5,PATH_BYTES);
        else if(l[0]=='n'&&l[1]=='a'&&l[2]=='m'&&l[3]=='e'&&l[4]=='=')copy(title,l+5,256);
        else if(l[0]=='c'&&l[1]=='r'&&l[2]=='l'&&l[3]=='f'&&l[4]=='=')*crlf=l[5]=='1';
        else if(l[0]=='t'&&l[1]=='i'&&l[2]=='m'&&l[3]=='e'&&l[4]=='='){for(char *d=l+5;*d>='0'&&*d<='9';d++)*when=*when*10+(*d-'0');}
        *e=c;l=*e?e+1:e;
    }
    if(*l!='\n')return 0;
    return (long)(l+1-recover_text)|((long)n<<32);
}
static void offer_recovery(void){
    while(recover_next<recover_count){
        char path[PATH_BYTES],title[256];int crlf;long when;
        long r=recovery_read(recover_names[recover_next],path,title,&crlf,&when);
        if(!r){char f[PATH_BYTES];join(f,recovery_dir,recover_names[recover_next++]);au_sys_unlink(f);continue;}
        char m[600],*o=put(m,"Aurora closed before \"");o=put(o,title[0]?title:"a document");o=put(o,"\" was saved");
        if(path[0]){o=put(o," (");char l[200];copy(l,path,sizeof(l));o=put(o,l);o=put(o,")");}
        put(o,". Restore the unsaved text from then?");
        mark("AURORA_RECOVERY_FOUND",title);
        ask(ASK_RECOVER,-1,"Recover unsaved work?",m,"Restore","Discard","Later");return;
    }
}
static void recovery_answer(int choice){
    if(recover_next>=recover_count)return;
    const char *name=recover_names[recover_next++];char f[PATH_BYTES];join(f,recovery_dir,name);
    if(choice==2){au_sys_unlink(f);mark("AURORA_RECOVERY_DISCARDED",name);}
    if(choice==1){
        char path[PATH_BYTES],title[256];int crlf;long when;long r=recovery_read(name,path,title,&crlf,&when);
        int slot=r?new_doc():-1;
        if(slot>=0){
            struct doc *d=&docs[slot];long start=r&0xffffffff,n=r>>32;
            if(au_load(&d->text,recover_text+start,(unsigned int)(n-start))){remove_doc(slot);fail("Cannot restore","The recovered text is too large for Aurora.");}
            else{
                if(path[0]){copy(d->path,path,sizeof(d->path));copy(d->name,leaf(path),sizeof(d->name));d->language=language_of(d->name);remember_disk(d);}
                else{name_untitled(d);if(title[0])copy(d->name,title,sizeof(d->name));}
                d->crlf=crlf;au_history_clear(&d->history);d->history.saved=-1;   /* restored text is unsaved */
                /* The copy now belongs to this document: kept until it is saved or closed. */
                unsigned long k=length(name);unsigned int id=0;for(unsigned long i=1;i+4<k;i++)id=id*10+(unsigned int)(name[i]-'0');
                d->recovery_id=id;d->has_recovery=1;d->snapshot_stamp=1;
                /* An empty, untouched new document from the start gives way to the restored one. */
                for(int i=doc_count-1;i>=0;i--){struct doc *o=&docs[order[i]];if(order[i]!=slot&&!o->path[0]&&!modified(o)&&!au_length(&o->text)&&!o->history.count)remove_doc(order[i]);}
                current=-1;switch_doc(slot);mark("AURORA_RECOVERED",d->name);
            }
        }
    }
    ui->dirty=1;
    offer_recovery();
}
/* Called by main.c on each pass: recovery copies, and files changed by other programs (every 3 seconds). */
static long last_disk_check;
void aurora_tick(long now){
    if(now<0)return;
    for(int i=0;i<doc_count;i++){
        struct doc *d=&docs[order[i]];
        unsigned long stamp=(unsigned long)d->history.current*131+au_length(&d->text)+1;
        if(!modified(d)){if(d->has_recovery&&d->path[0])recovery_drop(d);continue;}
        if(stamp!=d->snapshot_stamp&&now-d->last_change>=2000&&(now-d->last_snapshot>=15000||!d->has_recovery))recovery_write(d,now);
    }
    if(now-last_disk_check>=3000){
        last_disk_check=now;
        for(int i=0;i<doc_count;i++){
            struct doc *d=&docs[order[i]];if(d->disk_state)continue;
            int c=changed_on_disk(d);if(c){d->disk_state=c;mark("AURORA_CHANGED_ON_DISK",d->path);ui->dirty=1;}
        }
    }
}
void aurora_focus(int focused){
    ui->window_focused=focused;ui->dirty=1;
    if(!focused)return;
    for(int i=0;i<doc_count;i++){
        struct doc *d=&docs[order[i]];int c=changed_on_disk(d);
        if(c&&!d->disk_state){d->disk_state=c;mark("AURORA_CHANGED_ON_DISK",d->path);}
    }
}
void aurora_start(struct zui *u,const char *home_path,int count,char **paths){
    ui=u;if(home_path&&home_path[0]=='/')copy(home,home_path,sizeof(home));
    for(int i=0;i<MAX_DOCS;i++)docs[i].used=0;
    doc_count=0;current=-1;untitled=0;quitting=0;end_requested=0;close_after_save=-1;asking=ASK_NONE;dialog.open=0;
    file_dialog=goto_open=menu_open=find_open=replace_open=focus_find=find_flags=0;status_note[0]=0;hover=H_NONE;
    find_buffer[0]=replace_buffer[0]=goto_buffer[0]=0;zui_scroll_jump(&view,0);
    zui_input_init(&find_input,find_buffer,sizeof(find_buffer));
    zui_input_init(&replace_input,replace_buffer,sizeof(replace_buffer));
    zui_input_init(&goto_input,goto_buffer,sizeof(goto_buffer));
    cell_w=zui_mono.advance[0];line_h=zui_mono.height+5;
    side_scroll=(struct zui_scroll){0};tree_root[0]=0;tree_dirty=1;outline_slot=-1;overwrite=0;last_disk_check=au_sys_clock();
    char a[PATH_BYTES],b[PATH_BYTES];join(a,home,".local/share/aurora");join(b,a,"recovery");copy(recovery_dir,b,sizeof(recovery_dir));
    recover_count=recover_next=0;
    for(int i=0;i<count;i++)open_path(paths[i]);
    if(!doc_count)new_document();
    au_sys_list(recovery_dir,found,0);
    if(recover_count)offer_recovery();
    update_title();ui->dirty=1;
}
/* Called by main.c each loop: keeps the caret in view after edits (needs the last layout). */
void aurora_after_event(void){
    struct doc *d=D();
    if(d&&reveal_caret){reveal_caret=0;if(line_h)reveal(d);ui->dirty=1;}
}
