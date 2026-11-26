"""Exercise the SMB client against a disposable loopback Samba server requiring signing."""

import argparse
import contextlib
import functools
import hashlib
import http.server
import importlib.util
import os
import secrets
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "tools/fixtures/smb"
USER = "zenithtest"
DATA = bytes(range(256)) * 4609 + b"\x00\xfflast byte"


def run(args, **kwargs):
    return subprocess.run(args, check=True, capture_output=True, timeout=90, **kwargs)


@contextlib.contextmanager
def samba(image, password, keep):
    if subprocess.run(
        ["docker", "image", "inspect", image], capture_output=True, check=False
    ).returncode:
        raise SystemExit(
            f"Build the disposable Samba fixture first: docker build -t {image} tools/fixtures/smb"
        )
    cid = (
        run(["docker", "create", "--init", "-i", "-p", "127.0.0.1::445", image])
        .stdout.decode()
        .strip()
    )
    process = None
    try:
        with (keep / "samba.log").open("wb") as log:
            process = subprocess.Popen(
                ["docker", "start", "-ai", cid],
                stdin=subprocess.PIPE,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            process.stdin.write(password.encode() + b"\n")
            process.stdin.flush()
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError(f"Samba exited; see {log.name}")
                published = subprocess.run(
                    ["docker", "port", cid, "445/tcp"], capture_output=True, check=False
                )
                endpoint = published.stdout.decode().strip()
                if endpoint.startswith("127.0.0.1:"):
                    port = int(endpoint.rsplit(":", 1)[1])
                    try:
                        with socket.create_connection(
                            ("127.0.0.1", port), timeout=0.25
                        ):
                            pass
                        # Docker publishes the port before the daemon has created its sockets.
                        if (
                            run(
                                [
                                    "docker",
                                    "exec",
                                    cid,
                                    "sh",
                                    "-c",
                                    "test -f /run/samba/smbd.pid || test -f /var/run/samba/smbd.pid",
                                ]
                            ).returncode
                            == 0
                        ):
                            break
                    except (OSError, subprocess.CalledProcessError):
                        pass
                time.sleep(0.1)
            else:
                raise RuntimeError(f"Samba startup timeout; see {log.name}")
            yield cid, port
    finally:
        subprocess.run(
            ["docker", "rm", "--force", cid], capture_output=True, check=True
        )
        if process:
            process.stdin.close()
            process.wait(timeout=12)


def profile(host, port, password):
    return f"server={host}\nport={port}\nusername={USER}\ndomain=WORKGROUP\npassword={password}\n"


def build_host(keep):
    crypto = keep / "smb-crypto.o"
    run([sys.executable, ROOT / "tools/build-smb-crypto.py", crypto])
    sources = [
        ROOT / "userland/smb" / name
        for name in ("main.c", "client.c", "auth.c", "shares.c")
    ]
    binary = keep / "smb"
    run(
        [
            "cc",
            "-std=c11",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-Os",
            "-ffunction-sections",
            "-fdata-sections",
            "-no-pie",
            "-Wl,--gc-sections",
            *sources,
            FIXTURE / "posix_io.c",
            crypto,
            "-o",
            binary,
        ]
    )
    return binary


def host_checks(binary, cid, port, password, keep):
    share = "//127.0.0.1/transfer"
    auth = [
        "--port",
        str(port),
        "-U",
        USER,
        "--domain",
        "WORKGROUP",
        "--password-stdin",
    ]

    def invoke(*args, code=0, credentials=True, secret=password, env=None, message=None):
        command = [binary, *(auth if credentials else []), *map(str, args)]
        result = subprocess.run(
            command,
            check=False,
            input=secret.encode() + b"\n",
            capture_output=True,
            timeout=45,
            env=dict(os.environ, **(env or {})),
        )
        if result.returncode != code:
            raise AssertionError(
                f"{args}: exit {result.returncode}, expected {code}\n{result.stderr.decode(errors='replace')}"
            )
        if password.encode() in result.stdout + result.stderr:
            raise AssertionError("password appeared in command output")
        if message is not None and message not in result.stderr:
            raise AssertionError(f"missing diagnostic: {message.decode()}")
        return result.stdout

    assert b"transfer\n" in invoke("shares", "127.0.0.1")
    invoke("shares", "127.0.0.1", secret="invalid", code=1)
    invoke("ls", share)
    print(
        "PASS authenticated share discovery, mandatory signing, bad-password refusal",
        flush=True,
    )

    saved = keep / "credentials"
    saved.touch(mode=0o600)
    saved.write_text(profile("127.0.0.1", port, password))
    private = ["--port", str(port), "--credentials", saved]
    invoke(*private, "ls", share, credentials=False)
    invoke(*private, "shares", "localhost", code=1, credentials=False)
    saved.chmod(0o400)
    invoke(*private, "ls", share, credentials=False)
    for mode in (0o644, 0o700):
        saved.chmod(mode)
        invoke(*private, "ls", share, code=1, credentials=False)
    saved.chmod(0o600)
    linked = keep / "credentials-link"
    linked.symlink_to(saved.name)
    invoke("--port", str(port), "--credentials", linked, "ls", share, code=1, credentials=False)
    print("PASS private credentials, permissions and server binding", flush=True)

    src = keep / "source"
    target = keep / "download"
    original = b"Original file\n"
    src.write_bytes(DATA)
    invoke("put", src, share + "/transfer.bin")
    assert f"file\t{len(DATA)}\ttransfer.bin".encode() in invoke("ls", share)
    invoke("get", share + "/transfer.bin", target)
    assert target.read_bytes() == DATA
    remote = ["docker", "exec", cid, "cat", "/srv/transfer/transfer.bin"]
    assert run(remote).stdout == DATA
    empty = keep / "empty"
    empty.touch()
    invoke("put", empty, share + "/empty")
    invoke("get", share + "/empty", keep / "empty-copy")
    assert (keep / "empty-copy").read_bytes() == b""
    print(
        "PASS empty and multi-chunk binary transfers, independent server byte comparison",
        flush=True,
    )

    src.write_bytes(original)
    target.write_bytes(original)
    invoke("put", src, share + "/transfer.bin", code=1)
    invoke("get", share + "/transfer.bin", target, code=1)
    assert run(remote).stdout == DATA and target.read_bytes() == original
    invoke(
        "--replace",
        "get",
        share + "/transfer.bin",
        target,
        code=1,
        env={"ZENITH_SMB_TEST_FAIL_SINK": "1"},
    )
    assert target.read_bytes() == original
    invoke(
        "--replace",
        "get",
        share + "/transfer.bin",
        target,
        code=1,
        env={"ZENITH_SMB_TEST_DISCONNECT_RECV": "1"},
    )
    assert target.read_bytes() == original
    invoke("--replace", "get", share + "/transfer.bin", target)
    assert target.read_bytes() == DATA
    invoke("--replace", "put", src, share + "/transfer.bin")
    src.write_bytes(DATA)
    invoke(
        "--replace",
        "put",
        src,
        share + "/transfer.bin",
        code=1,
        env={"ZENITH_SMB_TEST_FAIL_SOURCE": "1"},
    )
    assert run(remote).stdout == original
    invoke(
        "--replace",
        "put",
        src,
        share + "/transfer.bin",
        code=1,
        env={"ZENITH_SMB_TEST_DISCONNECT_SEND": "1"},
    )
    assert run(remote).stdout == original
    for point, expected in (("before", original), ("ack", DATA)):
        invoke(
            "--replace",
            "put",
            src,
            share + "/transfer.bin",
            code=1,
            env={"ZENITH_SMB_TEST_DROP_PUBLISH": point},
            message=b"upload commit acknowledgement lost; check destination before retrying",
        )
        assert run(remote).stdout == expected
    for _ in range(25):
        names = run(
            ["docker", "exec", cid, "ls", "-A", "/srv/transfer"]
        ).stdout.splitlines()
        if sorted(names) == [b"empty", b"transfer.bin"]:
            break
        time.sleep(0.1)
    else:
        raise AssertionError(names)
    assert not list(keep.glob("download.smb-*"))
    print(
        "PASS no-clobber, explicit replacement, mid-transfer socket loss in both directions, interrupted source and sink I/O",
        flush=True,
    )
    print("PASS socket loss before upload publication and after server commit", flush=True)
    src.write_bytes(original)
    invoke("--replace", "put", src, share + "/missing/transfer.bin", code=1)
    for point in ("before", "partial", "ack"):
        invoke(
            "--replace", "put", src, share + "/transfer.bin", code=1,
            env={"ZENITH_SMB_TEST_DROP_STAGE": point},
        )
        assert run(remote).stdout == DATA
        for _ in range(25):
            names = run(["docker", "exec", cid, "ls", "-A", "/srv/transfer"]).stdout.splitlines()
            if sorted(names) == [b"empty", b"transfer.bin"]:
                break
            time.sleep(0.1)
        else:
            raise AssertionError(names)
    print("PASS failed or interrupted staging CREATE preserves the share with no orphan", flush=True)


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def guest_checks(keep, port, password, iso, elf, fixture_disk=None, limit_check=False):
    headless = ROOT / "tools/headless-smoke.py"
    spec = importlib.util.spec_from_file_location("headless", headless)
    h = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(h)
    served = keep / "served"
    served.mkdir()
    tool_project = ROOT / "tools/Zenith.PackageTool"
    run(["dotnet", "build", tool_project, "-c", "Release", "--nologo"])
    tool = tool_project / "bin/Release/net10.0/Zenith.PackageTool.dll"
    run(
        [
            sys.executable,
            ROOT / "tools/build-userland.py",
            keep / "source-payload",
            "--program",
            "smb",
            "--version",
            "0.1.0",
        ]
    )

    def package(*args):
        run(["dotnet", tool, *args])

    package("keygen", keep / "keys")
    key = (keep / "keys/public.pem").read_bytes()
    (served / "key").write_bytes(key)
    package(
        "build-native",
        "zenith-smb",
        "0.1.0",
        keep / "source-payload",
        keep / "bundle",
        "--repo",
        "extra",
    )
    package("seal", keep / "bundle", keep / "keys/private.pem", served / "smb.zpk")
    (served / "auth").touch(mode=0o600)
    (served / "auth").write_text(profile("10.0.2.2", port, password))
    (served / "source").write_bytes(DATA)
    server = http.server.ThreadingHTTPServer(
        ("127.0.0.1", 0), functools.partial(QuietHandler, directory=str(served))
    )
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f"http://10.0.2.2:{server.server_port}"
    m = None

    def boot(folder, disk=None):
        nonlocal m
        m = h.Machine(
            keep / folder,
            disk,
            iso=None if disk is not None else iso,
            network=["-netdev", "user,id=net0", "-device", "e1000e,netdev=net0"],
        )
        assert m.wait("headless ready", timeout=180) and m.wait(
            "network: online address=10.0.2.15", timeout=90
        )

    def cmd(command, label, code=0, timeout=120):
        off = len(m.log())
        m.send(command + "; echo " + label + "=$? | logger\r")
        marker = "user: " + label + "=" + str(code)
        assert m.wait(marker, off, timeout), f"{marker}, see {m.serial}"
        print("PASS " + label, flush=True)
        return m.log()[off:]

    def shutdown():
        off = len(m.log())
        m.send("poweroff\r")
        assert m.wait("all filesystems unmounted", off, 60)
        assert m.process.wait(timeout=30) == 0

    try:
        if fixture_disk is None:
            boot("live")
            cmd(
                "settings set windows compact; session set-default headless",
                "SMB_SESSION",
            )
            cmd("install sata0 --yes", "SMB_INSTALL", timeout=600)
            shutdown()
            fixture_disk = m.disk
            m.stop()
            m = None
        from boot_image import partitions, verify

        kernel_digest = hashlib.sha256(elf.read_bytes()).hexdigest()
        assert verify(fixture_disk)["current"] == kernel_digest, (
            "installed fixture kernel differs from intended ELF"
        )
        boot("installed", fixture_disk)
        cmd("fetch " + url + "/key /root/smb-key", "SMB_KEY")
        cmd("pkg trust /root/smb-key " + hashlib.sha256(key).hexdigest(), "SMB_TRUST")
        cmd(
            "fetch " + url + "/smb.zpk /root/smb.zpk; pkg install /root/smb.zpk",
            "SMB_PACKAGE",
        )
        assert "smb 0.1.0" in cmd("smb --version | logger", "SMB_VERSION")
        cmd(
            "fetch " + url + "/auth /root/smb-auth; chmod 0600 /root/smb-auth",
            "SMB_PROFILE",
        )
        cmd("fetch " + url + "/source /root/smb-source", "SMB_SOURCE")
        smb = "smb --port " + str(port) + " --credentials /root/smb-auth "
        share = "//10.0.2.2/transfer"
        assert "user: transfer" in cmd(smb + "shares 10.0.2.2 | logger", "SMB_SHARES")
        offset = len(m.log())
        m.send(
            f"smb --port {port} -U {USER} --domain WORKGROUP ls {share}; echo SMB_PROMPT=$? | logger\r"
        )
        assert m.wait("SMB password: ", offset, 30), "foreground hidden prompt"
        m.send(password + "\r")
        assert m.wait("user: SMB_PROMPT=0", offset, 60), "interactive authentication"
        assert password not in m.log()[offset:], "password echoed during authentication"
        print(
            "PASS interactive native password prompt and terminal restoration",
            flush=True,
        )
        cmd(smb + "put /root/smb-source " + share + "/guest.bin", "SMB_PUT")
        assert "guest.bin" in cmd(smb + "ls " + share + " | logger", "SMB_LIST")
        cmd(smb + "get " + share + "/guest.bin /root/smb-result", "SMB_GET")
        cmd(smb + "get " + share + "/guest.bin /root/smb-result", "SMB_EXISTS", 1)
        if limit_check:
            cmd(
                smb + "get " + share + "/over-limit.bin /root/oversized",
                "SMB_LIMIT_DENIED",
                1,
            )
            cmd(
                smb + "get " + share + "/at-limit.bin /root/at-limit",
                "SMB_LIMIT_GET",
                timeout=360,
            )
            cmd(
                smb + "put /root/at-limit " + share + "/guest-limit.bin",
                "SMB_LIMIT_PUT",
                timeout=360,
            )
        cmd("chmod 0644 /root/smb-auth; " + smb + "ls " + share, "SMB_DENIED", 1)
        cmd(
            "chmod 0600 /root/smb-auth; chown 1000:1000 /root/smb-auth; "
            + smb
            + "ls "
            + share,
            "SMB_OTHER_OWNER",
            1,
        )
        cmd("rm /root/smb-auth", "SMB_CLEAN")
        shutdown()
        root = verify(m.disk)["root"]
        part = next(p for p in partitions(m.disk) if str(p[1]) == root)
        volume = keep / "root.ext2"
        with m.disk.open("rb") as src, volume.open("wb") as out:
            src.seek(part[2] * 512)
            remaining = (part[3] - part[2] + 1) * 512
            while remaining:
                chunk = src.read(min(remaining, 1024 * 1024))
                assert chunk
                out.write(chunk)
                remaining -= len(chunk)
        audit = run(["e2fsck", "-fn", volume])
        (keep / "e2fsck.log").write_bytes(audit.stdout + audit.stderr)
        assert run(["debugfs", "-R", "cat /root/smb-result", volume]).stdout == DATA
        assert password not in m.log()
        print(
            "PASS signed guest command, independently extracted ext2 bytes, clean filesystem and no secret in serial",
            flush=True,
        )
    finally:
        if m:
            m.stop()
        server.shutdown()
        server.server_close()
        (served / "auth").unlink(missing_ok=True)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("keep", type=Path, help="new directory for evidence")
    p.add_argument("--image", default="zenith-smb-fixture:bookworm")
    p.add_argument(
        "--guest",
        action="store_true",
        help="also run signed transfers in installed Zenith",
    )
    p.add_argument(
        "--limit-check",
        action="store_true",
        help="also transfer exactly 32 MiB each way in the guest",
    )
    p.add_argument(
        "--disk",
        type=Path,
        help="optional clean, installed headless test disk copied by the harness",
    )
    p.add_argument(
        "--iso",
        type=Path,
        default=ROOT / "output-x64/Zenith.iso",
        help="guest install media",
    )
    p.add_argument(
        "--elf",
        type=Path,
        default=ROOT / "output-x64/Zenith.elf",
        help="expected guest kernel",
    )
    args = p.parse_args()
    if args.limit_check and not args.guest:
        p.error("--limit-check requires --guest")
    if args.disk and not args.guest:
        p.error("--disk requires --guest")
    keep = args.keep.resolve()
    keep.mkdir(mode=0o700, parents=True, exist_ok=False)
    binary = build_host(keep)
    password = secrets.token_urlsafe(24)
    with samba(args.image, password, keep) as (cid, port):
        host_checks(binary, cid, port, password, keep)
        if args.guest:
            if args.limit_check:
                run(
                    [
                        "docker",
                        "exec",
                        cid,
                        "sh",
                        "-ec",
                        (
                            "cd /srv/transfer; truncate -s 33554432 at-limit.bin; "
                            "printf ZENITH | dd of=at-limit.bin bs=1 seek=33554426 conv=notrunc status=none; "
                            "truncate -s 33554433 over-limit.bin"
                        ),
                    ]
                )
            guest_checks(
                keep, port, password, args.iso, args.elf, args.disk, args.limit_check
            )
            if args.limit_check:
                run(
                    [
                        "docker",
                        "exec",
                        cid,
                        "cmp",
                        "/srv/transfer/at-limit.bin",
                        "/srv/transfer/guest-limit.bin",
                    ]
                )
                print(
                    "PASS full 32 MiB signed transfer each way and over-limit refusal",
                    flush=True,
                )
    print("PASS SMB mandatory-signing interoperability", flush=True)


if __name__ == "__main__":
    main()
