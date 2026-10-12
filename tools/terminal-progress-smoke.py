#!/usr/bin/env python3
"""Capture single-line desktop fetch progress and check a real local transfer."""
import argparse
import http.server
import importlib.util
from pathlib import Path
import threading
import shutil
import time
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("smoke", ROOT / "tools/smoke-test.py")
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keep", type=Path)
    args = parser.parse_args(); args.keep.mkdir(parents=True, exist_ok=True)
    started = threading.Event()
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args): pass
        def do_GET(self):
            self.send_response(200); self.send_header("Content-Length", str(2 * 1048576)); self.end_headers(); started.set()
            for _ in range(128):
                self.wfile.write(b"x" * 16384); self.wfile.flush(); time.sleep(0.1)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start(); vm = None
    try:
        vm, _ = m.boot(ROOT / "output-x64/Zenith.iso", 120, "progress visual")
        if vm is None: raise RuntimeError("desktop boot failed")
        vm.type(f"fetch http://10.0.2.2:{server.server_port}/payload /tmp/progress-test; wc -c /tmp/progress-test | logger\n")
        if not started.wait(30): raise RuntimeError("HTTP request not received")
        time.sleep(4); vm.screenshot(vm.workdir / "progress.ppm")
        Image.open(vm.workdir / "progress.ppm").save(args.keep / "progress.png")
        # wc pads counts; match its count/path rather than assuming one space.
        if not vm.wait_for("2097152 /tmp/progress-test", 180): raise RuntimeError("payload did not complete")
        (args.keep / "serial.log").write_text(vm.raw_log())
        print("PASS: desktop fetch completed; mid-transfer screenshot retained", flush=True)
    finally:
        if vm:
            vm.stop()
            if vm.serial.exists(): (args.keep / "serial.log").write_text(vm.raw_log())
            shutil.rmtree(vm.workdir, ignore_errors=True)
        server.shutdown(); server.server_close(); thread.join(timeout=5)


if __name__ == "__main__": main()
