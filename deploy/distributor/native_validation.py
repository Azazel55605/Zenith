"""Bounded independent gate scheduling and private, exact-input acceptance receipts."""
from concurrent.futures import ThreadPoolExecutor, wait, FIRST_COMPLETED
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import time

DAY = 86400
GUI = {'s', 'a', 'v', 'm', 'g', 'u', 'c'}
WEIGHT = {'u': 800, 'k': 720, 'r': 420, 'n': 300, 'a': 290, 'c': 250, 's': 240, 'g': 160, 'm': 85, 'v': 40}


def digest(path):
    value = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1048576), b''): value.update(chunk)
    return value.hexdigest()


def workers(request=None, cpus=None, available=None):
    request = request or os.environ.get('ZENITH_NATIVE_JOBS', 'auto')
    if request != 'auto' and (not request.isdecimal() or not 1 <= int(request) <= 8):
        raise ValueError('ZENITH_NATIVE_JOBS must be auto or 1..8')
    if cpus is None:
        try: cpus = len(os.sched_getaffinity(0))
        except (AttributeError,OSError): cpus = os.cpu_count() or 1
    if available is None:
        try:
            rows = dict(line.split(':', 1) for line in Path('/proc/meminfo').read_text().splitlines())
            available = int(rows['MemAvailable'].split()[0])*1024
        except (OSError, ValueError, KeyError): available = 2*1024**3
    # Each gate has one 512 MiB guest plus compiler/HTTP/host overhead. Reserve
    # one CPU and 1 GiB for the host; never silently oversubscribe small VMs.
    budget = max(1, min(max(1, cpus-1), int(max(0, available-1024**3)/(1.5*1024**3))))
    wanted = 4 if request == 'auto' else int(request)
    return max(1, min(wanted, budget))


def execute(checks, run, jobs, cancelled):
    if jobs == 1:
        for check in checks: run(check)
        return
    # Fast feedback before the expensive matrix, then longest gates first so
    # kernel recovery overlaps process stress rather than extending the tail.
    front = [check for name in ('a', 's') for check in checks if check[0] == name]
    rest = sorted([check for check in checks if check not in front], key=lambda check: -WEIGHT.get(check[0], 0))
    with ThreadPoolExecutor(max_workers=jobs, thread_name_prefix='native-gate') as pool:
        try:
            for stage in (front, rest):
                pending = iter(stage); active = {}; failures = []
                def submit():
                    check = next(pending, None)
                    if check is not None: active[pool.submit(run, check)] = check
                for _ in range(jobs): submit()
                while active:
                    done, _ = wait(active, return_when=FIRST_COMPLETED)
                    for future in done:
                        check = active.pop(future)
                        try: future.result()
                        except Exception as error: failures.append((check[1], error))
                    # Do not start new work after failure. Already-running gates
                    # finish and preserve independent receipts for the next retry.
                    if not failures:
                        for _ in done: submit()
                if failures:
                    raise ValueError('Native validation failed: '+'; '.join(name+': '+str(error) for name,error in failures))
        except BaseException:
            cancelled.set()
            for future in active: future.cancel()
            raise


def identity(repo, controller, commit, firmware):
    repo, controller = Path(repo), Path(controller)
    if not firmware: raise ValueError('firmware preflight unavailable')
    if subprocess.check_output(['git','status','--porcelain','--untracked-files=all'],cwd=repo,text=True):
        raise ValueError('dirty checkout; native receipts disabled')
    artifacts = {name:digest(repo/'output-x64'/name) for name in ('Zenith.elf','Zenith.iso')}
    common = {p.name:digest(p) for p in controller.glob('validation-*.py') if p.name != 'validation-input.py'}
    common['native_validation.py'] = digest(Path(__file__))
    common['guest_auth.py'] = digest(controller.parents[1]/'tools/guest_auth.py')
    tools = {}
    for name in (os.environ.get('QEMU','qemu-system-x86_64'),'dotnet','gcc','clang','ld','as','nm','e2fsck','debugfs','mke2fs','mcopy','fsck.fat','openssl'):
        path = shutil.which(name)
        if path is None: raise ValueError('tool unavailable: '+name)
        tools[name] = dict(path=str(Path(path).resolve()), sha256=digest(path))
    backend=subprocess.check_output(['gcc','-print-prog-name=cc1'],text=True).strip()
    tools['gcc-cc1']=dict(path=backend,sha256=digest(backend))
    custom=shutil.which(os.environ.get('ZENITH_USER_CC','gcc'))
    if custom is None:raise ValueError('native C compiler missing')
    tools['user-cc']=dict(path=str(Path(custom).resolve()),sha256=digest(custom))
    sdk = subprocess.check_output(['dotnet','--version'],text=True).strip()
    tool = repo/'tools/Zenith.PackageTool/bin/Release/net10.0'
    package = {p.name:digest(p) for p in tool.iterdir() if p.suffix in ('.dll','.json') and p.is_file()}
    if 'Zenith.PackageTool.dll' not in package: raise ValueError('prebuilt PackageTool missing')
    env = {k:v for k,v in os.environ.items() if k.startswith('ZENITH_') and k not in ('ZENITH_BUILD_OUTPUT','ZENITH_NATIVE_JOBS','ZENITH_VALIDATION_FRESH')}
    base = dict(schema=1, source=commit, artifacts=artifacts, controller=common, tools=tools,
                sdk=sdk, package=package, firmware={str(p):digest(p) for p in firmware},
                python=sys.version, python_sha256=digest(sys.executable), pillow=importlib.metadata.version('Pillow'),
                environment=env, boot=Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
                kernel=os.uname().release, groups=os.getgroups(), kvm_access=os.access('/dev/kvm',os.R_OK|os.W_OK))
    return base, digest(controller/'validation-input.py')


class Receipts:
    def __init__(self, directory, base, input_policy):
        self.directory = Path(directory); self.base = base; self.input_policy = input_policy
        self.directory.mkdir(parents=True,exist_ok=True,mode=0o700)
        info=self.directory.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o022:
            raise ValueError('unsafe native receipt directory')
    def key(self, check):
        value=dict(identity=self.base, gate=list(check), input=self.input_policy if check[0] in GUI else None)
        return hashlib.sha256(json.dumps(value,sort_keys=True).encode()).hexdigest()
    def get(self, check):
        key=self.key(check); record=self.directory/(key+'.json'); log=self.directory/(key+'.log')
        try:
            for path, maximum in ((record,262144),(log,16*1024*1024)):
                info=path.lstat()
                if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o022 or info.st_size>maximum: return None
            row=json.loads(record.read_text())
            if row.get('source')!=self.base['source'] or row.get('artifacts')!=self.base['artifacts'] or row.get('gate')!=list(check):return None
            if row.get('key')!=key or row.get('passed') is not True or not -60 <= time.time()-row['completed'] <= 7*DAY: return None
            if row.get('log_sha256') != digest(log): return None
            return log
        except (OSError,ValueError,KeyError,TypeError): return None
    def _replace(self, path, data):
        descriptor, temporary=tempfile.mkstemp(prefix='.native-',dir=self.directory)
        try:
            with os.fdopen(descriptor,'wb') as output:
                os.fchmod(output.fileno(),0o600);output.write(data);output.flush();os.fsync(output.fileno())
            os.replace(temporary,path)
        finally:
            if os.path.exists(temporary):os.unlink(temporary)
    def put(self, check, log):
        key=self.key(check);data=Path(log).read_bytes()
        if len(data)>16*1024*1024:return
        self._replace(self.directory/(key+'.log'),data)
        row=dict(key=key,passed=True,completed=time.time(),log_sha256=hashlib.sha256(data).hexdigest(),
                 source=self.base['source'],artifacts=self.base['artifacts'],gate=list(check))
        self._replace(self.directory/(key+'.json'),(json.dumps(row,sort_keys=True)+'\n').encode())
    def prune(self):
        entries=[]
        for p in self.directory.glob('*.json'):
            if len(p.stem)!=64 or any(c not in '0123456789abcdef' for c in p.stem) or p.is_symlink():continue
            entries.append((p.stat().st_mtime,p))
        entries.sort(reverse=True)
        for index,(modified,p) in enumerate(entries):
            if index>=128 or time.time()-modified>7*DAY:
                p.unlink(missing_ok=True);p.with_suffix('.log').unlink(missing_ok=True)
