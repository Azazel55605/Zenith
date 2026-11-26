/* Drive the registered MIME handler through NetSurf's fetch and image caches. */
#include "content/hlcache.h"

static nserror jpeg_event(hlcache_handle *handle, const hlcache_event *event, void *result)
{
    (void)handle;
    if (event->type == CONTENT_MSG_DONE || event->type == CONTENT_MSG_ERROR)
        *(content_msg *)result = event->type;
    return NSERROR_OK;
}

static void check_jpeg_files(const char *cases)
{
    FILE *list = fopen(cases, "r");
    assert(list);
    char *line = NULL;
    size_t capacity = 0;
    unsigned tested = 0;
    while (getline(&line, &capacity, list) > 0) {
        char *address = strchr(line, ' ');
        assert(address);
        *address++ = '\0';
        address[strcspn(address, "\n")] = '\0';
        nsurl *url;
        hlcache_handle *handle = NULL;
        content_msg result = CONTENT_MSG_LOADING;
        assert(nsurl_create(address, &url) == NSERROR_OK);
        assert(hlcache_handle_retrieve(url, 0, NULL, NULL, jpeg_event, &result,
                                       NULL, CONTENT_IMAGE, &handle) == NSERROR_OK);
        long deadline = zui_motion_clock() + 5000;
        while (result == CONTENT_MSG_LOADING && zui_motion_clock() < deadline) {
            run_alarms();
            usleep(1000);
        }
        if (!strcmp(line, "oversized")) {
            assert(result == CONTENT_MSG_ERROR);
        } else {
            assert(result == CONTENT_MSG_DONE);
            struct bitmap *bitmap = content_get_bitmap(handle);
            if (!strcmp(line, "truncated")) {
                assert(bitmap == NULL);
            } else {
                bool gray = !strcmp(line, "gray");
                int width = gray ? 200 : !strcmp(line, "baseline") ? 96 : 640;
                int height = gray ? 120 : !strcmp(line, "baseline") ? 64 : 480;
                assert(bitmap && zenith_bitmaps.get_width(bitmap) == width && zenith_bitmaps.get_height(bitmap) == height);
                assert(zenith_bitmaps.get_opaque(bitmap));
                uint8_t *pixels = zenith_bitmaps.get_buffer(bitmap);
                size_t stride = zenith_bitmaps.get_rowstride(bitmap);
                const uint32_t expected[] = {0xffe61e1e, 0xff1ec829, 0xff283cdb, 0xfffafafa};
                const uint32_t grays[] = {0xff5a5a5a, 0xff838383, 0xff484848, 0xfffafafa};
                for (int i = 0; i < 4; i++) {
                    int x = (1 + 2 * (i % 2)) * width / 4, y = (1 + 2 * (i / 2)) * height / 4;
                    uint32_t pixel;
                    memcpy(&pixel, pixels + y * stride + x * 4, sizeof(pixel));
                    assert(pixel == (gray ? grays[i] : expected[i]));
                }
            }
        }
        hlcache_handle_release(handle);
        nsurl_unref(url);
        tested++;
    }
    free(line);
    assert(!ferror(list) && tested == 5);
    fclose(list);
    puts("PASS JPEG cache decodes baseline, progressive and grayscale pixels, rejects oversized headers and discards truncated bitmaps");
}
