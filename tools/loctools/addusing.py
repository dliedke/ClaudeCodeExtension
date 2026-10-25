import sys,re
for p in sys.argv[1:]:
    raw=open(p,'rb').read(); bom=raw.startswith(b'\xef\xbb\xbf'); s=raw.decode('utf-8-sig')
    if 'using static ClaudeCodeVS.UI.Loc;' in s: continue
    ms=list(re.finditer(r'^using [^\n]*;\r?\n', s, re.M))
    nl='\r\n' if '\r\n' in s else '\n'
    pos=ms[-1].end()
    s=s[:pos]+'using static ClaudeCodeVS.UI.Loc;'+nl+s[pos:]
    open(p,'wb').write((b'\xef\xbb\xbf' if bom else b'')+s.encode('utf-8'))
    print('using added',p)
