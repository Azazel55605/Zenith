#!/usr/bin/env python3
"""Manual build/sign/publish and independently checked static delivery."""
import argparse
from contextlib import ExitStack
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request

REPO = Path(__file__).resolve().parents[2]
CONFIG = Path.home() / ".config/zenith/distributor.json"
TOOL = REPO / "tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll"
spec = importlib.util.spec_from_file_location("publisher", REPO / "tools/publish-package.py")
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


def version(value):
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?", value):
        raise ValueError("expected numeric release version, e.g. 0.0.7")
    if any(int(part) > 0xffffffff for part in value.split(".")):
        raise ValueError("version component exceeds package format limit")
    return value


def package_name(value):
    if not re.fullmatch(r"[a-z][a-z0-9-]{0,63}", value): raise ValueError("invalid package name")
    return value


def base_url(value):
    parsed = urllib.parse.urlsplit(value)
    if not value.isascii() or any(ord(c) <= 32 for c in value) or parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username \
            or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("base URL must be HTTP/HTTPS without credentials, query or fragment")
    if parsed.port is not None and parsed.port < 1: raise ValueError("invalid delivery port")
    return value.rstrip("/")


def configure(args):
    config = dict(keys=str(args.keys.expanduser().resolve()), root=str(args.root.expanduser().resolve()),
                  base_url=base_url(args.url), tls_ca=str(args.tls_ca.expanduser().resolve()) if args.tls_ca else None)
    CONFIG.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor, temporary = tempfile.mkstemp(prefix=".distributor-", dir=CONFIG.parent)
    try:
        with os.fdopen(descriptor, "w") as file:
            json.dump(config, file, indent=2); file.write("\n"); file.flush(); os.fsync(file.fileno())
        os.replace(temporary, CONFIG)
    finally:
        if os.path.exists(temporary): os.unlink(temporary)
    print("Saved server settings: " + str(CONFIG))
    print("Existing signing keys and web service are unchanged.")


def configuration():
    if not CONFIG.is_file(): raise ValueError("run release.sh setup BASE_URL first")
    config = json.loads(CONFIG.read_text())
    base_url(config["base_url"])
    return config


def source_commit():
    return subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip()


def clean():
    if subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=all"], cwd=REPO):
        raise ValueError("commit or remove checkout changes before building a release")
    if (REPO / "cosmos.local.props").exists(): raise ValueError("local Cosmos override refused")


def paths(config, signing=False):
    keys = publisher.real_path(config["keys"], directory=True)
    root = publisher.real_path(config["root"], directory=True)
    if keys == root or keys.is_relative_to(root): raise ValueError("signing keys must be outside the web root")
    public = publisher.real_path(keys / "public.pem")
    if signing:
        private = publisher.real_path(keys / "private.pem")
        if private.stat().st_mode & 0o077: raise ValueError("private.pem must be readable only by its owner (chmod 600)")
        if keys.stat().st_mode & 0o077 or keys.stat().st_uid != os.getuid():
            raise ValueError("signing directory must belong to the release user with mode 700")
    return keys, root, public


def available(root, release, name="zenith-kernel", repository="core"):
    if not os.access(root, os.W_OK) or not os.access(root.parent, os.W_OK):
        raise ValueError("release user needs write access to publication root and its staging parent")
    if any(path.is_symlink() for path in (root / repository, root / repository / name)):
        raise ValueError("symlink publication directory refused")
    target = root / repository / name / release
    if target.exists() or target.is_symlink():
        raise ValueError("version already published; use verify instead of rebuilding/replacing it")


def phase(label, command, log):
    print(label + " — " + str(log), flush=True)
    started = time.monotonic()
    with log.open("wb") as output:
        process = subprocess.Popen(command, cwd=REPO, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            while process.poll() is None:
                time.sleep(0.2)
                if sys.stdout.isatty() and time.monotonic() - started >= 5:
                    print("\r" + label + " running; see " + str(log), end="", flush=True)
                    started = time.monotonic()
        except BaseException:
            try: os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError: pass
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: os.killpg(process.pid, signal.SIGKILL); process.wait()
            raise
    if sys.stdout.isatty(): print()
    if process.returncode:
        tail = log.read_text(errors="replace").splitlines()[-20:]
        raise ValueError(label + " failed; full log: " + str(log) + "\n" + "\n".join(tail))
    print(label + " passed", flush=True)


def publish(config, release, archive, commit, work, name="zenith-kernel", repository="core", kind="kernel"):
    keys, root, public = paths(config)
    target, receipt = publisher.publish(root, archive, public, TOOL, kind, name, release, repository, commit)
    (work / "release.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print("Published immutable release: " + str(target), flush=True)
    url = config["base_url"] + "/" + repository + "/" + name + "/" + release + "/" + name + ".zpk"
    print("Download: " + url)
    print("Archive SHA256: " + receipt["archive_sha256"])
    verify_command = "verify " + release if kind == "kernel" else "verify-data " + name + " " + release + " --repository " + repository
    print("Verify distribution: bash deploy/distributor/release.sh " + verify_command)
    if kind == "kernel":
        print("Guest: fetch --kernel " + url + " /root/kernel.zpk")
        print("Guest: pkg kernel check /root/kernel.zpk")
        print("Guest: pkg kernel install /root/kernel.zpk && sync && reboot")
        print("After booting the new kernel: boot confirm")
    else:
        print("Guest: fetch " + url + " /root/package.zpk")
        print("Guest: pkg check /root/package.zpk")
        print("Guest: pkg install /root/package.zpk")
    print("Native acceptance and the real installed VM check remain required.")
    return receipt


def locked_work(config=None):
    directory = REPO / "obj/distributor"; directory.mkdir(parents=True, exist_ok=True)
    stack = ExitStack()
    try:
        locks = [directory / "build.lock"]
        if config: locks.insert(0, Path(config["root"]).parent / ".distributor-build.lock")
        for path in locks:
            lock = stack.enter_context(path.open("a"))
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        stack.close(); raise ValueError("another distributor operation is running")
    except BaseException:
        stack.close(); raise
    return stack, Path(tempfile.mkdtemp(prefix="release-", dir=directory))


def build(config, release, native=False):
    keys, root, _ = paths(config, signing=True)
    available(root, release); clean(); commit = source_commit()
    prerequisites()
    lock, work = locked_work(config)
    with lock:
        print("Source: " + commit + "; logs: " + str(work), flush=True)
        phase("Host tests", ["dotnet", "test", "tests/Zenith.Tests", "-c", "Release"], work / "tests.log")
        phase("Kernel build", ["dotnet", "publish", "-c", "Release", "-r", "linux-x64", "-o", str(work / "build")], work / "build.log")
        clean()
        if source_commit() != commit: raise ValueError("source commit changed during build; publication refused")
        phase("Package tool", ["dotnet", "build", "tools/Zenith.PackageTool", "-c", "Release", "--nologo"], work / "package-tool.log")
        if native:
            # Acceptance harnesses use this checkout's output-x64 artifacts.
            shutil.copytree(work / "build", REPO / "output-x64", dirs_exist_ok=True)
            native_checks(work)
            clean()
            if source_commit() != commit: raise ValueError("source changed during native acceptance; signing refused")
        phase("Sign kernel", ["dotnet", str(TOOL), "kernel-seal", release, str(work / "build/Zenith.elf"),
                              str(keys / "private.pem"), str(work / "kernel.zpk")], work / "sign.log")
        clean()
        if source_commit() != commit: raise ValueError("source commit changed before publication; refused")
        publish(config, release, work / "kernel.zpk", commit, work)
        indexes(config, work)


def native_checks(work):
    # AF_UNIX paths are limited to 107 pathname bytes on Linux. Use an actual
    # short directory: repository-smoke resolves its keep path, so a symlink
    # alias to the deeply nested release workspace would not fix it.
    # /var/tmp avoids putting large validation disks in Debian's /tmp tmpfs.
    directory = Path(tempfile.mkdtemp(prefix="zn-", dir="/var/tmp"))
    print("Native workspace: " + str(directory) + "; retained afterward at " + str(work / "native"), flush=True)
    try:
        for name, script in (("k", "kernel-update-smoke.py"), ("r", "repository-smoke.py")):
            phase("Native " + script, [sys.executable, "-B", str(REPO / "tools" / script),
                  str(directory / name)], work / (script + ".log"))
    finally:
        # Stale Unix sockets cannot be copied if /var/tmp is another filesystem.
        for parent, _, files in os.walk(directory):
            for name in files:
                path = Path(parent) / name
                if stat.S_ISSOCK(path.lstat().st_mode): path.unlink()
        # Preserve failed runs too. Same-filesystem moves are just renames; a
        # separate /var filesystem falls back to copying the retained evidence.
        shutil.move(str(directory), str(work / "native"))


def prerequisites():
    missing = [command for command in ("dotnet", "git", "clang", "nm", "e2fsck", "debugfs", "cosmos.patcher")
               if shutil.which(command) is None]
    if missing: raise ValueError("missing build tools: " + ", ".join(missing))


def publish_existing(config, release, archive, commit):
    if not re.fullmatch(r"[0-9a-f]{40}", commit): raise ValueError("supply the full artifact source commit")
    _, root, _ = paths(config); available(root, release)
    lock, work = locked_work(config)
    with lock:
        phase("Package tool", ["dotnet", "build", "tools/Zenith.PackageTool", "-c", "Release", "--nologo"], work / "package-tool.log")
        return publish(config, release, archive, commit, work)


def verify(config, release, name="zenith-kernel", repository="core", kind="kernel"):
    _, root, public = paths(config)
    target = publisher.real_path(root / repository / name / release, directory=True)
    receipt = json.loads((target / "release.json").read_text())
    local = target / (name + ".zpk")
    # Receipts are operator records, never a substitute for the signed container.
    publisher.verify(TOOL, local, public, kind, name, release, repository)
    expected = publisher.digest(local); maximum = local.stat().st_size
    if expected != receipt["archive_sha256"] or maximum != receipt["archive_bytes"]:
        raise ValueError("local archive disagrees with publication receipt")
    url = config["base_url"] + "/" + repository + "/" + name + "/" + release + "/" + name + ".zpk"
    context = ssl.create_default_context(cafile=config.get("tls_ca"))
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args): raise ValueError("unexpected distributor redirect")
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPSHandler(context=context))
    digest = hashlib.sha256(); total = 0
    print("Checking complete delivery: " + url, flush=True)
    with opener.open(url, timeout=30) as response:
        if response.status != 200: raise ValueError("expected HTTP 200")
        if response.headers.get("Content-Length") != str(maximum): raise ValueError("unexpected content length")
        for data in iter(lambda: response.read(65536), b""):
            total += len(data)
            if total > maximum: raise ValueError("delivery exceeds archive size")
            digest.update(data)
    if total != maximum or digest.hexdigest() != expected: raise ValueError("delivered archive bytes/hash mismatch")
    print("Verified delivery and package signature: " + str(total) + " bytes; SHA256=" + expected)


def data(config, name, release, source, repository):
    package_name(name)
    keys, root, _ = paths(config, signing=True)
    available(root, release, name, repository)
    source = publisher.real_path(source, directory=True)
    if not source.is_relative_to(REPO): raise ValueError("data payload must be inside the committed source checkout")
    clean(); commit = source_commit()
    lock, work = locked_work(config)
    with lock:
        phase("Package tool", ["dotnet", "build", "tools/Zenith.PackageTool", "-c", "Release", "--nologo"], work / "package-tool.log")
        phase("Build data", ["dotnet", str(TOOL), "build", name, release, str(source), str(work / "bundle"),
                            "--repo", repository], work / "data.log")
        clean()
        if source_commit() != commit: raise ValueError("source commit changed before signing; refused")
        phase("Sign data", ["dotnet", str(TOOL), "seal", str(work / "bundle"), str(keys / "private.pem"),
                           str(work / "package.zpk")], work / "sign.log")
        clean()
        if source_commit() != commit: raise ValueError("source commit changed before publication; refused")
        publish(config, release, work / "package.zpk", commit, work, name, repository, "data")
        indexes(config, work)


def indexes(config, work=None):
    keys, root, public = paths(config, signing=True)
    clean(); commit = source_commit()
    if work is None:
        lock, directory = locked_work(config)
        with lock:
            phase("Package tool", ["dotnet", "build", "tools/Zenith.PackageTool", "-c", "Release", "--nologo"], directory / "package-tool.log")
            indexes(config, directory)
        return
    now = int(time.time())
    for repository in ("core", "extra"):
        destination = root / repository / "index.zri"
        generation = 1
        if destination.exists() or destination.is_symlink():
            publisher.real_path(destination)
            result = subprocess.run(["dotnet", str(TOOL), "index-check", str(destination), str(public), repository],
                                    capture_output=True, text=True, check=True, timeout=180)
            match = re.fullmatch(r"verified " + repository + r" generation=(\d+) issued=\d+ expires=\d+\n?", result.stdout)
            if match is None: raise ValueError("unexpected signed index identity")
            generation = int(match[1]) + 1
        archive = work / (repository + ".zri")
        phase("Index " + repository, ["dotnet", str(TOOL), "index-seal", repository, str(generation), str(now),
                                     str(now + 7 * 86400), str(root), str(keys / "private.pem"), str(archive)], work / (repository + "-index.log"))
        clean()
        if source_commit() != commit: raise ValueError("source changed while signing index; activation refused")
        directory = root / repository
        if directory.is_symlink(): raise ValueError("linked metadata directory refused")
        directory.mkdir(exist_ok=True, mode=0o755)
        descriptor, temporary = tempfile.mkstemp(prefix=".index-", dir=root.parent)
        try:
            with os.fdopen(descriptor, "wb") as output:
                output.write(archive.read_bytes()); output.flush(); os.fsync(output.fileno())
            os.chmod(temporary, 0o644); os.replace(temporary, destination)
            publisher.sync_directory(directory); publisher.sync_directory(root)
        finally:
            if os.path.exists(temporary): os.unlink(temporary)
        print("Published signed " + repository + " index generation=" + str(generation) + " (7-day validity)", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    setup = commands.add_parser("setup", help="save paths and delivery URL once")
    setup.add_argument("url"); setup.add_argument("--keys", type=Path, default=Path.home() / "signing")
    setup.add_argument("--root", type=Path, default=Path("/srv/zenith/packages"))
    setup.add_argument("--tls-ca", type=Path)
    for name in ("kernel", "verify"):
        commands.add_parser(name).add_argument("version", type=version)
    existing = commands.add_parser("publish", help="verify/publish an existing signed kernel without rebuilding")
    existing.add_argument("version", type=version); existing.add_argument("archive", type=Path)
    existing.add_argument("--source-commit", required=True)
    for name in ("data", "verify-data"):
        command = commands.add_parser(name)
        command.add_argument("name", type=package_name); command.add_argument("version", type=version)
        command.add_argument("--repository", choices=("core", "extra"), default="extra")
        if name == "data": command.add_argument("source", type=Path)
    commands.add_parser("doctor", help="check configured paths, source and tools")
    commands.add_parser("index", help="sign/activate fresh core and extra repository indexes")
    args = parser.parse_args()
    try:
        if args.command == "setup": configure(args); return 0
        config = configuration()
        if args.command == "kernel": build(config, args.version)
        elif args.command == "publish": publish_existing(config, args.version, args.archive, args.source_commit)
        elif args.command == "verify": verify(config, args.version)
        elif args.command == "data": data(config, args.name, args.version, args.source, args.repository)
        elif args.command == "verify-data": verify(config, args.version, args.name, args.repository, "data")
        elif args.command == "index": indexes(config)
        else:
            paths(config, signing=True); clean(); prerequisites()
            print("Source: " + source_commit()); print("Settings: " + str(CONFIG))
            result = subprocess.run(["bash", str(REPO / "deploy/distributor/check-setup.sh")])
            return result.returncode
        return 0
    except KeyboardInterrupt:
        print("Release operation cancelled; inspect retained logs and publication state before retrying.", file=sys.stderr)
        return 130
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print("Release operation failed: " + str(error), file=sys.stderr); return 1


if __name__ == "__main__": raise SystemExit(main())
