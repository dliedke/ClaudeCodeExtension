"""Merges t/<code>*.txt (lines "idx<TAB>json-escaped translation", idx into missing.json) into lang/<code>.json.
Validates placeholders, edge spaces, leading icon. Usage: merge.py repo_root code [code...]"""
import json, os, re, sys, glob

here = os.path.dirname(os.path.abspath(__file__))
root = sys.argv[1]
missing = json.load(open(os.path.join(here, 'missing.json'), encoding='utf-8'))

def ph(s): return set(re.findall(r'\{(\d+)', s))
def icon(s):
    f = s[0]
    if f.isalnum() or f.isspace() or f in '{"(': return None
    sp = s.find(' ')
    return s[:sp] if sp > 0 else s

for code in sys.argv[2:]:
    tr = {}
    bad = []
    for f in sorted(glob.glob(os.path.join(here, 't', code + '.*.txt'))):
        for n, line in enumerate(open(f, encoding='utf-8').read().split('\n'), 1):
            if not line.strip(): continue
            idx, _, text = line.partition('\t')
            try:
                i = int(idx); val = json.loads('"' + text + '"')
            except Exception as e:
                bad.append(f'{os.path.basename(f)}:{n} parse {e}'); continue
            en = missing[i]
            if ph(en) != ph(val): bad.append(f'{i} placeholders: {val!r}')
            if en[0].isspace() != val[0].isspace() or en[-1].isspace() != val[-1].isspace(): bad.append(f'{i} edge spaces: {val!r}')
            ic = icon(en)
            if ic and not val.startswith(ic): bad.append(f'{i} icon {ic!r}: {val!r}')
            if en.count('\n') and not val.count('\n'): bad.append(f'{i} lost newlines')
            tr[en] = val
    gaps = [i for i, k in enumerate(missing) if k not in tr]
    path = os.path.join(root, 'lang', code + '.json')
    data = json.load(open(path, encoding='utf-8-sig'))
    data.update(tr)
    data = dict(sorted(data.items(), key=lambda kv: kv[0].encode('utf-16-be')))
    out = json.dumps(data, ensure_ascii=False, indent=2).replace('\n', '\r\n') + '\r\n'
    open(path, 'w', encoding='utf-8', newline='').write(out)
    print(code, 'merged', len(tr), 'gaps', len(gaps), gaps[:20], 'problems', len(bad))
    for b in bad: print('  ', b)
