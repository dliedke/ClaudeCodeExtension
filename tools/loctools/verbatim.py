"""Turns `const string instructions = @"...";` into `string instructions = L("...\r\n" + ...);`."""
import re, sys
path = sys.argv[1]
s = open(path, encoding='utf-8').read()
pat = re.compile(r'const string instructions = @"((?:[^"]|"")*)";')

def conv(m):
    body = m.group(1).replace('""', '"').replace('\r\n', '\n')
    lines = body.split('\n')
    # indentation of the declaration line
    start = s.rfind('\n', 0, m.start()) + 1
    indent = re.match(r'\s*', s[start:m.start()]).group(0)
    pad = indent + '      '
    parts = []
    for i, line in enumerate(lines):
        esc = line.replace('\\', '\\\\').replace('"', '\\"')
        if i < len(lines) - 1:
            esc += '\\r\\n'
        parts.append('"' + esc + '"')
    nl = '\r\n' if '\r\n' in s else '\n'
    return 'string instructions = L(' + (' +' + nl + pad).join(parts) + ');'

s2, n = pat.subn(conv, s)
open(path, 'w', encoding='utf-8', newline='').write(s2)
print('converted', n)
