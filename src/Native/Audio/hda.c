/* Zenith Intel High Definition Audio transport. Register/verb definitions follow
 * Intel HDA 1.0a. Polling, fixed DMA storage, no allocator or IRQ callbacks.
 * All entry points are serialized by the BSP-owned AudioService. */
#include <stdint.h>
#include <stddef.h>
#define BLOCK 4096u
#define PERIODS 8u
#define RING (BLOCK*PERIODS)
#define MAX_NODES 128u
#define MAX_ENDPOINTS 16u
struct endpoint { uint32_t id,direction,pin,converter,codec,config,group; uint8_t path[12]; uint8_t length; };
static volatile uint8_t *mmio;
static uint8_t *dma;
static uint64_t physical;
static uint32_t pci,inputs,outputs,count,selected[2],last[2],running[2],serviced[2],errors;
static struct endpoint endpoints[MAX_ENDPOINTS];
static uint32_t caps[MAX_NODES],connections[MAX_NODES][32],connection_count[MAX_NODES];
static inline void out32(uint16_t p,uint32_t v){__asm__ volatile("outl %0,%1"::"a"(v),"Nd"(p));}
static inline uint32_t in32(uint16_t p){uint32_t v;__asm__ volatile("inl %1,%0":"=a"(v):"Nd"(p));return v;}
static uint32_t config(uint32_t slot,uint32_t reg){out32(0xcf8,0x80000000u|slot|reg);return in32(0xcfc);}
static void config_write(uint32_t slot,uint32_t reg,uint32_t v){out32(0xcf8,0x80000000u|slot|reg);out32(0xcfc,v);}
static uint32_t r32(uint32_t o){return *(volatile uint32_t*)(mmio+o);}
static uint16_t r16(uint32_t o){return *(volatile uint16_t*)(mmio+o);}
static void w32(uint32_t o,uint32_t v){*(volatile uint32_t*)(mmio+o)=v;}
static void w16(uint32_t o,uint16_t v){*(volatile uint16_t*)(mmio+o)=v;}
static void w8(uint32_t o,uint8_t v){mmio[o]=v;}
static void pause_cpu(void){__asm__ volatile("pause");}
static int wait32(uint32_t o,uint32_t mask,uint32_t expected){for(uint32_t i=0;i<1000000;i++){if((r32(o)&mask)==expected)return 1;pause_cpu();}errors++;return 0;}
static int delay(uint32_t ticks){uint32_t start=r32(0x30);for(uint32_t i=0;i<10000000;i++){if((uint32_t)(r32(0x30)-start)>=ticks)return 1;pause_cpu();}errors++;return 0;}
uint64_t zenith_hda_probe(void){
    pci=0;mmio=0;count=0;
    for(uint32_t bus=0;bus<256;bus++)for(uint32_t dev=0;dev<32;dev++)for(uint32_t fn=0;fn<8;fn++){
        uint32_t slot=(bus<<16)|(dev<<11)|(fn<<8);uint32_t vendor=config(slot,0);
        if((vendor&0xffff)==0xffff){if(!fn)break;continue;}
        if((config(slot,8)>>8)!=0x040300)continue;
        uint32_t bar=config(slot,0x10);if((bar&1)||!(bar&~15u))continue;
        pci=slot;uint64_t address=bar&~15u;if((bar&6)==4)address|=(uint64_t)config(slot,0x14)<<32;
        /* Preserve command bits; do not echo write-one-to-clear status. */
        config_write(slot,4,(config(slot,4)&0xffff)|6u);return address;
    }
    return 0;
}
static int verb(uint32_t codec,uint32_t node,uint32_t command,uint32_t payload,uint32_t *value){
    if(!wait32(0x68,1,0))return 0;
    w16(0x68,2);w32(0x60,(codec<<28)|(node<<20)|(command<<8)|payload);w16(0x68,1);
    if(!wait32(0x68,3,2))return 0;
    *value=r32(0x64);w16(0x68,2);return 1;
}
static uint32_t get(uint32_t c,uint32_t n,uint32_t cmd,uint32_t value){uint32_t v=0;verb(c,n,cmd,value,&v);return v;}
static void set(uint32_t c,uint32_t n,uint32_t cmd,uint32_t value){uint32_t ignored;verb(c,n,cmd,value,&ignored);}
static int path(uint32_t node,uint32_t wanted_type,uint32_t wanted_node,uint8_t *route,uint32_t depth,uint64_t visited[2]){
    if(node>=MAX_NODES||depth>=12||((visited[node/64]>>(node%64))&1))return 0;
    route[depth]=(uint8_t)node;
    if(wanted_node?node==wanted_node:((caps[node]>>20)&15)==wanted_type)return (int)depth+1;
    visited[node/64]|=1ull<<(node%64);
    for(uint32_t i=0;i<connection_count[node];i++){int n=path(connections[node][i],wanted_type,wanted_node,route,depth+1,visited);if(n)return n;}
    return 0;
}
static void enumerate(uint32_t codec,uint32_t group){
    uint32_t range=get(codec,group,0xf00,4),first=range>>16,end=first+(range&255);
    if(first>=MAX_NODES)return;if(end>MAX_NODES)end=MAX_NODES;
    for(uint32_t n=0;n<MAX_NODES;n++){caps[n]=0;connection_count[n]=0;}
    set(codec,group,0x705,0);
    for(uint32_t n=first;n<end;n++){
        caps[n]=get(codec,n,0xf00,9);uint32_t len=get(codec,n,0xf00,14),total=len&127,prev=0;
        for(uint32_t i=0;i<total&&i<128;){uint32_t values=get(codec,n,0xf02,i),stride=(len&128)?2:4,bits=(len&128)?16:8;
            for(uint32_t j=0;j<stride&&i<total;j++,i++){
                uint32_t item=(values>>(j*bits))&((1u<<bits)-1),node=item&((1u<<(bits-1))-1);
                uint32_t start=(item&(1u<<(bits-1)))?prev+1:node;
                for(uint32_t k=start;k<=node&&connection_count[n]<32;k++)if(k<MAX_NODES)connections[n][connection_count[n]++]=k;
                prev=node;
            }
        }
    }
    for(uint32_t pin=first;pin<end&&count<MAX_ENDPOINTS;pin++){
        if(((caps[pin]>>20)&15)!=4)continue;
        uint32_t cfg=get(codec,pin,0xf1c,0),pc=get(codec,pin,0xf00,12);if((cfg>>30)==1)continue;
        for(uint32_t direction=0;direction<2&&count<MAX_ENDPOINTS;direction++){
            if(!(pc&(direction?32:16)))continue;
            uint8_t route[12];uint64_t visited[2]={0,0};int length=0;uint32_t converter=0;
            if(!direction){length=path(pin,0,0,route,0,visited);if(length)converter=route[length-1];}
            else for(uint32_t n=first;n<end;n++)if(((caps[n]>>20)&15)==1){visited[0]=visited[1]=0;length=path(n,4,pin,route,0,visited);if(length){converter=n;break;}}
            if(!length)continue;
            uint32_t formats=get(codec,converter,0xf00,10);if(!formats)formats=get(codec,group,0xf00,10);
            if((formats&0x20040)!=0x20040)continue; /* 48 kHz, signed 16-bit */
            uint32_t streams=get(codec,converter,0xf00,11);if(!streams)streams=get(codec,group,0xf00,11);if(!(streams&1)||!(caps[converter]&1))continue;
            struct endpoint *e=&endpoints[count++];
            e->id=1+(pci<<8)+(codec<<12)+(direction<<11)+pin;e->direction=direction;e->pin=pin;e->converter=converter;e->codec=codec;e->config=cfg;e->group=group;e->length=(uint8_t)length;
            for(int i=0;i<length;i++)e->path[i]=route[i];
        }
    }
}
static uint32_t stream(uint32_t direction){return 0x80+32*(direction?0:inputs);}
static void stop(uint32_t direction){if(!mmio||!running[direction])return;uint32_t s=stream(direction);w8(s,0);wait32(s,2,0);running[direction]=0;}
uint64_t zenith_hda_select(uint64_t direction,uint64_t id){
    if(direction>1)return 0;
    uint32_t before=errors;
    struct endpoint *e=0;for(uint32_t i=0;i<count;i++)if(endpoints[i].direction==direction&&endpoints[i].id==id)e=&endpoints[i];if(!e)return 0;
    stop((uint32_t)direction);
    /* Detach the former pin; one engine per direction, endpoint selection is global. */
    if(selected[direction])for(uint32_t i=0;i<count;i++)if(endpoints[i].id==selected[direction]){
        set(endpoints[i].codec,endpoints[i].converter,0x706,0);set(endpoints[i].codec,endpoints[i].pin,0x707,0);
    }
    for(uint32_t i=0;i<e->length;i++){
        uint32_t input_index=0,num_inputs=1;
        uint32_t n=e->path[i];if(get(e->codec,n,0xf00,9)&0x400)set(e->codec,n,0x705,0);
        if(i+1<e->length){
            uint32_t len=get(e->codec,n,0xf00,14),index=0,previous=0;
            for(uint32_t k=0;k<(len&127);k++){
                uint32_t bits=(len&128)?16:8,per=32/bits;
                uint32_t value=get(e->codec,n,0xf02,(k/per)*per),item=(value>>((k%per)*bits))&((1u<<bits)-1),node=item&((1u<<(bits-1))-1);
                uint32_t first=(item&(1u<<(bits-1)))?previous+1:node;
                for(uint32_t v=first;v<=node;v++,index++)if(v==e->path[i+1]){input_index=index;set(e->codec,n,0x701,index);}
                previous=node;
            }
            num_inputs=index;
        }
        /* Amp offset is its unity-gain step, not an arbitrary maximum boost. */
        uint32_t wc=get(e->codec,n,0xf00,9);
        if(wc&4){uint32_t a=get(e->codec,n,0xf00,18);if(!a)a=get(e->codec,e->group,0xf00,18);set(e->codec,n,0x300,0xb000|(a&127));}
        if(wc&2){uint32_t a=get(e->codec,n,0xf00,13);if(!a)a=get(e->codec,e->group,0xf00,13);for(uint32_t k=0;k<num_inputs&&k<16;k++)set(e->codec,n,0x300,0x7000|(k<<8)|(a&127)|(k==input_index?0:128));}
    }
    uint32_t pinctl=direction?0x20:0x40,pc=get(e->codec,e->pin,0xf00,12);
    if(!direction&&(pc&8))pinctl|=0x80;
    if(direction){if(pc&(1<<12))pinctl|=4;else if(pc&(1<<10))pinctl|=2;}
    set(e->codec,e->pin,0x707,pinctl);set(e->codec,e->pin,0x70c,2);
    set(e->codec,e->converter,0x200,0x11); /* 48 kHz, 16 bits, stereo */
    set(e->codec,e->converter,0x706,(direction?2:1)<<4);
    if(errors!=before){selected[direction]=0;return 0;}
    selected[direction]=(uint32_t)id;return 1;
}
uint64_t zenith_hda_initialize(uint64_t address,uint64_t virtual_dma,uint64_t physical_dma){
    mmio=(volatile uint8_t*)(uintptr_t)address;dma=(uint8_t*)(uintptr_t)virtual_dma;physical=physical_dma;errors=0;
    uint32_t gcap=r16(0);inputs=(gcap>>8)&15;outputs=(gcap>>12)&15;
    if(!inputs||!outputs||(!(gcap&1)&&(physical_dma+69632>0x100000000ull)))return 0;
    w32(0x20,0);w8(0x4c,0);w8(0x5c,0);w32(8,0);if(!wait32(8,1,0))return 0;
    w32(8,1);if(!wait32(8,1,1)||!delay(24000))return 0;
    uint32_t codecs=r16(14);
    for(uint32_t c=0;c<15;c++)if(codecs&(1u<<c)){
        uint32_t range=get(c,0,0xf00,4),first=range>>16,number=range&255;
        for(uint32_t g=first;g<first+number&&g<MAX_NODES;g++)if((get(c,g,0xf00,5)&255)==1)enumerate(c,g);
    }
    for(uint32_t d=0;d<2;d++){
        selected[d]=0;running[d]=0;last[d]=0;
        for(uint32_t i=0;i<count;i++)if(endpoints[i].direction==d&&zenith_hda_select(d,endpoints[i].id))break;
    }
    return selected[0]||selected[1]?count:0;
}
uint64_t zenith_hda_field(uint64_t index,uint64_t field){
    if(index==UINT64_MAX)return field==0?count:errors;
    if(index>=count)return 0;struct endpoint *e=&endpoints[index];
    switch(field){case 0:return e->id;case 1:return e->direction;case 2:return e->config;case 3:return e->codec;case 4:return e->pin;case 5:return selected[e->direction]==e->id;default:return 0;}
}
uint64_t zenith_hda_run(uint64_t direction,uint64_t enable){
    if(!mmio||direction>1||!selected[direction])return 0;
    uint32_t d=(uint32_t)direction;if(!enable){stop(d);return 1;}if(running[d])return 1;
    uint32_t s=stream(d);w8(s,1);if(!wait32(s,1,1))return 0;w8(s,0);if(!wait32(s,1,0))return 0;
    uint32_t base=4096+d*RING;for(uint32_t i=0;i<RING;i++)dma[base+i]=0;
    for(uint32_t i=0;i<PERIODS;i++){
        uint64_t *entry=(uint64_t*)(dma+d*128+i*16);entry[0]=physical+base+i*BLOCK;entry[1]=BLOCK;
    }
    w32(s+8,RING);w16(s+12,PERIODS-1);w16(s+18,0x11);w32(s+24,(uint32_t)(physical+d*128));w32(s+28,(uint32_t)((physical+d*128)>>32));
    w8(s+2,(uint8_t)((d?2:1)<<4));w8(s+3,0x1c);last[d]=0;serviced[d]=r32(0x30);running[d]=1;__asm__ volatile("mfence":::"memory");w8(s,2);return 1;
}
/* Return a completed period, never the active DMA period. -1 means no data yet. */
int64_t zenith_hda_period(uint64_t direction){
    if(direction>1||!running[direction])return -1;
    uint32_t d=(uint32_t)direction,s=stream(d),position=(r32(s+4)%RING)/BLOCK,now=r32(0x30);
    /* A collector or stalled host can outlast a ring. Drop obsolete periods rather
     * than confusing a full wrap with no DMA progress. Bound recovery work. */
    if((uint32_t)(now-serviced[d])>4096000u){
        errors++;last[d]=position;
        if(!d)for(uint32_t i=0;i<RING;i++)dma[4096+i]=0;
    }
    serviced[d]=now;
    if(mmio[s+3]&0x18){errors++;w8(s+3,0x1c);}
    if(position==last[d])return -1;uint32_t done=last[d];last[d]=(last[d]+1)%PERIODS;return done;
}
uint64_t zenith_hda_transfer(uint64_t direction,uint64_t period,uint64_t buffer){
    if(direction>1||period>=PERIODS||!running[direction])return 0;
    if((r32(stream((uint32_t)direction)+4)%RING)/BLOCK==period){errors++;return 0;}
    volatile uint8_t *p=dma+4096+direction*RING+period*BLOCK;uint8_t *b=(uint8_t*)(uintptr_t)buffer;
    if(direction){for(uint32_t i=0;i<BLOCK;i++)b[i]=p[i];}else{for(uint32_t i=0;i<BLOCK;i++)p[i]=b[i];}
    __asm__ volatile("mfence":::"memory");return BLOCK;
}
