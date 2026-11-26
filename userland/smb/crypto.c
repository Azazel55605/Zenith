#include "crypto.h"
#include "mbedtls/aes.h"
#include "mbedtls/md4.h"
#include "mbedtls/md5.h"
#include "mbedtls/sha256.h"

void smb_wipe(void *p, size_t n)
{
    volatile uint8_t *bytes = p;
    while (n--)
        *bytes++ = 0;
}

void mbedtls_platform_zeroize(void *p, size_t n)
{
    smb_wipe(p, n);
}

void smb_md4(const void *p, size_t n, uint8_t out[16])
{
    mbedtls_md4_ret(p, n, out);
}

static void pad(const uint8_t key[16], unsigned char block[64], unsigned char value)
{
    for (unsigned i = 0; i < 64; ++i)
        block[i] = (i < 16 ? key[i] : 0) ^ value;
}

void smb_hmac_md5(const uint8_t key[16], const void *a, size_t an, const void *b, size_t bn, const void *c,
                  size_t cn, uint8_t out[16])
{
    uint8_t block[64], inner[16];
    mbedtls_md5_context ctx;
    mbedtls_md5_init(&ctx);
    pad(key, block, 0x36);
    mbedtls_md5_starts(&ctx);
    mbedtls_md5_update(&ctx, block, sizeof(block));
    if (an)
        mbedtls_md5_update(&ctx, a, an);
    if (bn)
        mbedtls_md5_update(&ctx, b, bn);
    if (cn)
        mbedtls_md5_update(&ctx, c, cn);
    mbedtls_md5_finish(&ctx, inner);
    pad(key, block, 0x5c);
    mbedtls_md5_starts(&ctx);
    mbedtls_md5_update(&ctx, block, sizeof(block));
    mbedtls_md5_update(&ctx, inner, sizeof(inner));
    mbedtls_md5_finish(&ctx, out);
    mbedtls_md5_free(&ctx);
    smb_wipe(block, sizeof(block));
    smb_wipe(inner, sizeof(inner));
}

void smb_hmac_sha256(const uint8_t key[16], const void *p, size_t n, uint8_t out[32])
{
    uint8_t block[64], inner[32];
    mbedtls_sha256_context ctx;
    mbedtls_sha256_init(&ctx);
    pad(key, block, 0x36);
    mbedtls_sha256_starts(&ctx, 0);
    mbedtls_sha256_update(&ctx, block, sizeof(block));
    mbedtls_sha256_update(&ctx, p, n);
    mbedtls_sha256_finish(&ctx, inner);
    pad(key, block, 0x5c);
    mbedtls_sha256_starts(&ctx, 0);
    mbedtls_sha256_update(&ctx, block, sizeof(block));
    mbedtls_sha256_update(&ctx, inner, sizeof(inner));
    mbedtls_sha256_finish(&ctx, out);
    mbedtls_sha256_free(&ctx);
    smb_wipe(block, sizeof(block));
    smb_wipe(inner, sizeof(inner));
}

static void double_block(uint8_t b[16])
{
    unsigned carry = 0;
    for (int i = 15; i >= 0; --i) {
        unsigned next = b[i] >> 7;
        b[i] = (uint8_t)((b[i] << 1) | carry);
        carry = next;
    }
    b[15] ^= (uint8_t)(0x87u & (0u - carry));
}

void smb_cmac(const uint8_t key[16], const uint8_t *p, size_t n, uint8_t out[16])
{
    mbedtls_aes_context ctx;
    uint8_t x[16] = {0}, subkey[16] = {0};
    mbedtls_aes_init(&ctx);
    mbedtls_aes_setkey_enc(&ctx, key, 128);
    mbedtls_aes_crypt_ecb(&ctx, MBEDTLS_AES_ENCRYPT, subkey, subkey);
    double_block(subkey);
    while (n > 16) {
        for (unsigned i = 0; i < 16; ++i)
            x[i] ^= p[i];
        mbedtls_aes_crypt_ecb(&ctx, MBEDTLS_AES_ENCRYPT, x, x);
        p += 16;
        n -= 16;
    }
    if (n < 16) {
        double_block(subkey);
        x[n] ^= 0x80;
    }
    for (unsigned i = 0; i < n; ++i)
        x[i] ^= p[i];
    for (unsigned i = 0; i < 16; ++i)
        x[i] ^= subkey[i];
    mbedtls_aes_crypt_ecb(&ctx, MBEDTLS_AES_ENCRYPT, x, out);
    mbedtls_aes_free(&ctx);
    smb_wipe(x, sizeof(x));
    smb_wipe(subkey, sizeof(subkey));
}
