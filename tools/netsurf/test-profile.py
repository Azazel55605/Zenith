#!/usr/bin/env python3
"""Exercise the browser profile through a POSIX-backed Zenith filesystem fixture."""
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix="nebula-profile-") as tmp:
    base = Path(tmp)
    (base / "etc").mkdir()
    (base / "users").mkdir()
    for user in ("one", "two"):
        (base / "users" / user).mkdir(mode=0o700)
    (base / "etc/passwd").write_text(
        "one:x:1001:1001:one:/users/one:/bin/sh\ntwo:x:1002:1002:two:/users/two:/bin/sh\n"
    )
    executable = base / "test-profile"
    subprocess.run(["cc", "-D_DEFAULT_SOURCE", "-DNEBULA_PROFILE_TEST", "-std=c11",
                    "-Wall", "-Wextra", "-Werror", "-fsanitize=address,undefined", "-no-pie",
                    "-I", ROOT / "userland/netsurf", "-I", ROOT / "userland/sunbeam",
                    ROOT / "userland/netsurf/profile.c", ROOT / "tools/fixtures/netsurf/profile.c",
                    "-o", executable], check=True)
    subprocess.run([executable, base], check=True)
    for store in (base / "users/one/.nebula", base / "users/two/.nebula"):
        revisions = list(store.iterdir())
        assert 1 <= len(revisions) <= 2
        for revision in revisions:
            assert revision.stat().st_mode & 0o777 == 0o700
            assert (revision / "data").stat().st_mode & 0o777 == 0o600
