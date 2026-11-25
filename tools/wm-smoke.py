#!/usr/bin/env python3
"""Exercise the automatic-tiling session on a disposable installed x64 guest."""
import argparse
import functools
import hashlib
import http.server
import importlib.util
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

from PIL import Image
import boot_image
from recovery_console import monitor_command

ROOT = Path(__file__).resolve().parents[1]
# Exercise normal shells with short physical keystrokes. Expanding environment
# variables in the sourced file also verifies which terminal received the input.
SCRIPTS = {
    "m": "export TILE=master; echo $TILE | logger",
    "s": "export TILE=stack; echo $TILE | logger",
    "t": "export TILE=third; echo $TILE | logger",
    "n": "export TILE=polaris; echo $TILE | logger",
    "i": "echo $TILE | logger",
    "f": "fps | logger",
    "c": "echo WM_COLUMNS=$COLUMNS | logger; logger WM_GRID_DONE",
    "p": "settings set panel-position top && logger WM_TOP",
    "r": "settings set tile-split 65 && logger WM_SPLIT",
    "g": "settings set tile-gaps 12 16 && logger WM_GAPS",
    "z": "settings set tile-gaps 0 0 && logger WM_NO_GAPS",
    "q": "settings show | logger",
    "b": "session status | logger",
}


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def serve_setup(keep):
    """Seed two named desktops and install an independently signed native terminal."""
    served = keep / "served"
    served.mkdir()
    keydir = keep / "keys"
    payload = keep / "payload"
    bundle = keep / "bundle"
    subprocess.run(["dotnet", "build", str(ROOT / "tools/Zenith.PackageTool"), "-c", "Release"], check=True, stdout=subprocess.DEVNULL)
    tool = ROOT / "tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll"
    def package(*args):
        subprocess.run(["dotnet", str(tool), *map(str, args)], check=True, stdout=subprocess.DEVNULL)

    subprocess.run([sys.executable, "-B", str(ROOT / "tools/build-userland.py"), str(payload), "--program", "polaris", "--version", "0.1.0"], check=True)
    package("keygen", keydir)
    package("build-native", "zenith-polaris", "0.1.0", payload, bundle)
    package("seal", bundle, keydir / "private.pem", served / "polaris.zpk")
    public = (keydir / "public.pem").read_bytes()
    (served / "key.pem").write_bytes(public)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(http.server.SimpleHTTPRequestHandler, directory=served))
    url = f"http://10.0.2.2:{server.server_port}"
    commands = [f"printf '%s\\n' '{command}' > /root/{name}" for name, command in SCRIPTS.items()]
    commands.extend([
        f"fetch {url}/key.pem /root/wm-key",
        f"pkg trust /root/wm-key {hashlib.sha256(public).hexdigest()}",
        f"fetch {url}/polaris.zpk /root/polaris.zpk",
        "pkg install /root/polaris.zpk",
        "settings set animations off",
        "settings set materials solid",
        "settings set taskbar docked",
        "printf 'VERSION=1\\nWORKSPACES_ENTRIES=0:V29yaw==,1:TGFi\\n' > /etc/zenith/desktop-preferences.conf",
        "session set-default wm",
        "logger WM_SAVED",
    ])
    (served / "setup").write_text("\n".join(command + " || exit 1" for command in commands) + "\n")
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, url


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keep", type=Path, help="new, short path for the disposable disks and screenshots")
    parser.add_argument("--build", type=Path, default=ROOT / "output-x64", help="directory with Zenith.iso and Zenith.elf")
    parser.add_argument("--disk", type=Path, help="reuse a copy of an unconfigured matching installed fixture")
    parser.add_argument("--keyboard", choices=("virtio", "ps2"), default="virtio")
    args = parser.parse_args()
    keep = args.keep.resolve()
    keep.mkdir(parents=True, exist_ok=False)
    build = args.build.resolve()
    h = load("wm_headless", ROOT / "tools/headless-smoke.py")
    login = load("wm_input", ROOT / "tools/login-session-smoke.py")
    graphics = ["-device", "virtio-gpu-pci", "-device", "virtio-mouse-pci"]
    if args.keyboard == "virtio":
        graphics += ["-device", "virtio-keyboard-pci"]
    monitor = keep / "monitor.sock"
    m = None
    server = None

    def expect(marker, since=0, timeout=45):
        if not m.wait(marker, since, timeout):
            raise RuntimeError(f"missing {marker!r} in {m.serial}")

    def console(command, marker, timeout=45):
        since = len(m.log())
        login.monitor_type(monitor, command + "\n")
        expect(marker, since, timeout)
        return m.log()[since:]

    def key(chord):
        monitor_command(monitor, "sendkey " + chord + " 60")
        time.sleep(.22)

    def snapshot(label):
        raw = console(". f", "user: last motion:")
        pattern = r"user: top window: (-?\d+) (-?\d+) (\d+) (\d+) pill: (\d+) (\d+) (\d+) (\d+)"
        top = re.search(pattern, raw)
        if not top:
            raise AssertionError(f"{label} lacks focused window bounds")
        x, y, w, h_, px, py, pw, ph = map(int, top.groups())
        path = keep / (label + ".ppm")
        monitor_command(monitor, "screendump " + str(path))
        with Image.open(path) as image:
            frame = image.convert("RGB")
        assert w > 0 and h_ > 0 and 0 <= x <= frame.width-w and 0 <= y <= frame.height-h_, (label, top.groups(), frame.size)
        return (x, y, w, h_), (px, py, pw, ph), frame

    def open_terminal():
        since = len(m.log())
        key("ctrl-alt-shift-t")
        expect("windows: opened at", since)

    def pixel(frame, bounds, dx, dy):
        x, y, w, h_ = bounds
        return frame.getpixel((x + min(w - 1, dx), y + min(h_ - 1, dy)))

    def shutdown(graphical):
        if graphical:
            console("poweroff", "all filesystems unmounted")
        else:
            since = len(m.log())
            m.send("poweroff\r")
            expect("all filesystems unmounted", since)
        assert m.process.wait(timeout=30) == 0
        disk = m.disk
        m.stop()
        return disk

    try:
        disk = args.disk
        if disk is None:
            m = h.Machine(keep / "live", iso=build / "Zenith.iso")
            expect("headless ready", timeout=180)
            since = len(m.log())
            m.send("install sata0 --yes; echo WM_INSTALL=$? | logger\r")
            expect("user: WM_INSTALL=0", since, 900)
            disk = shutdown(False)
            m = None
        boot_image.verify(disk, build / "Zenith.elf")

        # Use the real floating desktop to configure the next interactive session.
        # The desktop list is the same persistent format written by Meridian.
        server, url = serve_setup(keep)
        net = ["-netdev", "user,id=net", "-device", "e1000e,netdev=net"]
        m = h.Machine(keep / "de", disk, network=net + graphics, monitor=monitor)
        expect("desktop ready (installed)", timeout=180)
        expect("network: online address=", timeout=90)
        console(f"fetch {url}/setup /root/w && sh /root/w", "user: WM_SAVED", timeout=180)
        de_bounds, _, _ = snapshot("floating-de")
        console(". g", "user: WM_GAPS")
        assert snapshot("floating-de-with-gaps")[0] == de_bounds
        console(". z", "user: WM_NO_GAPS")
        print("PASS saved tiling gaps leave floating desktop geometry unchanged", flush=True)
        disk = shutdown(True)
        m = None

        m = h.Machine(keep / "wm", disk, network=graphics, monitor=monitor)
        expect("wm ready (installed)", timeout=180)
        console(". b", "active=wm (saved default)")
        key("ctrl-alt-right")
        open_terminal()
        console(". m", "user: master")
        full, pill, frame = snapshot("single-bottom")
        assert full[2] >= frame.width - 32 and full[3] >= frame.height - 100, (full, frame.size)
        assert full[1] + full[3] <= pill[1] and pill[3] > 0, (full, pill)
        print("PASS saved WM boots and its single tile fills the area above the panel", flush=True)

        open_terminal()
        console(". s", "user: stack")
        stack, _, _ = snapshot("stack")
        key("alt-left")
        console(". i", "user: master")
        master, _, panes = snapshot("master")
        assert master[0] == full[0] and master[0] + master[2] <= stack[0]
        assert stack[0] + stack[2] == full[0] + full[2]
        assert (master[1], master[3]) == (stack[1], stack[3]) == (full[1], full[3])
        # Both terminal interiors must now have a rendered window edge at the split.
        # Take an empty row near the bottom, away from either terminal's text.
        y = master[1] + master[3] - 24
        inside = panes.getpixel((master[0] + 36, y))
        seam = panes.crop((master[0]+master[2]-8, y, stack[0]+8, y+1))
        assert panes.getpixel((stack[0]+36, y)) == inside and len(seam.getcolors(seam.width)) > 1, "second terminal border did not render"
        print("PASS automatic split, presented borders of both terminals, and keyboard focus", flush=True)

        open_terminal()
        console(". t", "user: third")
        lower, _, _ = snapshot("three-lower")
        key("alt-up")
        console(". i", "user: stack")
        upper, _, _ = snapshot("three-upper")
        assert upper[:3] == stack[:3] and (lower[0], lower[2]) == (stack[0], stack[2]), (upper, lower, stack)
        assert upper[1]+upper[3] == lower[1] and upper[3]+lower[3] == stack[3], (upper, lower, stack)
        console(". g", "user: WM_GAPS")
        spaced_upper, _, _ = snapshot("gap-stack-upper")
        key("alt-down")
        console(". i", "user: third")
        spaced_lower, _, _ = snapshot("gap-stack-lower")
        key("alt-left")
        console(". i", "user: master")
        spaced_master, _, _ = snapshot("gap-master")
        x, y0, width, height = full
        assert spaced_master[:2] == (x+16, y0+16) and spaced_master[3] == height-32, spaced_master
        assert spaced_upper[0] == spaced_lower[0] == spaced_master[0]+spaced_master[2]+12
        assert spaced_upper[1] == y0+16 and spaced_lower[1] == spaced_upper[1]+spaced_upper[3]+12
        assert spaced_lower[0]+spaced_lower[2] == x+width-16 and spaced_lower[1]+spaced_lower[3] == y0+height-16
        console(". z", "user: WM_NO_GAPS")
        assert snapshot("zero-gap-master")[0] == master
        key("alt-pgdn")
        console(". i", "user: stack")
        print("PASS live inner and outer gaps on all three tiles, and zero restores the touching layout", flush=True)
        key("alt-pgdn")
        console(". i", "user: third")
        key("alt-pgup")
        console(". i", "user: stack")
        key("alt-down")
        console(". i", "user: third")
        key("ctrl-d")
        # Closing a shell returns input to whichever tile was previously active.
        # Focusing the master from either stack row now has a single destination.
        key("alt-left")
        console(". i", "user: master")
        key("alt-right")
        assert snapshot("two-again")[0] == stack
        key("alt-left")
        print("PASS third window divides the stack and closing it restores the previous split", flush=True)

        key("alt-equal")
        grown, _, _ = snapshot("wider-master")
        assert grown[0] == master[0] and grown[2] > master[2], (master, grown)
        console(". r", "user: WM_SPLIT")
        saved, _, resized = snapshot("saved-split")
        assert saved[0] == master[0] and saved[2] > grown[2], (saved, grown)
        previous_edge = panes.crop((master[0]+master[2]-2, y, master[0]+master[2]+2, y+1))
        saved_edge = resized.crop((saved[0]+saved[2]-2, y, saved[0]+saved[2]+2, y+1))
        assert saved_edge.tobytes() == previous_edge.tobytes(), "saved split did not present the new tile edge"
        key("alt-shift-right")
        console(". i", "user: master")
        moved, _, _ = snapshot("swapped")
        assert moved[0] == saved[0] + saved[2], (moved, saved)
        print("PASS adjustable split and swapping the focused window", flush=True)

        key("ctrl-alt-shift-1")
        console(". i", "user: master")
        key("ctrl-alt-2")
        console(". i", "user: stack")
        remaining, _, _ = snapshot("after-move")
        assert remaining == full, (remaining, full)
        print("PASS moving a window preserves its shell and reflows its original workspace", flush=True)

        console(". p", "user: WM_TOP")
        above, pill, top_frame = snapshot("single-top")
        assert above[0] == full[0] and above[2:] == full[2:] and above[1] > full[1], (above, full)
        assert pill[1]+pill[3] <= above[1] and pill[3] > 0 and above[1]+above[3] >= top_frame.height-16, (above, pill)
        assert pixel(top_frame, above, 36, above[3]-24) == inside
        # Compare the actual launcher mark at both edges. The neutral fill in
        # the middle of the panel intentionally matches the decoration above it.
        height = above[1] - full[1]
        top_icon = (8, 3, 44, height - 3)
        bottom_icon = (8, frame.height - height + 3, 44, frame.height - 3)
        launcher = top_frame.crop(top_icon).tobytes()
        assert launcher == frame.crop(bottom_icon).tobytes() and launcher != frame.crop(top_icon).tobytes(), "panel launcher did not move to the top"
        console(". q", "panel-position=top")
        print("PASS live move to the top reserves the opposite screen edge", flush=True)
        console(". g", "user: WM_GAPS")
        single, _, _ = snapshot("single-top-with-gaps")
        assert single == (above[0]+16, above[1]+16, above[2]-32, above[3]-32), (single, above)

        # Polaris reports the grid it actually received through the native terminal ABI.
        # It runs as the second tile; the shell marker distinguishes it from the recovery terminal.
        since = len(m.log())
        key("ctrl-alt-t")
        expect("native surface opened", since)
        console(". n", "user: polaris")
        def native_grid(label):
            raw = console(". c", "user: WM_GRID_DONE")
            columns = re.search(r"user: WM_COLUMNS=(\d+)", raw)
            if not columns:
                raise AssertionError(f"{label} missing the native shell's viewport width")
            return int(columns[1]), snapshot(label)[0]

        compact, docked = native_grid("polaris-tiled")
        assert docked[0] > single[0] and docked[1] == single[1] and docked[3] == single[3], docked
        key("f11")
        wide, screen = native_grid("polaris-fullscreen")
        assert wide > compact and screen == (0, 0, frame.width, frame.height), (compact, wide, screen)
        key("f11")
        assert native_grid("polaris-restored") == (compact, docked)
        key("ctrl-alt-f")
        _, floating = native_grid("polaris-floating")
        assert floating != docked, (floating, docked)
        console(". z", "user: WM_NO_GAPS")
        assert snapshot("floating-wm-without-gaps")[0] == floating
        console(". g", "user: WM_GAPS")
        assert snapshot("floating-wm-with-gaps")[0] == floating
        key("ctrl-alt-f")
        assert native_grid("polaris-rejoined") == (compact, docked)
        since = len(m.log())
        login.monitor_type(monitor, "exit\n")
        expect("native surface retired", since)
        assert snapshot("native-closed")[0] == single
        print("PASS signed Polaris negotiates its tiled, fullscreen and restored viewports, floats, rejoins, and closes", flush=True)

        disk = shutdown(True)
        m = None
        m = h.Machine(keep / "reboot", disk, network=graphics, monitor=monitor)
        expect("wm ready (installed)", timeout=180)
        console(". q", "tile-gaps=12,16")
        key("ctrl-alt-right")
        open_terminal()
        persisted, _, _ = snapshot("reboot-top")
        assert persisted == single, (persisted, single)
        shutdown(True)
        m = None
        print("PASS WM gaps, workspace order, and top panel survive an installed reboot", flush=True)
    finally:
        if server is not None:
            server.shutdown()
            server.server_close()
        if m is not None:
            if monitor.exists():
                try:
                    monitor_command(monitor, "screendump " + str(keep / "failure.ppm"))
                except (OSError, RuntimeError):
                    pass
            m.stop()


if __name__ == "__main__":
    main()
