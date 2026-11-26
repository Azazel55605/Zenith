#!/usr/bin/env python3
"""Install a test-signed NetSurf on a disposable disk and follow rendered file, HTTP and HTTPS links."""
import argparse
from contextlib import ExitStack
from functools import partial
import hashlib
import http.server
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import threading
import time

from PIL import Image, ImageChops
import boot_image
import guest_cpu_frame
import guest_netsurf_png
from tls_fixture import certificates, context

ROOT = Path(__file__).resolve().parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / filename)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def start_server(stack, served, requests, tls=None):
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            requests.append(self.path)
            return super().do_GET()

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), partial(Handler, directory=served))
    if tls:
        server.socket = tls.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    stack.callback(server.server_close)
    stack.callback(server.shutdown)
    return ("https" if tls else "http") + f"://10.0.2.2:{server.server_port}"


def fixture_file(directory, filename, color, title, label, target=None, external=False, png=False):
    css = ("body{background:#fff;color:#14212a;margin:24px}h1{font-size:28px}"
           f".swatch{{display:block;background-color:#{color};color:white;font-size:20px;"
           "font-weight:bold;padding:16px;width:238px;height:50px;text-decoration:none}")
    if external:
        (directory / "theme.css").write_text(css)
        style = '<link rel="stylesheet" href="/theme.css">'
    else:
        style = f"<style>{css}</style>"
    element = f'<a class="swatch" href="{target}">{label}</a>' if target else f'<p class="swatch">{label}</p>'
    (directory / filename).write_text(f'<!doctype html><html><head><title>{title}</title>{style}</head>'
                                      f'<body><h1>{title}</h1>{element}'
                                      f'{guest_netsurf_png.image_html() if png else ""}</body></html>\n')


def rendered_swatch(mouse, keep, name, color, png=False):
    deadline = time.monotonic() + 60
    rgb = bytes.fromhex(color)
    path = keep / (name + ".ppm")
    while True:
        frame = Image.open(io.BytesIO(mouse.capture(path))).convert("RGB")
        masks = [band.point([255 if i == target else 0 for i in range(256)])
                 for band, target in zip(frame.split(), rgb)]
        mask = ImageChops.multiply(ImageChops.multiply(masks[0], masks[1]), masks[2])
        bounds = mask.getbbox()
        if bounds and (not png or guest_netsurf_png.is_rendered(frame)):
            left, top, right, bottom = bounds
            if right-left >= 180 and bottom-top >= 40 and mask.histogram()[255] > 8000:
                # Text must be drawn *inside* the author's colored CSS box.
                # Choose a glyph on the link itself; block padding surrounds it.
                ink = frame.crop((left+3, top+3, right-3, bottom-3))
                white = [b.point([255 if i >= 235 else 0 for i in range(256)]) for b in ink.split()]
                white = ImageChops.multiply(ImageChops.multiply(white[0], white[1]), white[2])
                row_counts = [white.crop((0, y, white.width, y+1)).histogram()[255] for y in range(white.height)]
                if max(row_counts) > 20:
                    iy = row_counts.index(max(row_counts))
                    ix = [i for i in range(white.width) if white.getpixel((i, iy))]
                    print("PASS browser drew", name, "page and text" + (" with verified PNG pixels" if png else ""), flush=True)
                    frame.save(keep / (name + ".png"))
                    return left + 3 + ix[len(ix)//2], top + 3 + iy
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Missing {name} content with text; screen retained at {path}")
        time.sleep(.12)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keep", type=Path, help="new directory for screenshots, logs and fixture copies")
    parser.add_argument("--host", action="store_true", help="run the fetcher contract against the previously built upstream core")
    parser.add_argument("--disk", type=Path, help="installed disposable Zenith disk with recovery Terminal")
    parser.add_argument("--elf", type=Path, help="exact ELF from this disk's build")
    parser.add_argument("--payload", type=Path, help="existing build-userland payload to package")
    parser.add_argument("--version", default="0.1.0")
    args = parser.parse_args()
    keep = args.keep.resolve()
    if args.host:
        core = ROOT / "obj/netsurf-core"
        source = core / "netsurf-all-3.11/netsurf"
        if not (core / "libnetsurf.a").is_file():
            parser.error("build the netsurf payload first to provide its upstream core")
        keep.mkdir(parents=True, exist_ok=False)
        exe = keep / "fetch-contract"
        subprocess.run(["cc", "-std=c11", "-D_DEFAULT_SOURCE", "-g", "-fsanitize=address,undefined",
                        "-Wl,--gc-sections", "-no-pie", "-Wall", "-Wextra", "-Werror", "-I", core / "include",
                        "-I", source, "-I", source / "include", ROOT / "tools/fixtures/netsurf-fetch.c",
                        ROOT / "userland/netsurf/fetch.c", core / "libnetsurf.a", "-o", exe], check=True)
        subprocess.run([exe], check=True)
        return
    if not args.disk or not args.elf:
        parser.error("guest validation requires --disk and its matching --elf")
    if len(str(keep / "desktop/serial.sock").encode()) > 100:
        parser.error("evidence path is too long for the QEMU monitor")
    keep.mkdir(parents=True, exist_ok=False)
    boot_image.verify(args.disk, args.elf)
    h = load("ns_vm", "tools/headless-smoke.py")
    login = load("ns_login", "tools/login-session-smoke.py")
    inputs = load("ns_input", "deploy/distributor/validation-input.py")
    terminal = load("ns_terminal", "deploy/distributor/validation-terminal.py")
    load("ns_accel", "deploy/distributor/validation-accelerator.py").install(h)
    inputs.install(h.Machine)

    payload = args.payload if args.payload else keep / "payload"
    if args.payload is None:
        subprocess.run([sys.executable, "-B", ROOT / "tools/build-userland.py", payload,
                        "--program", "netsurf", "--version", args.version], check=True)
    subprocess.run(["dotnet", "build", ROOT / "tools/Zenith.PackageTool", "-c", "Release", "--nologo"], check=True)
    tool = ROOT / "tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll"
    served = keep / "served"
    served.mkdir()

    def package(*args):
        subprocess.run(["dotnet", tool, *args], check=True)

    package("keygen", keep / "keys")
    key = (keep / "keys/public.pem").read_bytes()
    (served / "app-key").write_bytes(key)
    package("build-native", "zenith-netsurf", args.version, payload, keep / "bundle", "--repo", "extra")
    package("seal", keep / "bundle", keep / "keys/private.pem", served / "browser.zpk")
    ca, fingerprint = certificates(keep / "tls")
    (served / "ns-ca.pem").write_bytes(ca)

    net = ["-netdev", "user,id=net0", "-device", "e1000e,netdev=net0"]
    images = load("ns_audit", "tools/desktop-launch-replay.py")
    monitor = keep / "monitor.sock"
    http_requests, https_requests = [], []
    with ExitStack() as resources:
        origin = start_server(resources, served, http_requests)
        secure = start_server(resources, served, https_requests, context(keep / "tls"))
        fixture_file(served, "local.html", "9635b2", "Local document", "Open HTTP page", origin + "/first.html", png=True)
        fixture_file(served, "first.html", "167b57", "HTTP styles", "Open secure page", secure + "/secure.html", external=True)
        fixture_file(served, "secure.html", "a74726", "Verified HTTPS", "Secure page loaded")
        (served / "b").write_text("native run netsurf file:///root/ns-local.html | logger; echo NS_BROWSER_EXIT=$? | logger\n")
        (served / "q").write_text("poweroff\n")
        setup = h.Machine(keep / "setup", disk=args.disk, argument="session=headless", network=net)
        try:
            if not setup.wait("headless ready") or not setup.wait("network: online address=10.0.2.15"):
                raise RuntimeError("Installed guest did not start its headless network session")
            def command(text, name):
                offset = len(setup.log())
                setup.send(text + "; echo " + name + "=$? | logger\r")
                if not setup.wait("user: " + name + "=", offset, timeout=150):
                    raise RuntimeError("Missing guest status for " + name)
                if not setup.wait("user: " + name + "=0", offset, timeout=1):
                    raise RuntimeError(f"{name} failed; see {setup.serial}")
            for remote, local in [("app-key", "ns-key"), ("ns-ca.pem", "ns-ca.pem"), ("local.html", "ns-local.html"),
                                  ("b", "b"), ("q", "q"), ("browser.zpk", "ns-browser.zpk")]:
                command(f"recovery fetch --kernel --replace {origin}/{remote} /root/{local}", "NS_GET_" + remote.split(".")[0].replace("-", "_"))
            command("pkg trust /root/ns-key " + hashlib.sha256(key).hexdigest(), "NS_TRUST")
            command("tls trust /root/ns-ca.pem " + fingerprint, "NS_TLS")
            command("pkg install /root/ns-browser.zpk", "NS_INSTALL")
            setup.send("poweroff\r")
            if not setup.wait("all filesystems unmounted") or setup.process.wait(timeout=45):
                raise RuntimeError("Setup did not shut down cleanly")
        finally:
            setup.stop()
        machine = h.Machine(keep / "desktop", disk=setup.disk, argument="session=de",
                            network=net + ["-device", "virtio-gpu-pci", "-device", "virtio-keyboard-pci"], monitor=monitor)
        try:
            if not machine.wait("desktop ready (installed)"):
                raise RuntimeError("Graphical session failed to start")
            mouse = inputs.RenderedMouse(monitor, inputs.monitor_text)
            try:
                guest_cpu_frame.prompt(mouse, terminal, timeout=4)
            except RuntimeError:
                inputs.monitor_text(monitor, "sendkey ctrl-alt-shift-t 50")
                guest_cpu_frame.prompt(mouse, terminal)
            login.monitor_type(monitor, "sh /root/b\n")
            def click(x, y):
                (cx, cy), _ = mouse.position()
                mouse.command(f"mouse_move {x-cx} {y-cy}")
                mouse.command("mouse_button 1")
                mouse.command("mouse_button 0")
            position = rendered_swatch(mouse, keep, "local", "9635b2", png=True)
            click(*position)
            position = rendered_swatch(mouse, keep, "http", "167b57")
            click(*position)
            rendered_swatch(mouse, keep, "https", "a74726")
            if "/first.html" not in http_requests or "/theme.css" not in http_requests or "/secure.html" not in https_requests:
                raise RuntimeError("Page, stylesheet or TLS request missing from fixture server")
            inputs.monitor_text(monitor, "sendkey ctrl-q 30")
            if not machine.wait("user: NS_BROWSER_EXIT=0", timeout=30):
                raise RuntimeError("Browser did not close cleanly")
            guest_cpu_frame.prompt(mouse, terminal)
            login.monitor_type(monitor, "sh /root/q\n")
            if not machine.wait("all filesystems unmounted") or machine.process.wait(timeout=45):
                raise RuntimeError("Desktop did not shut down cleanly")
        except BaseException:
            try:
                inputs.RenderedMouse(monitor, inputs.monitor_text).capture(keep / "failure.ppm")
            except (OSError, RuntimeError, ValueError):
                pass
            raise
        finally:
            (keep / "requests.json").write_text(json.dumps({"http": http_requests, "https": https_requests}, indent=2))
            machine.stop()
        images.audit(machine.disk, args.elf, keep)
    print("PASS signed NetSurf installation, PNG pixels, local/HTTP/verified HTTPS navigation and independent ext2 audit")


if __name__ == "__main__":
    main()
