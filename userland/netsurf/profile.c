/* Private, user-owned Nebula bookmarks and recent documents. */
#include "profile.h"
#include "../sunbeam/zenith.h"
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef NEBULA_PROFILE_TEST
long profile_test_call(long, unsigned long, unsigned long, unsigned long);
#define zenith_call profile_test_call
#endif

enum { path_cap = 1025, passwd_cap = 65536 };
#define PROFILE_CAP ((NEBULA_BOOKMARKS + NEBULA_RECENT) * (NEBULA_URL_BYTES + 132) + 32)
static const char signature[] = "NEBULA-PROFILE-1\n";
static const char store_name[] = ".nebula";

bool nebula_profile_accepts(const char *url)
{
    if (!url || !*url || strnlen(url, NEBULA_URL_BYTES) == NEBULA_URL_BYTES) return false;
    for (const unsigned char *p = (const unsigned char *)url; *p; p++)
        if (*p <= 32 || *p == 127 || *p == '\\') return false;
    if (!strncmp(url, "file:///", 8)) return true;
    const char *host = NULL;
    if (!strncmp(url, "https://", 8)) host = url + 8;
    if (!strncmp(url, "http://", 7)) host = url + 7;
    if (!host) return false;
    size_t authority = strcspn(host, "/?#");
    if (!authority || memchr(host, '@', authority) || memchr(host, '%', authority)) return false;
    const char *end = host + authority, *port;
    if (*host == '[') {
        const char *bracket = memchr(host, ']', authority);
        if (!bracket || bracket <= host+1) return false;
        if (bracket+1 == end) return true;
        if (bracket[1] != ':') return false;
        port = bracket+2;
    } else {
        const char *colon = memchr(host, ':', authority);
        if (!colon) return true;
        if (colon == host) return false;
        port = colon+1;
    }
    if (port == end || end-port > 5) return false;
    unsigned value = 0;
    while (port != end) {
        if (*port < '0' || *port > '9') return false;
        value = value*10 + (unsigned)(*port++-'0');
    }
    return value > 0 && value <= 65535;
}

static long path_op(unsigned op, const char *path, const char *target, unsigned mode)
{
    struct zenith_path_request req = {op, 0, (uintptr_t)path, strlen(path), (uintptr_t)target,
        target ? strlen(target) : 0, mode, 0};
    return zenith_call(405, (uintptr_t)&req, sizeof(req), 0);
}

static long file_open(const char *path, unsigned flags)
{
    return zenith_call(2, (uintptr_t)path, strlen(path), flags);
}

static long info(const char *path, struct zenith_stat *s)
{
    return zenith_call(408, (uintptr_t)path, strlen(path), (uintptr_t)s);
}

static bool join(char out[path_cap], const char *dir, const char *name)
{
    int n = snprintf(out, path_cap, "%s/%s", dir, name);
    return n > 0 && n < path_cap;
}

static long read_file(const char *path, char *buf, size_t cap)
{
    long fd = file_open(path, Z_OPEN_READ);
    if (fd < 0) return fd;
    size_t at = 0;
    long n = 0;
    while (at < cap) {
        size_t block = cap-at > 4096 ? 4096 : cap-at;
        n = zenith_call(0, fd, (uintptr_t)(buf+at), block);
        if (n <= 0 || (size_t)n > block) break;
        at += n;
    }
    long close = zenith_call(3, fd, 0, 0);
    if (close < 0 || n < 0 || at == cap) return -EIO;
    buf[at] = '\0';
    return memchr(buf, '\0', at) ? -EINVAL : (long)at;
}

static bool home_for_uid(char path[path_cap], unsigned uid)
{
    struct zenith_stat s;
    if (info("/", &s) < 0 || s.uid != 0 || (s.mode & 0170022) != 0040000 ||
        info("/etc", &s) < 0 || s.uid != 0 || (s.mode & 0170022) != 0040000 ||
        info("/etc/passwd", &s) < 0 || s.uid != 0 || (s.mode & 0170022) != 0100000)
        return false;
    char *db = malloc(passwd_cap + 1);
    if (!db) return false;
    bool found = false;
    if (read_file("/etc/passwd", db, passwd_cap) >= 0) {
        char id[24];
        snprintf(id, sizeof(id), "%u", uid);
        char *lines = db, *line;
        while ((line = strsep(&lines, "\n"))) {
            char *fields[7];
            for (unsigned i = 0; i < 7; i++) fields[i] = strsep(&line, ":");
            if (fields[6] && !strcmp(fields[2], id) && fields[5][0] == '/' &&
                fields[5][1] && strlen(fields[5]) < path_cap) {
                strcpy(path, fields[5]);
                found = true;
                break;
            }
        }
    }
    free(db);
    if (!found) return false;
    /* Check each ancestor without following symlinks. A caller-controlled HOME or
     * cwd must never select another user's profile, especially for the root session. */
    for (char *start = path + 1; ; ) {
        char *end = strchr(start, '/');
        if (end) *end = '\0';
        if (!*start || !strcmp(start, ".") || !strcmp(start, "..") || info(path, &s) < 0 ||
            (s.mode & 0170022) != 0040000 || (s.uid != uid && (!end || s.uid != 0)))
            return false;
        if (!end) return true;
        *end = '/';
        start = end + 1;
    }
}

static bool private_node(const struct zenith_stat *s, unsigned uid, bool directory)
{
    return s->uid == uid && (s->mode & 0177777) == (directory ? 0040700 : 0100600);
}

static long store(char path[path_cap], unsigned uid, bool create)
{
    char home[path_cap];
    if (!home_for_uid(home, uid) || !join(path, home, store_name)) return -EACCES;
    struct zenith_stat s;
    long rc = info(path, &s);
    if (rc == -ENOENT && create) {
        rc = path_op(1, path, NULL, 0700);
        if (rc < 0 && rc != -EEXIST) return rc;
        rc = info(path, &s);
    }
    return rc < 0 ? rc : private_node(&s, uid, true) ? 0 : -EACCES;
}

/* An immutable revision is a non-empty directory. Rename publishes it atomically.
 * Two writers claiming the same revision cannot replace each other's directory.
 * Readers ignore incomplete staging directories and can retry a retired revision. */
static unsigned revision_number(const char *name)
{
    if (strlen(name) != 11 || *name++ != 'r') return 0;
    unsigned long long n = 0;
    for (; *name; name++) {
        if (*name < '0' || *name > '9') return 0;
        n = 10 * n + (unsigned)(*name-'0');
    }
    return n < UINT32_MAX ? (unsigned)n : 0;
}

static bool latest(const char *dir, unsigned *revision)
{
    long fd = file_open(dir, Z_OPEN_DIRECTORY);
    if (fd < 0) return false;
    struct zenith_dirent e;
    long rc;
    *revision = 0;
    while ((rc = zenith_call(217, fd, (uintptr_t)&e, sizeof(e))) == sizeof(e)) {
        e.name[sizeof(e.name)-1] = 0;
        unsigned n = revision_number(e.name);
        if (n > *revision) *revision = n;
    }
    return zenith_call(3, fd, 0, 0) == 0 && rc == 0;
}

static bool revision_path(char out[path_cap], const char *dir, unsigned revision)
{
    char name[16];
    snprintf(name, sizeof(name), "r%010u", revision);
    return join(out, dir, name);
}

static int lookup(const struct nebula_entry *entries, unsigned count, const char *url)
{
    for (unsigned i = 0; i < count; i++) if (!strcmp(entries[i].url, url)) return (int)i;
    return -1;
}

bool nebula_profile_bookmarked(const struct nebula_profile *p, const char *url)
{
    return p && url && lookup(p->bookmarks, p->bookmark_count, url) >= 0;
}

static bool parse(struct nebula_profile *p, char *text)
{
    if (strncmp(text, signature, sizeof(signature)-1)) return false;
    text += sizeof(signature)-1;
    while (*text) {
        char *eol = strchr(text, '\n');
        if (!eol || (text[0] != 'b' && text[0] != 'h') || text[1] != '\t') return false;
        *eol = 0;
        bool book = *text == 'b';
        char *url = text+2, *title = strchr(url, '\t');
        if (!title) return false;
        *title++ = 0;
        if (!nebula_profile_accepts(url) || strlen(title) >= sizeof(p->bookmarks[0].title)) return false;
        for (const unsigned char *s = (const unsigned char *)title; *s; s++)
            if (*s < 32 || *s == 127) return false;
        unsigned *count = book ? &p->bookmark_count : &p->recent_count;
        struct nebula_entry *entries = book ? p->bookmarks : p->recent;
        if (*count >= (book ? NEBULA_BOOKMARKS : NEBULA_RECENT) || lookup(entries, *count, url) >= 0) return false;
        strcpy(entries[*count].url, url);
        strcpy(entries[(*count)++].title, title);
        text = eol + 1;
    }
    return true;
}

static bool read_current(struct nebula_profile *p, const char *dir, unsigned uid, unsigned *revision)
{
    char folder[path_cap], file[path_cap];
    struct zenith_stat s;
    for (unsigned attempt = 0; attempt < 4; attempt++) {
        p->bookmark_count = p->recent_count = 0;
        if (!latest(dir, revision)) return false;
        if (!*revision) return true;
        if (!revision_path(folder, dir, *revision) || !join(file, folder, "data")) return false;
        long rc = info(folder, &s);
        if (rc == -ENOENT) continue;
        if (rc < 0 || !private_node(&s, uid, true)) return false;
        rc = info(file, &s);
        if (rc == -ENOENT) continue;
        if (rc < 0 || !private_node(&s, uid, false) || s.size >= PROFILE_CAP) return false;
        char *buf = malloc(s.size+1);
        if (!buf) return false;
        rc = read_file(file, buf, s.size+1);
        bool valid = rc == (long)s.size && parse(p, buf);
        free(buf);
        if (rc != -ENOENT) return valid;
    }
    return false;
}

bool nebula_profile_load(struct nebula_profile *p)
{
    if (!p) return false;
    unsigned uid = (unsigned)zenith_call(102,0,0,0), revision;
    char dir[path_cap];
    long rc = store(dir, uid, false);
    struct nebula_profile *snapshot = calloc(1, sizeof(*snapshot));
    if (!snapshot) return false;
    bool ok = rc == -ENOENT || (rc == 0 && read_current(snapshot, dir, uid, &revision));
    if (ok) *p = *snapshot;
    free(snapshot);
    return ok;
}

static bool write_snapshot(long fd, const struct nebula_profile *p)
{
    if (zenith_call(1,fd,(uintptr_t)signature,sizeof(signature)-1) != sizeof(signature)-1) return false;
    for (unsigned list = 0; list < 2; list++) {
        const struct nebula_entry *entries = list ? p->recent : p->bookmarks;
        unsigned count = list ? p->recent_count : p->bookmark_count;
        for (unsigned i = 0; i < count; i++) {
            char line[NEBULA_URL_BYTES+132];
            int n = snprintf(line, sizeof(line), "%c\t%s\t%s\n", list ? 'h' : 'b', entries[i].url, entries[i].title);
            if (n < 0 || (size_t)n >= sizeof(line) || zenith_call(1,fd,(uintptr_t)line,n) != n) return false;
        }
    }
    return zenith_call(74,fd,0,0) == 0;
}

static void retire(const char *folder)
{
    char file[path_cap];
    if (join(file, folder, "data")) {
        path_op(2,file,NULL,0);
        path_op(3,folder,NULL,0);
    }
}

/* Discard interrupted staging and superseded revisions only after a newer
 * snapshot is durable. A writer still using such a revision must rebase anyway. */
static void prune(const char *dir, unsigned uid, unsigned committed)
{
    if (committed < 2) return;
    long fd = file_open(dir, Z_OPEN_DIRECTORY);
    if (fd < 0) return;
    struct zenith_dirent entry;
    while (zenith_call(217, fd, (uintptr_t)&entry, sizeof(entry)) == sizeof(entry)) {
        entry.name[sizeof(entry.name)-1] = 0;
        bool expired = false;
        unsigned n = revision_number(entry.name);
        if (n) expired = n < committed-1;
        else if (entry.name[0] == 'w' && strlen(entry.name) > 12 && entry.name[11] == '-') {
            char base[12];
            memcpy(base, entry.name, 11);
            base[0] = 'r';
            base[11] = 0;
            n = revision_number(base);
            const char *pid = entry.name+12;
            expired = (n || !memcmp(base, "r0000000000", 11)) && n < committed-1 &&
                strlen(pid) <= 10 && strspn(pid, "0123456789") == strlen(pid);
        }
        char path[path_cap];
        struct zenith_stat stat;
        if (expired && join(path, dir, entry.name) && info(path, &stat) == 0 && private_node(&stat, uid, true))
            retire(path);
    }
    zenith_call(3,fd,0,0);
}

static bool edit(struct nebula_profile *p, bool book, const char *url, const char *title, bool keep)
{
    unsigned *count = book ? &p->bookmark_count : &p->recent_count;
    struct nebula_entry *entries = book ? p->bookmarks : p->recent;
    unsigned max = book ? NEBULA_BOOKMARKS : NEBULA_RECENT;
    int i = lookup(entries, *count, url);
    if (i >= 0) {
        --*count;
        memmove(entries+i, entries+i+1, (*count-(unsigned)i)*sizeof(*entries));
    }
    if (!keep) return true;
    if (*count == max && book) return false;
    if (*count == max) --*count;
    memmove(entries+1,entries,(*count)++ *sizeof(*entries));
    strcpy(entries[0].url,url);
    unsigned char *to = (unsigned char *)entries[0].title;
    const unsigned char *s = (const unsigned char *)(title ? title : "");
    size_t n = strnlen((const char *)s, sizeof(entries[0].title)-1);
    while (n && (s[n] & 0xc0) == 0x80) n--;
    for (size_t j = 0; j < n; j++) to[j] = s[j] < 32 || s[j] == 127 ? ' ' : s[j];
    to[n] = 0;
    return true;
}

static bool update(struct nebula_profile *p, bool book, const char *url, const char *title, bool keep)
{
    if (!p || !nebula_profile_accepts(url)) return false;
    unsigned uid = zenith_call(102,0,0,0);
    char dir[path_cap], staging[path_cap], data[path_cap], dest[path_cap], name[40];
    unsigned long pid = zenith_call(39,0,0,0);
    if (store(dir,uid,true) < 0) return false;
    struct nebula_profile *snapshot = calloc(1,sizeof(*snapshot));
    if (!snapshot) return false;
    bool ok = false;
    for (unsigned attempt = 0; attempt < 8; attempt++) {
        unsigned was, now;
        if (!read_current(snapshot,dir,uid,&was) || !edit(snapshot,book,url,title,keep) ||
            was >= UINT32_MAX-1 || !revision_path(dest,dir,was+1)) break;
        snprintf(name,sizeof(name),"w%010u-%lu",was,pid);
        if (!join(staging,dir,name) || !join(data,staging,"data")) break;
        long rc = path_op(1,staging,NULL,0700);
        if (rc == -EEXIST) {
            /* Unique PID per boot. A crash on a previous boot may leave this PID's staging dir. */
            struct zenith_stat s;
            if (info(staging,&s) < 0 || !private_node(&s,uid,true)) break;
            retire(staging);
            rc = path_op(1,staging,NULL,0700);
        }
        if (rc < 0) break;
        long fd = file_open(data,Z_OPEN_WRITE|Z_OPEN_CREATE|Z_OPEN_TRUNCATE);
        bool wrote = fd >= 0 && path_op(5,data,NULL,0600) == 0 && write_snapshot(fd,snapshot);
        if (fd >= 0 && zenith_call(3,fd,0,0) != 0) wrote = false;
        if (!wrote) {
            retire(staging);
            if (latest(dir,&now) && now > was) continue;
            break;
        }
        rc = path_op(4,staging,dest,0);
        if (rc < 0) retire(staging);
        if (!latest(dir,&now)) break;
        if (rc < 0 && now <= was) break; /* I/O failure; the previous snapshot is intact. */
        if (rc < 0 || now != was+1) continue; /* Other writers advanced; merge against durable state. */
        prune(dir,uid,now);
        *p = *snapshot;
        ok = true;
        break;
    }
    free(snapshot);
    return ok;
}

bool nebula_profile_set_bookmark(struct nebula_profile *p, const char *url, const char *title, bool enabled)
{
    return update(p,true,url,title,enabled);
}

bool nebula_profile_visit(struct nebula_profile *p, const char *url, const char *title)
{
    return update(p,false,url,title,true);
}
