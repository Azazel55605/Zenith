#ifndef ZENITH_TLS_ASSERT_H
#define ZENITH_TLS_ASSERT_H
#define assert(condition) ((condition) ? (void)0 : __builtin_trap())
#endif
