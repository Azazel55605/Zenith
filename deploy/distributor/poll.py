#!/usr/bin/env python3
"""Poll package tags from a trusted Git remote; build immutable releases on the VM."""
import argparse
import fcntl
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("release", HERE / "release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def configure_environment():
    # Direct python invocations bypass release.sh and the systemd Environment.
    # Children inherit this too, including --execute and native validation.
    entries = [str(Path.home() / ".dotnet/tools"), *os.environ.get("PATH", os.defpath).split(os.pathsep),
               "/usr/local/bin", "/usr/bin", "/bin", "/usr/local/sbin", "/usr/sbin", "/sbin"]
    os.environ["PATH"] = os.pathsep.join(dict.fromkeys(entry for entry in entries if entry))


def git(repo, *args):
    return subprocess.check_output(["git", "-C", str(repo), *args], text=True).strip()


def save(path, value):
    descriptor, temporary = tempfile.mkstemp(prefix=".state-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w") as output:
            os.fchmod(output.fileno(), 0o640)
            json.dump(value, output, indent=2); output.write("\n")
            output.flush(); os.fsync(output.fileno())
        os.replace(temporary, path)
        release.publisher.sync_directory(path.parent)
    finally:
        if os.path.exists(temporary): os.unlink(temporary)


def discover(repo, recipes):
    found = []
    for tag in git(repo, "tag", "--list").splitlines():
        prefix, separator, suffix = tag.partition("/v")
        if not separator or prefix not in recipes:
            continue
        version = release.version(suffix)
        commit = git(repo, "rev-parse", tag + "^{commit}")
        subprocess.run(["git", "-C", str(repo), "merge-base", "--is-ancestor",
                        commit, "refs/remotes/origin/main"], check=True)
        found.append((tag, commit, version, recipes[prefix]))
    return sorted(found, key=lambda item: (item[0].partition("/v")[0], tuple(map(int, item[2].split(".")))))


def pin(records, found):
    # Validate all observations before any release can be built.
    for tag, commit, _, _ in found:
        if tag in records and records[tag]["commit"] != commit:
            raise ValueError("release tag moved: " + tag)
    for tag, commit, _, _ in found:
        records.setdefault(tag, dict(commit=commit, status="pending"))


def execute(config, checkout, version, recipe):
    release.REPO = checkout
    release.TOOL = checkout / "tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll"
    name, repository, kind = recipe["name"], recipe["repository"], recipe["kind"]
    target = Path(config["root"]) / repository / name / version
    if target.exists():
        # Resume after publication/index/delivery interruption, never rebuild it.
        receipt = json.loads((target / "release.json").read_text())
        if receipt["source_commit"] != release.source_commit():
            raise ValueError("published version belongs to another source commit")
        subprocess.run(["dotnet", "build", "tools/Zenith.PackageTool", "-c", "Release"], cwd=checkout, check=True)
    elif kind == "kernel":
        release.build(config, version, native=True)
    else:
        subprocess.run(["dotnet", "test", "tests/Zenith.Tests", "-c", "Release"], cwd=checkout, check=True)
        release.data(config, name, version, checkout / recipe["payload"], repository)
    release.verify(config, version, name, repository, kind)
    # Also renew indexes when recovering an already-published release.
    release.indexes(config)


def launch(checkout, version, recipe, log):
    return subprocess.run([sys.executable, str(HERE / "poll.py"), "--execute", str(checkout),
                           "--version", version, "--recipe", json.dumps(recipe)],
                          stdout=log, stderr=subprocess.STDOUT).returncode


def poll(remote, state, recipes, retry=None):
    state.mkdir(parents=True, exist_ok=True, mode=0o750)
    state.chmod(0o750)
    with (state / "poll.lock").open("a") as lock:
        os.fchmod(lock.fileno(), 0o640)
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        mirror = state / "source.git"
        if not mirror.exists():
            subprocess.run(["git", "init", "--bare", str(mirror)], check=True)
            git(mirror, "remote", "add", "origin", remote)
        if git(mirror, "remote", "get-url", "origin") != remote:
            raise ValueError("remote changed; inspect existing automation state first")
        # No force: Git refuses replacing previously fetched tag objects.
        git(mirror, "fetch", "--no-tags", "origin", "+refs/heads/main:refs/remotes/origin/main", "refs/tags/*:refs/tags/*")
        path = state / "releases.json"
        records = json.loads(path.read_text()) if path.exists() else {}
        found = discover(mirror, recipes)
        pin(records, found)
        save(path, records)
        if retry and retry not in records:
            raise ValueError("unknown retry tag")
        config = release.configuration()
        failures = []
        for tag, commit, version, recipe in found:
            entry = records[tag]
            if entry["status"] in ("complete", "failed") and retry != tag:
                continue
            checkout = state / ("build-" + tag.replace("/", "-"))
            # A fresh clone retains the complete failed checkout until explicit retry.
            if checkout.exists(): shutil.rmtree(checkout)
            entry.pop("error", None)
            entry.pop("finished", None)
            entry.update(status="building", started=int(time.time()))
            save(path, records)
            try:
                subprocess.run(["git", "clone", "--shared", "--no-checkout", str(mirror), str(checkout)], check=True)
                git(checkout, "checkout", "--detach", commit)
                with (state / (tag.replace("/", "-") + ".log")).open("a") as log:
                    os.fchmod(log.fileno(), 0o640)
                    # Child keeps all phase output, and is bounded by systemd's runtime limit.
                    result = launch(checkout, version, recipe, log)
                if result: raise ValueError("build/verification failed; inspect retained log and checkout")
                entry.update(status="complete", finished=int(time.time()))
                save(path, records)
                shutil.rmtree(checkout)
            except Exception as error:
                entry.update(status="failed", error=str(error), finished=int(time.time()))
                save(path, records); failures.append(tag)
                print(tag + ": " + str(error), file=sys.stderr, flush=True)
        if failures: raise ValueError("failed releases: " + ", ".join(failures))
        renewal = state / "index-renewal.json"
        previous = json.loads(renewal.read_text())["finished"] if renewal.exists() else 0
        if time.time() - previous >= 86400:
            release.indexes(config)
            save(renewal, dict(finished=int(time.time())))


def main():
    configure_environment()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--remote", default="https://github.com/Azazel55605/Zenith.git")
    parser.add_argument("--state", type=Path, default=Path.home() / ".local/state/zenith/distributor")
    parser.add_argument("--retry", help="explicitly retry a failed package tag")
    parser.add_argument("--execute", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--version", help=argparse.SUPPRESS)
    parser.add_argument("--recipe", help=argparse.SUPPRESS)
    args = parser.parse_args()
    try:
        if args.execute:
            execute(release.configuration(), args.execute, release.version(args.version), json.loads(args.recipe))
        else:
            poll(args.remote, args.state.resolve(), json.loads((HERE / "recipes.json").read_text()), args.retry)
        return 0
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print("Distributor polling failed: " + str(error), file=sys.stderr); return 1


if __name__ == "__main__": raise SystemExit(main())
