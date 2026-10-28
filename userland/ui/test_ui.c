/* Host-only contract tests: execute the same renderer and interaction code as the ELF. */
#include <assert.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include "../ui-gallery/gallery.h"
static unsigned int storage[600*440+2];
static struct gallery g;
static int event(int type,int code,int x,int y,int modifiers,int extra){
    struct zenith_gui_event e={type,code,x,y,modifiers,extra};return gallery_event(&g,&e);
}
static void image(const char *directory,const char *name){
    char path[4096];assert(snprintf(path,sizeof(path),"%s/%s.ppm",directory,name)>0);
    FILE *f=fopen(path,"wb");assert(f);fprintf(f,"P6\n600 440\n255\n");
    for(int i=0;i<600*440;i++){
        unsigned int p=g.ui.pixels[i];unsigned char bytes[]={p>>16,p>>8,p};
        assert(fwrite(bytes,1,3,f)==3);
    }
    assert(fclose(f)==0);
}
int main(int argc,char **argv){
    assert(argc==2);
    /* Material intent must preserve today's opaque pixels, geometry and clip.
     * It is not an alpha protocol or permission to sample a backdrop. */
    struct zui material;unsigned int actual[32*32],expected[32*32];
    assert(zui_init(&material,actual,32,32));
    for(int light=0;light<2;light++)for(int accent=0;accent<6;accent++){
        zui_appearance(&material,light,accent);
        for(int role=ZUI_SURFACE_CONTENT;role<=ZUI_SURFACE_POPOVER;role++){
            zui_clip(&material,(struct zui_rect){0,0,32,32});zui_fill(&material,material.clip,0x123456);
            zui_clip(&material,(struct zui_rect){3,4,20,19});
            assert(zui_surface(&material,(struct zui_rect){1,2,28,28},8,role));
            for(int k=0;k<32*32;k++)expected[k]=actual[k];
            zui_clip(&material,(struct zui_rect){0,0,32,32});zui_fill(&material,material.clip,0x123456);
            zui_clip(&material,(struct zui_rect){3,4,20,19});
            unsigned int color=role==ZUI_SURFACE_CONTENT?material.theme.surface:role==ZUI_SURFACE_NAVIGATION?material.theme.subtle:material.theme.raised;
            zui_round(&material,(struct zui_rect){1,2,28,28},8,color);
            for(int k=0;k<32*32;k++)assert(expected[k]==actual[k]&&actual[k]<=0xffffff);
        }
    }
    assert(!zui_backdrop_candidate(ZUI_SURFACE_CONTENT)&&!zui_backdrop_candidate(ZUI_SURFACE_CONTROL));
    assert(zui_backdrop_candidate(ZUI_SURFACE_NAVIGATION)&&zui_backdrop_candidate(ZUI_SURFACE_POPOVER));
    unsigned int before=actual[4*32+4];assert(!zui_surface(&material,(struct zui_rect){0,0,32,32},0,999));assert(actual[4*32+4]==before);

    storage[0]=0x12345678;storage[600*440+1]=0xabcdef01;
    assert(gallery_init(&g,storage+1,0));
    char palette_path[4096];assert(snprintf(palette_path,sizeof(palette_path),"%s/palette.tsv",argv[1])>0);
    FILE *palette=fopen(palette_path,"w");assert(palette);
    for(int mode=0;mode<2;mode++)for(int accent=0;accent<6;accent++){
        zui_appearance(&g.ui,mode,accent);struct zui_theme t=g.ui.theme;
        g.light=mode;g.accent=accent;g.ui.controls[0].value=!mode;g.ui.controls[1].value=mode;
        for(int i=0;i<6;i++)g.ui.controls[i+2].value=i==accent;
        gallery_draw(&g);char name[64];snprintf(name,sizeof(name),"gallery-%d-%d",mode,accent);image(argv[1],name);
        /* Compact color targets keep a readable name and a geometric selection
         * check; they cannot silently resolve to the currently selected color. */
        assert(g.ui.pixels[206*600+40]==zui_accent_color(&g.ui,0)||accent==0);
        assert(g.ui.pixels[193*600+24]==g.ui.theme.surface); /* circular corner */
        fprintf(palette,"%d %d %06x %06x %06x %06x %06x %06x %06x\n",mode,accent,
            t.surface,t.raised,t.subtle,t.text,t.secondary,t.accent,t.on_accent);
    }
    assert(fclose(palette)==0);g.light=0;g.accent=0;zui_appearance(&g.ui,0,0);
    g.ui.controls[0].value=1;g.ui.controls[1].value=0;
    for(int i=0;i<6;i++)g.ui.controls[i+2].value=i==0;
    gallery_draw(&g);image(argv[1],"dark");
    assert(g.ui.count==10&&g.ui.focus==-1&&!g.ui.dirty);
    assert(!event(Z_GUI_KEY,0,0,0,0,4));assert(g.ui.focus==-1); /* no unfocused input */
    event(Z_GUI_FOCUS,1,0,0,0,0);
    event(Z_GUI_KEY,0,0,0,0,4);assert(g.ui.focus==0);
    event(Z_GUI_KEY,0,0,0,0,4);assert(g.ui.focus==1);
    assert(event(Z_GUI_KEY,0,0,0,0,2)==2&&g.light);
    gallery_draw(&g);image(argv[1],"light");
    for(int i=0;i<6;i++){
        event(Z_GUI_DOWN,1,40+i*44,205,0,0);
        assert(event(Z_GUI_UP,1,40+i*44,205,0,0)==10+i&&g.accent==i);
        assert(g.ui.controls[i+2].value);
    }
    /* Arrow keys traverse choices without altering unrelated controls; focused
     * swatches retain a textual label and unavailable effects never activate. */
    assert(event(Z_GUI_KEY,0,0,0,0,5)==14&&g.accent==4);
    assert(event(Z_GUI_KEY,0,0,0,0,6)==15&&g.accent==5);
    gallery_draw(&g);image(argv[1],"focused");
    /* Tab skips unavailable controls, wraps; Shift+Tab reverses. */
    event(Z_GUI_KEY,0,0,0,0,4);assert(g.ui.focus==0);
    event(Z_GUI_KEY,0,0,0,1,4);assert(g.ui.focus==7);
    assert(!event(Z_GUI_KEY,' ',0,0,2,0)); /* Ctrl+Space is not activation */
    event(Z_GUI_DOWN,1,40,290,0,0);assert(g.ui.pressed==-1);
    assert(!event(Z_GUI_UP,1,40,290,0,0));
    event(Z_GUI_DOWN,1,40,120,0,0);event(Z_GUI_FOCUS,0,0,0,0,0);
    event(Z_GUI_FOCUS,1,0,0,0,0);assert(!event(Z_GUI_UP,1,40,120,0,0));
    event(Z_GUI_DOWN,1,40,120,0,0);event(Z_GUI_RESET,0,0,0,0,0);
    assert(!event(Z_GUI_UP,1,40,120,0,0));
    event(Z_GUI_DOWN,1,40,120,0,0);assert(!event(Z_GUI_UP,1,599,439,0,0));
    assert(!zui_add(&g.ui,1,ZUI_BUTTON,(struct zui_rect){0,0,10,10},"duplicate",0,1));
    assert(!zui_add(&g.ui,90,ZUI_BUTTON,(struct zui_rect){INT_MAX,0,10,10},"bad",0,1));
    /* Clip untrusted geometry before touching pixels; canaries and sanitizers guard writes. */
    zui_clip(&g.ui,(struct zui_rect){INT_MAX,INT_MAX,INT_MAX,INT_MAX});
    zui_fill(&g.ui,(struct zui_rect){0,0,600,440},0);
    zui_clip(&g.ui,(struct zui_rect){0,0,600,440});
    zui_round(&g.ui,(struct zui_rect){INT_MIN,INT_MIN,INT_MAX,INT_MAX},INT_MAX,0);
    zui_fill(&g.ui,(struct zui_rect){-10,-10,20,20},0xffffff);
    zui_text(&g.ui,-100,-100,"clip",4,&zui_body,0xffffff);
    zui_text(&g.ui,INT_MIN,INT_MIN,"offscreen",9,&zui_heading,0xffffff);
    assert(zui_measure("abc",2,&zui_body)==zui_measure("ab",2,&zui_body));
    assert(zui_measure("\xe2\x98\x83",3,&zui_body)==zui_measure("?",1,&zui_body));
    assert(zui_measure("\xf0",1,&zui_body)==zui_measure("?",1,&zui_body));
    /* Deterministic event/geometry stress also checks that reset never activates. */
    unsigned int seed=42;
    for(int i=0;i<10000;i++){
        seed=seed*1664525+1013904223;int x=(int)(seed%800)-100;
        seed=seed*1664525+1013904223;int y=(int)(seed%640)-100;
        event(Z_GUI_DOWN,1,x,y,0,0);event(Z_GUI_RESET,0,0,0,0,0);
        assert(!event(Z_GUI_UP,1,x,y,0,0));
        zui_round(&g.ui,(struct zui_rect){x,y,50,30},8,seed);
    }
    for(int i=0;i<g.ui.count;i++)g.ui.controls[i].enabled=0;
    event(Z_GUI_KEY,0,0,0,0,4);assert(g.ui.focus==-1);
    assert(storage[0]==0x12345678&&storage[600*440+1]==0xabcdef01);
    puts("PASS UI renderer bounds, fonts, theme, focus, activation, disabled/reset/capture and 10000 event sequences");
    return 0;
}
