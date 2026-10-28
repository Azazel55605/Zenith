#!/usr/bin/env python3
"""Unsigned pre-tag acceptance using the distributor's release gates."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
# Keep this list common to CI, local pre-tag validation and server pre-build tests.
CONTROLLER_TESTS = (
    "test_guest_auth.py", "test_validation_status.py", "test_validation_profile.py",
    "test_validation_diagnostics.py", "test_validation_scheduler.py", "test_native_pipeline_replay.py",
    "test_validation_accelerator.py", "test_validation_input.py", "test_guest_graphics.py", "test_graphical_replay.py", "test_distributor_validation.py",
    "test_recovery_console.py", "test_release_acceptance.py", "test_distributor_retry.py",
    "test_sunbeam_streams.py", "test_net_tools.py", "test_guest_pipeline.py",
    "test_native_validation.py", "test_validation_terminal.py", "test_distributor_dashboard.py", "test_distributor_poll.py", "test_distributor_release.py", "test_ui_library.py", "test_documentation.py",
)


def controller_tests():
    environment = dict(os.environ)
    environment["PYTHONPATH"] = str(ROOT / "tools") + os.pathsep + environment.get("PYTHONPATH", "")
    subprocess.run([sys.executable, "-B", "-m", "unittest",
                    *("tools/" + name for name in CONTROLLER_TESTS)],
                   cwd=ROOT, env=environment, check=True)


def acceptance(release, keep, existing=False):
    # Never reuse stale success logs or overwrite an operator's evidence.
    keep = keep.resolve()
    keep.mkdir(parents=True, exist_ok=False)
    print("Acceptance evidence: " + str(keep), flush=True)
    candidate = None
    if not existing:
        release.clean()
        candidate = release.source_commit()
    with (keep / "controller-tests.log").open("wb") as log:
        subprocess.run([sys.executable, "-B", str(HERE / "validate.py"), "--controller-only"],
                       cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
    release.native_prerequisites()
    release.phase("Host tests", ["dotnet", "test", "tests/Zenith.Tests", "-c", "Release"], keep / "tests.log")
    if not existing:
        release.prerequisites()
        release.phase("Kernel build", ["dotnet", "publish", "-c", "Release", "-r", "linux-x64",
                      "-o", "output-x64"], keep / "build.log")
    artifacts = {}
    for name in ("Zenith.iso", "Zenith.elf"):
        artifact = ROOT / "output-x64" / name
        if not artifact.is_file() or artifact.stat().st_size == 0:
            raise ValueError("missing built artifact: " + str(artifact))
        artifacts[name] = hashlib.sha256(artifact.read_bytes()).hexdigest()
    (keep / "artifacts.json").write_text(json.dumps(dict(sha256=artifacts, existing=existing), indent=2) + "\n")
    release.phase("Package tool", ["dotnet", "build", "tools/Zenith.PackageTool", "-c", "Release", "--nologo"],
                  keep / "package-tool.log")
    previous_fresh=os.environ.get('ZENITH_VALIDATION_FRESH')
    os.environ['ZENITH_VALIDATION_FRESH']='1'
    try:release.native_checks(keep)
    finally:
        if previous_fresh is None:os.environ.pop('ZENITH_VALIDATION_FRESH',None)
        else:os.environ['ZENITH_VALIDATION_FRESH']=previous_fresh
    for name, digest in artifacts.items():
        if hashlib.sha256((ROOT / "output-x64" / name).read_bytes()).hexdigest() != digest:
            raise ValueError("artifact changed during acceptance: " + name)
    if not existing:
        release.clean()
        if release.source_commit() != candidate:
            raise ValueError("source commit changed during acceptance")
    print("Unsigned acceptance passed. No release was signed, published or tagged.", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--controller-only", action="store_true", help="fast controller regressions, no guest or build")
    parser.add_argument("--existing", action="store_true", help="test existing output-x64 artifacts instead of rebuilding")
    parser.add_argument("--keep", type=Path, help="new evidence directory (must not exist)")
    args = parser.parse_args()
    if args.controller_only:
        if args.keep or args.existing: parser.error("--controller-only cannot be combined with --keep/--existing")
        controller_tests(); return
    if not args.keep: parser.error("--keep NEW_DIRECTORY is required")
    spec = importlib.util.spec_from_file_location("release_acceptance", HERE / "release.py")
    release = importlib.util.module_from_spec(spec); spec.loader.exec_module(release)
    try: acceptance(release, args.keep, args.existing)
    except (ValueError, OSError, RuntimeError, subprocess.CalledProcessError) as error:
        raise SystemExit("Acceptance failed; evidence: " + str(args.keep.resolve()) + "; " + str(error))


if __name__ == "__main__": main()
