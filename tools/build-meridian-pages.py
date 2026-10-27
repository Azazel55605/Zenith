#!/usr/bin/env python3
"""Compile the reviewed Meridian inventory into bounded userland page metadata."""
from pathlib import Path
import json
import re
import sys
ROOT = Path(__file__).resolve().parents[1]
def rows():
    result=[]; category=''
    for line in (ROOT/'docs/MERIDIAN-PAGES.md').read_text().splitlines():
        m=re.match(r'## [0-9]+\. (.+)',line)
        if m: category=m[1]
        m=re.match(r'\| `([a-z.-]+)` — ([^|]+) \| ([^|]+) \| ([^|]+) \|',line)
        if m:
            identity,title,description,foundation=(x.strip() for x in m.groups())
            result.append((identity,title,category,description,foundation))
    if len(result)!=43 or len({r[0] for r in result})!=43 or len({r[2] for r in result})!=10:
        raise ValueError('Meridian inventory must contain 43 unique pages and 10 categories')
    if result[16][0]!='style.colors' or result[38][0]!='system.about':raise ValueError('update page constants after inventory reordering')
    for row in result:
        if any(len(x.encode())>4096 for x in row): raise ValueError('oversized page text')
    return result

GROUPS=(
    ('input.mouse','input.touchpad'),
    ('input.keyboard','input.shortcuts'),
    ('audio.playback','audio.recording','audio.streams'),
    ('display.configuration','display.night-light'),
    ('style.colors','style.application','style.decorations','style.icons','style.cursors','style.sounds','style.splash','style.login','style.boot-splash'),
    ('style.fonts','style.font-management'),
    ('windows.behavior','windows.switcher','windows.desktops'),
)
def build(path):
    # ASCII atlas currently replaces unsupported scalars; generated content uses
    # equivalent punctuation. IDs and the complete reviewed requirements stay intact.
    def literal(s):
        for a,b in [('–','-'),('—','-'),('“','"'),('”','"'),('’',"'"),('…','...')]:s=s.replace(a,b)
        return json.dumps(s,ensure_ascii=True)
    labels=json.loads((ROOT/'userland/settings/pages.json').read_text())
    if set(labels)!={r[0] for r in rows()}:raise ValueError('presentation labels must cover exactly the reviewed page inventory')
    data=['/* Generated from docs/MERIDIAN-PAGES.md; do not edit. */', '#include "meridian.h"',
          'const struct meridian_page meridian_pages[MERIDIAN_PAGE_COUNT]={']
    for row in rows():
        group=next((i for i,ids in enumerate(GROUPS) if row[0] in ids),-1)
        sections=labels[row[0]]
        if not isinstance(sections,list) or not sections or any(not isinstance(x,str) or not x or len(x)>180 for x in sections):raise ValueError('invalid page labels')
        body='Planned options\n\n'+'\n'.join('- '+x for x in sections)
        row=(*row[:3],body,row[4])
        data.append('    {'+','.join(literal(x) for x in row)+','+str(group)+'},')
    data+=['};']
    path.write_text('\n'.join(data)+'\n')
if __name__=='__main__':build(Path(sys.argv[1]))
