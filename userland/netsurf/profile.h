#ifndef NEBULA_PROFILE_H
#define NEBULA_PROFILE_H

#include <stdbool.h>

#define NEBULA_URL_BYTES 2048
#define NEBULA_BOOKMARKS 24
#define NEBULA_RECENT 24

struct nebula_entry {
    char url[NEBULA_URL_BYTES];
    char title[128];
};

struct nebula_profile {
    unsigned bookmark_count, recent_count;
    struct nebula_entry bookmarks[NEBULA_BOOKMARKS], recent[NEBULA_RECENT];
};

bool nebula_profile_accepts(const char *url);
bool nebula_profile_load(struct nebula_profile *p);
bool nebula_profile_bookmarked(const struct nebula_profile *p, const char *url);
bool nebula_profile_set_bookmark(struct nebula_profile *p, const char *url, const char *title, bool enabled);
bool nebula_profile_visit(struct nebula_profile *p, const char *url, const char *title);

#endif
