/* Qualification client for the public audio ABI. Never linked into the kernel. */
#include "../include/zenith_audio.h"
static struct zenith_audio state;
static unsigned char pcm[8192];
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static int fail(const char *s){unsigned long n=0;while(s[n])n++;zenith_write(s,n);return 1;}
static int send(long handle,unsigned long bytes){
    unsigned long off=0;for(int tries=0;off<bytes&&tries<10000;tries++){
        long n=zenith_audio_write(handle,pcm+off,bytes-off);
        if(n==-11)continue;
        if(n<=0)return 0;
        off+=(unsigned long)n;
    }return off==bytes;
}
int main(int argc,char **argv){
    if(zenith_audio_read(&state)||state.version!=1||state.device_count>16||state.stream_count>16)return fail("AUDIO_PROBE inventory failed\n");
    if(argc==2&&same(argv[1],"--absent"))return !state.available&&zenith_audio_open(0,0)==-19?0:fail("AUDIO_PROBE absent device failure\n");
    if(argc==2&&same(argv[1],"--empty"))return !state.stream_count?0:fail("AUDIO_PROBE leaked stream\n");
    if(argc==2&&same(argv[1],"--deny"))return zenith_audio_open(1,0)==-13?0:fail("AUDIO_PROBE microphone admission failure\n");
    if(argc==2&&(same(argv[1],"--remember")||same(argv[1],"--remembered"))){
        for(unsigned int d=0;d<2;d++){
            unsigned int id=d?state.input:state.output,volume=d?23:70;
            if(!id)return 1;
            if(same(argv[1],"--remember")){
                struct zenith_audio_control r={1,Z_AUDIO_DEVICE_VOLUME,id,volume,0,state.revision};
                if(zenith_audio_control(&r)||zenith_audio_read(&state))return 1;
                r.action=Z_AUDIO_DEVICE_MUTE;r.value=d;r.revision=state.revision;
                if(zenith_audio_control(&r)||zenith_audio_read(&state))return 1;
            }
            int match=0;
            for(unsigned int i=0;i<state.device_count;i++){
                const struct zenith_audio_device *dev=&state.devices[i];
                if(dev->id==id&&dev->direction==d&&dev->volume==volume&&(dev->flags&3)==(2|d))match=1;
            }
            if(!match)return fail("AUDIO_PROBE missing device preference\n");
        }
        return state.stream_count!=0;
    }
    if(zenith_call(661,1,sizeof(state),0)!=-14||zenith_call(661,(unsigned long)&state,8,0)!=-22)return fail("AUDIO_PROBE range checks failed\n");
    struct zenith_audio_control bad={1,Z_AUDIO_DEVICE_VOLUME,state.output,101,0,state.revision};
    if(zenith_audio_control(&bad)!=-22)return fail("AUDIO_PROBE gain bound failed\n");
    bad.value=40;bad.revision=0;if(zenith_audio_control(&bad)!=-11)return fail("AUDIO_PROBE stale control failed\n");
    bad.action=Z_AUDIO_SELECT_OUTPUT;bad.value=0;bad.revision=state.revision;
    if(zenith_audio_control(&bad)||zenith_audio_read(&state))return fail("AUDIO_PROBE output selection failed\n");
    if(state.input){bad.action=Z_AUDIO_SELECT_INPUT;bad.target=state.input;bad.revision=state.revision;
        if(zenith_audio_control(&bad))return fail("AUDIO_PROBE input selection failed\n");}
    long h=zenith_audio_open(0,0);if(h<=0)return fail("AUDIO_PROBE output open failed\n");
    if(argc==2&&same(argv[1],"--muted")){
        if(zenith_audio_read(&state))return 1;
        struct zenith_audio_control mute={1,Z_AUDIO_STREAM_MUTE,(unsigned long)h,1,0,state.revision};
        if(zenith_audio_control(&mute))return fail("AUDIO_PROBE mute failed\n");
    }
    if(argc==2&&same(argv[1],"--fault")){__asm__ volatile("ud2");return 1;}
    if(zenith_call(664,(unsigned long)h,1,4)!=-14||zenith_audio_write(h,pcm,3)!=-22||zenith_audio_capture(h,pcm,4)!=-22)return fail("AUDIO_PROBE transfer validation failed\n");
    /* Square-wave PCM; enough quiet periods afterwards to drain every tone period. */
    for(unsigned long i=0;i<sizeof(pcm);i+=4){int sample=(i/4)%100<50?4000:-4000;pcm[i]=pcm[i+2]=(unsigned char)sample;pcm[i+1]=pcm[i+3]=(unsigned char)(sample>>8);}
    for(int i=0;i<12;i++)if(!send(h,sizeof(pcm)))return fail("AUDIO_PROBE output stalled\n");
    for(unsigned long i=0;i<sizeof(pcm);i++)pcm[i]=0;
    for(int i=0;i<8;i++)if(!send(h,sizeof(pcm)))return fail("AUDIO_PROBE output drain stalled\n");
    if(zenith_audio_close(h)||zenith_audio_close(h)!=-9||zenith_audio_write(h,pcm,4)!=-9)return fail("AUDIO_PROBE stale handle survived\n");
    if(argc==2&&(same(argv[1],"--output")||same(argv[1],"--muted")))return 0;
    h=zenith_audio_open(1,0);if(h<=0)return fail("AUDIO_PROBE input open failed\n");
    unsigned long captured=0;for(int tries=0;captured<192000&&tries<10000;tries++){
        long n=zenith_audio_capture(h,pcm,sizeof(pcm));if(n==-11)continue;
        if(n<=0)return fail("AUDIO_PROBE capture failed\n");
        captured+=(unsigned long)n;
    }
    if(zenith_audio_close(h)||captured<192000)return fail("AUDIO_PROBE capture DMA stalled\n");
    if(zenith_audio_read(&state)||state.stream_count)return fail("AUDIO_PROBE cleanup failed\n");
    zenith_write("AUDIO_PROBE_OK\n",15);return 0;
}
