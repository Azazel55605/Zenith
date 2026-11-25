#!/usr/bin/env python3
"""Native client algorithms on host mocks; ownership/TLS/VFS proof uses QEMU."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class NetTools(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='zenith-net-host-')
        folder = Path(cls.temp.name)
        wrapper = folder/'host.c'
        wrapper.write_text(r'''
#define ZENITH_NATIVE_H
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/stat.h>
static long zenith_call(long,unsigned long,unsigned long,unsigned long);
static void zenith_exit(int c){exit(c);}
'''+'#define main net_main\n#include "'+str(ROOT/'userland/net-tools/main.c')+'"\n#undef main\n'+r'''
static FILE *response,*sink;
static char destination[2048],stage[2048];
static unsigned long written;
static int committed,connections;
static const char *last_error="mock transport failure";
static long zenith_call(long op,unsigned long a,unsigned long b,unsigned long c){
    if(op==1)return c<=4096 ? write(a,(void *)b,c) : -22;
    if(op==500){struct net_request *r=(void *)a;
        if(r->operation==6){unsigned long n=strlen(last_error);if(n>r->capacity)return -34;memcpy((void *)r->output,last_error,n);return n;}
        if(r->operation==1){const char *s="network: online\n";memcpy((void *)r->output,s,strlen(s));return strlen(s);}
        if(r->operation==2){if(strcmp((void *)r->input,"bad")==0)return -5;const char *s="10.0.2.2";memcpy((void *)r->output,s,strlen(s));return strlen(s);}
        if(r->operation==3)return 7;
        if(r->operation==4){response=fopen(getenv(connections++ && getenv("NET_NEXT") ? "NET_NEXT" : "NET_RESPONSE"),"rb");return response?10000:-5;}
        if(r->operation==5){
            const char *root=getenv("NET_DIRECTORY");const char *path=(void *)r->input;
            char local[2048];if(path[0]!='/'){snprintf(local,sizeof(local),"%s/%s",root,path);path=local;}
            if(strncmp(root,path,strlen(root))||path[strlen(root)]!='/')return -13;
            struct stat st;if(!lstat(path,&st)&&(!r->flags||!S_ISREG(st.st_mode))){last_error="destination exists";return -5;}
            snprintf(destination,sizeof(destination),"%s",path);snprintf(stage,sizeof(stage),"%s/staging",root);
            sink=fopen(stage,"wb");written=0;return sink?10001:-5;
        }
        if(r->operation==7){if(r->extra!=written||!sink||getenv("NET_COMMIT_FAIL"))return -5;fclose(sink);sink=0;if(rename(stage,destination))return -5;committed=1;return 0;}
        return -38;
    }
    if(op==501){if(a!=10000||!response)return -9;unsigned long chunk=strtoul(getenv("NET_CHUNK")?getenv("NET_CHUNK"):"65536",0,10);if(c>chunk)c=chunk;return fread((void *)b,1,c,response);}
    if(op==502){if(a==10000){const char *capture=getenv("NET_REQUEST");FILE *f=fopen(capture,"ab");fwrite((void *)b,1,c,f);fclose(f);return c;}if(a==10001&&sink){if(getenv("NET_WRITE_FAIL")&&written>=17)return -5;if(getenv("NET_SHORT_WRITE")&&c>17)c=17;unsigned long n=fwrite((void *)b,1,c,sink);written+=n;return n;}return -9;}
    if(op==503){if(a==10000&&response){fclose(response);response=0;return 0;}if(a==10001&&sink){fclose(sink);sink=0;if(!committed)unlink(stage);return 0;}return -9;}
    return -38;
}
int main(int argc,char **argv,char **env){return net_main(argc,argv,env);}
''')
        cls.binary=folder/'net-tools'
        compiler=shutil.which('gcc') or shutil.which('clang')
        if not compiler: raise RuntimeError('network client tests need gcc or clang')
        subprocess.run([compiler,'-O2',str(wrapper),'-o',str(cls.binary)],check=True,capture_output=True)

    @classmethod
    def tearDownClass(cls): cls.temp.cleanup()

    def run_client(self,*args,response=b'',chunk=65536,following=None,mock=None):
        folder=Path(self.temp.name)
        (folder/'response').write_bytes(response);(folder/'request').write_bytes(b'')
        env=dict(os.environ,NET_RESPONSE=str(folder/'response'),NET_DIRECTORY=str(folder),NET_CHUNK=str(chunk),NET_REQUEST=str(folder/'request'))
        if mock: env.update(mock)
        if following is not None:
            (folder/'following').write_bytes(following);env['NET_NEXT']=str(folder/'following')
        return subprocess.run([str(self.binary),*args],capture_output=True,env=env,timeout=5)

    def test_diagnostics_and_cli_bounds(self):
        self.assertEqual(b'network: online\n',self.run_client('ip','route').stdout)
        self.assertEqual(b'example.test address=10.0.2.2\n',self.run_client('nslookup','example.test').stdout)
        self.assertEqual(b'reply from example.test time=7ms\n',self.run_client('ping','example.test').stdout)
        self.assertEqual(b'TCP connected example.test:443\n',self.run_client('nc','-z','example.test','443',response=b'').stdout)
        for args in [('ip','bad'),('ifconfig','addr'),('nc','-z','host','0'),('nslookup','host','1.2.3.4','65536'),('fetch','--insecure','https://host/a','x')]:
            self.assertNotEqual(0,self.run_client(*args).returncode,args)

    def test_binary_body_and_split_headers(self):
        folder=Path(self.temp.name);out=folder/'body'
        data=bytes(range(256))*100
        response=b'HTTP/1.0 200 OK\r\nContent-Length: '+str(len(data)).encode()+b'\r\n\r\n'+data
        for chunk in (1,2,3,7,128,4096,65536):
            out.unlink(missing_ok=True)
            result=self.run_client('fetch','https://example.test:8443/a?b=c',str(out),response=response,chunk=chunk)
            self.assertEqual(0,result.returncode,result.stderr)
            self.assertEqual(data,out.read_bytes())
            self.assertIn(b'(verified TLS 1.2)',result.stdout)
            self.assertEqual(b'GET /a?b=c HTTP/1.1\r\nHost: example.test:8443\r\nConnection: close\r\nAccept-Encoding: identity\r\n\r\n',(folder/'request').read_bytes())

    def test_stdout_and_head(self):
        body=bytes(range(256))*256
        headers=b'HTTP/1.1 200 OK\r\nContent-Length: 65536\r\nX-Check: 1\r\n\r\n'
        for args in [[], ['-o','-'], ['-s']]:
            result=self.run_client('fetch',*args,'HTTPS://host?query=yes#local',response=headers+body)
            self.assertEqual((0,body,b''),(result.returncode,result.stdout,result.stderr))
        self.assertIn(b'GET /?query=yes HTTP/1.1',(Path(self.temp.name)/'request').read_bytes())
        hints=b'HTTP/1.1 103 Early Hints\r\nLink: </app.css>\r\n\r\n'
        result=self.run_client('fetch','-I','https://host/file',response=hints+headers,chunk=1)
        self.assertEqual((0,headers),(result.returncode,result.stdout))
        self.assertTrue((Path(self.temp.name)/'request').read_bytes().startswith(b'HEAD /file HTTP/1.1\r\n'))
        result=self.run_client('fetch','http://host/file',response=hints+headers+body)
        self.assertEqual((0,body),(result.returncode,result.stdout))
        for option in ('--max-filesize','--kernel'):
            args=[option,'32'] if option=='--max-filesize' else [option]
            result=self.run_client('fetch',*args,'http://host',response=b'HTTP/1.1 200 OK\r\nContent-Length: 1048577\r\n\r\n')
            self.assertNotEqual(0,result.returncode)
            self.assertEqual(b'',result.stdout)

    def test_remote_name_output_and_cli_bounds(self):
        folder=Path(self.temp.name)
        response=b'HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nxyz'
        for flags,file in [(['-sO'],'original'),(['-so','a'],'a'),(['--output','b'],'b')]:
            result=self.run_client('fetch',*flags,'https://host/path/original?x=/other',response=response)
            self.assertEqual(0,result.returncode,result.stderr)
            self.assertEqual(b'xyz',(folder/file).read_bytes())
            self.assertEqual(b'',result.stdout)
        for args in [('-O','https://host/'),('-O','https://host/a/..'),('-o',),('-O','-o','file','https://host/a'),('--max-filesize','33558529','https://host/a')]:
            result=self.run_client('fetch',*args,response=response)
            self.assertNotEqual(0,result.returncode,args)
            self.assertEqual(b'',(folder/'request').read_bytes())

    def test_redirect_targets_and_budgets(self):
        response=b'HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok'
        for location,target,host in [(b'../b?x=1#local',b'/b?x=1',b'a:443'),(b'?b',b'/dir/original?b',b'a:443'),
                                    (b'#part',b'/dir/original?z',b'a:443'),(b'//b/x',b'/x',b'b:443'),(b'https://b:9443/x',b'/x',b'b:9443')]:
            redirect=b'HTTP/1.1 307 Temporary Redirect\r\nLocation: '+location+b'\r\n\r\n'
            result=self.run_client('fetch','-L','https://a/dir/original?z',response=redirect,following=response)
            self.assertEqual((0,b'ok'),(result.returncode,result.stdout),result.stderr)
            self.assertIn(b'GET '+target+b' HTTP/1.1\r\nHost: '+host+b'\r\n',(Path(self.temp.name)/'request').read_bytes())
        redirect=b'HTTP/1.1 302 Found\r\nLocation: next\r\n\r\n'
        result=self.run_client('fetch','-L','http://a/x/y/..',response=redirect,following=response)
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertIn(b'GET /x/next HTTP/1.1',(Path(self.temp.name)/'request').read_bytes())
        target=Path(self.temp.name)/'preserve-redirect'
        target.write_bytes(b'original')
        for location,requests in [(b'http://other/a',1),(b'file:///x',1),(b'/again',6),(b'',1)]:
            redirect=b'HTTP/1.1 302 Found\r\nLocation: '+location+b'\r\n\r\n'
            result=self.run_client('fetch','--replace','--location','https://a/file',str(target),response=redirect)
            self.assertNotEqual(0,result.returncode)
            self.assertEqual(requests,(Path(self.temp.name)/'request').read_bytes().count(b'GET '))
            self.assertEqual(b'original',target.read_bytes())

    def test_chunk_framing_and_limits(self):
        headers=b'HTTP/1.1 200 OK\r\nTransfer-Encoding: Chunked\r\n\r\n'
        body=b'\0\xff\x80'+b'x'*4096
        encoded=b'3;flag=on\r\n'+body[:3]+b'\r\n1000\r\n'+body[3:]+b'\r\n0\r\nX-End: yes\r\n\r\n'
        for chunk in (1,2,7,256,4096,65536):
            result=self.run_client('fetch','--max-filesize',str(len(body)),'https://host/chunks',response=headers+encoded,chunk=chunk)
            self.assertEqual((0,body),(result.returncode,result.stdout),result.stderr)
        out=Path(self.temp.name)/'chunk-keep'
        for invalid in [b'Z\r\n',b'1\r\na!',b'1\r\na\r\n',b'0\r\n',b'0\r\nbad trailer\r\n\r\n',
                        b'0\r\nContent-Length: 0\r\n\r\n',b'1;key="oops\r\na\r\n0\r\n\r\n',
                        b'0\r\n\r\nsmuggled',encoded]:
            out.write_bytes(b'KEEP')
            result=self.run_client('fetch','--replace','--max-filesize','4098','http://host','--output',str(out),response=headers+invalid)
            self.assertNotEqual(0,result.returncode,invalid[:30])
            self.assertEqual(b'KEEP',out.read_bytes())
            self.assertFalse((out.parent/'staging').exists())

    def test_short_staging_writes_and_transaction_failures(self):
        folder=Path(self.temp.name)
        out=folder/'transaction'
        body=bytes(range(100))
        response=b'HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n'+body
        for failure in ('NET_WRITE_FAIL','NET_COMMIT_FAIL',None):
            out.write_bytes(b'old')
            env={'NET_SHORT_WRITE':'1'}
            if failure: env[failure]='1'
            result=self.run_client('fetch','--replace','-o',str(out),'https://host',response=response,mock=env)
            self.assertEqual((1,b'old') if failure else (0,body),(result.returncode,out.read_bytes()))
            self.assertFalse((folder/'staging').exists())

    def test_empty_download_and_replace_policy(self):
        out=Path(self.temp.name)/'empty'
        out.unlink(missing_ok=True)
        response=b'HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n'
        self.assertEqual(0,self.run_client('fetch','http://example.test',str(out),response=response).returncode)
        self.assertEqual(b'',out.read_bytes())
        out.write_bytes(b'KEEP')
        self.assertNotEqual(0,self.run_client('fetch','http://example.test',str(out),response=response).returncode)
        self.assertEqual(b'KEEP',out.read_bytes())
        self.assertEqual(0,self.run_client('fetch','--replace','http://example.test',str(out),response=response).returncode)
        self.assertEqual(b'',out.read_bytes())

    def test_malformed_or_partial_http_preserves_existing_destination(self):
        out=Path(self.temp.name)/'keep'
        responses=[b'HTTP/1.0 200 OK\nINJECTED\r\nContent-Length: 0\r\n\r\n',
                   b'HTTP/1.0 200 OK\r\nX-Bad: value\nINJECTED\r\nContent-Length: 0\r\n\r\n',
                   b'HTTP/1.0 404 Not Found\r\nContent-Length: 0\r\n\r\n',
                   b'HTTP/1.0 302 Found\r\nLocation: /other\r\nContent-Length: 0\r\n\r\n',
                   b'HTTP/1.0 200 OK\r\n\r\n',
                   b'HTTP/1.0 200 OK\r\nContent-Length: 3\r\n\r\na',
                   b'HTTP/1.0 200 OK\r\nContent-Length: 1\r\n\r\nab',
                   b'HTTP/1.0 200 OK\r\nContent-Length: 0\r\nContent-Length: 0\r\n\r\n',
                   b'HTTP/1.0 200 OK\r\nContent-Length: +1\r\n\r\na',
                   b'HTTP/1.0 200 OK\r\nContent-Length: 999999999999999999999\r\n\r\n',
                   b'HTTP/1.0 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 0\r\n\r\n',
                   b'HTTP/1.0 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 0\r\n\r\n',
                   b'HTTP/1.0 200 OK\r\n folded: value\r\nContent-Length: 0\r\n\r\n',
                   b'HTTP/1.0 200 OK\r\n'+b'x'*8192]
        for response in responses:
            out.write_bytes(b'KEEP')
            result=self.run_client('fetch','--replace','http://example.test/a',str(out),response=response)
            self.assertNotEqual(0,result.returncode,response[:100]);self.assertEqual(b'KEEP',out.read_bytes())
            self.assertFalse((Path(self.temp.name)/'staging').exists())

    def test_url_injection_and_unsupported_schemes(self):
        for url in ('file:///root/key','https://user:pass@host/a','http://host:0/a','http://host:65536/a','http://[::1]/a','http://host/a\r\nInjected:yes','http://host/a b','https://host/a\\b','http://'+('x'*254)+'/a'):
            result=self.run_client('fetch',url,str(Path(self.temp.name)/'bad'))
            self.assertNotEqual(0,result.returncode,url)
            self.assertEqual(b'',(Path(self.temp.name)/'request').read_bytes())


if __name__=='__main__':unittest.main()
