#!/usr/bin/env python3
"""Disposable TCP failure/recovery and repeated HTTPS connection regression."""
import argparse
import importlib.util
from pathlib import Path
import threading
from tls_fixture import certificates, context

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('lab', HERE/'network-smoke.py')
lab = importlib.util.module_from_spec(spec); spec.loader.exec_module(lab)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--iso', type=Path, required=True)
    parser.add_argument('--keep', type=Path, required=True)
    args = parser.parse_args(); args.keep.mkdir(parents=True, exist_ok=False)
    ca, fingerprint = certificates(args.keep/'certificates')
    http = lab.TcpServer(('127.0.0.1', 0), lab.Http); http.ca = ca
    https = lab.TcpServer(('127.0.0.1', 0), lab.Https)
    https.tls = context(args.keep/'certificates', 'server'); https.ca = ca
    https.handle_error = lambda *args: None
    for server in (http, https):
        threading.Thread(target=server.serve_forever, daemon=True).start()
    m = None
    try:
        m = lab.h.Machine(args.keep/'guest', iso=args.iso.resolve(),
            network=['-netdev','user,id=net0','-device','e1000e,netdev=net0'])
        assert m.wait('headless ready', timeout=180), 'boot'
        assert m.wait('network: online address=10.0.2.15', timeout=60), 'DHCP'
        def status(command, label, code=0, timeout=90):
            offset = len(m.log()); m.send(command+'; echo '+label+'=$?\r')
            assert m.wait(label+'='+str(code), offset, timeout), label
            print('ok '+label, flush=True); return m.log()[offset:]
        # An unused address on the isolated slirp subnet cannot complete ARP/SYN.
        # Closed host ports are unsuitable: slirp can synthesize the handshake.
        output = status('nc -z 10.0.2.99 443', 'TCP_EXHAUSTED', 1)
        assert 'TCP connect retry 2/2:' in output, 'must exercise failed handshake'
        assert '10.0.2.99:443' in output and 'after 2 attempts' in output, 'endpoint diagnostic'
        url = f'http://10.0.2.2:{http.server_address[1]}'
        status(f'fetch {url}/ca.pem /tmp/ca.pem', 'TCP_AFTER_FAILURE')
        status(f'tls trust /tmp/ca.pem {fingerprint}', 'TLS_TRUST')
        for index in range(12):
            status(f'fetch https://10.0.2.2:{https.server_address[1]}/ca.pem /tmp/seq-{index}', f'HTTPS_{index}')
        offset = len(m.log()); m.send('poweroff\r')
        assert m.wait('all filesystems unmounted', offset, 60), 'clean shutdown'
        assert m.process.wait(timeout=30) == 0, 'poweroff'
        print('PASS bounded retry/failure, recovery and 12 sequential verified HTTPS fetches', flush=True)
    finally:
        if m: m.stop()
        for server in (http, https): server.shutdown(); server.server_close()

if __name__ == '__main__': main()
