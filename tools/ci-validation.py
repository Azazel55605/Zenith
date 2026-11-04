#!/usr/bin/env python3
"""Parallel CI gates over one immutable build; only the aggregate proves coverage."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import stat
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('ci_release', ROOT / 'deploy/distributor/release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def legacy_plan(keep):
    def command(script, *args): return [sys.executable, '-B', str(ROOT / 'tools' / script), *map(str, args)]
    # Separate runners own separate disks and firmware. Only the ordinary smoke
    # and its disk-only UEFI follow-up share a fixture and must remain ordered.
    return {
        'ext2-pressure': [command('smoke-test.py', '--timeout', 600, '--step-timeout', 180,
                                 '--memory', 512, '--large-ext2-stress', '--keep', keep / 'large')],
        'boot': [command('smoke-test.py', '--timeout', 600, '--step-timeout', 180, '--keep', keep / 'boot'),
                 command('test-installed-boot.py', keep / 'boot/installed-disk.img', '--test-session-override', '--keep', keep / 'efi')],
        'security': [command('security-smoke.py', keep / 'security')],
        'foundation': [command('foundation-smoke.py', keep / 'foundation')],
        'guided-gui': [command('guided-install-smoke.py', 'gui', keep / 'guided-gui')],
        'guided-headless': [command('guided-install-smoke.py', 'headless', keep / 'guided-headless')],
        'rescue': [command('install-smoke.py', keep / 'install')],
        'headless': [command('headless-smoke.py', keep / 'headless')],
        'offline-packages': [command('package-smoke.py', keep / 'packages')],
        'idt': [command('idt-boot-smoke.py', keep / 'idt')],
        'dhcp-receive': [command('dhcp-receive-smoke.py', keep / 'dhcp-receive')],
        'transfer': [command('transfer-bench.py', keep / 'transfer', '--disk-mib', 16)],
        'keyboard': [command('keyboard-smoke.py', keep / 'keyboard')],
        'network': [command('network-smoke.py', keep / 'network')],
    }


def matrix(kind):
    if kind == 'native':
        checks = sorted(release.native_gate_plan(), key=lambda check: -release.native_validation.WEIGHT.get(check[0], 0))
        return {'include': [dict(gate=key, name=script.removesuffix('.py')) for key, script in checks]}
    return {'include': [dict(gate=name) for name in legacy_plan(Path('/fixture'))]}


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def seal_build():
    release.clean()
    value = dict(source=release.source_commit(), artifacts={name: digest(ROOT / 'output-x64' / name)
                 for name in ('Zenith.iso', 'Zenith.elf')})
    (ROOT / 'output-x64/ci-build.json').write_text(json.dumps(value, indent=2) + '\n')
    return value


def verify_build():
    release.clean()
    value = json.loads((ROOT / 'output-x64/ci-build.json').read_text())
    if value.get('source') != release.source_commit(): raise ValueError('CI build is from another source commit')
    actual = {name: digest(ROOT / 'output-x64' / name) for name in ('Zenith.iso', 'Zenith.elf')}
    if value.get('artifacts') != actual: raise ValueError('CI artifact hash mismatch')
    return value


def run_gate(kind, gate, keep):
    allowed = {row['gate'] for row in matrix(kind)['include']}
    if gate not in allowed: raise ValueError('unknown ' + kind + ' gate: ' + gate)
    before = verify_build()
    keep.mkdir(parents=True, exist_ok=False)
    if kind == 'native':
        release.native_prerequisites()
        release.phase('Package tool', ['dotnet', 'build', 'tools/Zenith.PackageTool', '-c', 'Release', '--nologo'], keep / 'package-tool.log')
        # CI never consumes a pass from a previous runner or candidate.
        os.environ['ZENITH_VALIDATION_FRESH'] = '1'
        release.native_checks(keep, gate=gate)
    else:
        short = Path(tempfile.mkdtemp(prefix='zc-', dir='/var/tmp'))
        try:
            for index, command in enumerate(legacy_plan(short)[gate]):
                release.phase('Legacy ' + gate + ' ' + str(index + 1), command, keep / (str(index + 1) + '.log'))
        finally:
            for parent, _, names in os.walk(short):
                for name in names:
                    path = Path(parent) / name
                    if stat.S_ISSOCK(path.lstat().st_mode): path.unlink()
            shutil.move(str(short), str(keep / 'fixtures'))
    if verify_build() != before: raise ValueError('CI build changed during gate')
    (keep / 'result.json').write_text(json.dumps(dict(before, kind=kind, gate=gate, passed=True)) + '\n')
    print('PASS CI ' + kind + '/' + gate + '; individual gate only', flush=True)


def aggregate(directory):
    build = verify_build()
    expected = {(kind, row['gate']) for kind in ('native', 'legacy') for row in matrix(kind)['include']}
    seen = set()
    for path in directory.rglob('result.json'):
        row = json.loads(path.read_text())
        key = (row.get('kind'), row.get('gate'))
        if key not in expected or key in seen: raise ValueError('unexpected or duplicate CI result: ' + str(key))
        if row.get('passed') is not True or any(row.get(name) != build[name] for name in ('source', 'artifacts')):
            raise ValueError('failed, stale or mismatched CI result: ' + str(key))
        seen.add(key)
    if seen != expected: raise ValueError('missing CI gates: ' + str(sorted(expected - seen)))
    print('PASS complete CI coverage: ' + str(len(seen)) + ' fresh gates on one source/ISO/ELF')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    plan = commands.add_parser('matrix'); plan.add_argument('kind', choices=('native', 'legacy'))
    commands.add_parser('seal-build')
    commands.add_parser('verify-build')
    for kind in ('native', 'legacy'):
        run = commands.add_parser(kind); run.add_argument('gate'); run.add_argument('keep', type=Path)
    total = commands.add_parser('aggregate'); total.add_argument('directory', type=Path)
    args = parser.parse_args()
    if args.command == 'matrix': print(json.dumps(matrix(args.kind), separators=(',', ':')))
    elif args.command == 'seal-build': seal_build()
    elif args.command == 'verify-build': verify_build()
    elif args.command == 'aggregate': aggregate(args.directory)
    else: run_gate(args.command, args.gate, args.keep.resolve())


if __name__ == '__main__':
    try: main()
    except (OSError, ValueError, RuntimeError) as error: raise SystemExit(str(error))
