#!/usr/bin/env python3
"""Check local doc links and the single-tracker/page-inventory invariants."""
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote
ROOT=Path(__file__).resolve().parents[1]

def slug(s):
    s=re.sub(r'<[^>]*>','',s).replace('`','').lower()
    return ''.join(c for c in s if c.isalnum() or c in '_- ' ).replace(' ','-')

def check():
    files=subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','-z'],cwd=ROOT).decode().split('\0')
    files=[ROOT/p for p in files if p.endswith('.md') and (ROOT/p).is_file() and not p.startswith('docs/cosmos/')]
    cache={};errors=[]
    def anchors(p):
        if p not in cache:
            counts={};result=set()
            for heading in re.findall(r'^#{1,6}\s+(.+?)\s*#*$',p.read_text(),re.M):
                key=slug(heading);n=counts.get(key,0);counts[key]=n+1
                result.add(key+('-'+str(n) if n else ''))
            cache[p]=result
        return cache[p]
    for p in files:
        text=re.sub(r'```[^\n]*\n[\s\S]*?```','',p.read_text())
        for target in re.findall(r'!?\[[^\]\n]*\]\(([^()\n]+)\)',text):
            if re.match(r'^[a-zA-Z][\w+.-]*:',target):continue
            target=target.strip('<>');path,_,anchor=unquote(target).partition('#')
            q=(p.parent/path).resolve() if path else p
            if not q.exists():errors.append(f'{p.relative_to(ROOT)}: missing {target}');continue
            # Old evidence anchors are preserved as history, not current contracts.
            if anchor and q.suffix=='.md' and 'archive' not in p.parts and not str(q).startswith(str(ROOT/'docs/cosmos')) and anchor not in anchors(q):
                errors.append(f'{p.relative_to(ROOT)}: missing anchor {target}')
    master=(ROOT/'docs/ROADMAP.md').read_text()
    ids=re.findall(r'^\| ([A-Z]+\d+) \|',master,re.M)
    if len(ids)!=len(set(ids)):errors.append('ROADMAP: duplicate work IDs')
    families=sorted({re.sub(r'[0-9]+$','',identity) for identity in ids},key=len,reverse=True)
    references=r'(?<![\w-])((?:'+ '|'.join(families)+r')[0-9]+)(?![\w])'
    for p in (ROOT/'docs/components').glob('*.md'):
        for identity in set(re.findall(references,p.read_text())):
            if identity not in ids:errors.append(f'{p.relative_to(ROOT)}: unknown master work ID {identity}')
    pages=set(re.findall(r'\| `([a-z.-]+)` —', (ROOT/'docs/MERIDIAN-PAGES.md').read_text()))
    covered=set(re.findall(r'`([a-z]+\.[a-z.-]+)`',master))
    if len(pages)!=43 or covered!=pages:errors.append('ROADMAP: 43-page coverage differs: '+str(pages^covered))
    for p in (ROOT/'docs').glob('*.md'):
        if p.name not in ('PROGRESS.md','ROADMAP.md') and re.search(r'^\s*- \[[ x]\]',p.read_text(),re.M):
            errors.append(f'{p.relative_to(ROOT)}: work checklists belong in master/component pages')
    if len((ROOT/'docs/PROGRESS.md').read_text().splitlines())>16:errors.append('PROGRESS: compatibility pointer grew into another ledger')
    for obsolete in ('STATUS.md','SYSTEM-PLAN.md','USERLAND.md','PACKAGE-MANAGER.md','NATIVE-APP-MIGRATION.md','BASH-PORT.md','TECHNICAL-NOTES.md','PACKAGE-PROTOTYPE.md'):
        if (ROOT/'docs'/obsolete).exists():errors.append('duplicate root document: '+obsolete)
    if errors:
        print('\n'.join(errors));return 1
    print(f'PASS {len(files)} documentation files; local links/anchors, {len(ids)} master work IDs, 43 Meridian pages and single-tracker boundaries')
    return 0
if __name__=='__main__':sys.exit(check())
