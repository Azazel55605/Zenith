#include "profile.h"
#include "zenith.h"
#include <assert.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

static const char *root;
static unsigned uid = 1001;
static int fail_flush, race_rename;
static DIR *directories[1024];

static void map(char dst[4096], const char *guest)
{
    assert(guest && *guest == '/');
    assert(snprintf(dst,4096,"%s%s",root,guest) < 4096);
}

long profile_test_call(long op, unsigned long a, unsigned long b, unsigned long c)
{
    long result;
    char path[4096], target[4096];
    switch (op) {
    case 39: return getpid();
    case 102: return uid;
    case 408: {
        const char *name = (const char *)a; map(path,name);
        struct stat st;
        if (lstat(path,&st)) return -errno;
        struct zenith_stat *out = (void *)c;
        *out = (struct zenith_stat){.mode=st.st_mode,.size=st.st_size,
            .uid=!strncmp(name,"/users/one",10)?1001:!strncmp(name,"/users/two",10)?1002:0};
        return 0;
    }
    case 2: {
        map(path,(const char *)a);
        if (c & Z_OPEN_DIRECTORY) {
            DIR *d = opendir(path);
            if (!d) return -errno;
            int fd = dirfd(d);
            assert(fd < 1024); directories[fd] = d;
            return fd;
        }
        int flags = c & Z_OPEN_WRITE ? O_WRONLY : O_RDONLY;
        if (c & Z_OPEN_CREATE) flags |= O_CREAT;
        if (c & Z_OPEN_TRUNCATE) flags |= O_TRUNC;
        result = open(path,flags,0644);
        break;
    }
    case 0: result = read(a,(void *)b,c); break;
    case 1: result = write(a,(const void *)b,c); break;
    case 3:
        if (a < 1024 && directories[a]) {
            result = closedir(directories[a]); directories[a]=NULL;
        } else result = close(a);
        break;
    case 74: return fail_flush ? -ENOSPC : fsync(a);
    case 217: {
        assert(a < 1024 && directories[a] && c == sizeof(struct zenith_dirent));
        errno = 0;
        struct dirent *e = readdir(directories[a]);
        if (!e) return -errno;
        struct zenith_dirent *record = (void *)b;
        strcpy(record->name,e->d_name); return sizeof(*record);
    }
    case 405: {
        const struct zenith_path_request *req = (void *)a;
        map(path,(const char *)req->path);
        switch (req->operation) {
        case 1: result = mkdir(path,req->value); break;
        case 2: result = unlink(path); break;
        case 3: result = rmdir(path); break;
        case 4:
            if (race_rename) {
                race_rename=0;
                pid_t child = fork();
                assert(child >= 0);
                if (!child) {
                    struct nebula_profile *p = calloc(1,sizeof(*p));
                    assert(p && nebula_profile_set_bookmark(p,"https://other.example/","Other session",true));
                    free(p); _exit(0);
                }
                int status;
                assert(waitpid(child,&status,0) == child && WIFEXITED(status) && !WEXITSTATUS(status));
            }
            map(target,(const char *)req->target); result = rename(path,target); break;
        case 5: result = chmod(path,req->value); break;
        default: abort();
        }
        break;
    }
    default: abort();
    }
    return result < 0 ? -errno : result;
}

int main(int argc, char **argv)
{
    assert(argc==2);
    root = argv[1];
    struct nebula_profile p = {0};
    setenv("HOME","/users/two",1);
    assert(nebula_profile_load(&p) && !p.bookmark_count && !p.recent_count);
    assert(!nebula_profile_accepts("https://me:password@host/test"));
    assert(!nebula_profile_accepts("http://name@host/"));
    assert(!nebula_profile_accepts("http://name%3Asecret%40host/"));
    assert(!nebula_profile_accepts("http://:pass/"));
    assert(!nebula_profile_accepts("http://host:abc/"));
    assert(nebula_profile_accepts("https://[::1]:8443/"));
    assert(nebula_profile_accepts("https://host:8443/path?q=email@example.org"));
    assert(!nebula_profile_visit(&p,"https://name:password@host/","User"));
    assert(!nebula_profile_set_bookmark(&p,"https://name:password@host/","User",true));
    assert(!nebula_profile_accepts("data:text/plain,test"));
    assert(!nebula_profile_accepts("https://site/line\nforged"));
    assert(!nebula_profile_accepts("http:///path"));
    assert(nebula_profile_accepts("file:///my/document.html"));
    char long_url[NEBULA_URL_BYTES+1];
    memset(long_url,'x',sizeof(long_url)); memcpy(long_url,"https://",8);
    long_url[sizeof(long_url)-1]=0;
    assert(!nebula_profile_accepts(long_url));
    assert(nebula_profile_set_bookmark(&p,"https://one.example/","First\tline\n",true));
    assert(!strcmp(p.bookmarks[0].title,"First line "));
    /* Inject an independent process after our snapshot read, just before publication. */
    race_rename=1;
    assert(nebula_profile_set_bookmark(&p,"https://mine.example/","Mine",true));
    assert(p.bookmark_count==3 && nebula_profile_bookmarked(&p,"https://other.example/"));
    fail_flush=1;
    assert(!nebula_profile_set_bookmark(&p,"https://lost.example/","Lost",true));
    fail_flush=0;
    assert(nebula_profile_load(&p) && p.bookmark_count==3);
    assert(!nebula_profile_bookmarked(&p,"https://lost.example/"));
    assert(nebula_profile_set_bookmark(&p,"https://mine.example/","",false));
    assert(p.bookmark_count==2 && !nebula_profile_bookmarked(&p,"https://mine.example/"));
    for (unsigned i=0;i<NEBULA_RECENT+4;i++) {
        char url[80]; snprintf(url,sizeof(url),"https://history.example/%u",i);
        assert(nebula_profile_visit(&p,url,"visited"));
    }
    assert(p.recent_count==NEBULA_RECENT && !strcmp(p.recent[0].url,"https://history.example/27"));
    assert(nebula_profile_visit(&p,"https://history.example/9","updated"));
    assert(nebula_profile_load(&p) && p.recent_count==NEBULA_RECENT && !strcmp(p.recent[0].title,"updated"));
    for (unsigned i=p.bookmark_count;i<NEBULA_BOOKMARKS;i++) {
        char url[80]; snprintf(url,sizeof(url),"https://bookmarks.example/%u",i);
        assert(nebula_profile_set_bookmark(&p,url,"saved",true));
    }
    assert(!nebula_profile_set_bookmark(&p,"https://overflow.example/","full",true));
    assert(nebula_profile_load(&p) && p.bookmark_count==NEBULA_BOOKMARKS);
    uid=1002;
    assert(nebula_profile_load(&p) && !p.bookmark_count && !p.recent_count);
    assert(nebula_profile_set_bookmark(&p,"https://two.example/","Two",true));
    uid=1001;
    assert(nebula_profile_load(&p) && !nebula_profile_bookmarked(&p,"https://two.example/"));
    char path[4096], out[4096]; map(path,"/users/one/.nebula"); map(out,"/users/one/original");
    assert(!rename(path,out) && !symlink(out,path));
    assert(!nebula_profile_load(&p));
    assert(!nebula_profile_set_bookmark(&p,"https://bad.example/","",true));
    assert(!unlink(path) && !rename(out,path));
    assert(!chmod(path,0755) && !nebula_profile_load(&p));
    assert(!chmod(path,0700) && nebula_profile_load(&p));
    DIR *dir = opendir(path);
    assert(dir);
    char newest[32] = "";
    for (struct dirent *entry; (entry = readdir(dir)); ) {
        if (entry->d_name[0] == 'r' && strcmp(entry->d_name,newest)>0) {
            assert(strlen(entry->d_name) < sizeof(newest));
            strcpy(newest,entry->d_name);
        }
    }
    assert(!closedir(dir) && *newest);
    char saved[4096];
    assert(snprintf(out,sizeof(out),"%s/%s/data",path,newest) < (int)sizeof(out));
    assert(snprintf(saved,sizeof(saved),"%s/%s-saved",path,newest) < (int)sizeof(saved));
    assert(!rename(out,saved) && !symlink(saved,out));
    assert(!nebula_profile_load(&p));
    assert(!unlink(out) && !rename(saved,out));
    assert(!chmod(out,0644) && !nebula_profile_load(&p));
    assert(!chmod(out,0600) && nebula_profile_load(&p));
    map(path,"/etc/passwd"); map(out,"/etc/passwd-original");
    assert(!rename(path,out) && !symlink(out,path) && !nebula_profile_load(&p));
    assert(!unlink(path) && !rename(out,path));
    puts("PASS user-owned home, private storage, unsafe addresses, concurrent merge, interrupted save, bounded history and bookmark persistence");
}
