#ifndef ZENITH_AUDIO_H
#define ZENITH_AUDIO_H
#include "../sunbeam/zenith.h"
/* Audio ABI 1: copied stereo S16LE PCM at 48000 Hz; nonblocking, whole 4-byte
 * frames, max 8192 bytes per call, -11 means try after yielding. 16 streams,
 * 32768 queued bytes each. Handles belong to the invocation, never a PID supplied
 * by a client. Devices are controller/codec/pin identities, not list indices.
 * Selection, gains and mute are live session controls. Apps follow the selected
 * input/output; independent physical routing and persistence are later extensions.
 * Recording requires root or org.zenith.audio.record via the authorization portal.
 * Releasing permission, exiting or ending the session revokes capture and queues. */
struct zenith_audio_device {
    unsigned int id,direction,flags /* bit 0 muted, bit 1 selected */,volume,peak,rate,channels,reserved;
    char name[64];
};
struct zenith_audio_stream {
    unsigned long handle;
    unsigned int pid,uid,direction,device,volume,flags /* bit 0 muted */,peak,queued;
    char name[64];unsigned long reserved[3];
};
struct zenith_audio {
    unsigned int version,device_count,stream_count,flags /* bit 0 controls allowed */;
    unsigned long revision;
    unsigned int output,input,available,rate,channels,bits;
    unsigned long errors,reserved;
    struct zenith_audio_device devices[16];
    struct zenith_audio_stream streams[16];
    unsigned char padding[448];
};
struct zenith_audio_control {
    unsigned int version,action;
    unsigned long target;
    unsigned int value,reserved;
    unsigned long revision;
};
enum { Z_AUDIO_OUTPUT=0,Z_AUDIO_INPUT=1,Z_AUDIO_SELECT_OUTPUT=1,Z_AUDIO_SELECT_INPUT=2,
       Z_AUDIO_DEVICE_VOLUME=3,Z_AUDIO_DEVICE_MUTE=4,Z_AUDIO_STREAM_VOLUME=5,Z_AUDIO_STREAM_MUTE=6,Z_AUDIO_TEST=7 };
_Static_assert(sizeof(struct zenith_audio_device)==96,"audio device ABI");
_Static_assert(sizeof(struct zenith_audio_stream)==128,"audio stream ABI");
_Static_assert(sizeof(struct zenith_audio)==4096,"audio snapshot ABI");
_Static_assert(sizeof(struct zenith_audio_control)==32,"audio control ABI");
static inline long zenith_audio_read(struct zenith_audio *s){return zenith_call(661,(unsigned long)s,sizeof(*s),0);}
static inline long zenith_audio_control(const struct zenith_audio_control *r){return zenith_call(662,(unsigned long)r,sizeof(*r),0);}
static inline long zenith_audio_open(unsigned int direction,unsigned int device){return zenith_call(663,direction,device,0);}
static inline long zenith_audio_write(long handle,const void *pcm,unsigned long bytes){return zenith_call(664,(unsigned long)handle,(unsigned long)pcm,bytes);}
static inline long zenith_audio_capture(long handle,void *pcm,unsigned long bytes){return zenith_call(665,(unsigned long)handle,(unsigned long)pcm,bytes);}
static inline long zenith_audio_close(long handle){return zenith_call(666,(unsigned long)handle,0,0);}
#endif
