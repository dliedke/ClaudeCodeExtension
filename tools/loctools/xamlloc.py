"""Turns literal ToolTip/Header/Text/Content/Title captions into {local:Loc '...'} in a XAML file.
Usage: xamlloc.py file.xaml [--skip "caption"]...   (exact captions to leave alone)"""
import re, sys

path = sys.argv[1]
skips = set()
args = sys.argv[2:]
i = 0
while i < len(args):
    if args[i] == '--skip':
        skips.add(args[i + 1]); i += 2
    else:
        i += 1

s = open(path, encoding='utf-8').read()
pat = re.compile(r'(?<=\s)(ToolTip|Header|Text|Content|Title)="([^"{]*[A-Za-z]{2,}[^"]*)"')
count = 0

def conv(m):
    global count
    text = m.group(2)
    if text in skips:
        return m.group(0)
    count += 1
    esc = text.replace('\\', '\\\\').replace("'", "\\'")
    return '%s="{local:Loc \'%s\'}"' % (m.group(1), esc)

s2 = pat.sub(conv, s)
if 'xmlns:local=' not in s2:
    s2 = re.sub(r'(xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml")',
                r'\1\n             xmlns:local="clr-namespace:ClaudeCodeVS.UI"', s2, count=1)
open(path, 'w', encoding='utf-8', newline='').write(s2)
print('converted', count)
