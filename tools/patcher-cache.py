#!/usr/bin/env python3
"""Include Zenith's post-patcher and wrapper in Cosmos's patch cache invalidation."""
import hashlib
from pathlib import Path
import sys
root=Path(__file__).resolve().parents[1]
cache=Path(sys.argv[1])
inputs=[root/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll',root/'tools/cosmos-patcher.py',Path(__file__)]
digest=hashlib.sha256(b''.join(p.read_bytes() for p in inputs)).hexdigest()
stamp=cache/'.zenith-patcher-hash'
if not stamp.exists() or stamp.read_text()!=digest:
    (cache/'.patcher-hash').unlink(missing_ok=True)
    cache.mkdir(parents=True,exist_ok=True);stamp.write_text(digest)
    print('Zenith post-patcher changed; invalidated Cosmos patch cache')
