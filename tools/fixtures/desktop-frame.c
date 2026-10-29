#include "ui.h"
#include <stdio.h>
static unsigned int pixels[600*480];
int main(int argc,char **argv){
    if(argc!=2&&argc!=3)return 2;
    struct zui ui;
    if(!zui_init(&ui,pixels,600,480))return 3;
    zui_appearance(&ui,0,5);ui.window_focused=1;
    struct zui_workspace w=zui_workspace_begin(&ui,156);
    struct zui_rect c=w.content;
    zui_text(&ui,24,28,"System",6,&zui_label,ui.theme.text);
    zui_add(&ui,1,ZUI_BUTTON,(struct zui_rect){c.x+16,c.y+c.h-48,100,34},"Refresh",0,1);
    zui_add(&ui,2,ZUI_BUTTON,(struct zui_rect){c.x+c.w-112,c.y+c.h-48,96,34},"Close",0,argc==2);
    zui_draw_controls(&ui);
    FILE *out=fopen(argv[1],"wb");if(!out)return 4;
    fprintf(out,"P6\n600 480\n255\n");
    for(int i=0;i<600*480;i++){
        unsigned char rgb[3]={(unsigned char)(pixels[i]>>16),(unsigned char)(pixels[i]>>8),(unsigned char)pixels[i]};
        if(fwrite(rgb,1,3,out)!=3){fclose(out);return 5;}
    }
    return fclose(out)?6:0;
}
