/* A second authenticated SMB connection changes an ordinary file while the
 * read-only client holds it. The writer is fixture code, not a mount API. */
#include "../../../userland/smb/wire.h"
#include <assert.h>
#include <stdio.h>

static const char name[] = "session-live";

static void write_at(struct smb_session *writer, uint64_t offset, const char *text)
{
    size_t length = strlen(text);
    smb_request_start(writer, SMB_WRITE, 49);
    uint8_t *p = writer->packet + 64;
    w16(p + 2, 112);
    w32(p + 4, (uint32_t)length);
    w64(p + 8, offset);
    memcpy(p + 16, writer->file, 16);
    memcpy(p + 48, text, length);
    assert(!smb_exchange(writer, 112 + length) && !smb_response(writer, 17));
    assert(r32(p + 4) == length);
}

static void set_file_info(struct smb_session *writer, unsigned info, const void *data, size_t length)
{
    smb_request_start(writer, SMB_SET_INFO, 33);
    uint8_t *p = writer->packet + 64;
    p[2] = 1; /* SMB2_0_INFO_FILE */
    p[3] = (uint8_t)info;
    w32(p + 4, (uint32_t)length);
    w16(p + 8, 96);
    memcpy(p + 16, writer->file, 16);
    memcpy(p + 32, data, length);
    assert(!smb_exchange(writer, 96 + length) && !smb_response(writer, 2));
}

void check_live_reader(struct smb_session *reader, struct smb_session *writer)
{
    /* FILE_WRITE_DATA | DELETE, FILE_CREATE, FILE_NON_DIRECTORY_FILE. */
    assert(!smb_open(writer, name, 0x10002, SMB_SHARE_ALL, 2, 0x40, NULL));
    write_at(writer, 0, "start");
    struct smb_read_handle file;
    uint64_t size;
    char data[16];
    assert(!smb_session_open_read(reader, name, &file, &size) && size == 5);
    assert(smb_session_read_at(reader, file, 5, data, sizeof(data)) == 0);
    write_at(writer, 5, "-new");
    assert(smb_session_read_at(reader, file, 5, data, sizeof(data)) == 4 && !memcmp(data, "-new", 4));

    uint8_t eof[8];
    w64(eof, 2);
    set_file_info(writer, 20, eof, sizeof(eof)); /* FileEndOfFileInformation */
    assert(smb_session_read_at(reader, file, 0, data, sizeof(data)) == 2 && !memcmp(data, "st", 2));
    assert(smb_session_read_at(reader, file, 2, data, sizeof(data)) == 0);
    assert(smb_session_read_at(reader, file, UINT64_MAX - 1, data, sizeof(data)) == 0);

    uint8_t rename[96] = {0};
    int bytes = smb_utf16("renamed-live", rename + 20, sizeof(rename) - 20);
    assert(bytes > 0);
    w32(rename + 16, (unsigned)bytes);
    set_file_info(writer, 10, rename, 20 + (size_t)bytes); /* FileRenameInformation */
    write_at(writer, 2, "ill");
    assert(smb_session_read_at(reader, file, 0, data, sizeof(data)) == 5 && !memcmp(data, "still", 5));
    assert(!smb_session_close_read(reader, file));
    smb_request_start(writer, SMB_CLOSE, 24);
    memcpy(writer->packet + 72, writer->file, 16);
    assert(!smb_exchange(writer, 88) && !smb_response(writer, 60));
    puts("PASS signed live reader alongside a writer, growth, truncation, rename and extreme offsets");
}

/* The CLI's whole-file copy keeps its original length stable by denying SMB
 * writers. Attempt a conflicting open on the independent session from the
 * real download_begin callback while the source handle is live. */
struct copy_check {
    int fd; /* First field is the descriptor used by the test socket callbacks. */
    struct smb_session *peer;
    size_t bytes;
};

static int verify_share(void *context, uint64_t size)
{
    struct copy_check *check = context;
    assert(size == 5);
    assert(smb_open(check->peer, "renamed-live", 2, SMB_SHARE_ALL, 1, 0x40, NULL) < 0);
    assert(check->peer->status == 0xc0000043u); /* STATUS_SHARING_VIOLATION */
    return 0;
}

static int verify_bytes(void *context, const void *data, size_t length)
{
    struct copy_check *check = context;
    assert(length <= 5 - check->bytes && !memcmp(data, "still" + check->bytes, length));
    check->bytes += length;
    return (int)length;
}

void check_cli_read_sharing(int fd, const struct smb_io *transport, struct smb_request request,
                            struct smb_session *peer)
{
    struct copy_check check = {.fd = fd, .peer = peer};
    struct smb_io io = *transport;
    io.user = &check;
    io.download_begin = verify_share;
    io.download_write = verify_bytes;
    request.operation = SMB_GET;
    request.path = "renamed-live";
    assert(!smb_execute(&io, &request) && check.bytes == 5);
    smb_clear();
    puts("PASS whole-file CLI copy keeps its source protected from concurrent writers");
}
