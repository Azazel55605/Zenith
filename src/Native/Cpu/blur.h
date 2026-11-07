/* C4 private snapshots. No managed/scene pointer survives submission. Each AP
 * retains one payload until its generation is reaped. Tile halos are clamped
 * by BSP against the whole region, never against the tile boundary. */
#define BLUR_W 256
#define BLUR_H 64
#define BLUR_INPUT ((BLUR_W+8)*(BLUR_H+8))
struct blur_work {
    u64 token;
    u32 width,height,operation;
    u32 source[BLUR_INPUT], horizontal[BLUR_W*(BLUR_H+8)], output[BLUR_W*BLUR_H];
};
static struct blur_work blur_work[CPUS];
static u64 blur_cursor, blur_completed, blur_cancelled;
static u64 compose_completed, compose_cancelled;

static u32 blur_color(u32 r,u32 g,u32 b) {
    return 0xff000000U|((r+4)/9<<16)|((g+4)/9<<8)|(b+4)/9;
}
static void blur_execute(struct job *j) {
    struct blur_work *b=&blur_work[(j->token&63)/JOBS];
    if(b->token!=j->token) { STORE(j->state,FAULTED);return; }
    u32 w=b->width,h=b->height,sw=w+8,sh=h+8;
    if(b->operation==1) {
        for(u32 y=0;y<h;y++) {
            if(cancelled(j))return;
            for(u32 x=0;x<w;x++) {
                u32 i=y*w+x,f=b->source[i],bg=b->horizontal[i],a=f>>24;
                if(a==255){b->output[i]=f;continue;}
                if(!a){b->output[i]=bg;continue;}
                u32 out=0xff000000U;
                for(u32 shift=0;shift<24;shift+=8)
                    out|=(((f>>shift)&255)+(((bg>>shift)&255)*(255-a)+127)/255)<<shift;
                b->output[i]=out;
            }
        }
        return;
    }
    for(u32 y=0;y<sh;y++) {
#ifdef ZENITH_CPU_HOST_TEST
        if(y==1&&LOAD(host_pause)) {
            STORE(host_seen,1);
            while(LOAD(host_pause)&&!cancelled(j))__asm__ volatile("pause":::"memory");
        }
#endif
        if(cancelled(j))return;
        u32 r=0,g=0,blue=0;
        for(u32 x=0;x<9;x++) {u32 c=b->source[y*sw+x];r+=(c>>16)&255;g+=(c>>8)&255;blue+=c&255;}
        for(u32 x=0;x<w;x++) {
            b->horizontal[y*w+x]=blur_color(r,g,blue);
            if(x+1<w) {
                u32 a=b->source[y*sw+x],c=b->source[y*sw+x+9];
                r+=((c>>16)&255)-((a>>16)&255);g+=((c>>8)&255)-((a>>8)&255);blue+=(c&255)-(a&255);
            }
        }
    }
    /* Contiguous output rows keep copies/cache lines bounded; use a running
     * vertical sum for each column, preserving exact scalar rounding. */
    for(u32 x=0;x<w;x++) {
        if(cancelled(j))return;
        u32 r=0,g=0,blue=0;
        for(u32 y=0;y<9;y++) {u32 c=b->horizontal[y*w+x];r+=(c>>16)&255;g+=(c>>8)&255;blue+=c&255;}
        for(u32 y=0;y<h;y++) {
            b->output[y*w+x]=blur_color(r,g,blue);
            if(y+1<h) {
                u32 a=b->horizontal[y*w+x],c=b->horizontal[(y+9)*w+x];
                r+=((c>>16)&255)-((a>>16)&255);g+=((c>>8)&255)-((a>>8)&255);blue+=(c&255)-(a&255);
            }
        }
    }
}
