#!/usr/bin/env python3
"""C5.4 stress gate: every native gate at several CPU counts on the existing build, counts in parallel.

ZENITH_TEST_CPUS=N makes a gate's single-CPU guests boot with N CPUs, and therefore the default multicore
kernel (guests that choose their own CPU setup keep it; see tools/headless-smoke.py). The shared steps
(native prerequisites, host tests, PackageTool) run once; then each count runs the distributor's native
gate set (`release.native_checks`) with its own evidence in KEEP/cpusN and its own worker count, so the
guests' vCPUs roughly fit the host. The summary lists the result of each count.
"""
import argparse, importlib.util, os, subprocess, sys, threading, time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEPLOY = ROOT / 'deploy/distributor'
# One count's gates: imported in a child so each count has its own environment and workspace.
CHILD = ("import importlib.util,sys;from pathlib import Path;"
         "s=importlib.util.spec_from_file_location('release_stress',sys.argv[1]);r=importlib.util.module_from_spec(s);"
         "s.loader.exec_module(r);Path(sys.argv[2]).mkdir(parents=True);r.native_checks(Path(sys.argv[2]))")


def jobs(count, host_cpus):
    # Each worker runs one guest at `count` vCPUs; leave a CPU for the host and give every count one worker.
    return max(1, min(4, (host_cpus - 1) // (4 * count))) if count > 1 else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('keep', type=Path, help='new evidence directory (must not exist)')
    parser.add_argument('--cpus', type=int, nargs='+', choices=range(1, 9), default=[1, 2, 4, 8])
    parser.add_argument('--accelerator', choices=('auto', 'kvm', 'tcg'), default=os.environ.get('ZENITH_VALIDATION_ACCELERATOR', 'auto'))
    parser.add_argument('--serial', action='store_true', help='one count after another instead of all at once')
    args = parser.parse_args()
    if args.keep.exists(): parser.error('evidence directory exists: ' + str(args.keep))
    args.keep.mkdir(parents=True)
    spec = importlib.util.spec_from_file_location('release_stress', DEPLOY / 'release.py')
    release = importlib.util.module_from_spec(spec); spec.loader.exec_module(release)
    os.chdir(ROOT)
    release.native_prerequisites()
    release.phase('Host tests', ['dotnet', 'test', 'tests/Zenith.Tests', '-c', 'Release'], args.keep / 'tests.log')
    release.phase('Package tool', ['dotnet', 'build', 'tools/Zenith.PackageTool', '-c', 'Release', '--nologo'], args.keep / 'package-tool.log')
    host = len(os.sched_getaffinity(0))
    results = {}
    def run(count):
        keep = args.keep / ('cpus' + str(count)); log = args.keep / ('cpus' + str(count) + '.log')
        env = dict(os.environ, ZENITH_TEST_CPUS=str(count), ZENITH_VALIDATION_ACCELERATOR=args.accelerator,
                   ZENITH_VALIDATION_FRESH='1', ZENITH_NATIVE_JOBS=str(jobs(count, host)))
        started = time.monotonic()
        with log.open('w') as output:
            code = subprocess.run([sys.executable, '-B', '-c', CHILD, str(DEPLOY / 'release.py'), str(keep)],
                                  cwd=ROOT, env=env, stdout=output, stderr=subprocess.STDOUT).returncode
        results[count] = code
        print(('PASS' if code == 0 else 'FAIL') + ' %d CPU(s): %.1f min, %s workers; log %s'
              % (count, (time.monotonic() - started) / 60, env['ZENITH_NATIVE_JOBS'], log), flush=True)
    if args.serial:
        for count in args.cpus: run(count)
    else:
        threads = [threading.Thread(target=run, args=(count,)) for count in args.cpus]
        for thread in threads: thread.start(); time.sleep(20)  # stagger first boots
        for thread in threads: thread.join()
    failed = [count for count in args.cpus if results.get(count) != 0]
    print('C5.4 stress: ' + ('all counts passed' if not failed else 'failed at ' + ', '.join(map(str, failed)) + ' CPU(s)'), flush=True)
    raise SystemExit(1 if failed else 0)


if __name__ == '__main__': main()
