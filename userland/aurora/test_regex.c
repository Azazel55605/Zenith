#include <stdio.h>
#include <string.h>
#include "regex.h"
static char data[1<<16];static unsigned int lines[4096];static int fails;
static void T(const char *pat,int flags,const char *text,long want,int want_len,const char *g1){
    struct au_text t;au_text_init(&t,data,sizeof(data),lines,4096);au_load(&t,text,(unsigned)strlen(text));
    static struct rx rx;if(rx_compile(&rx,pat,(unsigned)strlen(pat),flags)){if(want!=-9){printf("FAIL compile %s: %s\n",pat,rx.error);fails++;}return;}
    if(want==-9){printf("FAIL %s should not compile\n",pat);fails++;return;}
    unsigned int end;long g[20];long r=rx_search(&rx,&t,0,au_length(&t),&end,g);
    int ok=r==want&&(r<0||(int)(end-r)==want_len);
    if(ok&&g1&&r>=0){char b[64]={0};if(g[2]<0)ok=!g1[0];else{memcpy(b,text+g[2],g[3]-g[2]);ok=!strcmp(b,g1);}}
    if(!ok){printf("FAIL /%s/ on \"%s\": got %ld len %d\n",pat,text,r,r>=0?(int)(end-r):0);fails++;}
}
int main(void){
    T("abc",0,"xxabcx",2,3,0); T("a.c",0,"abc",0,3,0); T("a.c",0,"a\nc",-1,0,0);
    T("colou?r",0,"the color",4,5,0); T("go+gle",0,"gooogle",0,7,0); T("x*y",0,"y",0,1,0);
    T("[0-9]+",0,"abc 2026 x",4,4,0); T("[^a-z ]+",0,"abc DEF",4,3,0); T("\\d{2,3}",0,"1 12345",2,3,0);
    T("\\bcat\\b",0,"concat cat",7,3,0); T("^foo",0,"x foo\nfoo",6,3,0); T("bar$",0,"bar x\nbar",6,3,0);
    T("(ab)+",0,"xababab",1,6,"ab"); T("(a|b)c",0,"zbc",1,2,"b"); T("<.+?>",0,"<a><b>",0,3,0);
    T("Try(Restore|Save)\\(",0,"bool TryRestore(path)",5,11,"Restore"); T("HELLO",RX_ICASE,"say hello",4,5,0);
    T("\\w+@\\w+\\.com",0,"mail: me@zenith.com!",6,13,0); T("(x)?y",0,"y",0,1,""); T("a{3}",0,"aa aaa",3,3,0);
    T("(",0,"",-9,0,0); T("a)",0,"",-9,0,0); T("*a",0,"",-9,0,0); T("[z-a]",0,"",-9,0,0);
    /* Catastrophic patterns stop at the step budget instead of hanging. */
    T("(a*)*b",0,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaac",-1,0,0);
    /* Replacement with groups. */
    struct au_text t;au_text_init(&t,data,sizeof(data),lines,4096);au_load(&t,"key=value",9);
    static struct rx rx;rx_compile(&rx,"(\\w+)=(\\w+)",11,0);unsigned int end;long g[20];rx_search(&rx,&t,0,9,&end,g);
    char out[64];unsigned int n=rx_expand(&t,"$2:$1 $$",8,g,out,64);out[n]=0;
    if(strcmp(out,"value:key $")){printf("FAIL expand %s\n",out);fails++;}
    printf("regex: %s\n",fails?"FAILED":"passed");return fails!=0;
}
