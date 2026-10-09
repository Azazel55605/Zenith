"""Disposable, independently formatted ext2 fixture for QEMU integration tests."""
from pathlib import Path
import re
import struct
import subprocess

START = 2048 * 512
SIZE = 8 * 1024 * 1024


def create(workdir: Path) -> Path:
    seed = workdir / "ext2-seed"
    seed.mkdir()
    (seed / "host.txt").write_text("host-ext2\n")
    (seed / "delete.bin").write_bytes(bytes([0x5a]) * (20 * 1024))
    with (seed / "sparse.bin").open("wb") as file:
        file.seek(268 * 1024)  # first double-indirect data block
        file.write(bytes([0x6b]) * 1024)
    volume = workdir / "ext2-volume.img"
    with volume.open("wb") as file:
        file.truncate(SIZE)
    subprocess.run(["mke2fs", "-q", "-F", "-t", "ext2", "-b", "1024", "-I", "128",
                    "-O", "none,filetype", "-E", "lazy_itable_init=0", "-d", str(seed), str(volume)],
                   check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    disk = workdir / "ext2-disk.img"
    mbr = bytearray(512)
    struct.pack_into("<B3sB3sII", mbr, 446, 0, b"\xfe\xff\xff", 0x83,
                     b"\xfe\xff\xff", START // 512, SIZE // 512)
    mbr[510:512] = b"\x55\xaa"
    with disk.open("wb") as file:
        file.truncate(START + SIZE)
        file.seek(0)
        file.write(mbr)
        file.seek(START)
        file.write(volume.read_bytes())
    return disk


def verify(disk: Path, report: Path) -> bool:
    volume = disk.with_name("ext2-checked.img")
    with disk.open("rb") as file:
        file.seek(START)
        data = file.read(SIZE)
    if len(data) != SIZE:
        report.write_text("FAIL: truncated fixture disk\n")
        return False
    volume.write_bytes(data)
    fsck = subprocess.run(["e2fsck", "-f", "-n", str(volume)], capture_output=True, text=True)
    content = subprocess.run(["debugfs", "-R", "cat /persist.txt", str(volume)], capture_output=True, text=True)
    seed = subprocess.run(["debugfs", "-R", "cat /host.txt", str(volume)], capture_output=True, text=True)
    indirect = subprocess.run(["debugfs", "-R", "stat /indirect", str(volume)], capture_output=True, text=True)
    sparse = subprocess.run(["debugfs", "-R", "stat /sparse.bin", str(volume)], capture_output=True, text=True)
    shrunk = subprocess.run(["debugfs", "-R", "cat /sparse.bin", str(volume)], capture_output=True, text=True)
    report.write_text(fsck.stdout + fsck.stderr + "\nPersisted file:\n" + content.stdout + content.stderr
                      + "\nHost seed file:\n" + seed.stdout + seed.stderr
                      + "\nTruncated indirect inode:\n" + indirect.stdout + indirect.stderr
                      + "\nTruncated sparse inode:\n" + sparse.stdout + sparse.stderr)
    return (fsck.returncode == 0 and content.returncode == 0 and content.stdout == "persisted-ext2\n"
            and seed.returncode == 0 and seed.stdout == "host-ext2\n"
            and re.search(r"\bBlockcount:\s+2\b", indirect.stdout) is not None and "(IND)" not in indirect.stdout
            and re.search(r"\bBlockcount:\s+2\b", sparse.stdout) is not None and "(IND)" not in sparse.stdout and "(DIND)" not in sparse.stdout
            and shrunk.stdout == "sparse-shrunk\n")
