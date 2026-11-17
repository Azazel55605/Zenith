#ifndef AURORA_REGEX_H
#define AURORA_REGEX_H
/* Aurora's find with regular expressions (AU2): a compiled backtracking matcher, freestanding and bounded.
 * Syntax: literals, ".", [abc] [a-z] [^...], \d \w \s \D \W \S \b \t \n and escaped punctuation, ^ and $ (line
 * start/end), groups ( ), alternation |, and the quantifiers * + ? {n} {n,} {n,m}, greedy, with a lazy "?" suffix.
 * Matching never crosses a line end, and each attempt has a step budget so no pattern can hang the editor. */
#include "buffer.h"

#define RX_NODES 256
#define RX_GROUPS 10
struct rx_node { unsigned char type,negate,group,lazy; unsigned short min,max,next,alt,child,set; };
struct rx { struct rx_node nodes[RX_NODES]; unsigned char sets[32][32]; int count,sets_used,start,groups,flags; const char *error; };
enum { RX_ICASE=1 };
/* 0, or -1 with rx->error set. */
int rx_compile(struct rx *rx,const char *pattern,unsigned int bytes,int flags);
/* The first match starting in [from, limit) of the text (limit = the text's length for "to the end"); returns
 * the start or -1; *end gets the end, groups[2*i..2*i+1] the group spans (-1 when unset). */
long rx_search(const struct rx *rx,const struct au_text *t,unsigned int from,unsigned int limit,unsigned int *end,long *groups);
/* The last match starting before `before`, for find-previous. */
long rx_search_back(const struct rx *rx,const struct au_text *t,unsigned int before,unsigned int *end,long *groups);
/* Expands $0..$9 and $$ in a replacement into out; returns the bytes written (0 on overflow). */
unsigned int rx_expand(const struct au_text *t,const char *replacement,unsigned int bytes,const long *groups,char *out,unsigned int capacity);
#endif
