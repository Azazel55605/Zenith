#ifndef ZENITH_NETWORK_H
#define ZENITH_NETWORK_H
/* zenith-native-network-center-abi 1.0 (kernel 0.0.69): call 667 (request, 64, 0) for Beacon, Meridian's Network page,
 * Observatory and anything else that shows or changes the connection. docs/components/BEACON.md. Older kernels answer -38.
 * Results: bytes written for reads, milliseconds for ping, 0 for changes; -13 without root or the network grant
 * (org.zenith.network.manage), -22 malformed, -34 buffer too small, -5 failed (a plain-words message is written to
 * the output buffer when one was given). */
#include "zenith_graphics.h"

struct zenith_network_request { unsigned int version,op; unsigned long output; unsigned int capacity,value; unsigned long input; unsigned int input_bytes,reserved[7]; };
_Static_assert(sizeof(struct zenith_network_request)==64,"network request");
enum { Z_NET_STATUS=1,Z_NET_HISTORY=2,Z_NET_EVENTS=3,Z_NET_CHECK=4,Z_NET_PING=5,Z_NET_APPLY=6,Z_NET_CONNECT=7,Z_NET_PROGRAMS=8 };

/* Z_NET_STATUS (512 bytes). state: 0 no adapter, 1 off/disconnected, 2 cable out, 3 connecting, 4 online, 5 failed.
 * mode (the saved settings): 0 automatic (DHCP), 1 manual, 2 off. Seconds are -1 when not applicable. */
enum { Z_NET_NO_ADAPTER=0,Z_NET_OFF=1,Z_NET_CABLE_OUT=2,Z_NET_CONNECTING=3,Z_NET_ONLINE=4,Z_NET_FAILED=5 };
enum { Z_NET_LINK=1,Z_NET_LEASE=2,Z_NET_BUSY=8,Z_NET_DNS_OVERRIDE=16 };
struct zenith_network_status {
    unsigned int version,state,mode,flags;
    unsigned char address[4],mask[4],gateway[4],dns[4][4];
    unsigned int dns_count; unsigned char mac[6],pad[2]; unsigned int link_mbps,pad2;
    long connected_seconds,lease_seconds,renew_seconds;
    unsigned long received,sent; unsigned int receive_rate,send_rate,events;
    char adapter[16],hostname[64],state_text[96];
    unsigned char saved_address[4],saved_mask[4],saved_gateway[4],saved_dns[4];
    unsigned int wifi,vpn; char adapter_model[160];
    int utc_offset; long utc_now;   /* the clock, so times show in local time: seconds east of UTC, UTC seconds now */
    unsigned char reserved[24];
};
_Static_assert(sizeof(struct zenith_network_status)==512,"network status");

/* Z_NET_HISTORY, value = range: 0 a minute of seconds, 1 an hour of minutes, 2 a day of quarter hours, 3 a week of hours. */
enum { Z_NET_MINUTE=0,Z_NET_HOUR=1,Z_NET_DAY=2,Z_NET_WEEK=3 };
struct zenith_network_sample { unsigned int received,sent; };   /* bytes per second, averaged */
struct zenith_network_history { unsigned int version,range,count,step_seconds; struct zenith_network_sample samples[168]; };

/* Z_NET_EVENTS: newest first. kind: 1 connected, 2 lease renewed, 3 could not connect, 4 cable out, 5 cable in,
 * 6 disconnected, 7 turned off, 8 settings changed, 9 previous settings kept, 10 slow DNS. */
struct zenith_network_event { long time; unsigned int kind; int value; char text[48]; };
struct zenith_network_events { unsigned int version,count,total,pad; struct zenith_network_event events[64]; };

/* Z_NET_CHECK, value = step 1 adapter, 2 address, 3 gateway, 4 DNS, 5 internet, 6 secure web. */
enum { Z_CHECK_OK=0,Z_CHECK_SLOW=1,Z_CHECK_FAILED=2,Z_CHECK_SKIPPED=3 };
struct zenith_network_check { unsigned int version,step,result,milliseconds; char subject[32]; char detail[128]; };

/* Z_NET_APPLY input (48 bytes): mode 0 automatic, 1 manual, 2 off. When the new settings do not connect, the previous
 * ones come back and the call answers -5 with the reason. */
struct zenith_network_settings { unsigned int version,mode; unsigned char address[4],mask[4],gateway[4],dns[4]; unsigned char reserved[24]; };
_Static_assert(sizeof(struct zenith_network_settings)==48,"network settings");

/* Z_NET_PROGRAMS: programs that used the network (only the caller's own unless root). */
struct zenith_network_program { int pid; unsigned int connections; long received,sent; char peer[40]; };
struct zenith_network_programs { unsigned int version,count,pad[2]; struct zenith_network_program programs[16]; };

static inline long zenith_network(unsigned int op,unsigned int value,void *output,unsigned int capacity,const void *input,unsigned int input_bytes){
    struct zenith_network_request r={0};
    r.version=1;r.op=op;r.value=value;r.output=(unsigned long)output;r.capacity=capacity;r.input=(unsigned long)input;r.input_bytes=input_bytes;
    return zenith_call(667,(unsigned long)&r,sizeof(r),0);
}
#define Z_ACTION_NETWORK "org.zenith.network.manage"
#endif
