/* Zenith's serialized, fixed-memory Mbed TLS client bridge. Apache-2.0. */
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "mbedtls/ssl.h"
#include "mbedtls/aes.h"
#include "mbedtls/gcm.h"
#include "mbedtls/sha256.h"
#include "mbedtls/sha512.h"
#include "mbedtls/ctr_drbg.h"
#include "mbedtls/x509_crt.h"
#include "mbedtls/memory_buffer_alloc.h"
#include "mbedtls/platform_util.h"
#ifdef ZENITH_TLS_HOST_TEST
#include <sys/random.h>
#endif

#define QUEUE 32768
static unsigned char arena[1048576], incoming[QUEUE], outgoing[QUEUE];
static size_t in_count, out_count;
static mbedtls_ssl_context ssl;
static mbedtls_ssl_config config;
static mbedtls_x509_crt roots;
static mbedtls_ctr_drbg_context random_state;
static int active;
static mbedtls_x509_time now;
static const int suites[] = {
    MBEDTLS_TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
    MBEDTLS_TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
    MBEDTLS_TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
    MBEDTLS_TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384, 0
};

#ifndef ZENITH_TLS_HOST_TEST
extern int zenith_rdseed_word(uint64_t *value);
#endif
#ifdef ZENITH_TLS_HOST_TEST
static int entropy_fault;
#endif
static int entropy(void *unused, unsigned char *data, size_t length)
{
    (void)unused;
#ifdef ZENITH_TLS_HOST_TEST
    if (entropy_fault) return MBEDTLS_ERR_CTR_DRBG_ENTROPY_SOURCE_FAILED;
    return getrandom(data, length, 0) == (long)length ? 0 : MBEDTLS_ERR_CTR_DRBG_ENTROPY_SOURCE_FAILED;
#else
    while (length) {
        uint64_t value;
        int success = 0;
        for (int attempt = 0; attempt < 256 && !success; ++attempt) success = zenith_rdseed_word(&value);
        if (!success) return MBEDTLS_ERR_CTR_DRBG_ENTROPY_SOURCE_FAILED;
        size_t count = length < 8 ? length : 8;
        memcpy(data, &value, count);
        mbedtls_platform_zeroize(&value, sizeof(value));
        data += count; length -= count;
    }
    return 0;
#endif
}
static int send_bytes(void *unused, const unsigned char *data, size_t length)
{
    (void)unused;
    if (length > QUEUE - out_count) return MBEDTLS_ERR_SSL_WANT_WRITE;
    memcpy(outgoing + out_count, data, length); out_count += length; return (int)length;
}
static int receive_bytes(void *unused, unsigned char *data, size_t length)
{
    (void)unused;
    if (!in_count) return MBEDTLS_ERR_SSL_WANT_READ;
    if (length > in_count) length = in_count;
    memcpy(data, incoming, length); in_count -= length;
    memmove(incoming, incoming + length, in_count); return (int)length;
}
static int compare_time(const mbedtls_x509_time *a, const mbedtls_x509_time *b)
{
    const int left[] = { a->year, a->mon, a->day, a->hour, a->min, a->sec };
    const int right[] = { b->year, b->mon, b->day, b->hour, b->min, b->sec };
    for (int i = 0; i < 6; ++i) if (left[i] != right[i]) return left[i] < right[i] ? -1 : 1;
    return 0;
}
static int verify_time(void *unused, mbedtls_x509_crt *crt, int depth, uint32_t *flags)
{
    (void)unused; (void)depth;
    /* Mbed TLS has no libc clock in this port. Check every certificate's dates here;
       preserve all library flags, including chain, name, key usage and algorithm. */
    if (compare_time(&now, &crt->valid_from) < 0) *flags |= MBEDTLS_X509_BADCERT_FUTURE;
    if (compare_time(&now, &crt->valid_to) > 0) *flags |= MBEDTLS_X509_BADCERT_EXPIRED;
    return 0;
}
void zenith_tls_close(void)
{
    if (active != 1) return;
    mbedtls_ssl_free(&ssl); mbedtls_ssl_config_free(&config);
    mbedtls_x509_crt_free(&roots); mbedtls_ctr_drbg_free(&random_state);
    mbedtls_memory_buffer_alloc_free();
    mbedtls_platform_zeroize(arena, sizeof(arena));
    mbedtls_platform_zeroize(incoming, sizeof(incoming));
    mbedtls_platform_zeroize(outgoing, sizeof(outgoing));
    in_count = out_count = 0; active = 0;
}
int zenith_tls_open(const unsigned char *ca, size_t ca_length, const char *hostname,
                    int year, int month, int day, int hour, int minute, int second)
{
    if (!ca || ca_length < 1 || ca_length > 65536 || !hostname || year < 2020 || year > 2100 || !__sync_bool_compare_and_swap(&active, 0, 1)) return -1;
    mbedtls_memory_buffer_alloc_init(arena, sizeof(arena));
    mbedtls_ssl_init(&ssl); mbedtls_ssl_config_init(&config);
    mbedtls_x509_crt_init(&roots); mbedtls_ctr_drbg_init(&random_state);
    now = (mbedtls_x509_time){ year, month, day, hour, minute, second };
    int result = mbedtls_ctr_drbg_seed(&random_state, entropy, NULL, (const unsigned char *)"Zenith TLS", 10);
    if (!result) result = mbedtls_x509_crt_parse(&roots, ca, ca_length);
    /* Positive results mean a partly invalid trust bundle: reject it too. */
    if (!result) result = mbedtls_ssl_config_defaults(&config, MBEDTLS_SSL_IS_CLIENT, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT);
    if (!result) {
        mbedtls_ssl_conf_authmode(&config, MBEDTLS_SSL_VERIFY_REQUIRED);
        mbedtls_ssl_conf_ca_chain(&config, &roots, NULL);
        mbedtls_ssl_conf_verify(&config, verify_time, NULL);
        mbedtls_ssl_conf_rng(&config, mbedtls_ctr_drbg_random, &random_state);
        mbedtls_ssl_conf_ciphersuites(&config, suites);
        mbedtls_ssl_conf_min_tls_version(&config, MBEDTLS_SSL_VERSION_TLS1_2);
        mbedtls_ssl_conf_max_tls_version(&config, MBEDTLS_SSL_VERSION_TLS1_2);
        result = mbedtls_ssl_setup(&ssl, &config);
    }
    if (!result) result = mbedtls_ssl_set_hostname(&ssl, hostname);
    if (!result) mbedtls_ssl_set_bio(&ssl, NULL, send_bytes, receive_bytes, NULL);
    if (result) zenith_tls_close();
    return result;
}
int zenith_tls_handshake(void) { return active ? mbedtls_ssl_handshake(&ssl) : -1; }
uint32_t zenith_tls_verify(void) { return active ? mbedtls_ssl_get_verify_result(&ssl) : UINT32_MAX; }
int zenith_tls_read(unsigned char *data, size_t length) { return active ? mbedtls_ssl_read(&ssl, data, length) : -1; }
int zenith_tls_write(const unsigned char *data, size_t length) { return active ? mbedtls_ssl_write(&ssl, data, length) : -1; }
int zenith_tls_push(const unsigned char *data, size_t length)
{
    if (!active || length > QUEUE - in_count) return -1;
    memcpy(incoming + in_count, data, length); in_count += length; return (int)length;
}
int zenith_tls_pull(unsigned char *data, size_t length)
{
    if (!active) return -1;
    if (length > out_count) length = out_count;
    memcpy(data, outgoing, length); out_count -= length;
    memmove(outgoing, outgoing + length, out_count); return (int)length;
}

/* Text formatting convenience APIs are unused by this freestanding client. */
int zenith_tls_snprintf(char *out, size_t length, const char *format, ...)
{ (void)format; if (length) out[0] = 0; return -1; }

void mbedtls_platform_zeroize(void *buffer, size_t length)
{ volatile unsigned char *p = buffer; while (length--) *p++ = 0; }
void zenith_tls_exit(int status) { (void)status; __builtin_trap(); }

/* Private byte/string primitives; never enter the managed runtime from C. */
void *memcpy(void *dst, const void *src, size_t n)
{ unsigned char *d = dst; const unsigned char *s = src; while (n--) *d++ = *s++; return dst; }
void *memmove(void *dst, const void *src, size_t n)
{ unsigned char *d = dst; const unsigned char *s = src;
  if ((uintptr_t)d < (uintptr_t)s) return memcpy(dst, src, n);
  while (n) { --n; d[n] = s[n]; } return dst; }
void *memset(void *dst, int value, size_t n)
{ unsigned char *d = dst; while (n--) *d++ = (unsigned char)value; return dst; }
int memcmp(const void *a, const void *b, size_t n)
{ const unsigned char *x = a, *y = b; while (n--) { if (*x != *y) return *x - *y; ++x; ++y; } return 0; }
size_t strlen(const char *s) { size_t n = 0; while (s[n]) ++n; return n; }
int strcmp(const char *a, const char *b)
{ while (*a && *a == *b) { ++a; ++b; } return (unsigned char)*a - (unsigned char)*b; }
char *strchr(const char *s, int c)
{ do { if (*s == c) return (char *)s; } while (*s++); return NULL; }
char *strstr(const char *s, const char *needle)
{ size_t n = strlen(needle); if (!n) return (char *)s;
  for (; *s; ++s) { size_t i = 0; while (i < n && s[i] && s[i] == needle[i]) ++i; if (i == n) return (char *)s; } return NULL; }

int zenith_tls_printf(const char *format, ...) { (void)format; return 0; }
int zenith_tls_selftest(void)
{
    if (!__sync_bool_compare_and_swap(&active, 0, 3)) return -1;
    mbedtls_memory_buffer_alloc_init(arena, sizeof(arena));
    int result = mbedtls_aes_self_test(0) || mbedtls_gcm_self_test(0)
        || mbedtls_sha256_self_test(0) || mbedtls_sha512_self_test(0)
        || mbedtls_ctr_drbg_self_test(0);
    mbedtls_memory_buffer_alloc_free(); mbedtls_platform_zeroize(arena, sizeof(arena));
    active = 0; return result;
}
#ifdef ZENITH_TLS_HOST_TEST
void zenith_tls_test_entropy_fault(int fail) { entropy_fault = fail; }
#endif

/* Offline package verification: RSA-PSS SHA256, MGF1 SHA256, 32-byte salt.
   No private keys, system time or entropy required for public verification. */
int zenith_pkg_verify(const unsigned char *pem, size_t length,
                      const unsigned char *hash, const unsigned char *signature, size_t signature_length)
{
    if (!pem || !hash || (!signature && signature_length != 0) || length < 1 || length > 4097
        || !__sync_bool_compare_and_swap(&active, 0, 2)) return -1;
    mbedtls_memory_buffer_alloc_init(arena, sizeof(arena));
    mbedtls_pk_context key; mbedtls_pk_init(&key);
    int result = mbedtls_pk_parse_public_key(&key, pem, length);
    if (!result && (mbedtls_pk_get_type(&key) != MBEDTLS_PK_RSA
        || mbedtls_pk_get_bitlen(&key) < 2048 || mbedtls_pk_get_bitlen(&key) > 4096
        || (signature_length != 0 && signature_length != mbedtls_pk_get_len(&key)))) result = -1;
    mbedtls_pk_rsassa_pss_options options = { MBEDTLS_MD_SHA256, 32 };
    if (!result && signature_length != 0) result = mbedtls_pk_verify_ext(MBEDTLS_PK_RSASSA_PSS, &options, &key,
        MBEDTLS_MD_SHA256, hash, 32, signature, signature_length);
    mbedtls_pk_free(&key); mbedtls_memory_buffer_alloc_free();
    mbedtls_platform_zeroize(arena, sizeof(arena)); active = 0;
    return result;
}
