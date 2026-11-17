#!/usr/bin/env python3
"""Local installer ISOs: seed a locally built ISO with the published mirror's trust and apps.

The distributor publishes seeded installer ISOs only for its own kernel releases. This makes the
same kind of ISO on a workstation from any local build (a branch, a debug kernel), so a test
install starts with the package key, TLS CA, mirror and the core apps, like a published one.

  local-installer.py setup https://zenith-stable.net.local [--package-key PEM] [--tls-ca PEM]
  local-installer.py [BASE] [OUTPUT] [--refresh]

`setup` pins the mirror once in ~/.config/zenith/local-installer/. A key or CA not given as a
file is fetched (the CA from the TLS handshake, the key from the mirror over that CA) and its
SHA256 printed: compare both with the server (`sha256sum ~/signing/public.pem` and the
setup's --tls-ca PEM) before trusting ISOs made with them.

Building downloads the mirror's signed core index over the pinned CA, verifies it with the
pinned key (PackageTool index-check), fetches the newest version of every core package into
~/.cache/zenith/local-installer/ (checked against the index's SHA256 and the key), then
seeds BASE (default output-x64/Zenith.iso) with tools/seed-iso.py into OUTPUT (default
/var/tmp/zenith-local-installer.iso). Cached archives are reused; --refresh refetches them.
"""
import argparse, functools, hashlib, json, re, shutil, ssl, subprocess, sys, tempfile, urllib.request
from pathlib import Path

print = functools.partial(print, flush=True)
ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / 'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'
CONFIG = Path.home() / '.config/zenith/local-installer'
CACHE = Path.home() / '.cache/zenith/local-installer'
NAME = re.compile(r'[a-z0-9][a-z0-9._-]{0,63}')
VERSION = re.compile(r'[0-9]+(\.[0-9]+)*')
EXCLUDED = {'zenith-kernel', 'zenith-gui-probe'}  # as tools/seed-iso.py: the ISO brings its kernel
REPOSITORY = 'core'


def digest(data): return hashlib.sha256(data).hexdigest()


def version_key(text): return tuple(int(part) for part in text.split('.'))


def fetch(url, context, limit=64 << 20):
    with urllib.request.urlopen(url, context=context, timeout=60) as response:
        data = response.read(limit + 1)
    if len(data) > limit: raise SystemExit('too large: ' + url)
    return data


def settings():
    path = CONFIG / 'mirror.json'
    if not path.is_file(): raise SystemExit('no pinned mirror: run `local-installer.py setup https://MIRROR` first')
    mirror = json.loads(path.read_text())['mirror']
    key, ca = CONFIG / 'package-key.pem', CONFIG / 'tls-ca.pem'
    for file in (key, ca):
        if not file.is_file(): raise SystemExit('missing ' + str(file) + ': run setup again')
    return mirror, key, ca


def setup(args):
    mirror = args.mirror.rstrip('/')
    match = re.fullmatch(r'https://([A-Za-z0-9.-]+)(?::([0-9]+))?', mirror)
    if not match: raise SystemExit('the mirror must be an https://HOST[:PORT] URL')
    CONFIG.mkdir(parents=True, exist_ok=True)
    if args.tls_ca: ca = args.tls_ca.read_bytes()
    else:
        ca = ssl.get_server_certificate((match.group(1), int(match.group(2) or 443))).encode()
        print('fetched the TLS certificate from the handshake (trust on first use)')
    (CONFIG / 'tls-ca.pem').write_bytes(ca)
    context = ssl.create_default_context(cafile=str(CONFIG / 'tls-ca.pem'))
    index = fetch(mirror + '/' + REPOSITORY + '/index-v3.zri', context, 1 << 20)
    header = index.split(b'\n', 1)[0].decode().split('\t')
    if len(header) != 5 or header[0] != 'ZENITH-SIGNED-INDEX': raise SystemExit('not a signed Zenith index')
    if args.package_key: key = args.package_key.read_bytes()
    else:
        entry = next(p for p in (line.split('\t') for line in index.decode(errors='replace').splitlines()[2:]) if len(p) > 2)
        key = fetch(mirror + '/' + REPOSITORY + '/' + entry[1] + '/' + entry[2] + '/public.pem', context, 4096)
        print('fetched the package key from the mirror')
    if digest(key) != header[2]: raise SystemExit('the package key does not match the key the index is signed with')
    (CONFIG / 'package-key.pem').write_bytes(key)
    (CONFIG / 'mirror.json').write_text(json.dumps({'mirror': mirror}) + '\n')
    print('pinned ' + mirror + '\n  package key SHA256 ' + digest(key) + '\n  TLS CA      SHA256 ' + digest(ca))
    print('compare both with the server before trusting ISOs made with them')


def packages(mirror, key, ca, refresh):
    """Newest version of every core package, verified, in a served-root layout under CACHE."""
    context = ssl.create_default_context(cafile=str(ca))
    index = fetch(mirror + '/' + REPOSITORY + '/index-v3.zri', context, 1 << 20)
    with tempfile.TemporaryDirectory(prefix='zenith-local-index-') as work:
        path = Path(work) / 'index-v3.zri'; path.write_bytes(index)
        checked = subprocess.run(['dotnet', str(TOOL), 'index-check', str(path), str(key), REPOSITORY], capture_output=True, text=True)
        if checked.returncode: raise SystemExit('index verification failed: ' + (checked.stderr or checked.stdout).strip())
        print(checked.stdout.strip())
    _, body = index.split(b'\n', 1)
    length = int(index.split(b'\n', 1)[0].split(b'\t')[3])
    newest = {}
    for line in body[:length].decode().splitlines()[1:]:
        kind, name, version, size, sha = line.split('\t')[:5]
        if name in EXCLUDED or not NAME.fullmatch(name) or not VERSION.fullmatch(version): continue
        if name not in newest or version_key(version) > version_key(newest[name][0]): newest[name] = (version, int(size), sha)
    root = CACHE / 'packages'
    if (root / REPOSITORY).exists(): shutil.rmtree(root / REPOSITORY)  # the seed takes every package here: only today's newest
    for name, (version, size, sha) in sorted(newest.items()):
        cached = CACHE / 'archives' / (name + '-' + version + '-' + sha[:16] + '.zpk')
        if refresh or not cached.is_file() or digest(cached.read_bytes()) != sha:
            data = fetch(mirror + '/' + REPOSITORY + '/' + name + '/' + version + '/' + name + '.zpk', context)
            if len(data) != size or digest(data) != sha: raise SystemExit(name + ' ' + version + ' does not match the signed index')
            cached.parent.mkdir(parents=True, exist_ok=True); cached.write_bytes(data)
            print('fetched ' + name + ' ' + version)
        checked = subprocess.run(['dotnet', str(TOOL), 'check', str(cached), str(key)], capture_output=True, text=True)
        if checked.returncode: raise SystemExit(name + ' ' + version + ': ' + (checked.stderr or checked.stdout).strip())
        target = root / REPOSITORY / name / version; target.mkdir(parents=True)
        shutil.copyfile(cached, target / (name + '.zpk'))
    print(str(len(newest)) + ' packages: ' + ', '.join(n + ' ' + v[0] for n, v in sorted(newest.items())))
    return root


def build(args):
    mirror, key, ca = settings()
    if not args.base.is_file(): raise SystemExit('no ISO at ' + str(args.base) + ': build the kernel first')
    if not TOOL.is_file():
        subprocess.run(['dotnet', 'build', str(ROOT / 'tools/Zenith.PackageTool'), '-c', 'Release', '--nologo'], check=True, stdout=subprocess.DEVNULL)
    root = packages(mirror, key, ca, args.refresh)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run([sys.executable, '-B', str(ROOT / 'tools/seed-iso.py'), str(args.base), str(args.output),
                    '--package-key', str(key), '--tls-ca', str(ca), '--mirror', mirror,
                    '--packages-root', str(root), '--repository', REPOSITORY], check=True)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == 'setup':
        parser = argparse.ArgumentParser(prog='local-installer.py setup', description='Pin the package mirror once.')
        parser.add_argument('mirror', help='https://HOST of the package mirror')
        parser.add_argument('--package-key', type=Path, help='the mirror\'s public.pem (default: fetch it)')
        parser.add_argument('--tls-ca', type=Path, help='PEM CA of the mirror\'s HTTPS certificate (default: fetch it)')
        setup(parser.parse_args(sys.argv[2:])); return
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('base', type=Path, nargs='?', default=ROOT / 'output-x64/Zenith.iso', help='local Zenith ISO (default output-x64/Zenith.iso)')
    parser.add_argument('output', type=Path, nargs='?', default=Path('/var/tmp/zenith-local-installer.iso'))
    parser.add_argument('--refresh', action='store_true', help='download the packages again')
    build(parser.parse_args())


if __name__ == '__main__': main()
