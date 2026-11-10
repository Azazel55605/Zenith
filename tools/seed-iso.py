#!/usr/bin/env python3
"""Make an installer ISO: add a seed (/boot/zenith-seed.zsd) to a Zenith ISO.

The seed carries the package signing key, the TLS CA and the mirror an installed system should
trust, and the signed packages Sunrise installs with it (Meridian, Polaris, the session, fonts...).
A live boot of the seeded ISO pins that trust in its /etc; Sunrise copies it to the installed root
and installs the packages there. Format: src/Core/Packages/SeedBundle.cs.

Packages come from a served package root (the newest version of every package in a repository,
as the distributor publishes them) or are built here from the committed recipes with a key:

  seed-iso.py output-x64/Zenith.iso zenith-installer.iso --package-key ~/signing/public.pem \\
      --tls-ca ca.pem --mirror https://zenith-stable.net.local --packages-root /srv/zenith/packages
  seed-iso.py output-x64/Zenith.iso zenith-installer.iso --keys ~/signing --build core ...
"""
import argparse, hashlib, json, re, shutil, subprocess, sys, tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / 'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'
NAME = re.compile(r'[a-z0-9][a-z0-9._-]{0,63}')
# Packages an installer never seeds: the kernel is the ISO's own, and probes are for tests.
EXCLUDED = {'zenith-kernel', 'zenith-gui-probe'}


def version_key(text):
    return tuple(int(part) if part.isdigit() else 0 for part in text.split('.'))


def build_seed(mirror, files):
    header = 'ZENITH-SEED\t1\n'
    if mirror and not mirror.startswith('https://'): raise ValueError('the mirror must be an https URL')
    if mirror: header += 'mirror\t' + mirror.rstrip('/') + '\n'
    for kind, name, data in files:
        if not NAME.fullmatch(name): raise ValueError('invalid seed file name: ' + name)
        header += '\t'.join(('file', kind, name, str(len(data)), hashlib.sha256(data).hexdigest())) + '\n'
    header += 'end\n'
    if len(header.encode()) > 16384: raise ValueError('seed header too large')
    return header.encode() + b''.join(data for _, _, data in files)


def published(root, repository):
    """The newest published archive of every package in ROOT/REPOSITORY."""
    found = []
    for package in sorted((Path(root) / repository).iterdir()):
        if not package.is_dir() or package.name in EXCLUDED: continue
        versions = [v for v in package.iterdir() if v.is_dir() and re.fullmatch(r'[0-9]+(\.[0-9]+)*', v.name)]
        if not versions: continue
        newest = max(versions, key=lambda v: version_key(v.name))
        archive = newest / (package.name + '.zpk')
        if archive.is_file(): found.append((package.name, newest.name, archive))
    return found


def tag_version(key):
    tags = subprocess.run(['git', 'tag', '-l', key + '/v*'], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split()
    versions = [t.split('/v', 1)[1] for t in tags if re.fullmatch(r'[0-9]+(\.[0-9]+)*', t.split('/v', 1)[1])]
    return max(versions, key=version_key) if versions else '0.0.1'


def build_recipes(repository, keys, work):
    """Builds every data recipe of REPOSITORY from this checkout, at its newest tag's version."""
    subprocess.run(['dotnet', 'build', str(ROOT / 'tools/Zenith.PackageTool'), '-c', 'Release', '--nologo'], check=True, stdout=subprocess.DEVNULL)
    recipes = json.loads((ROOT / 'deploy/distributor/recipes.json').read_text())
    built = []
    for key, recipe in recipes.items():
        if recipe['kind'] != 'data' or recipe['repository'] != repository or recipe['name'] in EXCLUDED: continue
        name, version = recipe['name'], tag_version(key)
        payload = ROOT / recipe['payload']
        generate = recipe.get('generate')
        if generate in ('manuals', 'fonts', 'icons', 'native'):
            payload = work / ('payload-' + name)
            if payload.exists(): shutil.rmtree(payload)
            if generate == 'manuals': command = ['dotnet', 'run', '--project', str(ROOT / 'tools/Zenith.ManualBuilder'), '--', str(payload)]
            elif generate == 'fonts': command = [sys.executable, '-B', str(ROOT / 'tools/build-font-package.py'), str(payload)]
            elif generate == 'icons': command = [sys.executable, '-B', str(ROOT / 'tools/build-ui-icons.py'), '--package', str(payload)]
            else:
                command = [sys.executable, '-B', str(ROOT / 'tools/build-userland.py'), str(payload), '--version', version]
                if 'program' in recipe: command += ['--program', recipe['program']]
            subprocess.run(command, cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
        elif generate is not None: raise ValueError('unknown package generator: ' + generate)
        mode = 'build-meta' if recipe.get('meta') else 'build-native' if generate == 'native' else 'build'
        bundle = work / ('bundle-' + name)
        if bundle.exists(): shutil.rmtree(bundle)
        subprocess.run(['dotnet', str(TOOL), mode, name, version, str(payload), str(bundle), '--repo', repository], check=True, stdout=subprocess.DEVNULL)
        if recipe.get('requires'):
            manifest = bundle / 'manifest.tsv'
            manifest.write_text(manifest.read_text() + ''.join('requires\t' + d + '\t' + c + '\n' for d, c in recipe['requires']))
        archive = work / (name + '.zpk')
        subprocess.run(['dotnet', str(TOOL), 'seal', str(bundle), str(Path(keys) / 'private.pem'), str(archive)], check=True, stdout=subprocess.DEVNULL)
        built.append((name, version, archive))
        print('built ' + name + ' ' + version, flush=True)
    return built


def limine_tool(work):
    """The limine CLI the SDK built for this checkout, or one built from the SDK's bundled source."""
    for candidate in sorted(ROOT.glob('obj/*/net10.0/linux-x64/cosmos/limine/limine')):
        if candidate.is_file(): return candidate
    bundled = sorted(Path.home().glob('.nuget/packages/cosmos.build.common/*/tools/Limine'))
    if not bundled: raise SystemExit('limine CLI not found: build the kernel first')
    directory = Path(work) / 'limine'; shutil.copytree(bundled[-1], directory)
    subprocess.run(['make', '-C', str(directory)], check=True, capture_output=True)
    return directory / 'limine'


def write_iso(base, output, seed):
    """Rebuilds BASE with the seed added, the way the Cosmos SDK builds the ISO (xorriso hybrid
    BIOS/UEFI image, then limine bios-install)."""
    with tempfile.TemporaryDirectory(prefix='zenith-iso-') as temporary:
        tree = Path(temporary) / 'tree'
        subprocess.run(['xorriso', '-osirrox', 'on', '-indev', str(base), '-extract', '/', str(tree)], check=True, capture_output=True)
        for path in tree.rglob('*'): path.chmod(0o755 if path.is_dir() else 0o644)
        if not (tree / 'boot/limine/limine-bios-cd.bin').is_file(): raise SystemExit('base is not a Limine Zenith ISO')
        (tree / 'boot/zenith-seed.zsd').write_bytes(seed)
        output = Path(output)
        if output.exists(): output.unlink()
        subprocess.run(['xorriso', '-as', 'mkisofs', '-R', '-J', '-b', 'boot/limine/limine-bios-cd.bin', '-no-emul-boot',
                        '-boot-load-size', '4', '-boot-info-table', '--efi-boot', 'boot/limine/limine-uefi-cd.bin',
                        '-efi-boot-part', '--efi-boot-image', '--protective-msdos-label', str(tree), '-o', str(output)],
                       check=True, capture_output=True)
        subprocess.run([str(limine_tool(temporary)), 'bios-install', str(output)], check=True, capture_output=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('base', type=Path, help='a Zenith ISO (output-x64/Zenith.iso)')
    parser.add_argument('output', type=Path)
    parser.add_argument('--package-key', type=Path, help='public.pem the packages are signed with (default: KEYS/public.pem)')
    parser.add_argument('--keys', type=Path, help='signing directory (private.pem) for --build')
    parser.add_argument('--tls-ca', type=Path, help='PEM CA the mirror\'s HTTPS certificate chains to')
    parser.add_argument('--mirror', help='package mirror base URL the installed system uses')
    source = parser.add_mutually_exclusive_group()
    source.add_argument('--packages-root', type=Path, help='served package root: seed the newest version of each package')
    source.add_argument('--build', metavar='REPOSITORY', help='build every data recipe of REPOSITORY (core) from this checkout')
    parser.add_argument('--repository', default='core', help='repository for --packages-root (default core)')
    parser.add_argument('--package', type=Path, action='append', default=[], help='also seed this signed archive')
    args = parser.parse_args()
    key_path = args.package_key or (args.keys / 'public.pem' if args.keys else None)
    if args.build and not args.keys: parser.error('--build needs --keys')
    if (args.packages_root or args.build or args.package) and key_path is None: parser.error('packages need --package-key or --keys')

    files = []
    if key_path: files.append(('package-key', 'package-key.pem', key_path.read_bytes()))
    if args.tls_ca: files.append(('tls-ca', 'tls-ca.pem', args.tls_ca.read_bytes()))
    with tempfile.TemporaryDirectory(prefix='zenith-seed-') as work:
        packages = published(args.packages_root, args.repository) if args.packages_root else build_recipes(args.build, args.keys, Path(work)) if args.build else []
        packages += [(p.stem, '', p) for p in args.package]
        names = set()
        for name, version, archive in packages:
            if name in names: raise SystemExit('package listed twice: ' + name)
            names.add(name); files.append(('package', name + '.zpk', archive.read_bytes()))
        if len(files) > 32: raise SystemExit('a seed holds at most 32 files')
        seed = build_seed(args.mirror, files)
        write_iso(args.base, args.output, seed)
    digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
    print('seed: ' + str(len(packages)) + ' packages, ' + str(len(seed)) + ' bytes'
          + (', package key ' + hashlib.sha256(key_path.read_bytes()).hexdigest()[:16] if key_path else '')
          + (', TLS CA ' + hashlib.sha256(args.tls_ca.read_bytes()).hexdigest()[:16] if args.tls_ca else '')
          + (', mirror ' + args.mirror if args.mirror else ''))
    print('installer ISO: ' + str(args.output) + ' SHA256=' + digest)


if __name__ == '__main__': main()
