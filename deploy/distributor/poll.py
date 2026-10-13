#!/usr/bin/env python3
"""Poll package tags from a trusted Git remote; build immutable releases on the VM."""
import argparse
import fcntl
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
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
    existing = target.exists()
    if existing:
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
    if existing: release.indexes(config)


def launch(checkout, version, recipe, log):
    details = Path(log.name).with_suffix(".details.json")
    details.unlink(missing_ok=True)
    environment = dict(os.environ, ZENITH_BUILD_OUTPUT=str(details))
    process = subprocess.Popen([sys.executable, str(HERE / "poll.py"), "--execute", str(checkout),
                                "--version", version, "--recipe", json.dumps(recipe)],
                               stdout=log, stderr=subprocess.STDOUT, start_new_session=True, env=environment)
    try: return process.wait()
    except BaseException:
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=20)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
        raise


def queue_retry(state, tag):
    state.mkdir(parents=True, exist_ok=True, mode=0o750); state.chmod(0o750)
    with (state / "poll.lock").open("a") as lock:
        os.fchmod(lock.fileno(), 0o640)
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        records = json.loads((state / "releases.json").read_text())
        if tag not in records: raise ValueError("unknown release tag")
        if records[tag]["status"] == "complete": raise ValueError("release already complete")
        request = state / "retry-request.json"
        if request.exists(): raise ValueError("a retry is already queued")
        save(request, dict(tag=tag))
    print("Retry queued. Start zenith-distributor.service or wait for its timer.", flush=True)


def prepare_checkout(mirror, checkout, commit):
    if checkout.exists():
        if checkout.is_symlink() or git(checkout, "rev-parse", "HEAD") != commit:
            raise ValueError("retained checkout does not match pinned commit; inspect it before retrying")
        if git(checkout, "status", "--porcelain", "--untracked-files=all") or (checkout / "cosmos.local.props").exists():
            raise ValueError("retained checkout is dirty or has a local Cosmos override; inspect it before retrying")
        print("Reusing clean pinned checkout and incremental build outputs: " + str(checkout), flush=True)
        return
    subprocess.run(["git", "clone", "--shared", "--no-checkout", str(mirror), str(checkout)], check=True)
    git(checkout, "checkout", "--detach", commit)


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
        new_tags = any(tag not in records for tag, _, _, _ in found)
        pin(records, found)
        if new_tags:
            for entry in records.values():
                if entry["status"] == "complete": entry["dashboard_hidden"] = True
        save(path, records)
        if retry and retry not in records:
            raise ValueError("unknown retry tag")
        config = release.configuration()
        request_path = state / "retry-request.json"
        queued = json.loads(request_path.read_text())["tag"] if retry is None and request_path.exists() else None
        if queued:
            if queued not in records: raise ValueError("unknown queued retry tag")
            retry = queued
        failures = []
        for tag, commit, version, recipe in found:
            entry = records[tag]
            if entry["status"] in ("complete", "failed", "interrupted") and retry != tag:
                continue
            checkout = state / ("build-" + tag.replace("/", "-"))
            entry.pop("error", None)
            entry.pop("finished", None)
            entry.pop("dashboard_hidden", None)
            entry.update(status="building", started=int(time.time()))
            save(path, records)
            if queued == tag: request_path.unlink()
            try:
                prepare_checkout(mirror, checkout, commit)
                with (state / (tag.replace("/", "-") + ".log")).open("a") as log:
                    os.fchmod(log.fileno(), 0o640)
                    # Child keeps all phase output, and is bounded by systemd's runtime limit.
                    result = launch(checkout, version, recipe, log)
                if result == 130: raise KeyboardInterrupt("Execution worker cancelled (exit 130)")
                if result: raise ValueError("build/verification failed (worker exit " + str(result) + "); inspect retained log and checkout")
                entry.update(status="complete", finished=int(time.time()))
                save(path, records)
                save(state / "index-renewal.json", dict(finished=int(time.time())))
                shutil.rmtree(checkout)
            except BaseException as error:
                cancelled = isinstance(error, KeyboardInterrupt)
                entry.update(status="interrupted" if cancelled else "failed", error=str(error) or "Operation cancelled", finished=int(time.time()))
                save(path, records); failures.append(tag)
                with (state / (tag.replace("/", "-") + ".log")).open("a") as output:
                    output.write("Controller " + entry["status"] + ": " + entry["error"] + "\n")
                print(tag + ": " + str(error), file=sys.stderr, flush=True)
                if not isinstance(error, Exception): raise
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
    parser.add_argument("--queue-retry", help="queue a recorded failed/interrupted tag for the background service")
    parser.add_argument("--execute", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--version", help=argparse.SUPPRESS)
    parser.add_argument("--recipe", help=argparse.SUPPRESS)
    args = parser.parse_args()
    try:
        if args.queue_retry:
            if args.retry or args.execute: raise ValueError("queue-retry cannot be combined with retry/execute")
            queue_retry(args.state.resolve(), args.queue_retry)
        elif args.execute:
            execute(release.configuration(), args.execute, release.version(args.version), json.loads(args.recipe))
        else:
            poll(args.remote, args.state.resolve(), json.loads((HERE / "recipes.json").read_text()), args.retry)
        return 0
    except KeyboardInterrupt as error:
        print("Distributor operation interrupted: " + (str(error) or "keyboard cancellation"), file=sys.stderr); return 130
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print("Distributor polling failed: " + str(error), file=sys.stderr); return 1


if __name__ == "__main__":
    def interrupted(signum, frame): raise KeyboardInterrupt("Received " + signal.Signals(signum).name)
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGHUP, interrupted)
    raise SystemExit(main())
