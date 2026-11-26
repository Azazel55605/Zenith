/* Integration checks against the signed Samba fixture in smb-smoke.py. */
#include "../../../userland/smb/session.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>

enum { ITEMS = 600 };

struct listing {
    unsigned char seen[ITEMS];
    unsigned count;
};

static int collect_item(void *user, const char *name, int directory, uint64_t size)
{
    struct listing *list = user;
    assert(strlen(name) == 9 && strncmp(name, "item-", 5) == 0);
    unsigned index = 0;
    for (unsigned i = 5; i < 9; i++) {
        assert(name[i] >= '0' && name[i] <= '9');
        index = 10 * index + (unsigned)(name[i] - '0');
    }
    assert(index < ITEMS && !list->seen[index] && !directory && size == 0);
    list->seen[index] = 1;
    list->count++;
    return 0;
}

static int stop_listing(void *user, const char *name, int directory, uint64_t size)
{
    (void)name;
    (void)directory;
    (void)size;
    (*(unsigned *)user)++;
    return -1;
}

void check_directory_cursors(struct smb_session *s)
{
    struct smb_read_handle first, second, empty;
    assert(smb_session_open_directory(s, "cursor-many", &first) == 0);
    assert(smb_session_open_directory(s, "cursor-many", &second) == 0);
    assert(smb_session_open_directory(s, "cursor-empty", &empty) == 0);
    struct listing a = {0}, b = {0};
    unsigned rejected = 0;
    int result;
    do {
        result = smb_session_next_directory(s, empty, stop_listing, &rejected);
    } while (result == 1);
    assert(result == 0 && rejected == 0);
    assert(smb_session_next_directory(s, empty, stop_listing, &rejected) == 0);
    assert(smb_session_close_directory(s, empty) == 0);

    assert(smb_session_next_directory(s, first, collect_item, &a) == 1);
    assert(a.count > 0 && a.count < ITEMS);
    assert(smb_session_next_directory(s, second, collect_item, &b) == 1);
    assert(b.count > 0 && b.count < ITEMS);
    assert(smb_session_next_directory(s, second, stop_listing, &rejected) < 0 && rejected == 1);
    assert(smb_session_next_directory(s, second, stop_listing, &rejected) < 0 && rejected == 1);
    assert(smb_session_close_directory(s, second) == 0);

    unsigned char byte;
    assert(smb_session_read_at(s, first, 0, &byte, sizeof(byte)) < 0);
    assert(smb_session_close_read(s, first) < 0);
    do {
        result = smb_session_next_directory(s, first, collect_item, &a);
    } while (result == 1);
    assert(result == 0 && a.count == ITEMS);
    assert(smb_session_close_directory(s, first) == 0);
    assert(smb_session_next_directory(s, first, collect_item, &a) < 0);
    puts("PASS signed directory pagination, independent cursors, empty directory, cancellation and stale handles");
}
