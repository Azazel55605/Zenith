#!/usr/bin/env python3
"""Orbit gate (docs/components/ORBIT.md): the signed zenith-orbit package installs from a signed HTTPS mirror, requires
the package-center ABI (call 668, kernel 0.0.70) and carries its own about file. Headless, as root: the overview, the
package list with states and a reason for a package that needs a newer kernel, details read from the package itself
(downloaded for one that is not installed), capabilities from what a package declares, a plan with what is also
needed and the exact pkg command, a blocked plan, an ordinary user refused a change but allowed to read, apply as one
transaction, the shared history (Orbit and the shell), refresh, an update and a removal that another package needs.
On the desktop: the window opens on the overview, the updates are in the plan, Ctrl+Enter applies them, search opens
a package's details, and the history page shows what happened."""
import functools, hashlib, http.server, importlib.util, re, subprocess, sys, threading, time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from tls_fixture import certificates, context
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py'); h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
spec = importlib.util.spec_from_file_location('login', ROOT / 'tools/login-session-smoke.py'); login = importlib.util.module_from_spec(spec); spec.loader.exec_module(login)


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True


class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args): pass


def main():
    spec = importlib.util.spec_from_file_location('orbit_input', ROOT / 'deploy/distributor/validation-input.py')
    profile = importlib.util.module_from_spec(spec); spec.loader.exec_module(profile); profile.install(h.Machine)
    import recovery_console
    profile.install_monitor(recovery_console)
    monitor_command = recovery_console.monitor_command
    keep = Path(sys.argv[1]).resolve(); keep.mkdir(parents=True, exist_ok=False)
    tool = ROOT / 'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'
    subprocess.run(['dotnet', 'build', str(ROOT / 'tools/Zenith.PackageTool'), '-c', 'Release'], check=True, stdout=subprocess.DEVNULL)
    def package_tool(*args): subprocess.run(['dotnet', str(tool), *map(str, args)], check=True, capture_output=True, cwd=ROOT)
    served = keep / 'served'; served.mkdir(); package_tool('keygen', keep / 'keys')
    key = (keep / 'keys/public.pem').read_bytes(); (served / 'key.pem').write_bytes(key)
    ca, _ = certificates(keep / 'tls'); (served / 'tls.pem').write_bytes(ca)

    # zenith-orbit from source: its about file comes from packaging/about.
    subprocess.run([sys.executable, str(ROOT / 'tools/build-userland.py'), str(keep / 'orbit'), '--program', 'orbit', '--version', '0.1.0'], check=True)
    package_tool('build-native', 'zenith-orbit', '0.1.0', keep / 'orbit', keep / 'bundle-orbit', '--repo', 'core')
    manifest = (keep / 'bundle-orbit/manifest.tsv').read_text()
    assert 'requires\tzenith-native-package-center-abi\t1.0\n' in manifest and 'requires\tzenith-native-portal-abi\t1.0\n' in manifest, manifest
    assert 'file\t/usr/share/zenith/about/zenith-orbit.about\t0644\t' in manifest, manifest
    target = served / 'core/zenith-orbit/0.1.0/zenith-orbit.zpk'; target.parent.mkdir(parents=True)
    package_tool('seal', keep / 'bundle-orbit', keep / 'keys/private.pem', target)

    def data(name, version, files, records=''):
        source = keep / (name + '-' + version)
        for path, text in files.items():
            payload = source / path.lstrip('/'); payload.parent.mkdir(parents=True, exist_ok=True); payload.write_text(text)
        bundle = keep / (name + '-' + version + '-bundle')
        package_tool('build', name, version, source, bundle, '--repo', 'extra')
        text = (bundle / 'manifest.tsv').read_text().replace('file\t', records + 'file\t', 1)
        (bundle / 'manifest.tsv').write_text(text)
        out = served / 'extra' / name / version / (name + '.zpk'); out.parent.mkdir(parents=True, exist_ok=True)
        package_tool('seal', bundle, keep / 'keys/private.pem', out)

    def about(title, summary, change):
        return 'ZENITH-ABOUT\t1\ntitle\t' + title + '\nsummary\t' + summary + '\ndescription\tA test package for the Orbit gate.\ncategory\tdocuments\nicon\tfile-text\nchange\t' + change + '\n'
    data('hello-docs', '1.0.0', {'/usr/share/doc/hello-docs.txt': 'hello docs v1\n', '/usr/share/zenith/about/hello-docs.about': about('Hello docs', 'Words for the hello tools', '1.0.0\tFirst words')})
    data('hello-tools', '1.0.0', {'/usr/share/doc/hello-tools.txt': 'hello tools\n'}, 'requires\thello-docs\t>=1.0,<2.0\n')
    data('future-tool', '0.4.0', {'/usr/share/doc/future-tool.txt': 'from the future\n'}, 'requires\tzenith-native-tomorrow-abi\t1.0\n')
    now = int(time.time())
    def index(repository, generation):
        out = served / repository / 'index.zri'; out.parent.mkdir(exist_ok=True); out.unlink(missing_ok=True)
        package_tool('index-seal', repository, generation, now - 30, now + 86400, served, keep / 'keys/private.pem', out)
    index('core', 1); index('extra', 1)

    gui = {'g0': 'native run orbit | logger & sleep 6; true', 'g1': 'pkg list | logger; echo O_DESKTOP_LIST=done | logger', 'g2': 'poweroff'}
    for name, text in gui.items(): (served / name).write_text(text + '\n')
    handler = functools.partial(Handler, directory=str(served))
    plain = Server(('127.0.0.1', 0), handler); threading.Thread(target=plain.serve_forever, daemon=True).start()
    secure = Server(('127.0.0.1', 0), handler)
    secure.socket = context(keep / 'tls').wrap_socket(secure.socket, server_side=True)
    threading.Thread(target=secure.serve_forever, daemon=True).start()
    http_url = 'http://10.0.2.2:' + str(plain.server_port); https_url = 'https://10.0.2.2:' + str(secure.server_port)
    net = ['-netdev', 'user,id=net0', '-device', 'e1000e,netdev=net0']
    monitor = keep / 'monitor.sock'
    m = None; checks = 0

    def check(ok, label):
        nonlocal checks
        if not ok:
            if m is not None and monitor.exists():
                try: monitor_command(monitor, 'screendump ' + str(keep / 'failure.ppm'))
                except (OSError, RuntimeError): pass
            raise AssertionError(label + '; log tail: ' + (m.log()[-3000:] if m else ''))
        checks += 1; print('PASS ' + label, flush=True)

    def command(text, marker, timeout=300, label=None):
        off = len(m.log()); m.send(text + '\r')
        check(m.wait(marker, off, timeout), label or marker)
        return m.log()[off:]

    def status(text, label, code=0, timeout=300):
        return command(text + '; echo ' + label + '=$? | logger', 'user: ' + label + '=' + str(code), timeout)

    def press(keys, marker, label, timeout=60):
        off = len(m.log()); monitor_command(monitor, 'sendkey ' + keys + ' 30')
        check(m.wait(marker, off, timeout), label)
        return m.log()[off:]

    def typed(text, marker, label, timeout=60):
        off = len(m.log()); login.monitor_type(monitor, text)
        check(m.wait(marker, off, timeout), label)
        return m.log()[off:]

    def shot(name):
        time.sleep(1); monitor_command(monitor, 'screendump ' + str(keep / (name + '.ppm')))

    try:
        m = h.Machine(keep / 'live', network=net); assert m.wait('headless ready', timeout=180)
        assert m.wait('network: online address=10.0.2.15', timeout=90)
        status('settings set windows compact; session set-default headless', 'O_SESSION'); status('install sata0 --yes', 'O_INSTALL', timeout=900)
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'setup', disk, network=net); assert m.wait('headless ready', timeout=180)
        check(m.wait('network: online address=10.0.2.15', 0, 90), 'the installed system is online through DHCP')
        status('fetch ' + http_url + '/key.pem /root/key.pem', 'O_KEY')
        status('pkg trust /root/key.pem ' + hashlib.sha256(key).hexdigest(), 'O_TRUST')
        status('tls bootstrap ' + http_url + '/tls.pem ' + hashlib.sha256(ca).hexdigest(), 'O_TLS')
        status('pkg mirror ' + https_url, 'O_MIRROR')
        status('pkg refresh', 'O_REFRESH')
        status('pkg install --yes zenith-orbit', 'O_PACKAGE')
        for name in gui: status('fetch ' + http_url + '/' + name + ' /root/' + name, 'O_SCRIPT_' + name)
        out = status('native run orbit --version | logger', 'O_VERSION')
        check('user: orbit 0.1.0' in out, 'the installed program answers --version')
        out = status('native run orbit --status | logger', 'O_STATUS')
        check(re.search(r'ORBIT_COUNTS \| 1 \| 0 \| 2 \| 1', out) is not None, 'the overview: 1 installed, no updates, 2 available, 1 not offered')
        check('ORBIT_KEY | ' + hashlib.sha256(key).hexdigest() in out, 'the overview names the trusted key')
        check(re.search(r'ORBIT_SOURCE \| core \| 10 \| https://10\.0\.2\.2:\d+ \| 1 \|', out) is not None, 'the core source: mirror and generation')
        status('sudo -u user -- native run orbit --schedule off', 'O_SCHEDULE_DENY', 1)
        status('native run orbit --schedule off', 'O_SCHEDULE_OFF')
        out = status('native run orbit --status | logger', 'O_READ_SCHEDULE')
        check('ORBIT_SCHEDULED | 0 |' in out, 'disabled daily checks read back')
        status('native run orbit --schedule on', 'O_SCHEDULE_ON')
        out = status('native run orbit --list | logger', 'O_LIST')
        check('ORBIT_PACKAGE zenith-orbit state=0 installed=0.1.0' in out and 'title=Orbit' in out, 'Orbit is installed and titled from its about file')
        check('ORBIT_PACKAGE hello-tools state=2 installed=- candidate=1.0.0' in out, 'hello-tools is available')
        check(re.search(r'ORBIT_PACKAGE future-tool state=3 .*reason=0\.4\.0 needs a newer kernel than this one \(zenith-native-tomorrow-abi 1\.0\)', out) is not None,
              'future-tool is listed as not offered, with the reason')
        out = status('native run orbit --details hello-docs --fetch | logger', 'O_DETAILS_FETCH')
        check('ORBIT_TITLE | Hello docs' in out and 'ORBIT_FETCHED' in out, 'details of a package that is not installed: its own about file, downloaded and verified')
        check('ORBIT_CAN | Runs nothing' in out, 'a data package runs nothing')
        out = status('native run orbit --details zenith-orbit | logger', 'O_DETAILS')
        check('ORBIT_SUMMARY | Software, as a plan' in out, 'details: the installed about file')
        check('ORBIT_CAN | Install and remove packages | Only after you allow it | 1' in out and 'ORBIT_CAN | Run as root | Never by itself | 0' in out,
              'capabilities from what the package declares')
        check('ORBIT_FILE | /usr/share/zenith/native/orbit.elf' in out, 'details list the files')
        check(re.search(r'ORBIT_MINE \| \d+ \| Installed zenith-orbit 0\.1\.0 \| root \| shell', out) is not None, 'its own history: installed by the shell')
        out = status('native run orbit --plan install hello-tools | logger', 'O_PLAN')
        check('ORBIT_ACTION | install | hello-tools | - | 1.0.0 | extra' in out, 'the plan installs hello-tools')
        check(re.search(r'ORBIT_ACTION \| install \| hello-docs \| - \| 1\.0\.0 \| extra \| \d+ \| 1', out) is not None, 'and shows hello-docs as also needed')
        check('ORBIT_COMMAND | pkg install hello-tools' in out, 'and the exact pkg command')
        out = status('native run orbit --plan install future-tool | logger', 'O_PLAN_BLOCKED')
        check('ORBIT_BLOCKED | future-tool: 0.4.0 needs a newer kernel' in out, 'a plan for a package that cannot be installed is blocked, with the reason')
        out = status('sudo -n -u user native run orbit --apply install hello-tools | logger', 'O_USER')
        check('ORBIT_DENIED' in out, 'an ordinary user cannot change packages')
        out = status('sudo -n -u user native run orbit --list | logger', 'O_USER_LIST')
        check('ORBIT_PACKAGE hello-tools state=2' in out, 'but may read them')
        out = status('native run orbit --apply install hello-tools | logger', 'O_APPLY', timeout=600)
        check('ORBIT_JOB apply done 2 changes applied' in out, 'apply: one job, two changes')
        out = status('pkg list | logger', 'O_PKG_LIST')
        check('hello-tools 1.0.0' in out and 'hello-docs 1.0.0' in out, 'pkg sees what Orbit installed (one database)')
        out = status('native run orbit --history | logger', 'O_HISTORY')
        check(re.search(r'ORBIT_TRANSACTION \| \d+ \| 0 \| root \| Orbit \| 1 \| pkg install hello-tools', out) is not None, 'the history records the Orbit transaction and its command')
        check('ORBIT_CHANGE | Installed hello-docs 1.0.0' in out and 'ORBIT_CHANGE | Installed hello-tools 1.0.0' in out, 'with each change')
        check(re.search(r'ORBIT_TRANSACTION \| \d+ \| 0 \| root \| shell \| 1 \| pkg install --yes zenith-orbit', out) is not None, 'and the shell transaction before it')
        out = status('native run orbit --plan remove hello-docs | logger', 'O_PLAN_REMOVE')
        check('ORBIT_BLOCKED | hello-docs is needed by hello-tools' in out, 'removing what another package needs is blocked, with who needs it')
        # A newer hello-docs on the mirror.
        data('hello-docs', '1.1.0', {'/usr/share/doc/hello-docs.txt': 'hello docs v2\n', '/usr/share/zenith/about/hello-docs.about': about('Hello docs', 'Words for the hello tools', '1.1.0\tBetter words')})
        index('extra', 2)
        out = status('native run orbit --refresh | logger', 'O_REFRESH_ORBIT', timeout=300)
        check('ORBIT_JOB refresh done' in out, 'Orbit checks for updates (a job any user may start)')
        out = status('native run orbit --list | logger', 'O_LIST_UPDATE')
        check('ORBIT_PACKAGE hello-docs state=1 installed=1.0.0 candidate=1.1.0' in out, 'the update is offered')
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'desktop', disk, 'session=de', network=net + ['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci', '-device', 'virtio-mouse-pci'], monitor=monitor)
        check(m.wait('desktop ready (installed)', timeout=180), 'the desktop starts')
        if '--scheduled' in sys.argv[2:]:
            check(m.wait('pkg: background update check started', 0, 180), 'the desktop starts its scheduled refresh')
            title = 'from Orbit: 1 update is ready'
            check(m.wait(title, 0, 120), 'the scheduled check offers the pending update')
            posts = re.findall(r'notify: posted (\d+) ' + re.escape(title), m.log())
            check(len(posts) == 1, 'a single notice for this offer')
            toast = 'notify: toast ' + posts[0] + ' shown at '
            check(m.wait(toast, 0, 30) and m.wait(' buttons=1', m.log().index(toast), 30)
                  and re.search(re.escape(toast) + r'\d+,\d+ \d+x\d+ buttons=1\b', m.log()),
                  'the update toast has its review action')
        time.sleep(2)
        typed('sh /root/g0\n', 'user: ORBIT_READY', 'Orbit opens')
        check(m.wait('user: ORBIT_READY updates=1 installed=3', 0, 30), 'on the overview: one update, three installed')
        check(m.wait('user: ORBIT_FOCUSED', 0, 60), 'and takes focus')
        check(m.wait('user: ORBIT_PLAN 1 changes 0 blocked', 0, 30), 'the update is in the plan from the start')
        shot('overview')
        press('ctrl-2', 'user: ORBIT_PAGE Updates', 'Ctrl+2 shows the updates'); shot('updates')
        off = len(m.log()); monitor_command(monitor, 'sendkey ctrl-ret 30')
        check(m.wait('user: ORBIT_APPLY start', off, 60), 'Ctrl+Enter applies the plan')
        check(m.wait('user: ORBIT_JOB apply done 1 change applied', off, 600), 'the update is applied')
        shot('applied')
        press('ctrl-f', 'user: ORBIT_PAGE Browse', 'Ctrl+F searches in Browse')
        typed('hello-t\n', 'user: ORBIT_DETAILS hello-tools', 'Enter opens the first match'); shot('details')
        press('esc', 'user: ORBIT_PAGE Browse', 'Esc goes back')
        press('ctrl-5', 'user: ORBIT_PAGE History', 'Ctrl+5 shows the history'); shot('history')
        off = len(m.log()); monitor_command(monitor, 'sendkey esc 30')
        check(m.wait('native surface retired', off, 60), 'Esc closes Orbit')
        time.sleep(2)
        out = typed('sh /root/g1\n', 'user: O_DESKTOP_LIST=done', 'pkg lists the result')
        check('hello-docs 1.1.0' in out, 'hello-docs 1.1.0 is installed')
        typed('sh /root/g2\n', 'all filesystems unmounted', 'the desktop powers off', timeout=120)
        m.process.wait(timeout=30); m.stop(); m = None
        print(f'PASS Orbit: {checks} checks', flush=True)
    finally:
        if m is not None: m.stop()
        for server in (plain, secure): server.shutdown(); server.server_close()


if __name__ == '__main__': main()
