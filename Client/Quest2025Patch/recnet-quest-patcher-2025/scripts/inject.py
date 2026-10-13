#!/usr/bin/env python3
"""
Add libvrsupport.so as the first DT_NEEDED entry of libmain.so, so the game's own
loader pulls our hook library in before Unity starts (no root required).

Usage: inject.py <input libmain.so> <output libmain.so>
"""
import sys

import lief


def main():
    if len(sys.argv) != 3:
        sys.exit(f"usage: {sys.argv[0]} <in libmain.so> <out libmain.so>")
    src, dst = sys.argv[1], sys.argv[2]

    binary = lief.ELF.parse(src)
    if binary is None:
        sys.exit(f"could not parse {src}")

    needed = [e.name for e in binary.dynamic_entries
              if e.tag == lief.ELF.DynamicEntry.TAG.NEEDED]
    if "libvrsupport.so" in needed:
        print("libvrsupport.so already NEEDED; rewriting anyway")
    else:
        binary.add_library("libvrsupport.so")  # LIEF inserts it first

    binary.write(dst)

    check = lief.ELF.parse(dst)
    out = [e.name for e in check.dynamic_entries
           if e.tag == lief.ELF.DynamicEntry.TAG.NEEDED]
    print("DT_NEEDED:", ", ".join(out))
    if out and out[0] != "libvrsupport.so":
        print("WARNING: libvrsupport.so is not the first NEEDED entry")


if __name__ == "__main__":
    main()
