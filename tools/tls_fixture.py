"""Disposable private CA and TLS endpoints for both native and bridge tests."""
from pathlib import Path
import hashlib
import ssl
import subprocess

def certificates(directory):
    directory = Path(directory); directory.mkdir(parents=True, exist_ok=True)
    # openssl ca gives explicit validity dates, independent of the build host RTC.
    def run(*args):
        return subprocess.run(['openssl', *args], check=True, capture_output=True).stdout
    run('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(directory/'ca.key'),
        '-out', str(directory/'initial-ca.pem'), '-days', '3650', '-subj', '/CN=Zenith disposable test CA')
    (directory/'index').write_text(''); (directory/'serial').write_text('01\n')
    config = directory/'ca.conf'
    config.write_text(f'''[ca]
default_ca=default
[default]
database={directory}/index
serial={directory}/serial
new_certs_dir={directory}
private_key={directory}/ca.key
certificate={directory}/initial-ca.pem
default_md=sha256
policy=policy
unique_subject=no
[policy]
commonName=supplied
[server]
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature
extendedKeyUsage=serverAuth
subjectAltName=DNS:mirror.home,IP:10.0.2.2,IP:127.0.0.1
[root]
basicConstraints=critical,CA:TRUE
keyUsage=critical,keyCertSign,cRLSign
''')
    run('req', '-new', '-key', str(directory/'ca.key'), '-out', str(directory/'ca.csr'), '-subj', '/CN=Zenith disposable test CA')
    run('ca', '-batch', '-selfsign', '-config', str(config), '-extensions', 'root', '-in', str(directory/'ca.csr'),
        '-out', str(directory/'ca.pem'), '-startdate', '20200101000000Z', '-enddate', '20300101000000Z', '-notext')
    for name,start,end in [('ca-expired','20200101000000Z','20210101000000Z'),
                            ('ca-future','20290101000000Z','20300101000000Z')]:
        run('ca', '-batch', '-selfsign', '-config', str(config), '-extensions', 'root', '-in', str(directory/'ca.csr'),
            '-out', str(directory/(name+'.pem')), '-startdate', start, '-enddate', end, '-notext')
    run('req', '-new', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(directory/'server.key'),
        '-out', str(directory/'server.csr'), '-subj', '/CN=mirror.home')
    for name, start, end in [('server','20200101000000Z','20300101000000Z'),
                             ('expired','20200101000000Z','20210101000000Z'),
                             ('future','20290101000000Z','20300101000000Z')]:
        run('ca', '-batch', '-config', str(config), '-extensions', 'server', '-in', str(directory/'server.csr'),
            '-out', str(directory/(name+'.pem')), '-startdate', start, '-enddate', end, '-notext')
    ca = (directory/'ca.pem').read_bytes()
    return ca, hashlib.sha256(ca).hexdigest()

def context(directory, name='server'):
    result = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    result.minimum_version = result.maximum_version = ssl.TLSVersion.TLSv1_2
    result.set_ciphers('ECDHE-RSA-AES128-GCM-SHA256')
    result.load_cert_chain(str(Path(directory)/(name+'.pem')), str(Path(directory)/'server.key'))
    return result
