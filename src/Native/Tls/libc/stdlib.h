#ifndef ZENITH_TLS_STDLIB_H
#define ZENITH_TLS_STDLIB_H
#include <stddef.h>
#define EXIT_FAILURE 1
#define EXIT_SUCCESS 0
void exit(int);
void *calloc(size_t, size_t);
void free(void *);
#endif
