/* The frontend fixtures dispatch input without calling the guest filesystem.
 * Storage and permissions are covered by the profile tests and the installed guest. */
bool nebula_profile_accepts(const char *url)
{
    return url && (!strncmp(url, "https://", 8) || !strncmp(url, "http://", 7) || !strncmp(url, "file:///", 8));
}
bool nebula_profile_bookmarked(const struct nebula_profile *p, const char *url)
{
    for (unsigned i = 0; i < p->bookmark_count; ++i)
        if (!strcmp(p->bookmarks[i].url, url)) return true;
    return false;
}
bool nebula_profile_set_bookmark(struct nebula_profile *p, const char *url, const char *title, bool enabled)
{
    assert(enabled && !p->bookmark_count);
    snprintf(p->bookmarks[0].url, sizeof(p->bookmarks[0].url), "%s", url);
    snprintf(p->bookmarks[0].title, sizeof(p->bookmarks[0].title), "%s", title);
    p->bookmark_count = 1;
    return true;
}
bool nebula_profile_visit(struct nebula_profile *p, const char *url, const char *title)
{
    (void)p; (void)url; (void)title;
    return true;
}
