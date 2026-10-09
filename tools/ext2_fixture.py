"""Disposable, independently formatted ext2 fixture for QEMU integration tests."""
from pathlib import Path
import re
import struct
import subprocess

START = 2048 * 512
SIZE = 16 * 1024 * 1024


def create(workdir: Path) -> Path:
    seed = workdir / "ext2-seed"
    seed.mkdir()
    (seed / "host.txt").write_text("host-ext2\n")
    (seed / "delete.bin").write_bytes(bytes([0x5a]) * (20 * 1024))
    with (seed / "sparse.bin").open("wb") as file:
        file.seek(268 * 1024)  # first double-indirect data block
        file.write(bytes([0x6b]) * 1024)
    # Occupy the first group so guest allocation must use the next group.
    (seed / "fill.bin").write_bytes(bytes([0x37]) * (8 * 1024 * 1024))
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
    managed = subprocess.run(["debugfs", "-R", "cat /fm/notes.txt", str(volume)], capture_output=True, text=True)
    report.write_text(fsck.stdout + fsck.stderr + "\nPersisted file:\n" + content.stdout + content.stderr
                      + "\nHost seed file:\n" + seed.stdout + seed.stderr
                      + "\nTruncated indirect inode:\n" + indirect.stdout + indirect.stderr
                      + "\nTruncated sparse inode:\n" + sparse.stdout + sparse.stderr
                      + "\nFile manager saved file:\n" + managed.stdout + managed.stderr)
    return (fsck.returncode == 0 and content.returncode == 0 and content.stdout == "persisted-ext2\n"
            and seed.returncode == 0 and seed.stdout == "host-ext2\n"
            and re.search(r"\bBlockcount:\s+2\b", indirect.stdout) is not None and "(IND)" not in indirect.stdout
            and re.search(r"\bBlockcount:\s+2\b", sparse.stdout) is not None and "(IND)" not in sparse.stdout and "(DIND)" not in sparse.stdout
            and shrunk.stdout == "sparse-shrunk\n"
            and managed.stdout == "file-manager-data\n"
            and any(int(block) >= 8193 for block in re.findall(r"\(0\):(\d+)", indirect.stdout)))


def create_format_disk(workdir: Path) -> Path:
    """Unformatted partition with guards outside its bounds; only the guest formats it."""
    disk = workdir / "format-disk.img"
    mbr = bytearray(512)
    struct.pack_into("<B3sB3sII", mbr, 446, 0, b"\xfe\xff\xff", 0x83,
                     b"\xfe\xff\xff", START // 512, SIZE // 512)
    mbr[510:512] = b"\x55\xaa"
    with disk.open("wb") as file:
        file.truncate(START + SIZE + 512)
        file.write(mbr)
        file.seek(START - 512)
        file.write(bytes([0xa6]) * 512)
        file.seek(START + SIZE)
        file.write(bytes([0xb7]) * 512)
    return disk


def verify_format_disk(disk: Path, report: Path, require_second_group: bool = False) -> bool:
    volume = disk.with_name("format-checked.img")
    with disk.open("rb") as file:
        mbr = file.read(512)
        file.seek(START - 512)
        before = file.read(512)
        data = file.read(SIZE)
        after = file.read(512)
    volume.write_bytes(data)
    fsck = subprocess.run(["e2fsck", "-f", "-n", str(volume)], capture_output=True, text=True)
    contents = []
    for name in ("/persist.txt", "/nested/child.txt", "/second.txt"):
        result = subprocess.run(["debugfs", "-R", "cat " + name, str(volume)], capture_output=True, text=True)
        contents.append(result.stdout)
    second = subprocess.run(["debugfs", "-R", "stat /second.txt", str(volume)], capture_output=True, text=True)
    header = subprocess.run(["dumpe2fs", "-h", str(volume)], capture_output=True, text=True)
    link_reports = []
    links_ok = True
    for name, target, sectors in (
        ("relative", "persist.txt", 0), ("absolute", "/mnt/new/persist.txt", 0),
        ("dir-link", "/mnt/new/nested", 0), ("dangling", "missing.txt", 0),
        ("slow", "./" * 30 + "persist.txt", 2),
    ):
        info = subprocess.run(["debugfs", "-R", "stat /" + name, str(volume)], capture_output=True, text=True)
        link_reports.append(info.stdout + info.stderr)
        links_ok = links_ok and "Type: symlink" in info.stdout and "Mode:  0777" in info.stdout
        links_ok = links_ok and re.search(r"\bBlockcount:\s+" + str(sectors) + r"\b", info.stdout) is not None
        if sectors == 0:
            links_ok = links_ok and ('Fast link dest: "' + target + '"') in info.stdout
        else:
            data_block = re.search(r"\(0\):(\d+)", info.stdout)
            links_ok = links_ok and data_block is not None
            if data_block is not None:
                pos = int(data_block.group(1)) * 1024
                links_ok = links_ok and data[pos:pos + len(target)] == target.encode() and data[pos + len(target)] == 0
    for name in ("temporary", "cycle-a", "cycle-b"):
        info = subprocess.run(["debugfs", "-R", "stat /" + name, str(volume)], capture_output=True, text=True)
        links_ok = links_ok and "File not found" in info.stderr
    attributes = []
    for name, mode, uid, gid in (("persist.txt", "00600", 123456, 80000), ("nested", "02750", 70001, 80001)):
        info = subprocess.run(["debugfs", "-R", "stat /" + name, str(volume)], capture_output=True, text=True)
        attributes.append(info.stdout + info.stderr)
        links_ok = links_ok and re.search(r"Mode:\s+0*" + mode.lstrip("0") + r"\s", info.stdout) is not None
        links_ok = links_ok and re.search(r"User:\s+" + str(uid) + r"\s+Group:\s+" + str(gid) + r"\s", info.stdout) is not None
    expected_mbr = bytearray(512)
    struct.pack_into("<B3sB3sII", expected_mbr, 446, 0, b"\xfe\xff\xff", 0x83,
                     b"\xfe\xff\xff", START // 512, SIZE // 512)
    expected_mbr[510:512] = b"\x55\xaa"
    report.write_text(fsck.stdout + fsck.stderr + "\nHeader:\n" + header.stdout + header.stderr
                      + "\nSaved contents:\n" + repr(contents) + "\nRetained file:\n" + second.stdout + second.stderr
                      + "\nSymlinks:\n" + "\n".join(link_reports)
                      + "\nAttributes:\n" + "\n".join(attributes))
    return (len(data) == SIZE and fsck.returncode == 0 and links_ok
            and mbr == expected_mbr and before == bytes([0xa6]) * 512 and after == bytes([0xb7]) * 512
            and contents == ["format-data\n", "child-data\n", "second-group\n"]
            and "SCRATCH" in header.stdout
            and (not require_second_group or any(int(block) >= 8193 for block in re.findall(r"\(0\):(\d+)", second.stdout))))
