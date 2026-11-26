#include "client.h"
#include "platform.h"

#ifndef SUNBEAM_VERSION
#define SUNBEAM_VERSION "0.1.0"
#endif

static const char usage[] = "Usage: smb [options] shares HOST\n"
                            "       smb [options] ls //HOST/SHARE[/DIR]\n"
                            "       smb [options] get //HOST/SHARE/FILE LOCAL\n"
                            "       smb [options] put LOCAL //HOST/SHARE/FILE\n"
                            "Options: -U USER, --domain DOMAIN, --port PORT (default 445),\n"
                            "         --password-stdin, --credentials FILE, --replace (get/put)\n"
                            "The default password prompt is hidden. Use a credential file owned by\n"
                            "your user (mode 0400 or 0600) with server=, username=, password=\n"
                            "and optional domain=, port=. The server and port must match.\n";

static size_t length(const char *s)
{
    size_t n = 0;
    while (s[n])
        n++;
    return n;
}

static int equal(const char *a, const char *b)
{
    while (*a && *a == *b) {
        a++;
        b++;
    }
    return *a == *b;
}

static int equal_host(const char *a, const char *b)
{
    for (; *a || *b; a++, b++) {
        unsigned char ac = (unsigned char)*a, bc = (unsigned char)*b;
        if (ac >= 'A' && ac <= 'Z')
            ac += (unsigned char)('a' - 'A');
        if (bc >= 'A' && bc <= 'Z')
            bc += (unsigned char)('a' - 'A');
        if (ac != bc)
            return 0;
    }
    return 1;
}

static void erase(void *data, size_t n)
{
    volatile unsigned char *p = data;
    while (n--)
        *p++ = 0;
}

static int output(int fd, const char *s) { return platform_output(fd, s, length(s)); }

static int error(const char *s)
{
    output(2, "smb: ");
    output(2, s);
    output(2, "\n");
    return 1;
}

static int port_value(const char *s)
{
    unsigned int n = 0;
    if (!*s)
        return 0;
    while (*s) {
        if (*s < '0' || *s > '9')
            return 0;
        n = n * 10 + (unsigned int)(*s++ - '0');
        if (n > 65535)
            return 0;
    }
    return (int)n;
}

struct options {
    struct smb_request request;
    int port, password_stdin;
    const char *local, *credential_path;
    char server[254], share[256];
    char password[256], credentials[SMB_CREDENTIAL_LIMIT + 1];
};

static int valid_server(const char *s)
{
    size_t n = length(s);
    if (!n || n >= 254 || *s == '.' || s[n - 1] == '.')
        return 0;
    for (; *s; s++)
        if (!((*s >= 'A' && *s <= 'Z') || (*s >= 'a' && *s <= 'z') || (*s >= '0' && *s <= '9') || *s == '-' ||
              *s == '.'))
            return 0;
    return 1;
}

static int valid_component(const char *s, size_t n)
{
    if (!n || n > 255 || (n == 1 && *s == '.') || (n == 2 && s[0] == '.' && s[1] == '.'))
        return 0;
    for (size_t i = 0; i < n; i++) {
        unsigned char c = (unsigned char)s[i];
        if (c < 32 || c == 127 || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' ||
            c == '>' || c == '|')
            return 0;
    }
    return 1;
}

static int parse_remote(struct options *o, const char *unc)
{
    if (unc[0] != '/' || unc[1] != '/')
        return 0;
    unc += 2;
    size_t n = 0;
    while (unc[n] && unc[n] != '/')
        n++;
    if (!n || n >= sizeof(o->server) || !unc[n])
        return 0;
    for (size_t i = 0; i < n; i++)
        o->server[i] = unc[i];
    if (!valid_server(o->server))
        return 0;
    unc += n + 1;
    n = 0;
    while (unc[n] && unc[n] != '/')
        n++;
    if (!valid_component(unc, n))
        return 0;
    for (size_t i = 0; i < n; i++)
        o->share[i] = unc[i];
    o->request.server = o->server;
    o->request.share = o->share;
    unc += n;
    if (*unc == '/')
        unc++;
    o->request.path = unc;
    if (length(unc) > 1024)
        return 0;
    while (*unc) {
        n = 0;
        while (unc[n] && unc[n] != '/')
            n++;
        if (!valid_component(unc, n))
            return 0;
        unc += n;
        if (*unc == '/' && !*++unc)
            return 0;
    }
    return o->request.operation == SMB_LIST || *o->request.path;
}

static int parse(int argc, char **argv, struct options *o)
{
    int at = 1;
    o->port = 445;
    for (; at < argc && argv[at][0] == '-'; at++) {
        const char *a = argv[at];
        if (equal(a, "--replace"))
            o->request.replace = 1;
        else if (equal(a, "--password-stdin"))
            o->password_stdin = 1;
        else if (at + 1 == argc)
            return 0;
        else if (equal(a, "--port")) {
            o->port = port_value(argv[++at]);
            if (!o->port)
                return 0;
        } else if (equal(a, "-U") || equal(a, "--user"))
            o->request.username = argv[++at];
        else if (equal(a, "--domain"))
            o->request.domain = argv[++at];
        else if (equal(a, "--credentials"))
            o->credential_path = argv[++at];
        else
            return 0;
    }
    if (o->credential_path ? (o->request.username || o->request.domain || o->password_stdin)
                           : !o->request.username)
        return 0;
    if (at >= argc)
        return 0;
    const char *verb = argv[at++];
    if (equal(verb, "shares")) {
        if (argc - at != 1 || o->request.replace || !valid_server(argv[at]))
            return 0;
        o->request.operation = SMB_SHARES;
        o->request.server = argv[at];
        return 1;
    }
    if (equal(verb, "ls")) {
        o->request.operation = SMB_LIST;
        return argc - at == 1 && !o->request.replace && parse_remote(o, argv[at]);
    }
    if (argc - at != 2)
        return 0;
    if (equal(verb, "get")) {
        o->request.operation = SMB_GET;
        o->local = argv[at + 1];
        return *o->local && parse_remote(o, argv[at]);
    }
    if (equal(verb, "put")) {
        o->request.operation = SMB_PUT;
        o->local = argv[at];
        return *o->local && parse_remote(o, argv[at + 1]);
    }
    return 0;
}

static int read_credentials(struct options *o)
{
    int n = platform_open_private(o->credential_path, o->credentials, SMB_CREDENTIAL_LIMIT);
    if (n <= 0 || n > SMB_CREDENTIAL_LIMIT)
        return 0;
    for (int i = 0; i < n; i++)
        if (!o->credentials[i])
            return 0;
    o->credentials[n] = 0;
    char *p = o->credentials;
    const char *server = 0, *port = 0;
    while (*p) {
        char *key = p, *value;
        while (*p && *p != '=' && *p != '\n')
            p++;
        if (*p != '=')
            return 0;
        *p++ = 0;
        value = p;
        while (*p && *p != '\n')
            p++;
        char *end = p;
        if (*p)
            *p++ = 0;
        if (end > value && end[-1] == '\r')
            end[-1] = 0;
        const char **slot;
        if (equal(key, "server"))
            slot = &server;
        else if (equal(key, "port"))
            slot = &port;
        else if (equal(key, "username"))
            slot = &o->request.username;
        else if (equal(key, "domain"))
            slot = &o->request.domain;
        else if (equal(key, "password"))
            slot = &o->request.password;
        else
            return 0;
        if (*slot)
            return 0;
        *slot = value;
    }
    return server && equal_host(server, o->request.server) && (port ? port_value(port) : 445) == o->port &&
           o->request.username && o->request.password;
}

static int read_password(struct options *o)
{
    if (platform_password_mode(o->password_stdin) < 0)
        return 0;
    if (!o->password_stdin)
        output(2, "SMB password: ");
    size_t n = 0;
    int ok = 0;
    for (;;) {
        unsigned char buf[64];
        int got = platform_password_read(buf, sizeof(buf));
        if (got <= 0) {
            ok = o->password_stdin && got == 0 && n > 0;
            break;
        }
        int done = 0;
        for (int i = 0; i < got; i++) {
            unsigned char c = buf[i];
            if (c == '\n' || c == '\r') {
                done = 1;
                ok = n > 0;
                break;
            }
            if (!o->password_stdin && (c == 8 || c == 127)) {
                if (n) {
                    do {
                        n--;
                    } while (n && ((unsigned char)o->password[n] & 0xc0) == 0x80);
                }
            } else if (c < 32 || n + 1 >= sizeof(o->password)) {
                done = 1;
                break;
            } else
                o->password[n++] = (char)c;
        }
        erase(buf, sizeof(buf));
        if (done)
            break;
    }
    platform_password_restore();
    if (!o->password_stdin)
        output(2, "\n");
    o->password[n] = 0;
    o->request.password = o->password;
    return ok;
}

static int list_entry(void *context, const char *name, int directory, uint64_t size)
{
    const struct options *o = context;
    if (o->request.operation == SMB_LIST) {
        if (output(1, directory ? "dir\t" : "file\t") < 0)
            return -1;
        char digits[22];
        size_t i = sizeof(digits);
        digits[--i] = '\t';
        do {
            digits[--i] = (char)('0' + size % 10);
            size /= 10;
        } while (size);
        if (platform_output(1, digits + i, sizeof(digits) - i) < 0)
            return -1;
    }
    // Render server-controlled control characters as '?' so names cannot emit terminal commands.
    size_t start = 0, n = length(name);
    for (size_t i = 0; i < n; i++) {
        unsigned int c = (unsigned char)name[i];
        int c1 = c == 0xc2 && i + 1 < n && ((unsigned char)name[i + 1] & 0xe0) == 0x80;
        if (c >= 32 && c != 127 && !c1)
            continue;
        if (platform_output(1, name + start, i - start) < 0 || output(1, "?") < 0)
            return -1;
        i += c1;
        start = i + 1;
    }
    if (platform_output(1, name + start, n - start) < 0)
        return -1;
    return output(1, "\n");
}

static int begin_download(void *context, uint64_t size)
{
    const struct options *o = context;
    return size > SMB_FILE_LIMIT ? -1 : platform_begin_sink(o->local, size, o->request.replace);
}

int main(int argc, char **argv)
{
    static struct options opts;
    if (argc == 2 && equal(argv[1], "--version"))
        return output(1, "smb " SUNBEAM_VERSION "\n") < 0;
    if (argc == 2 && equal(argv[1], "--help"))
        return output(1, usage) < 0;
    if (!parse(argc, argv, &opts)) {
        output(2, usage);
        return 2;
    }
    int result = 1;
    if (opts.credential_path) {
        if (!read_credentials(&opts)) {
            error("invalid private credential file or server/port mismatch");
            goto done;
        }
    } else if (!read_password(&opts)) {
        error("unable to read password; use a foreground terminal or --password-stdin");
        goto done;
    }
    if (!opts.request.domain)
        opts.request.domain = "";
    if (!*opts.request.username || length(opts.request.username) > 128 || length(opts.request.domain) > 128 ||
        !*opts.request.password || length(opts.request.password) >= sizeof(opts.password)) {
        error("invalid credential length");
        goto done;
    }
    if (opts.request.operation == SMB_PUT &&
        (platform_open_source(opts.local, &opts.request.upload_size) < 0 ||
         opts.request.upload_size > SMB_FILE_LIMIT)) {
        error("cannot read local regular file within 32 MiB limit");
        goto done;
    }
    if (platform_open_socket(opts.request.server, (unsigned)opts.port) < 0) {
        error("connection failed");
        goto done;
    }
    struct smb_io io = {&opts,      platform_send,  platform_recv,       platform_random,
                        list_entry, begin_download, platform_write_sink, platform_read_source};
    if (smb_execute(&io, &opts.request) != 0) {
        error(smb_error());
        uint32_t status = smb_ntstatus();
        if (status) {
            char code[] = "NTSTATUS 0x00000000\n";
            for (int j = 0; j < 8; j++) {
                code[18 - j] = "0123456789abcdef"[status & 15];
                status >>= 4;
            }
            output(2, code);
        }
        goto done;
    }
    if (opts.request.operation == SMB_GET && platform_commit_sink() < 0) {
        error("cannot commit local download");
        goto done;
    }
    result = 0;
done:
    platform_close();
    smb_clear();
    erase(&opts, sizeof(opts));
    return result;
}
