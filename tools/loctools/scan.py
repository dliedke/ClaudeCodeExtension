"""Lists translatable string-literal spans in C# files.

A span is a maximal chain of plain "..." literals joined by '+' (across lines).
Chains mixing literals with expressions are reported as MIXED for manual work.
Interpolated strings ($"...") containing letters+space are reported as INTERP.

usage: scan.py file.cs [...]   -> prints  id|kind|line|context|text
       scan.py --apply file.cs ids...  -> wraps the listed span ids in L(...)
"""
import re, sys, json

def tokenize(src):
    toks = []  # (kind, start, end)
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if src.startswith('//', i):
            j = src.find('\n', i); j = n if j < 0 else j
            toks.append(('comment', i, j)); i = j; continue
        if src.startswith('/*', i):
            j = src.find('*/', i + 2); j = n if j < 0 else j + 2
            toks.append(('comment', i, j)); i = j; continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == '\\' else 1
            toks.append(('char', i, j + 1)); i = j + 1; continue
        m = re.match(r'(\$@|@\$|@|\$)?"', src[i:i+3])
        if m and (m.group(1) or c == '"'):
            prefix = m.group(1) or ''
            j = i + len(prefix) + 1
            verbatim = '@' in prefix
            interp = '$' in prefix
            depth = 0
            while j < n:
                ch = src[j]
                if interp and ch == '{':
                    if src.startswith('{{', j) and depth == 0:
                        j += 2; continue
                    depth += 1; j += 1; continue
                if interp and ch == '}' and depth > 0:
                    depth -= 1; j += 1; continue
                if depth > 0 and ch == '"':
                    # nested string inside interpolation hole
                    k = j + 1
                    while k < n and src[k] != '"':
                        k += 2 if src[k] == '\\' else 1
                    j = k + 1; continue
                if verbatim:
                    if ch == '"':
                        if src.startswith('""', j): j += 2; continue
                        break
                    j += 1; continue
                if ch == '\\': j += 2; continue
                if ch == '"' or ch == '\n': break
                j += 1
            kind = 'interp' if interp else ('verbatim' if verbatim else 'str')
            toks.append((kind, i, j + 1)); i = j + 1; continue
        if c.isspace():
            i += 1; continue
        m = re.match(r'[A-Za-z_][A-Za-z0-9_]*', src[i:])
        if m:
            toks.append(('id', i, i + m.end())); i += m.end(); continue
        toks.append(('op', i, i + 1)); i += 1
    return toks

def unescape(lit):
    body = lit[1:-1]
    return bytes(body, 'utf-8').decode('unicode_escape').encode('latin-1').decode('utf-8') if '\\' in body else body

def text_of(src, t):
    return src[t[1]:t[2]]

WORDISH = re.compile(r'^[A-Z][a-z]+(\.\.\.|:|…)?$')

def candidates(src):
    toks = [t for t in tokenize(src) if t[0] != 'comment']
    out = []
    i = 0
    while i < len(toks):
        t = toks[i]
        if t[0] in ('str', 'interp', 'verbatim'):
            # build chain
            j = i
            parts = [i]
            mixed = False
            while j + 2 < len(toks) and text_of(src, toks[j+1]) == '+':
                nxt = toks[j+2]
                if nxt[0] == 'str':
                    parts.append(j+2); j += 2
                else:
                    mixed = True; break
            # look backwards for '+ expr' before chain
            if i > 0 and text_of(src, toks[i-1]) == '+':
                mixed = True
            kinds = {toks[p][0] for p in parts}
            start, end = toks[parts[0]][1], toks[parts[-1]][2]
            raw = ''.join(text_of(src, toks[p])[1:-1] if toks[p][0]=='str' else text_of(src, toks[p]) for p in parts)
            prev = ''.join(text_of(src, toks[k]) for k in range(max(0, i-4), i))
            prevtok = text_of(src, toks[i-1]) if i else ''
            prev2 = text_of(src, toks[i-2]) if i > 1 else ''
            line = src.count('\n', 0, start) + 1
            ls = src.rfind('\n', 0, start) + 1
            le = src.find('\n', start)
            fullline = src[ls:le]
            out.append(dict(i=i, kind=t[0] if len(parts)==1 else 'str', mixed=mixed, start=start, end=end,
                            text=raw, prev=prev, prevtok=prevtok, prev2=prev2, line=line, fullline=fullline.strip()))
            i = j + 1
            continue
        i += 1
    return out

SKIP_LINE = re.compile(r'Debug\.Write|Trace\.|throw new|LogDebug|\bLog\(|\[Provide|\bcase\s+"|==|!=|\.Equals\(|Contains\(|StartsWith\(|EndsWith\(|IndexOf\(|\.Replace\(|Split\(|Regex|const string|Arguments\s*=|FileName\s*=|Process\.Start|GetEnvironmentVariable|Path\.Combine|new FontFamily|Registry|DllImport|GetProperty\(|nameof|JObject|\["|SelectToken|Matches\(|\.Match\(|IsMatch')

def interesting(c):
    t = c['text']
    if c['prev2'] in ('L', 'N') and c['prevtok'] == '(':
        return False
    if not re.search(r'[A-Za-z]', t):
        return False
    if c['kind'] == 'verbatim':
        return False
    if c['kind'] == 'interp':
        return ' ' in t and re.search(r'[A-Za-z]{3,} [A-Za-z]', t) is not None
    return (' ' in t.strip() and re.search(r'[A-Za-z]{2,}', t)) or WORDISH.match(t.strip())

def split_interp(lit):
    # lit like $"abc {x} def" -> (format, [exprs]) or None when a hole is not simple
    body = lit[2:-1]
    fmt, exprs, i = '', [], 0
    while i < len(body):
        ch = body[i]
        if body.startswith('{{', i): fmt += '{{'; i += 2; continue
        if body.startswith('}}', i): fmt += '}}'; i += 2; continue
        if ch == '{':
            depth, j = 1, i + 1
            while j < len(body) and depth:
                if body[j] == '{': depth += 1
                elif body[j] == '}': depth -= 1
                elif body[j] == '"': return None
                j += 1
            expr = body[i+1:j-1]
            spec = ''
            if ':' in expr and '?' not in expr and '::' not in expr:
                expr, spec = expr.split(':', 1); spec = ':' + spec
            elif ':' in expr:
                return None
            if ',' in expr and '(' not in expr:
                return None
            fmt += '{%d%s}' % (len(exprs), spec); exprs.append(expr.strip()); i = j; continue
        fmt += ch; i += 1
    return fmt, exprs

def main():
    args = sys.argv[1:]
    if args and args[0] == '--interp':
        path = args[1]; ids = set(int(x) for x in args[2:])
        src = open(path, encoding='utf-8-sig').read()
        sel = [c for c in candidates(src) if c['i'] in ids and c['kind'] == 'interp']
        done = 0
        for c in sorted(sel, key=lambda c: -c['start']):
            lit = src[c['start']:c['end']]
            r = split_interp(lit)
            if r is None:
                print('MANUAL', c['i'], c['line'], lit[:80]); continue
            fmt, exprs = r
            new = 'L("' + fmt + '"' + ''.join(', ' + e for e in exprs) + ')' if exprs else 'L("' + fmt + '")'
            src = src[:c['start']] + new + src[c['end']:]
            done += 1
        missing = ids - {c['i'] for c in sel}
        if missing: print('NOT INTERP', sorted(missing))
        with open(path, 'w', encoding='utf-8', newline='') as f:
            f.write(src)
        print(f'converted {done} interpolations in {path}')
        return
    if args and args[0] == '--apply':
        path = args[1]; skip = set(int(x) for x in args[2:] if not x.startswith('+'))
        src = open(path, encoding='utf-8-sig').read()
        force = set(int(x[1:]) for x in args[2:] if x.startswith('+'))
        cands = [c for c in candidates(src) if interesting(c) and c['kind'] == 'str' and not c['mixed']]
        sel = [c for c in cands if (c['i'] in force) or (c['i'] not in skip and not SKIP_LINE.search(c['fullline']))]
        for c in sorted(sel, key=lambda c: -c['start']):
            src = src[:c['start']] + 'L(' + src[c['start']:c['end']] + ')' + src[c['end']:]
        bom = open(path, 'rb').read(3) == b'\xef\xbb\xbf'
        with open(path, 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='') as f:
            f.write(src)
        print(f'wrapped {len(sel)} spans in {path}')
        return
    for path in args:
        src = open(path, encoding='utf-8-sig').read()
        for c in candidates(src):
            if not interesting(c): continue
            kind = 'INTERP' if c['kind']=='interp' else ('MIXED' if c['mixed'] else ('skip' if SKIP_LINE.search(c['fullline']) else 'str'))
            print(f"{c['i']}|{kind}|{c['line']}|{c['prev'][-22:]!s:>22}|{c['text'][:110]}")

main()
