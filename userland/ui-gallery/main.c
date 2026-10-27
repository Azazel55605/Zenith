#include "gallery.h"
static unsigned int pixels[600*440];
static struct gallery gallery;
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){unsigned long n=0;while(s[n])n++;zenith_write(s,n);}
int main(int argc,char **argv){
    if(argc>1&&same(argv[1],"--version")){print("ui-gallery " SUNBEAM_VERSION "\n");return 0;}
    if(!gallery_init(&gallery,pixels,argc>1&&same(argv[1],"--light")))return 1;
    long h=zenith_gui_open(600,440,"Zenith widget gallery",21);
    if(h<0){print("UI_UNAVAILABLE\n");return 1;}
    gallery_draw(&gallery);
    if(zenith_gui_present(h,pixels,sizeof(pixels))<0){zenith_gui_close(h);return 2;}
    print("UI_READY\n");
    if(argc>1&&same(argv[1],"--probe")){zenith_gui_close(h);print("UI_PROBE_OK\n");return 0;}
    int result=0;
    /* Bounded ABI 1 lifetime; no redraw on idle polls. */
    for(int i=0;i<30000;i++){
        struct zenith_gui_event e={0};long r=zenith_gui_poll(h,&e);
        if(r<0){result=3;break;}if(!r)continue;
        if(e.type==Z_GUI_CLOSE||(e.type==Z_GUI_KEY&&e.extra==1))break;
        if(gallery_event(&gallery,&e))print("UI_CHANGED\n");
        if(gallery.ui.dirty){gallery_draw(&gallery);if(zenith_gui_present(h,pixels,sizeof(pixels))<0){result=4;break;}}
    }
    zenith_gui_close(h);print("UI_EXIT\n");return result;
}
