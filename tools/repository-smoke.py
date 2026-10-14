#!/usr/bin/env python3
"""Signed core/extra discovery, planning, download/install/upgrade and reboot acceptance."""

import argparse, functools, hashlib, http.server, importlib.util, os, struct, subprocess, threading, time
from pathlib import Path
from tls_fixture import certificates, context

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "headless", ROOT / "tools/headless-smoke.py"
)
h = importlib.util.module_from_spec(spec)
spec.loader.exec_module(h)


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True


class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keep", type=Path)
    args = parser.parse_args()
    keep = args.keep.resolve()
    keep.mkdir(parents=True, exist_ok=True)
    served = keep / "served"
    served.mkdir()
    subprocess.run(
        ["dotnet", "build", str(ROOT / "tools/Zenith.PackageTool"), "-c", "Release"],
        check=True,
        stdout=subprocess.DEVNULL,
    )
    tool = [
        "dotnet",
        str(
            ROOT / "tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll"
        ),
    ]

    def run(*args):
        subprocess.run([*tool, *map(str, args)], check=True, stdout=subprocess.DEVNULL)

    run("keygen", keep / "keys")
    key = (keep / "keys/public.pem").read_bytes()
    (served / "public.pem").write_bytes(key)
    ca, _ = certificates(keep / "tls")
    (served / "tls.pem").write_bytes(ca)

    def package(name, version, data, path, records=""):
        source = keep / (name + "-" + version)
        payload = source / path.lstrip("/")
        payload.parent.mkdir(parents=True)
        payload.write_bytes(data)
        bundle = keep / (name + "-" + version + "-bundle")
        run("build", name, version, source, bundle, "--repo", "extra")
        manifest = (
            (bundle / "manifest.tsv").read_text().replace("file\t", records + "file\t")
        )
        (bundle / "manifest.tsv").write_text(manifest)
        target = served / "extra" / name / version / (name + ".zpk")
        target.parent.mkdir(parents=True, exist_ok=True)
        run("seal", bundle, keep / "keys/private.pem", target)
        return target

    script = "/usr/share/zenith/scripts/hello.sh"
    package("hello-data", "1.0.0", b"library-data\n", "/usr/share/doc/hello-data.txt")
    package(
        "hello-tools",
        "1.0.0",
        b"echo hello-online-v1\n",
        script,
        "requires\thello-data\t>=1.0,<2.0\n",
    )
    package(
        "conflicting-data",
        "1.0.0",
        b"conflict\n",
        "/usr/share/doc/conflict.txt",
        "conflicts\thello-tools\n",
    )
    now = int(time.time())

    def index(repository, generation=1, issued=None, expires=None):
        target = served / repository / "index.zri"
        target.parent.mkdir(exist_ok=True)
        target.unlink(missing_ok=True)
        run(
            "index-seal",
            repository,
            generation,
            now - 30 if issued is None else issued,
            now + 86400 if expires is None else expires,
            served,
            keep / "keys/private.pem",
            target,
        )

    original_kernel = (ROOT / "output-x64/Zenith.elf").read_bytes()
    upgraded_kernel = original_kernel + b"\nUnified package update acceptance kernel\n"
    (keep / "fixture.elf").write_bytes(upgraded_kernel)
    kernel = served / "core/zenith-kernel/2.0.0/zenith-kernel.zpk"
    kernel.parent.mkdir(parents=True)
    run("kernel-seal", "2.0.0", keep / "fixture.elf", keep / "keys/private.pem", kernel)
    index("core")
    index("extra")
    v1 = (served / "extra/index.zri").read_bytes()
    handler = functools.partial(Handler, directory=str(served))
    plain = Server(("127.0.0.1", 0), handler)
    threading.Thread(target=plain.serve_forever, daemon=True).start()
    secure = Server(("127.0.0.1", 0), handler)
    secure.socket = context(keep / "tls").wrap_socket(secure.socket, server_side=True)
    threading.Thread(target=secure.serve_forever, daemon=True).start()
    http = f"http://10.0.2.2:{plain.server_port}"
    https = f"https://10.0.2.2:{secure.server_port}"
    checks = 0
    m = None
    secret = "P4!" + os.urandom(8).hex()

    def check(passed, label):
        nonlocal checks
        checks += 1
        print(("ok   " if passed else "FAIL ") + label, flush=True)
        if not passed:
            raise RuntimeError(label)

    def command(text, marker, timeout=600):
        offset = len(m.log())
        m.send(text + "\r")
        check(m.wait(marker, offset, timeout), marker)
        raw = m.log()[offset:]
        return raw + "\n" + h.efi.smoke.COSMOS_NOISE.sub("", raw)

    def status(text, label, code=0):
        return command(text + "; echo " + label + "=$?", label + "=" + str(code))

    def boot(label, disk=None):
        machine = h.Machine(
            keep / label,
            disk,
            "session=headless" if disk else None,
            network=["-netdev", "user,id=net0", "-device", "e1000e,netdev=net0"],
        )
        if not machine.wait("headless ready", timeout=600):
            machine.stop()
            raise RuntimeError("boot failed " + label)
        check(
            machine.wait("network: online address=10.0.2.15", timeout=90),
            label + " DHCP",
        )
        return machine

    def shutdown(label):
        command("poweroff", "all filesystems unmounted")
        check(m.process.wait(timeout=60) == 0, label + " clean poweroff")
        disk = m.disk
        m.stop()
        return disk

    try:
        m = boot("live")
        command("install sata0 --yes; echo P4_ROOT=$?", "P4_ROOT=0", 300)
        disk = shutdown("live")
        m = boot("installed", disk)
        status(f"fetch {http}/public.pem /tmp/public.pem", "P4_KEY_FETCH")
        status("pkg trust /tmp/public.pem " + hashlib.sha256(key).hexdigest(), "P4_KEY")
        status(
            f"tls bootstrap {http}/tls.pem "
            + hashlib.sha256((served / "tls.pem").read_bytes()).hexdigest(),
            "P4_TLS",
        )
        status(f"pkg mirror {http}", "P4_HTTP_MIRROR_DENIED", 1)
        status(f"pkg mirror {https}", "P4_MIRROR")
        status("pkg refresh", "P4_REFRESH")
        output = status("pkg search hello", "P4_SEARCH")
        check(
            "hello-tools 1.0.0" in output and "hello-data 1.0.0" in output,
            "signed catalog search",
        )
        output = status("pkg plan hello-tools", "P4_PLAN")
        check(
            "Apply hello-data" in output and "Apply hello-tools" in output,
            "dependency included in visible plan",
        )
        status("test ! -e " + script, "P4_PLAN_NO_WRITES")
        offset = len(m.log())
        m.send("pkg install hello-tools\r")
        check(m.wait("Proceed [y/N]? ", offset), "interactive confirmation prompt")
        offset = len(m.log())
        m.send("n\r")
        check(m.wait("package operation cancelled", offset), "declined install")
        status("test ! -e " + script, "P4_DECLINE_NO_WRITES")
        status("pkg install --yes hello-tools", "P4_INSTALL")
        output = status("sh " + script, "P4_SCRIPT")
        check("hello-online-v1" in output, "installed script runs")
        status("pkg install --yes hello-tools", "P4_NOOP")
        status("pkg install --yes conflicting-data", "P4_CONFLICT", 1)
        package("hello-tools", "1.0.1", b"echo hello-combined-update\n", script,
                "requires\thello-data\t>=1.0,<2.0\n")
        index("extra", 2)
        offset = len(m.log()); m.send("pkg update\r")
        check(m.wait("Proceed [y/N]? ", offset), "combined update confirmation")
        offset = len(m.log()); m.send("n\r")
        check(m.wait("system update cancelled", offset), "combined update declined")
        output = status("sh " + script, "P4_COMBINED_DECLINE")
        check("hello-online-v1" in output, "declined system update preserves installed data")
        status("pkg update --yes", "P4_KERNEL_STAGE")
        output = status("sh " + script, "P4_COMBINED_DATA")
        check("hello-combined-update" in output, "one system update commits data and stages kernel")
        output = status("boot status", "P4_KERNEL_PENDING")
        check(
            "pending-confirmation=true" in output,
            "named kernel update retains confirmation boundary",
        )
        status("pkg update --yes", "P4_PENDING_REFUSAL", 1)
        disk = shutdown("combined")
        m = boot("combined-activated", disk)
        output = status("boot status", "P4_COMBINED_BOOT")
        check("running-kernel=" + hashlib.sha256(upgraded_kernel).hexdigest() in output,
              "combined update boots actual new signed ELF")
        status("boot confirm", "P4_COMBINED_CONFIRM")
        status("boot rollback", "P4_KERNEL_ROLLBACK")
        package(
            "hello-tools",
            "1.1.0",
            b"echo hello-online-v2\n",
            script,
            "requires\thello-data\t>=1.0,<2.0\n",
        )
        index("extra", 3)
        status("pkg refresh", "P4_REFRESH_UPGRADE")
        v2 = (served / "extra/index.zri").read_bytes()
        status("pkg update --yes", "P4_UPGRADE")
        status("pkg update --yes", "P4_UPDATE_NOOP")
        output = status("sh " + script, "P4_NEW_SCRIPT")
        check("hello-online-v2" in output, "named upgrade executes new script")
        (served / "extra/index.zri").write_bytes(v1)
        status("pkg refresh", "P4_REPLAY", 1)
        status("pkg plan hello-tools", "P4_CACHE_PRESERVED")
        bad = bytearray(v2)
        bad[-1] ^= 1
        (served / "extra/index.zri").write_bytes(bad)
        status("pkg refresh", "P4_TAMPER", 1)
        index("extra", 4, now - 2 * 86400, now - 86400)
        status("pkg refresh", "P4_EXPIRED", 1)
        (served / "extra/index.zri").write_bytes(v2)
        status("pkg refresh", "P4_VALID_AGAIN")
        missing = package(
            "unavailable-data",
            "1.0.0",
            b"never-installed\n",
            "/usr/share/doc/unavailable.txt",
        )
        bad_archive = package(
            "bad-data", "1.0.0", b"verified-later\n", "/usr/share/doc/bad-data.txt"
        )
        good_bytes = bad_archive.read_bytes()
        index("extra", 4)
        changed = bytearray(good_bytes)
        changed[-1] ^= 1
        bad_archive.write_bytes(changed)
        missing.unlink()
        status("pkg refresh", "P4_INCOMPLETE_MIRROR")
        status("pkg install --yes unavailable-data", "P4_MISSING_ARCHIVE", 1)
        status("pkg install --yes bad-data", "P4_BAD_DOWNLOAD", 1)
        status("test ! -e /usr/share/doc/bad-data.txt", "P4_BAD_NO_WRITES")
        bad_archive.write_bytes(good_bytes)
        status("pkg install --yes bad-data", "P4_RETRY_GOOD_DOWNLOAD")
        status("test ! -e /usr/share/doc/unavailable.txt", "P4_NO_PARTIAL_INSTALL")
        status("pkg verify", "P4_VERIFY")
        status(
            "cp /var/lib/zenith/pkg/repositories/core.zri /var/lib/zenith/pkg/repositories/core.zri.pkg-new",
            "P4_CACHE_STAGE",
        )
        disk = shutdown("upgraded")
        m = boot("rebooted", disk)
        output = status("pkg search hello", "P4_PERSISTENT_CATALOG")
        check("hello-tools 1.1.0" in output, "signed cache survives reboot")
        (served / "extra/index.zri").write_bytes(v1)
        status("pkg refresh", "P4_PERSISTENT_FLOOR", 1)
        status(
            "test ! -e /var/lib/zenith/pkg/repositories/core.zri.pkg-new",
            "P4_CACHE_STAGE_RECOVERED",
        )
        output = status("sh " + script, "P4_PERSISTENT_SCRIPT")
        check("hello-online-v2" in output, "installed data survives reboot")
        meta_source = keep / "meta-source"; meta_source.mkdir()
        (meta_source / "requires.tsv").write_text("requires\thello-tools\t>=1.1.0\n")
        meta_bundle = keep / "meta-bundle"
        run("build-meta", "hello-suite", "1.0.0", meta_source, meta_bundle, "--repo", "extra")
        meta_archive = served / "extra/hello-suite/1.0.0/hello-suite.zpk"; meta_archive.parent.mkdir(parents=True)
        run("seal", meta_bundle, keep / "keys/private.pem", meta_archive)
        # Same-generation protocol migration retains the old floor while using
        # a separate signed-cache domain. Legacy catalogs omit unsupported metas.
        index("extra", 4)
        extended = served / "extra/index-v3.zri"
        run("index-seal-v3", "extra", 4, now - 30, now + 86400, served, keep / "keys/private.pem", extended)
        extended_bytes = extended.read_bytes()
        status("pkg refresh", "P4_EXTENDED")
        output = status("pkg plan hello-suite", "P4_META_PLAN")
        check("hello-suite" in output, "dependency-only package has visible plan")
        status("pkg install --yes hello-suite", "P4_META_INSTALL")
        status("pkg remove hello-tools", "P4_META_DEP_REFUSAL", 1)
        extended.unlink()
        status("pkg refresh", "P4_NO_PROTOCOL_DOWNGRADE", 1)
        extended.write_bytes(extended_bytes)
        status("pkg refresh", "P4_EXTENDED_RESTORED")
        status("pkg remove hello-suite", "P4_META_REMOVE")
        status("sh " + script, "P4_META_DEPS_RETAINED")
        command("passwd root", "New password: ")
        offset = len(m.log())
        m.send(secret + "\r")
        check(m.wait("Repeat password: ", offset), "hidden password confirmation")
        offset = len(m.log())
        m.send(secret + "\r")
        check(m.wait("password updated for root", offset), "root password set")
        status("su user", "P4_USER")
        status("pkg refresh", "P4_USER_REFRESH", 1)
        status("pkg install --yes hello-tools", "P4_USER_INSTALL", 1)
        status("pkg search hello", "P4_USER_SEARCH")
        command("login root", "Password: ")
        offset = len(m.log())
        m.send(secret + "\r")
        check(m.wait("authenticated as root", offset), "root authenticated")
        status("pkg remove hello-tools hello-data bad-data", "P4_REMOVE")
        status("pkg verify", "P4_FINAL_VERIFY")
        disk = shutdown("final")
        m = None
        volume = keep / "root.ext2"
        with disk.open("rb") as source:
            source.seek(512)
            header = source.read(512)
            entries, _, stride = struct.unpack_from("<QII", header, 72)
            source.seek(entries * 512 + stride)
            entry = source.read(stride)
            start, end = struct.unpack_from("<QQ", entry, 32)
            source.seek(start * 512)
            with volume.open("wb") as target:
                remaining = (end - start + 1) * 512
                while remaining:
                    data = source.read(min(remaining, 1048576))
                    if not data:
                        raise RuntimeError("short root partition")
                    target.write(data)
                    remaining -= len(data)
        result = subprocess.run(
            ["e2fsck", "-fn", str(volume)], capture_output=True, text=True
        )
        (keep / "e2fsck.log").write_text(result.stdout + result.stderr)
        check(result.returncode == 0, "independent installed ext2 audit")
        logs = "\n".join(
            p.read_text(errors="replace") for p in keep.glob("*/serial.log")
        )
        check(secret not in logs, "secret-free serial transcripts")
        print("RESULT: " + str(checks) + " online repository checks passed", flush=True)
        return 0
    finally:
        if m:
            m.stop()
        plain.shutdown()
        secure.shutdown()
        plain.server_close()
        secure.server_close()


if __name__ == "__main__":
    raise SystemExit(main())
