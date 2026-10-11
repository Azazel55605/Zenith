#!/usr/bin/env python3
"""Run the released patcher with its kernel references visible to Cecil's resolver."""
import os
from pathlib import Path
import subprocess
import sys

args = sys.argv[1:]
for option in ("--target", "--output", "--plugs"):
    index = args.index(option) + 1
    args[index] = ";".join(str(Path(p).resolve()) for p in args[index].split(";"))
plug = args[args.index("--plugs") + 1].split(";")[0]
os.chdir(Path(plug).parent)
result = subprocess.run(["cosmos.patcher", *args])
if result.returncode != 0:
    sys.exit(result.returncode)
fixup = Path(__file__).resolve().parent / "PatcherFixup/bin/Release/net10.0/PatcherFixup.dll"
output = args[args.index("--output") + 1]
sys.exit(subprocess.run(["dotnet", str(fixup), output]).returncode)
