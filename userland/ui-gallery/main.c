#include "gallery.h"
#include "../ui/text.h"
#include "../include/zenith_settings.h"
static unsigned int pixels[600*440];
static struct gallery gallery;
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void print(const char *s){unsigned long n=0;while(s[n])n++;zenith_write(s,n);}
static int materials(void){
    struct zenith_material_caps caps={0};
    if(zenith_gui_material_caps(&caps)||caps.version!=2||(caps.features&15)!=15){print("MATERIALS_UNAVAILABLE\n");return 1;}
    struct zenith_appearance appearance={0};
    int have=zenith_appearance_read(&appearance)==0&&appearance.version==1&&appearance.active_mode<=1&&appearance.active_accent<=5;
    if(!gallery_init(&gallery,pixels,have?(int)appearance.active_mode:0))return 2;
    if(have){
        const unsigned int *p=appearance.palette[appearance.active_mode][appearance.active_accent];
        gallery.ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};
    }
    long h=zenith_gui_open_material(600,440,"Material renderer",17);if(h<0)return 3;
    /* Bounds checks precede presentation and must leave the last frame intact. */
    if(zenith_call(607,0xffff800000000000UL,32,0)!=-14){zenith_gui_close(h);return 10;}
    if(zenith_call(606,(unsigned long)h,0xffff800000000000UL,48)!=-14){zenith_gui_close(h);return 11;}
    struct zenith_material_region region={0,0,200,440,0,3,0,0};
    for(unsigned int i=0;i<600*440;i++)pixels[i]=0xff202125;
    region.x=-1;
    if(zenith_gui_present_material(h,pixels,sizeof(pixels),&region,1)!=-22){zenith_gui_close(h);return 12;}
    region.x=0;region.role=1;
    if(zenith_gui_present_material(h,pixels,sizeof(pixels),&region,1)!=-22){zenith_gui_close(h);return 13;}
    region.role=3;pixels[0]=0x00123456;
    if(zenith_gui_present_material(h,pixels,sizeof(pixels),&region,1)!=-22){zenith_gui_close(h);return 14;}
    pixels[0]=0xff202125;pixels[300]=0;
    if(zenith_gui_present_material(h,pixels,sizeof(pixels),&region,1)!=-22){zenith_gui_close(h);return 15;}
    if(zenith_gui_present(h,pixels,sizeof(pixels))!=-22){zenith_gui_close(h);return 16;}
    print("MATERIALS_BOUNDS_OK\n");
    zenith_gui_interactive(h);int effect=0,draw=1,result=0;
    print("MATERIALS_READY\n");
    for(int step=0;step<30000;step++){
        if(draw){
            struct zui *u=&gallery.ui;zui_begin_material_frame(u);
            if(!zui_material_surface(u,(struct zui_rect){0,0,200,440},0,ZUI_SURFACE_NAVIGATION,effect)){result=4;break;}
            zui_surface(u,(struct zui_rect){200,0,400,440},0,ZUI_SURFACE_CONTENT);
            zui_text(u,24,24,"Materials",9,&zui_heading,u->theme.text);
            zui_text(u,24,66,"Opaque foreground",17,&zui_body,u->theme.text);
            zui_text(u,24,96,"1 Solid",7,&zui_label,u->theme.text);
            zui_text(u,24,130,"2 Transparency",14,&zui_label,u->theme.text);
            zui_text(u,24,164,"3 Backdrop blur",15,&zui_label,u->theme.text);
            zui_text(u,224,24,"Renderer preview",16,&zui_heading,u->theme.text);
            zui_paragraph(u,(struct zui_rect){224,78,352,120},"The left navigation can reveal the lower scene. Text and controls keep their foreground coverage. The content canvas stays solid.",&zui_body,u->theme.secondary,0);
            zui_round(u,(struct zui_rect){224,224,200,36},8,u->theme.accent);
            zui_text(u,240,232,"Opaque control",14,&zui_label,u->theme.on_accent);
            if(zenith_gui_present_material(h,pixels,sizeof(pixels),u->materials,u->material_count)){result=5;break;}
            print(effect==0?"MATERIALS_SOLID\n":effect==1?"MATERIALS_TINT\n":"MATERIALS_BLUR\n");draw=0;
        }
        struct zenith_gui_event e={0};long r=zenith_gui_poll(h,&e);if(r<0){result=6;break;}if(!r)continue;
        if(e.type==Z_GUI_CLOSE||(e.type==Z_GUI_KEY&&e.extra==1))break;
        if(e.type==Z_GUI_KEY&&e.code>='1'&&e.code<='3'){effect=e.code-'1';draw=1;}
    }
    zenith_gui_close(h);print("MATERIALS_EXIT\n");return result;
}
int main(int argc,char **argv){
    if(argc>1&&same(argv[1],"--materials"))return materials();
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
