#!/usr/bin/env python3
"""Host algorithm checks only; native descriptor/containment proof is in the guest gate."""
import subprocess
import shutil
import tempfile
import unittest
import random
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class SunbeamStreams(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="zenith-sunbeam-")
        folder = Path(cls.temp.name)
        wrapper = folder / "host.c"
        wrapper.write_text(r'''
#define ZENITH_NATIVE_H
#include <unistd.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <errno.h>
#include <stdlib.h>
#include <dirent.h>
#include <string.h>
#define Z_OPEN_READ 0UL
#define Z_OPEN_DIRECTORY 1UL
#define Z_OPEN_WRITE 2UL
#define Z_OPEN_CREATE 4UL
#define Z_OPEN_TRUNCATE 8UL
#define Z_OPEN_APPEND 16UL
struct zenith_stat { unsigned long size; unsigned int mode, uid, gid, reserved; };
struct zenith_dirent { struct zenith_stat stat; char name[256]; };
struct zenith_path_request { unsigned int operation, flags; unsigned long path, length, target, target_length, value, extra; };
static DIR *host_directories[128];
static long zenith_path(unsigned int op,const char *path,unsigned long length,const char *target,unsigned long target_length,unsigned long value,unsigned long extra) { return -38; }
static long zenith_isatty(unsigned long fd) { return isatty(fd); }
static long zenith_write(const void *p, unsigned long n) { return write(1,p,n); }
static void zenith_exit(int code) { exit(code); }
static long zenith_identity(long n) { return n==39?getpid():n==102?getuid():getgid(); }
static long zenith_call(long n, unsigned long a, unsigned long b, unsigned long c) {
    long r=-1;
    if(n==0)r=read(a,(void *)b,c);
    else if(n==1)r=write(a,(void *)b,c);
    else if(n==3){if(a<128 && host_directories[a]){closedir(host_directories[a]);host_directories[a]=0;}r=close(a);}
    else if(n==2){char path[1025];if(b>1024)return -36;for(unsigned long i=0;i<b;i++)path[i]=((char *)a)[i];path[b]=0;r=open(path,O_RDONLY);}
    else if(n==217){
        if(a>=128)return -24;
        if(!host_directories[a])host_directories[a]=fdopendir(dup(a));
        if(!host_directories[a])return -errno;
        struct dirent *entry=readdir(host_directories[a]);if(!entry)return 0;
        struct zenith_dirent *out=(void *)b;memset(out,0,sizeof(*out));
        strncpy(out->name,entry->d_name,255);return sizeof(*out);
    }
    else if(n==4 || n==408) {
        char path[1025];if(b>1024)return -36;for(unsigned long i=0;i<b;i++)path[i]=((char *)a)[i];path[b]=0;
        struct stat native;r=n==4?stat(path,&native):lstat(path,&native);
        if(r==0) { struct zenith_stat *out=(void *)c;out->size=native.st_size;out->mode=native.st_mode;out->uid=native.st_uid;out->gid=native.st_gid;out->reserved=0; }
    }
    else return -38;
    return r<0?-errno:r;
}
''' + '#include "' + str(ROOT / 'userland/sunbeam/main.c') + '"\n')
        cls.binary = folder / "sunbeam"
        compiler = shutil.which("gcc") or shutil.which("clang")
        if not compiler: raise RuntimeError("Sunbeam host tests need gcc or clang")
        compiled = subprocess.run([compiler, "-O2", str(wrapper), "-o", str(cls.binary)], capture_output=True, text=True)
        if compiled.returncode: raise RuntimeError(compiled.stderr)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def run_tool(self, *args, data=b""):
        return subprocess.run([str(self.binary), *args], input=data, capture_output=True, timeout=5)

    def test_wc_binary_and_word_boundaries(self):
        self.assertEqual(b"2 4 18\n", self.run_tool("wc", data=b"one two\nthree\nlast").stdout)
        self.assertEqual(b"256\n", self.run_tool("wc", "-c", data=bytes(range(256))).stdout)
        self.assertEqual(b"1\n", self.run_tool("wc", "-w", data=b"a" * 10000).stdout)
        self.assertEqual(b"0 0 0\n", self.run_tool("wc").stdout)

    def test_head_zero_does_not_read_and_preserves_partial_line(self):
        self.assertEqual(b"", self.run_tool("head", "-n", "0", data=b"abc").stdout)
        self.assertEqual(b"one\ntwo\n", self.run_tool("head", "-n", "2", data=b"one\ntwo\nthree").stdout)
        self.assertEqual(b"partial", self.run_tool("head", data=b"partial").stdout)

    def test_tail_wrapping_and_unterminated_last_line(self):
        self.assertEqual(b"two\nthree", self.run_tool("tail", "-n", "2", data=b"one\ntwo\nthree").stdout)
        self.assertEqual(b"two\nthree\n", self.run_tool("tail", "-n", "2", data=b"one\ntwo\nthree\n").stdout)
        self.assertEqual(b"last\n", self.run_tool("tail", "-n", "1", data=b"long line\n" * 40000 + b"last\n").stdout)
        self.assertEqual(b"", self.run_tool("tail", "-n", "0", data=b"abc").stdout)

    def test_tail_is_bounded_and_errors_use_stderr(self):
        result = self.run_tool("tail", data=b"x" * 65537)
        self.assertEqual(1, result.returncode)
        self.assertEqual(b"", result.stdout)
        self.assertIn(b"64 KiB", result.stderr)
        for value in ("1025", "-1", "9999999999999999999999", "nope"):
            self.assertEqual(1, self.run_tool("head", "-n", value).returncode)

    def test_file_operands_and_utf8_bytes(self):
        path = Path(self.temp.name) / "input"
        path.write_bytes("a\nü\n終".encode())
        self.assertEqual("ü\n終".encode(), self.run_tool("tail", "-n", "2", str(path)).stdout)
        self.assertEqual(b"2", self.run_tool("wc", "-l", str(path)).stdout.split(b" ")[0])

    def test_multiple_counts_total_and_separate_options(self):
        first=Path(self.temp.name)/"count-a";second=Path(self.temp.name)/"count-b"
        first.write_bytes(b"one two\n");second.write_bytes(b"last\n")
        result=self.run_tool("wc","-l","-w",str(first),str(second))
        self.assertEqual(0,result.returncode)
        self.assertEqual(f"1 2 {first}\n1 1 {second}\n2 3 total\n".encode(),result.stdout)
        result=self.run_tool("wc","-l",str(first)+"-missing",str(second))
        self.assertEqual(1,result.returncode);self.assertIn(f"1 {second}\n".encode(),result.stdout)

    def test_short_line_counts_multiple_files_and_headers(self):
        first=Path(self.temp.name)/"lines-a";second=Path(self.temp.name)/"lines-b"
        first.write_bytes(b"one\ntwo\n");second.write_bytes(b"partial")
        result=self.run_tool("head","-1",str(first),str(second))
        self.assertEqual(0,result.returncode)
        self.assertEqual(f"==> {first} <==\none\n==> {second} <==\npartial\n".encode(),result.stdout)
        self.assertEqual(b"two\n",self.run_tool("tail","-1",str(first)).stdout)
        self.assertEqual(b"first\n",self.run_tool("head","-1",data=b"first\nlast\n").stdout)

    def test_ls_file_and_stat_multiple_paths_without_following_final_link(self):
        first=Path(self.temp.name)/"file-info";link=Path(self.temp.name)/"link-info"
        first.write_bytes(b"data");link.symlink_to(first)
        result=self.run_tool("ls",str(first));self.assertEqual(0,result.returncode)
        self.assertEqual((str(first)+"\n").encode(),result.stdout)
        result=self.run_tool("stat",str(first),str(link));self.assertEqual(0,result.returncode)
        lines=result.stdout.decode().splitlines();self.assertEqual(2,len(lines))
        self.assertTrue(lines[0].startswith(str(first)+": -"));self.assertTrue(lines[1].startswith(str(link)+": l"))

    def test_grep_flags_multiple_files_and_failure_status(self):
        data=b"one\nTwo\nTHREE\n"
        self.assertEqual(b"2:Two\n", self.run_tool("grep","-in","two",data=data).stdout)
        self.assertEqual(b"one\nTHREE\n", self.run_tool("grep","-iv","two",data=data).stdout)
        self.assertEqual(1,self.run_tool("grep","absent",data=data).returncode)
        self.assertEqual(2,self.run_tool("grep","-z","x").returncode)
        self.assertEqual(b"b\n", self.run_tool("grep","-iE","^[^a]$",data=b"A\na\nb\n").stdout)
        self.assertEqual(b"-option\n",self.run_tool("grep","--","-option",data=b"-option\n").stdout)
        a=Path(self.temp.name)/"grep-a";b=Path(self.temp.name)/"grep-b"
        a.write_bytes(data);b.write_bytes(data)
        self.assertEqual(f"{a}:Two\n{b}:Two\n".encode(),self.run_tool("grep","Two",str(a),str(b)).stdout)

    def test_bounded_regex_alternation_classes_anchors_and_repetition(self):
        data=b"abbb\nfoo42\nhello\nbar9\nab\n"
        cases={"^(ab+|foo[0-9]+)$":b"abbb\nfoo42\nab\n", "^bar.?$":b"bar9\n", "[^a-z]":b"foo42\nbar9\n", "^h(e|a)l*o$":b"hello\n", "^z*$":b""}
        for pattern,expected in cases.items():
            result=self.run_tool("grep","-E",pattern,data=data)
            self.assertEqual(expected,result.stdout,pattern)
            self.assertEqual(0 if expected else 1,result.returncode,pattern)
        for pattern in ("[", "a{2}", "[[:digit:]]", "(a)\\1", "(?=a)", "("*25+"a"+")"*25, "a"*257):
            self.assertEqual(2,self.run_tool("grep","-E",pattern,data=data).returncode,pattern)
        self.assertEqual(b"a"*1000+b"!\n",self.run_tool("grep","-E","(a+)+!",data=b"a"*1000+b"!\n").stdout)

    def test_regex_filter_matches_independent_reference_on_seeded_inputs(self):
        rng=random.Random(21)
        lines=[''.join(rng.choices('ab12XYZ',k=rng.randrange(0,20))) for _ in range(100)]
        data=('\n'.join(lines)+'\n').encode()
        patterns=('a','^a','a$','a*','a+','a?','(a|b)+','a.*b','[a-z]+','[^0-9]',
                  '^(ab|a)*$','(a?)*','^$','(a|ab)b','.*','[A-Z0-9]+')
        for flags in ('-E','-iE','-vE'):
            for pattern in patterns:
                rx=re.compile(pattern,re.ASCII | (re.IGNORECASE if 'i' in flags else 0))
                selected=[line for line in lines if bool(rx.search(line)) != ('v' in flags)]
                expected=(''.join(line+'\n' for line in selected)).encode()
                result=self.run_tool('grep',flags,pattern,data=data)
                self.assertEqual((0 if selected else 1,expected),(result.returncode,result.stdout),(flags,pattern))

    def test_sort_uniq_boundaries_and_unterminated_lines(self):
        self.assertEqual(b"a\nb\nb\nz\n",self.run_tool("sort",data=b"z\nb\na\nb").stdout)
        self.assertEqual(b"z\nb\na\n",self.run_tool("sort","-r",data=b"a\nz\nb\n").stdout)
        self.assertEqual(b"a\nb\na\n",self.run_tool("uniq",data=b"a\na\nb\na").stdout)
        self.assertEqual(2,self.run_tool("sort",data=b"a\n"*2049).returncode)
        self.assertNotEqual(0,self.run_tool("grep","x",data=b"x"*8193).returncode)
        self.assertNotEqual(0,self.run_tool("uniq",data=b"a\0b\n").returncode)

    def test_seq_signed_overflow_and_printf_format_progress(self):
        self.assertEqual(b"3\n1\n-1\n",self.run_tool("seq","3","-2","-1").stdout)
        self.assertEqual(b"9223372036854775807\n",self.run_tool("seq","9223372036854775807","9223372036854775807").stdout)
        self.assertEqual(b"-9223372036854775808\n",self.run_tool("seq","-9223372036854775808","-1","-9223372036854775808").stdout)
        self.assertEqual(2,self.run_tool("seq","1","0","4").returncode)
        self.assertEqual(2,self.run_tool("seq","10001").returncode)
        self.assertEqual(b"a:1\nb:2\n",self.run_tool("printf","%s:%d\\n","a","1","b","2").stdout)
        self.assertEqual(b"%",self.run_tool("printf","%%","unused").stdout)
        self.assertEqual(2,self.run_tool("printf","%n","x").returncode)
        self.assertEqual(2,self.run_tool("printf","%d","bad").returncode)

    def test_tr_binary_and_cut_field_ranges(self):
        self.assertEqual(b"ABZ\x00\n",self.run_tool("tr","a-z","A-Z",data=b"abz\x00\n").stdout)
        self.assertEqual(b"abc",self.run_tool("tr","-d","0-9",data=b"a1b2c3").stdout)
        self.assertEqual(2,self.run_tool("tr","a-z","").returncode)
        self.assertEqual(b"a:c:d\nplain\n",self.run_tool("cut","-d:","-f1,3-",data=b"a:b:c:d\nplain\n").stdout)
        self.assertEqual(b"abc\n",self.run_tool("cut","-c","-3",data=b"abcdef").stdout)
        for selection in ("0","3-1","1,","-", "99999999999999"):
            self.assertEqual(2,self.run_tool("cut","-f",selection).returncode,selection)

    def test_sed_addresses_substitution_global_and_zero_width(self):
        self.assertEqual(b"one\nTWO\n",self.run_tool("sed","s/two/TWO/",data=b"one\ntwo\n").stdout)
        self.assertEqual(b"bXbX\n",self.run_tool("sed","s/a/X/g",data=b"baba\n").stdout)
        self.assertEqual(b"[aaa]b\n",self.run_tool("sed","s/a+/[&]/",data=b"aaab\n").stdout)
        self.assertEqual(b"two\n",self.run_tool("sed","-n","2p",data=b"one\ntwo\nthree\n").stdout)
        self.assertEqual(b"one\nthree\n",self.run_tool("sed","/two/d",data=b"one\ntwo\nthree\n").stdout)
        self.assertEqual(b"-a-b-\n",self.run_tool("sed","s/x*/-/g",data=b"ab\n").stdout)
        self.assertEqual(b"-\n",self.run_tool("sed","s/a*/-/g",data=b"a\n").stdout)
        self.assertEqual(b"X\n",self.run_tool("sed","s/a/b/;s/b/X/",data=b"a\n").stdout)
        for script in ("s/[//", "s/a/\\1/", "1x", "s/a/b/i", "s/a/" ):
            self.assertEqual(2,self.run_tool("sed",script,data=b"a\n").returncode,script)

    def test_diff_hunks_missing_final_newline_and_size_refusal(self):
        a=Path(self.temp.name)/"diff-a";b=Path(self.temp.name)/"diff-b"
        a.write_bytes(b"one\ntwo\nthree\n");b.write_bytes(b"one\nTWO\nthree\n")
        result=self.run_tool("diff",str(a),str(b));self.assertEqual(1,result.returncode)
        self.assertEqual(b"2c2\n< two\n---\n> TWO\n",result.stdout)
        b.write_bytes(a.read_bytes());self.assertEqual(0,self.run_tool("diff",str(a),str(b)).returncode)
        b.write_bytes(b"one\ntwo\nthree");result=self.run_tool("diff",str(a),str(b));self.assertEqual(1,result.returncode);self.assertIn(b"No newline",result.stdout)
        b.write_bytes(b"x\n"*257);self.assertEqual(2,self.run_tool("diff",str(a),str(b)).returncode)

    def test_traversal_symlink_cycle_leaf_and_depth_refusal(self):
        root=Path(self.temp.name)/"walk";root.mkdir(exist_ok=True);(root/"file.txt").write_bytes(b"data");(root/"sub").mkdir(exist_ok=True)
        link=root/"sub"/"cycle"
        if not link.exists(): link.symlink_to(root)
        result=self.run_tool("find",str(root),"-type","f","-name","*.txt");self.assertEqual(0,result.returncode);self.assertEqual((str(root/"file.txt")+"\n").encode(),result.stdout)
        self.assertEqual((str(root)+"\n").encode(),self.run_tool("find",str(root)+"/","-name","walk").stdout)
        result=self.run_tool("find",str(root),"-type","l");self.assertEqual(0,result.returncode);self.assertEqual((str(link)+"\n").encode(),result.stdout)
        self.assertEqual(b"1\t"+str(root).encode()+b"\n",self.run_tool("du","-s",str(root)).stdout)
        result=self.run_tool("tree",str(root));self.assertEqual(0,result.returncode);self.assertIn(b"1 directories, 2 files",result.stdout)
        deep=root/"deep";deep.mkdir(exist_ok=True)
        for _ in range(16): deep=deep/"d";deep.mkdir(exist_ok=True)
        self.assertNotEqual(0,self.run_tool("find",str(root)).returncode)


if __name__ == "__main__":
    unittest.main()
