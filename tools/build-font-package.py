#!/usr/bin/env python3
"""Package the existing Latin-1 font subsets and their upstream licenses."""
from pathlib import Path
import shutil
import sys
ROOT=Path(__file__).resolve().parents[1]
output=Path(sys.argv[1]);fonts=output/'usr/share/zenith/fonts';fonts.mkdir(parents=True,exist_ok=True)
for name in ('Inter-Regular.ttf','Inter-SemiBold.ttf','Hack-Regular.ttf','LICENSE-Inter.txt','LICENSE-Hack.txt'):
    shutil.copyfile(ROOT/'Resources/Fonts'/name,fonts/name)
print('Prepared independent font package: '+str(output))
