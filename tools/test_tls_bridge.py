#!/usr/bin/env python3
"""Exercise the exact C bridge against OpenSSL in-memory TLS (no network dependency)."""
import ctypes
from pathlib import Path
import ssl
import sys
import tempfile
from tls_fixture import certificates, context

WANT = (-0x6900, -0x6880)
def main():
    library = ctypes.CDLL(sys.argv[1])
    library.zenith_tls_open.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p] + [ctypes.c_int]*7
    for name in ('push','pull','read','write'):
        getattr(library, 'zenith_tls_'+name).argtypes = [ctypes.c_void_p, ctypes.c_size_t]
    checks=0
    def check(value, label):
        nonlocal checks
        assert value, label; checks+=1; print('ok   '+label,flush=True)
    with tempfile.TemporaryDirectory(prefix='zenith-tls-') as temporary:
        check(library.zenith_tls_selftest()==0,"native primitive known-answer tests")
        ca,_=certificates(temporary); buffer=ctypes.create_string_buffer(32768)
        def open_client(host='mirror.home', trust=ca, year=2026, web=False):
            pem = None if trust is None else trust + b'\0'
            return library.zenith_tls_open(pem,len(pem or b''),host.encode(),year,10,3,7,6,40,web)
        def session(name='server',host='mirror.home',trust=ca,web=False,curve=None):
            check(open_client(host,trust,web=web)==0,'client initialized '+name+' '+host)
            incoming,outgoing=ssl.MemoryBIO(),ssl.MemoryBIO()
            server_context=context(temporary,name)
            if curve: server_context.set_ecdh_curve(curve)
            server=server_context.wrap_bio(incoming,outgoing,server_side=True)
            client_done=server_done=False; result=0
            for _ in range(10000):
                if not client_done:
                    result=library.zenith_tls_handshake()
                    if result==0: client_done=True
                    elif result not in WANT: break
                count=library.zenith_tls_pull(buffer,len(buffer))
                if count>0: incoming.write(buffer.raw[:count])
                if not server_done:
                    try: server.do_handshake();server_done=True
                    except ssl.SSLWantReadError: pass
                    except ssl.SSLError: break
                if outgoing.pending:
                    data=outgoing.read();check(library.zenith_tls_push(data,len(data))==len(data),'bounded encrypted input')
                if client_done and server_done: break
            return result,server,incoming,outgoing
        result,server,incoming,outgoing=session()
        check(result==0 and library.zenith_tls_verify()==0,'chain/name/date verified')
        request=b'hello authenticated server'
        check(library.zenith_tls_write(request,len(request))==len(request),'TLS request encrypted')
        count=library.zenith_tls_pull(buffer,len(buffer));incoming.write(buffer.raw[:count])
        check(server.read()==request,'OpenSSL receives exact request')
        block = bytes(range(256)) * 8
        written = 0
        while written < 32768:
            result = library.zenith_tls_write(block, len(block))
            if result == -0x6880: break
            assert result == len(block), result
            written += result
        check(result == -0x6880 and written > 0, 'full encrypted output queue requests write')
        count = library.zenith_tls_pull(buffer, len(buffer))
        incoming.write(buffer.raw[:count])
        check(library.zenith_tls_write(block, len(block)) == len(block),
              'pending write completes after drain without peer input')
        count = library.zenith_tls_pull(buffer, len(buffer))
        incoming.write(buffer.raw[:count])
        sent = bytearray()
        while len(sent) < written + len(block): sent.extend(server.read())
        check(sent == block * (written // len(block) + 1), 'resumed write delivered exactly once')
        payload=b'Zenith TLS payload '*1000
        server.write(payload);wire=outgoing.read()
        check(library.zenith_tls_push(wire,len(wire))==len(wire),'encrypted response queued')
        actual=b''
        while len(actual)<len(payload):
            count=library.zenith_tls_read(buffer,len(buffer));check(count>0,'TLS response decrypts');actual+=buffer.raw[:count]
        check(actual==payload,'streamed response exact bytes')
        payload = bytes(range(256)) * 257
        assert server.write(payload) == len(payload)
        wire = outgoing.read()
        capacity = len(buffer)
        assert library.zenith_tls_push(wire[:capacity], capacity) == capacity
        count = library.zenith_tls_read(buffer, capacity)
        assert count > 0
        actual = bytearray(buffer.raw[:count])
        pending = library.zenith_tls_pending_input()
        check(0 < pending < capacity, 'partial record drain retains unread ciphertext')
        room = capacity - pending
        check(library.zenith_tls_push(wire[capacity:], room + 1) == -1,
              'partial queue rejects overflow without consuming bytes')
        assert library.zenith_tls_push(wire[capacity:], room) == room
        pushed = capacity + room
        while len(actual) < len(payload):
            count = library.zenith_tls_read(buffer, capacity)
            if count == -0x6900:
                assert library.zenith_tls_pending_input() == 0 and pushed < len(wire)
                chunk = wire[pushed:pushed + capacity]
                assert library.zenith_tls_push(chunk, len(chunk)) == len(chunk)
                pushed += len(chunk)
            else:
                assert count > 0, count
                actual.extend(buffer.raw[:count])
        check(actual == payload and library.zenith_tls_pending_input() == 0,
              'whole windows and fragmented records decrypt byte for byte')
        server.write(b'tamper test');wire=bytearray(outgoing.read());wire[-1]^=1
        library.zenith_tls_push(bytes(wire),len(wire))
        result=library.zenith_tls_read(buffer,len(buffer))
        check(result<0 and result not in WANT,'tampered encrypted record rejected')
        library.zenith_tls_close()
        alien,_=certificates(Path(temporary)/'alien')
        for name,host,trust in [('server','mirror.home',(Path(temporary)/'ca-expired.pem').read_bytes()),('server','mirror.home',(Path(temporary)/'ca-future.pem').read_bytes()),('server','mirror.home',alien),('expired','mirror.home',ca),('future','mirror.home',ca),('server','wrong.home',ca)]:
            result,*_=session(name,host,trust)
            check(result not in (0,*WANT),'reject '+name+' '+host);library.zenith_tls_close()
        check(open_client(trust=b'not a certificate')!=0,'malformed trust rejected')
        check(open_client(trust=None)!=0,'repository TLS requires explicit local trust')
        check(open_client()==0 and library.zenith_tls_test_root_count()==1,'repository trusts only configured CA')
        library.zenith_tls_close()
        public_roots=(Path(__file__).resolve().parents[1]/'vendor/web-pki/cacert.pem').read_bytes().count(b'-----BEGIN CERTIFICATE-----')
        check(open_client(trust=None,web=True)==0 and library.zenith_tls_test_root_count()==public_roots,'all bundled web roots loaded without a local CA')
        library.zenith_tls_close()
        check(open_client(trust=b'',web=True)!=0,'empty configured web trust rejected')
        check(open_client(trust=b'not a certificate',web=True)!=0,'malformed configured web trust rejected')
        result,*_=session(trust=None,web=True)
        check(result not in (0,*WANT),'public roots reject untrusted private server')
        library.zenith_tls_close()
        for curve in ('X25519','secp384r1'):
            result,*_=session(web=True,curve=curve)
            check(result==0 and library.zenith_tls_verify()==0,'web plus configured trust verifies '+curve)
            library.zenith_tls_close()
        check(open_client(year=1970)!=0,'invalid clock rejected')
        library.zenith_tls_test_entropy_fault(1)
        check(open_client()!=0,'missing entropy rejects initialization')
        library.zenith_tls_test_entropy_fault(0)
        check(open_client()==0,'reuse after failures')
        check(library.zenith_tls_push(buffer,32769)==-1,'input overflow rejected before copy')
        library.zenith_tls_close()
    print(f'RESULT: {checks}/{checks} TLS bridge checks passed')
if __name__=='__main__':main()
