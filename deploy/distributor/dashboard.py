#!/usr/bin/env python3
"""Loopback-only distributor management; nginx supplies authentication and TLS."""
import argparse
import datetime
import fcntl
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import secrets
import stat
import subprocess
import threading
import time
import urllib.parse

HERE = Path(__file__).resolve().parent
SERVICE = "zenith-distributor.service"
TIMER = "zenith-distributor.timer"
TAG = re.compile(r"[a-z][a-z0-9-]{0,63}/v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?")
UNITS = {"us": .000001, "µs": .000001, "ms": .001, "s": 1, "min": 60, "h": 3600, "d": 86400, "w": 604800}


def read_file(path, limit, tail=False):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "rb") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode): raise ValueError("not a regular file")
        if tail:
            source.seek(max(0, info.st_size - limit))
        elif info.st_size > limit: raise ValueError("file exceeds dashboard limit")
        return source.read(limit).decode("utf-8", errors="replace")


def duration(value):
    parts = list(re.finditer(r"(\d+(?:\.\d+)?)\s*(µs|us|ms|min|s|h|d|w)", value))
    if not parts or re.sub(r"(\d+(?:\.\d+)?)\s*(µs|us|ms|min|s|h|d|w)", "", value).strip(): return None
    return sum(float(part[1]) * UNITS[part[2]] for part in parts)


def system_properties(unit, properties):
    result = subprocess.run(["/usr/bin/systemctl", "show", unit, *["--property=" + prop for prop in properties]],
                            capture_output=True, text=True, timeout=5,
                            env=dict(os.environ, LC_ALL="C", TZ="UTC"))
    if result.returncode: raise ValueError("systemd status unavailable")
    values = {}
    for line in result.stdout.splitlines():
        key, separator, value = line.partition("=")
        if separator: values[key] = values.get(key, "") + ("\n" if key in values else "") + value
    return values


class Dashboard:
    def __init__(self, state, root):
        self.state, self.root = state.resolve(), root.resolve()
        self.csrf = secrets.token_urlsafe(32)
        self.run_lock = threading.Lock()

    def records(self):
        path = self.state / "releases.json"
        if not path.exists(): return {}
        data = json.loads(read_file(path, 2 * 1024 * 1024))
        if not isinstance(data, dict) or len(data) > 4096: raise ValueError("invalid build records")
        return {tag: item for tag, item in data.items() if TAG.fullmatch(tag) and isinstance(item, dict)}

    def poll_busy(self):
        try:
            descriptor = os.open(self.state / "poll.lock", os.O_RDONLY | os.O_NOFOLLOW)
        except FileNotFoundError: return False
        with os.fdopen(descriptor, "rb") as lock:
            try: fcntl.flock(lock.fileno(), fcntl.LOCK_SH | fcntl.LOCK_NB)
            except BlockingIOError: return True
        return False

    def inventory(self):
        packages, warnings = [], []
        def children(directory):
            try: return sorted(directory.iterdir())
            except FileNotFoundError: return []
            except OSError as error:
                warnings.append("Cannot list " + str(directory.relative_to(self.root)) + ": " + str(error.strerror or "access failed"))
                return []
        for repository in ("core", "extra"):
            directory = self.root / repository
            if directory.is_symlink(): warnings.append(repository + ": symlink directory refused"); continue
            for package in children(directory):
                if package.is_symlink() or not package.is_dir(): continue
                for version in children(package):
                    if version.is_symlink() or not version.is_dir(): continue
                    receipt = version / "release.json"
                    if len(packages) >= 4096: return packages, warnings + ["Package list limited to 4096 releases"]
                    try:
                        if not receipt.exists(): continue
                        data = json.loads(read_file(receipt, 16384))
                        if data["repository"] != repository or data["name"] != package.name or data["version"] != version.name:
                            raise ValueError("receipt/path mismatch")
                        if not re.fullmatch(r"[a-z][a-z0-9-]{0,63}", package.name) or len(version.name) > 32 or \
                           not re.fullmatch(r"\d+(?:\.\d+){1,2}", version.name): raise ValueError("invalid package identity")
                        size = data["archive_bytes"]
                        if not isinstance(size, int) or size < 0: raise ValueError("invalid archive size")
                        packages.append(dict(repository=repository, name=package.name, version=version.name,
                                             bytes=size, published=receipt.stat().st_mtime))
                    except (OSError, ValueError, KeyError, TypeError): warnings.append("Invalid receipt: " + str(receipt.relative_to(self.root)))
        packages.sort(key=lambda item: (item["repository"], item["name"], tuple(-int(part) for part in item["version"].split("."))
                                       if re.fullmatch(r"\d+(?:\.\d+){1,2}", item["version"]) else (0,)))
        return packages, warnings

    def snapshot(self):
        warnings = []
        try: records = self.records()
        except (OSError, ValueError): records = {}; warnings.append("Build records unavailable or invalid")
        service, timer = {}, {}
        try:
            service = system_properties(SERVICE, ["LoadState", "ActiveState", "SubState", "Result", "ExecMainStartTimestamp"])
            timer = system_properties(TIMER, ["LoadState", "ActiveState", "TimersMonotonic", "NextElapseUSecMonotonic", "LastTriggerUSec"])
        except (OSError, ValueError, subprocess.SubprocessError): warnings.append("Timer/service status unavailable")
        if service.get("ActiveState") == "failed": warnings.append("Last distributor poll failed (" + service.get("Result", "unknown") + "). Check its service journal.")
        busy = service.get("ActiveState") in ("activating", "active", "deactivating")
        try: busy = busy or self.poll_busy()
        except OSError: warnings.append("Poll lock unreadable"); busy = True
        interval = re.search(r"OnUnitInactiveUSec=([^;]+)", timer.get("TimersMonotonic", ""))
        next_seconds = duration(timer.get("NextElapseUSecMonotonic", ""))
        next_run = None
        if not busy and timer.get("ActiveState") == "active" and next_seconds is not None:
            next_run = datetime.datetime.fromtimestamp(time.time() + next_seconds - time.monotonic(), datetime.timezone.utc).isoformat()
        builds = []
        for tag, item in records.items():
            status = item.get("status", "unknown")
            if status == "complete" and item.get("dashboard_hidden") is True: continue
            if status not in ("pending", "building", "complete", "failed", "interrupted"): status = "unknown"
            if status == "building" and not busy: status = "interrupted"
            timestamps = {name: value if isinstance(value, (int, float)) and 0 < value < 40000000000 else None
                          for name, value in (("started", item.get("started")), ("finished", item.get("finished")))}
            builds.append(dict(tag=tag, commit=str(item.get("commit", ""))[:40], status=status,
                               **timestamps, error=str(item.get("error", ""))[:1000]))
        builds.sort(key=lambda item: item["started"] or 0, reverse=True)
        try: packages, issues = self.inventory(); warnings.extend(issues)
        except OSError: packages = []; warnings.append("Package inventory unavailable")
        return dict(csrf=self.csrf, busy=busy, service=service, timer=dict(state=timer.get("ActiveState", "unknown"),
                    interval=interval[1].strip() if interval else None, last_run=service.get("ExecMainStartTimestamp") or timer.get("LastTriggerUSec"),
                    next_run=next_run), builds=builds, packages=packages, warnings=warnings, refreshed=time.time())

    def log(self, tag):
        if not TAG.fullmatch(tag) or tag not in self.records(): raise ValueError("unknown build tag")
        path = self.state / (tag.replace("/", "-") + ".log")
        if not path.exists(): return "No build log yet."
        return read_file(path, 65536, tail=True)

    def output(self, build):
        tags = {tag.replace("/", "-"): tag for tag in self.records()}
        if build not in tags: raise ValueError("unknown build")
        return self.log(tags[build])

    def detailed_output(self, build, view="auto"):
        text = self.output(build)
        result = dict(text=text, phases=[], selected="controller", diagnostic="", refreshed=None)
        path = self.state / (build + ".details.json")
        if not path.exists(): return result
        details = json.loads(read_file(path, 1048576))
        if not isinstance(details, dict): raise ValueError("invalid phase output")
        rows = details.get("phases", [])
        if not isinstance(rows, list) or len(rows) > 32: raise ValueError("invalid phase output")
        for row in rows:
            if not isinstance(row, dict) or row.get("status") not in ("running", "passed", "failed", "interrupted"):
                raise ValueError("invalid phase output")
            if not all(isinstance(row.get(key), (int, float)) for key in ("started", "elapsed")):
                raise ValueError("invalid phase timing")
            result["phases"].append({key: row.get(key) for key in ("name", "status", "started", "elapsed", "updated")})
        if view != "controller" and rows:
            index = len(rows) - 1 if view == "auto" else int(view)
            if not 0 <= index < len(rows): raise ValueError("unknown phase")
            row = rows[index]
            result.update(text=str(row.get("text", ""))[-16384:], diagnostic=str(row.get("diagnostic", ""))[-16384:], selected=str(index))
        result["refreshed"] = details.get("refreshed")
        return result

    def run(self):
        with self.run_lock:
            status = self.snapshot()
            if status["busy"]: return 409, {"error": "A distributor operation is already running."}
            if status["service"].get("LoadState") != "loaded": return 503, {"error": "Distributor service is not installed or status is unavailable."}
            result = subprocess.run(["/usr/bin/sudo", "-n", "/usr/bin/systemctl", "start", "--no-block", SERVICE],
                                    capture_output=True, text=True, timeout=10)
            if result.returncode: return 503, {"error": "Could not start distributor. Check its service and dashboard sudo rule."}
            return 202, {"message": "Distributor poll queued."}


class Handler(BaseHTTPRequestHandler):
    def reply(self, code, data, kind="application/json; charset=utf-8"):
        body = json.dumps(data).encode() if kind.startswith("application/json") else data
        self.send_response(code)
        self.send_header("Content-Type", kind); self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store"); self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'")
        self.send_header("Referrer-Policy", "no-referrer"); self.end_headers(); self.wfile.write(body)

    def do_GET(self):
        url = urllib.parse.urlsplit(self.path)
        try:
            if url.path == "/manage/api/status": return self.reply(200, self.server.dashboard.snapshot())
            if url.path == "/manage/api/log":
                tags = urllib.parse.parse_qs(url.query).get("tag", [])
                if len(tags) != 1: raise ValueError("supply one build tag")
                return self.reply(200, {"text": self.server.dashboard.log(tags[0])})
            if url.path == "/manage/api/build-output":
                builds = urllib.parse.parse_qs(url.query).get("build", [])
                if len(builds) != 1: raise ValueError("supply one build ID")
                views = urllib.parse.parse_qs(url.query).get("view", ["auto"])
                if len(views) != 1: raise ValueError("supply one view")
                return self.reply(200, self.server.dashboard.detailed_output(builds[0], views[0]))
            files = {"/manage/": ("index.html", "text/html; charset=utf-8"),
                     "/manage/style.css": ("style.css", "text/css; charset=utf-8"),
                     "/manage/app.js": ("app.js", "text/javascript; charset=utf-8"),
                     "/manage/favicon.svg": ("favicon.svg", "image/svg+xml"),
                     "/manage/search.svg": ("search.svg", "image/svg+xml")}
            if url.path not in files: return self.reply(404, {"error": "Not found"})
            name, kind = files[url.path]
            return self.reply(200, (HERE / "web" / name).read_bytes(), kind)
        except ValueError as error: self.reply(400, {"error": str(error)})
        except (OSError, subprocess.SubprocessError): self.reply(503, {"error": "Distributor data unavailable"})

    def do_POST(self):
        if self.path != "/manage/api/run": return self.reply(404, {"error": "Not found"})
        try:
            origin = urllib.parse.urlsplit(self.headers.get("Origin", ""))
            valid = origin.scheme in ("http", "https") and origin.netloc == self.headers.get("Host") and \
                secrets.compare_digest(self.headers.get("X-Zenith-CSRF", "").encode(), self.server.dashboard.csrf.encode())
        except ValueError: valid = False
        if not valid:
            return self.reply(403, {"error": "Request verification failed; refresh the page."})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 1024 or self.headers.get("Content-Type", "").split(";")[0] != "application/json":
                raise ValueError("expected a small JSON request")
            if json.loads(self.rfile.read(length)) != {}: raise ValueError("run request takes no arguments")
            code, result = self.server.dashboard.run(); self.reply(code, result)
        except (ValueError, OSError, subprocess.SubprocessError): self.reply(503, {"error": "Could not queue distributor poll"})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8090)
    parser.add_argument("--state", type=Path, default=Path("/home/zenith-release/.local/state/zenith/distributor"))
    parser.add_argument("--root", type=Path, default=Path("/srv/zenith/packages"))
    args = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.dashboard = Dashboard(args.state, args.root)
    print("Dashboard listening on loopback port " + str(args.port), flush=True)
    server.serve_forever()


if __name__ == "__main__": main()
