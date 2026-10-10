import sys, html
src, outdir = sys.argv[1], sys.argv[2]
rows = [l.rstrip('\n').split('\t') for l in open(src, encoding='utf-8') if l.strip()]
keys = [r[0] for r in rows]
assert len(keys) == len(set(keys)), "duplicate keys"
assert all(len(r) == 3 for r in rows), [r for r in rows if len(r) != 3]
head = '''<?xml version="1.0" encoding="utf-8"?>
<!-- GENERATED from docs/strings.tsv by tools/gen_resx.py. Edit the TSV, not this file. -->
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
'''
for idx, name in ((1, 'Strings.resx'), (2, 'Strings.nl.resx')):
    with open(f'{outdir}/{name}', 'w', encoding='utf-8') as f:
        f.write(head)
        for r in rows:
            f.write(f'  <data name="{r[0]}" xml:space="preserve"><value>{html.escape(r[idx], quote=False)}</value></data>\n')
        f.write('</root>\n')
print(len(rows), "strings")
