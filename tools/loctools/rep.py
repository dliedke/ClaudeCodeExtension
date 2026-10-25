"""rep.py file edits.txt  — edits.txt holds blocks:
<<<<
old text
====
new text
>>>>
Each old text must occur exactly once."""
import sys
path, edits = sys.argv[1], sys.argv[2]
s = open(path, encoding='utf-8').read()
crlf = '\r\n' in s
e = open(edits, encoding='utf-8').read().replace('\r\n', '\n')
blocks = e.split('<<<<\n')[1:]
ok = True
for b in blocks:
    old, rest = b.split('\n====\n', 1)
    new = rest.split('\n>>>>', 1)[0]
    if crlf:
        old = old.replace('\n', '\r\n'); new = new.replace('\n', '\r\n')
    n = s.count(old)
    if n != 1:
        print(f'FAIL ({n}x): {old[:120]!r}'); ok = False; continue
    s = s.replace(old, new)
open(path, 'w', encoding='utf-8', newline='').write(s)
print('applied' if ok else 'applied with failures', len(blocks), 'blocks to', path)
