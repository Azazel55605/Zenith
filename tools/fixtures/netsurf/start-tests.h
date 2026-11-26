/* On the real NetSurf engine verify private navigation, fresh profile data and escaped clickable URLs. */
#include "content/fetch.h"

static void no_profile_data(const fetch_msg *message, void *unused)
{
    (void)message; (void)unused;
    assert(!"Untrusted start-page request reached the profile");
}

static char *start_html(void)
{
    size_t bytes;
    const uint8_t *data = content_get_source_data(browser_window_get_content(window.browser), &bytes);
    assert(data && bytes);
    char *html = strndup((const char *)data, bytes);
    assert(html);
    return html;
}

static void check_start(void)
{
    assert(nebula_start_register(&W.profile, &W.ui.theme) == NSERROR_OK);
    nsurl *home, *remote;
    struct fetch *handle = NULL;
    assert(nsurl_create(NEBULA_START_URL, &home) == NSERROR_OK);
    assert(nsurl_create("https://outside.invalid/", &remote) == NSERROR_OK);
    assert(fetch_start(home, remote, no_profile_data, NULL, false, NULL, NULL,
                       false, false, NULL, &handle) == NSERROR_BAD_URL);
    /* A suppressed Referer alone does not prove a top-level navigation. */
    assert(fetch_start(home, NULL, no_profile_data, NULL, false, NULL, NULL,
                       false, false, NULL, &handle) == NSERROR_BAD_URL);
    assert(fetch_start(home, remote, no_profile_data, NULL, false, NULL, NULL,
                       true, false, NULL, &handle) == NSERROR_BAD_URL);
    nsurl_unref(home);
    nsurl_unref(remote);

    struct nebula_profile *p = &W.profile;
    memset(p, 0, sizeof(*p));
    p->bookmark_count = 2;
    p->recent_count = 1;
    strcpy(p->bookmarks[0].url, "https://saved.invalid/?p=1&q=\"'\"");
    strcpy(p->bookmarks[0].title, "Zoë & <one> \"quoted\" '");
    strcpy(p->bookmarks[1].url, "javascript:alert(1)");
    strcpy(p->recent[0].url, "file:///home/test/reading.html");
    enter_address(NEBULA_START_URL);
    await_document("Welcome to Nebula");
    char *body = start_html();
    assert(strstr(body, "href=\"https://saved.invalid/?p=1&amp;q=&quot;&#39;&quot;\""));
    assert(strstr(body, "Zoë &amp; &lt;one&gt; &quot;quoted&quot; &#39;"));
    assert(strstr(body, ">file:///home/test/reading.html</a>"));
    assert(!strstr(body, "javascript:"));
    assert(strstr(body, "<style>") && !strstr(body, "<!--NEBULA-"));
    free(body);

    /* Reuse the same core and URL after changing the current profile; no previous
     * account's data may survive in a fetcher or in NetSurf's content cache. */
    memset(p, 0, sizeof(*p));
    p->recent_count = 1;
    strcpy(p->recent[0].url, "https://second.invalid/news");
    enter_address("data:text/html,%3Ctitle%3EBetween%20profiles%3C/title%3E");
    await_document("Between profiles");
    enter_address(NEBULA_START_URL);
    await_document("Welcome to Nebula");
    body = start_html();
    assert(strstr(body, "second.invalid/news") && !strstr(body, "saved.invalid"));
    assert(strstr(body, "Use the star"));
    free(body);
    puts("PASS themed internal start page, profile isolation, escaped bookmarks and recents and blocked embeds");
}
