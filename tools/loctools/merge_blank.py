import re, sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
s2 = re.sub(r'\\r\\n" \+(\r?\n)\s+"\\r\\n" \+', r'\\r\\n\\r\\n" +', s)
s2 = re.sub(r'\\r\\n" \+(\r?\n)\s+"\\r\\n"\)', r'\\r\\n\\r\\n")', s2)
print(len(s) - len(s2))
open(p, 'w', encoding='utf-8', newline='').write(s2)
