#!/usr/bin/env python3
"""Verify and atomically publish one immutable signed package for manual fetch.

release.json is an unsigned operator receipt, not a Zenith repository index.
"""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def real_path(path, directory=False):
    path = Path(os.path.abspath(path))
    for ancestor in (path, *path.parents):
        if ancestor.is_symlink():
            raise ValueError("symlink path refused: " + str(ancestor))
    if directory:
        if not path.is_dir(): raise ValueError("expected existing directory: " + str(path))
    elif not path.is_file():
        raise ValueError("expected regular file: " + str(path))
    return path


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as file:
        for block in iter(lambda: file.read(65536), b""): result.update(block)
    return result.hexdigest()


def verify(tool, package, key, kind, name, version, repository):
    command = "kernel-check" if kind == "kernel" else "check"
    result = subprocess.run(["dotnet", str(tool), command, str(package), str(key)],
                            capture_output=True, text=True, timeout=180)
    if result.returncode:
        raise ValueError("signature/container verification failed: " + result.stderr.strip())
    expected = (rf"verified zenith-kernel {re.escape(version)} sha256=[0-9a-f]{{64}}"
                if kind == "kernel" else rf"verified {re.escape(name)} {re.escape(version)} repo={repository}")
    if re.fullmatch(expected, result.stdout.strip()) is None:
        raise ValueError("verified package identity does not match requested publication")


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try: os.fsync(descriptor)
    finally: os.close(descriptor)


def publish(root, package, key, tool, kind, name, version, repository, commit):
    if repository not in ("core", "extra") or kind not in ("kernel", "data"):
        raise ValueError("unsupported repository or package kind")
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", name): raise ValueError("invalid name")
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?", version):
        raise ValueError("invalid numeric version")
    if not re.fullmatch(r"[0-9a-f]{40}", commit): raise ValueError("expected full source commit")
    if kind == "kernel" and (name != "zenith-kernel" or repository != "core"):
        raise ValueError("kernels must publish as core/zenith-kernel")
    root = real_path(root, directory=True)
    package, key, tool = (real_path(path) for path in (package, key, tool))
    if root == root.parent: raise ValueError("filesystem root cannot be a publication directory")
    if package.stat().st_size > 32 * 1024 * 1024 + 4096: raise ValueError("package exceeds bound")
    pem = key.read_bytes() if key.stat().st_size <= 4096 else b""
    if not pem.startswith(b"-----BEGIN PUBLIC KEY-----\n") or not pem.rstrip().endswith(b"-----END PUBLIC KEY-----"):
        raise ValueError("expected public SPKI PEM; private keys must never be published")
    # Lock and staging are outside the nginx root. Renaming a complete directory
    # publishes package, public key and receipt together, never partial files.
    lock_path = root.parent / ("." + root.name + "-publish.lock")
    descriptor = os.open(lock_path, os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        target = root / repository / name / version
        for parent in (root / repository, root / repository / name):
            if parent.is_symlink(): raise ValueError("symlink publication directory refused")
            parent.mkdir(mode=0o755, exist_ok=True)
            # The build service's 0077 umask must not hide public repositories.
            parent.chmod(0o755)
        if target.exists() or target.is_symlink(): raise ValueError("version already published; immutable releases cannot be replaced")
        staging = Path(tempfile.mkdtemp(prefix="." + root.name + "-staging-", dir=root.parent))
        try:
            archive = staging / (name + ".zpk")
            shutil.copyfile(package, archive)
            (staging / "public.pem").write_bytes(pem)
            verify(tool, archive, staging / "public.pem", kind, name, version, repository)
            receipt = {"schema": "zenith-manual-publication-1", "signed_index": False,
                       "name": name, "version": version, "repository": repository, "kind": kind,
                       "source_commit": commit, "archive": archive.name,
                       "archive_sha256": digest(archive), "archive_bytes": archive.stat().st_size,
                       "public_pem_sha256": digest(staging / "public.pem")}
            (staging / "release.json").write_text(json.dumps(receipt, sort_keys=True, indent=2) + "\n")
            for file in staging.iterdir():
                file.chmod(0o644)
                with file.open("rb") as stream: os.fsync(stream.fileno())
            staging.chmod(0o755); sync_directory(staging)
            os.rename(staging, target)
            sync_directory(target.parent); sync_directory(root / repository); sync_directory(root)
            sync_directory(root.parent)
            return target, receipt
        finally:
            if staging.exists(): shutil.rmtree(staging)
    finally: os.close(descriptor)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("root", "package", "public-key", "tool"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--kind", choices=("kernel", "data"), required=True)
    parser.add_argument("--repository", choices=("core", "extra"), required=True)
    for name in ("name", "version", "source-commit"): parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    try:
        target, receipt = publish(args.root, args.package, args.public_key, args.tool, args.kind,
                                  args.name, args.version, args.repository, args.source_commit)
        print("published " + str(target))
        print("public PEM SHA256=" + receipt["public_pem_sha256"])
        print("archive SHA256=" + receipt["archive_sha256"])
        print("manual fetch path=/" + target.relative_to(args.root.absolute()).as_posix() + "/" + receipt["archive"])
        print("release.json is an unsigned receipt; no signed online index is provided")
        return 0
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print("publication refused: " + str(error), file=__import__("sys").stderr)
        return 1


if __name__ == "__main__": raise SystemExit(main())
