#!/usr/bin/env python3
"""List il2cpp v27 string literals matching a substring, with table entry info."""
import struct
import sys

path, needle = sys.argv[1], sys.argv[2]
buf = open(path, "rb").read()
sanity, version = struct.unpack_from("<Ii", buf, 0)
lit_off, lit_size, data_off, data_size = struct.unpack_from("<4I", buf, 8)

print(f"version={version} entries={lit_size // 8:,}\n")
hits = []
for i in range(lit_size // 8):
    ent = lit_off + i * 8
    length, data_index = struct.unpack_from("<2I", buf, ent)
    if length == 0 or length > 4096:
        continue
    s = buf[data_off + data_index:data_off + data_index + length]
    try:
        t = s.decode("utf-8")
    except UnicodeDecodeError:
        continue
    if needle in t:
        hits.append((i, ent, length, data_index, t))

for i, ent, length, di, t in hits:
    print(f"idx={i:<6} entry@0x{ent:<7X} len={length:<4} dataIdx=0x{di:<8X} {t!r}")
print(f"\n{len(hits)} match(es) for {needle!r}")
