/* Optional offline check against MDN's original mobile-first example. The
 * regular gate uses local deterministic fixtures and requires no network. */
static struct box *published_element(struct box *box, const char *tag, const char *class)
{
    for (; box; box = box->next) {
        dom_string *name = NULL;
        if (box->node && dom_node_get_node_name(box->node, &name) == DOM_NO_ERR && name) {
            bool matches = strlen(tag) == dom_string_byte_length(name) &&
                           !strncasecmp(tag, dom_string_data(name), strlen(tag));
            dom_string_unref(name);
            if (matches && class) {
                lwc_string *klass;
                assert(lwc_intern_string(class, strlen(class), &klass) == lwc_error_ok);
                assert(dom_element_has_class(box->node, klass, &matches) == DOM_NO_ERR);
                lwc_string_unref(klass);
            }
            if (matches) return box;
        }
        struct box *found = published_element(box->children, tag, class);
        if (found) return found;
    }
    return NULL;
}

static void check_published_layout(const char *path)
{
    int dpi = browser_get_dpi(), original_width = W.ui.width;
    bool sidebar = W.sidebar_hidden;
    assert(browser_set_dpi(96) == NSERROR_OK);
    W.sidebar_hidden = true;
    enter_address(path);
    await_document(" Media Queries: a simple mobile first design, adding a grid component ");
    struct hlcache_handle *page = browser_window_get_content(window.browser);
    dom_document *original = html_get_document(page);
    const int widths[] = {400, 920, 1440, 400};
    const unsigned cards_per_row[] = {1, 2, 3, 1};
    for (size_t i = 0; i < sizeof(widths) / sizeof(widths[0]); ++i) {
        W.ui.width += widths[i] - page_rect().w;
        relayout_all();
        run_alarms();
        struct zui_rect view = page_rect();
        assert(view.w == widths[i] && render_page(&window, view));
        assert(browser_window_get_content(window.browser) == page && html_get_document(page) == original);
        struct box *root = html_get_box_tree(page);
        struct box *cards = published_element(root, "ul", "grid");
        struct box *article = published_element(root, "article", NULL);
        struct box *main = published_element(root, "main", NULL);
        assert(cards && article && main);
        assert(css_computed_display_static(cards->style) == CSS_DISPLAY_GRID);
        assert((css_computed_display_static(article->style) == CSS_DISPLAY_GRID) == (widths[i] >= 640));
        assert((css_computed_display_static(main->style) == CSS_DISPLAY_GRID) == (widths[i] >= 1120));
        unsigned first_row = 0, count = 0;
        int start_y = 0, previous_right = 0;
        for (struct box *card = cards->children; card; card = card->next) {
            if (card->type == BOX_INLINE_CONTAINER) continue;
            int x, y;
            box_coords(card, &x, &y);
            if (!count) start_y = y;
            assert(card->width > 0 && card->height > 0);
            if (y == start_y) {
                if (first_row) assert(x >= previous_right + 20);
                first_row++;
                previous_right = x + card->width;
            } else assert(y > start_y);
            count++;
        }
        fprintf(stderr, "MDN example at %d px: %u/%u cards on first row\n", view.w, first_row, count);
        assert(count == 5 && first_row == cards_per_row[i]);
    }
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    W.sidebar_hidden = sidebar;
    W.ui.width = original_width;
    relayout_all();
    puts("PASS MDN mobile-first example changes its article, sidebar and card layout at three viewport widths");
}

/* Exercise the published Simple.css sheet with ordinary article markup. */
static void check_published_stylesheet(const char *path)
{
    char document[] = "/tmp/nebula-simple-XXXXXX.html";
    int fd = mkstemps(document, 5);
    assert(fd >= 0);
    FILE *file = fdopen(fd, "w"), *css = fopen(path, "rb");
    assert(file && css);
    fputs("<!doctype html><meta charset=utf-8><title>Simple.css article</title><style>", file);
    char chunk[8192];
    size_t size;
    while ((size = fread(chunk, 1, sizeof(chunk), css)) > 0) assert(fwrite(chunk, 1, size, file) == size);
    assert(!ferror(css) && fclose(css) == 0);
    fputs("</style><header><h1>Journal</h1></header><main><p>A short article.</p></main>", file);
    assert(fclose(file) == 0);
    int dpi = browser_get_dpi(), original_width = W.ui.width;
    bool hidden = W.sidebar_hidden;
    int original_font = nsoption_int(font_size);
    nsoption_set_int(font_size, 120); /* 12pt at 96 dpi gives a 16 CSS pixel root. */
    assert(browser_set_dpi(96) == NSERROR_OK);
    W.sidebar_hidden = true;
    enter_address(document);
    await_document("Simple.css article");
    assert(unlink(document) == 0);
    const int widths[] = {400, 1200, 400};
    for (size_t i = 0; i < sizeof(widths) / sizeof(widths[0]); i++) {
        W.ui.width += widths[i] - page_rect().w;
        relayout_all();
        run_alarms();
        assert(render_page(&window, page_rect()));
        struct box *root = html_get_box_tree(browser_window_get_content(window.browser));
        struct box *header = published_element(root, "header", NULL);
        struct box *main = published_element(root, "main", NULL);
        assert(header && main);
        int x, y;
        box_coords(main, &x, &y);
        int expected = widths[i] < 800 ? widths[i] * 9 / 10 : 720;
        fprintf(stderr, "Simple.css at %d px: article x=%d width=%d, expected %d\n",
                widths[i], x, main->width, expected);
        assert(x == (widths[i] - expected) / 2 && main->width == expected);
        /* Its header spans every track, and its background comes from :root. */
        uint32_t pixel = W.page[2 * W.page_w + 2];
        assert(pixel == 0xfff5f7ff || pixel == 0xff2b2b2b);
        assert(W.page[2 * W.page_w + W.page_w - 3] == pixel);
    }
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    nsoption_set_int(font_size, original_font);
    W.sidebar_hidden = hidden;
    W.ui.width = original_width;
    relayout_all();
    puts("PASS published Simple.css article layout, full-width header and inherited theme across resize");
}
