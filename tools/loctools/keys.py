"""Mirrors LocalizationTests.CollectKeysFromSource; prints missing keys (as JSON list) and orphans vs lang/pt-BR.json.
Usage: keys.py repo_root [--write missing.json]"""
import re, sys, os, json, html, glob

root = sys.argv[1]
CODE = re.compile(r'\b[LN]\(\s*((?:"(?:[^"\\]|\\.)*"\s*\+\s*)*"(?:[^"\\]|\\.)*")')
LIT = re.compile(r'"((?:[^"\\]|\\.)*)"')
XKEY = re.compile(r"\{local:Loc\s+'((?:[^'\\]|\\.)*)'\s*\}")

def unescape(s):
    out = []; i = 0
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            n = s[i + 1]
            if n == 'n': out.append('\n'); i += 2; continue
            if n == 'r': out.append('\r'); i += 2; continue
            if n == 't': out.append('\t'); i += 2; continue
            if n == 'u': out.append(chr(int(s[i + 2:i + 6], 16))); i += 6; continue
            out.append(n); i += 2; continue
        out.append(c); i += 1
    return ''.join(out)

keys = set()
for folder in ["UI", "Controls", "Diff", "ToolWindows", "Package"]:
    for f in glob.glob(os.path.join(root, folder, '*.cs')):
        if os.path.basename(f) == 'Loc.cs': continue
        src = open(f, encoding='utf-8-sig').read()
        for m in CODE.finditer(src):
            keys.add(''.join(unescape(p.group(1)) for p in LIT.finditer(m.group(1))))
    for f in glob.glob(os.path.join(root, folder, '*.xaml')):
        src = open(f, encoding='utf-8-sig').read()
        for m in XKEY.finditer(src):
            keys.add(html.unescape(re.sub(r'\\(.)', r'\1', m.group(1))))

ref = json.load(open(os.path.join(root, 'lang', 'pt-BR.json'), encoding='utf-8-sig'))
missing = sorted(k for k in keys if k not in ref)
orphans = sorted(k for k in ref if k not in keys)
print('keys', len(keys), 'missing', len(missing), 'orphans', len(orphans))
for o in orphans: print('ORPHAN', json.dumps(o, ensure_ascii=False))
if '--write' in sys.argv:
    json.dump(missing, open(sys.argv[sys.argv.index('--write') + 1], 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
