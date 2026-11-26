#ifndef ZENITH_SMB_CRYPTO_H
#define ZENITH_SMB_CRYPTO_H
#include <stdint.h>
#include <stddef.h>
void smb_wipe(void *p, size_t n);
void smb_md4(const void *p, size_t n, uint8_t out[16]);
void smb_hmac_md5(const uint8_t key[16], const void *a, size_t an, const void *b, size_t bn, const void *c,
                  size_t cn, uint8_t out[16]);
void smb_hmac_sha256(const uint8_t key[16], const void *data, size_t n, uint8_t out[32]);
void smb_cmac(const uint8_t key[16], const uint8_t *p, size_t n, uint8_t out[16]);
#endif
