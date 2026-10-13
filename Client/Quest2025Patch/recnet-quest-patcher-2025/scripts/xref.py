#!/usr/bin/env python3
"""
Find which il2cpp methods call a given RVA in libil2cpp.so.

dump.cs lists every method's RVA but no cross-references, so "who calls this?" normally
means loading a 240 MB binary into a disassembler. IL2CPP is AOT-compiled, though, so a
call is just a `bl` with a PC-relative immediate: scan .text for `bl`/`b` instructions
whose computed target is the address we care about, then map each hit back to the method
that contains it using the RVA table parsed out of dump.cs.

In this ELF file offset == VA == RVA, so no section translation is needed.

Usage: xref.py <libil2cpp.so> <dump.cs> 0xRVA [0xRVA ...]   # who calls this?
       xref.py --calls <libil2cpp.so> <dump.cs> 0xRVA        # what does this call?
       xref.py --whois <libil2cpp.so> <dump.cs> 0xRVA        # which method is this in?

--whois turns a runtime address into a name: subtract the libil2cpp.so base logged at
startup from an address seen on the device, and pass the resulting RVA.
"""
import re
import sys

import numpy as np

# Both are PC-relative with a 26-bit signed word displacement; they differ only in bit 31.
# `b` matters as well as `bl` because the compiler tail-calls.
BRANCH_MASK = np.uint32(0xFC000000)
BL_OP = np.uint32(0x94000000)
B_OP = np.uint32(0x14000000)


def load_methods(dump_path):
    """[(rva, 'Class.Method'), ...] sorted by rva, from dump.cs comments."""
    rva_re = re.compile(rb"^\s*// RVA: 0x([0-9A-Fa-f]+) Offset")
    name_re = re.compile(rb"^\s*(?:\[[^\]]*\]\s*)?(?:[\w<>., `\[\]*&]+?\s+)?([\w<>.`]+)\s*\(")
    methods = []
    pending = None
    cls = b"?"
    with open(dump_path, "rb") as f:
        for line in f:
            if line.startswith((b"public class", b"internal class", b"public struct",
                               b"private class", b"public sealed class",
                               b"internal sealed class", b"public static class",
                               b"internal static class", b"public abstract class")):
                m = re.search(rb"(?:class|struct)\s+([\w<>.`]+)", line)
                if m:
                    cls = m.group(1)
                continue
            m = rva_re.match(line)
            if m:
                # An attribute's own RVA comment precedes the method's; the last one wins.
                pending = int(m.group(1), 16)
                continue
            if pending is not None:
                m = name_re.match(line)
                if m:
                    methods.append((pending, cls.decode() + "." + m.group(1).decode()))
                pending = None
    methods.sort()
    return methods


def owner(methods, addr):
    """The method whose RVA is the greatest one <= addr."""
    lo, hi = 0, len(methods) - 1
    best = None
    while lo <= hi:
        mid = (lo + hi) // 2
        if methods[mid][0] <= addr:
            best = methods[mid]
            lo = mid + 1
        else:
            hi = mid - 1
    return best


def main():
    args = sys.argv[1:]
    calls_mode = args and args[0] == "--calls"
    whois_mode = args and args[0] == "--whois"
    if calls_mode or whois_mode:
        args = args[1:]
    if len(args) < 3:
        sys.exit(f"usage: {sys.argv[0]} [--calls] <libil2cpp.so> <dump.cs> 0xRVA [...]")
    so_path, dump_path = args[0], args[1]
    targets = [int(a, 16) for a in args[2:]]

    if whois_mode:
        # No disassembly needed -- the RVA table alone answers this.
        methods = load_methods(dump_path)
        for t in targets:
            o = owner(methods, t)
            if o is None:
                print(f"0x{t:X}  (before the first known method)")
            else:
                print(f"0x{t:X}  in {o[1]} @ 0x{o[0]:X}  (+0x{t - o[0]:X})")
        return

    with open(so_path, "rb") as f:
        blob = f.read()
    words = np.frombuffer(blob[: len(blob) & ~3], dtype="<u4")
    del blob

    op = words & BRANCH_MASK
    is_branch = (op == BL_OP) | (op == B_OP)
    idx = np.flatnonzero(is_branch)
    # Sign-extend the 26-bit immediate, then target = pc + imm*4.
    imm = (words[idx] & np.uint32(0x03FFFFFF)).astype(np.int64)
    imm = np.where(imm >= 1 << 25, imm - (1 << 26), imm)
    target = idx.astype(np.int64) * 4 + imm * 4

    print(f"scanned {len(words):,} words, {len(idx):,} b/bl instructions", file=sys.stderr)
    methods = load_methods(dump_path)
    print(f"loaded {len(methods):,} method RVAs from dump.cs", file=sys.stderr)

    if calls_mode:
        # A method's body runs until the next method's RVA, so that bounds the scan.
        rvas = [m[0] for m in methods]
        for t in targets:
            here = owner(methods, t)
            nxt = next((r for r in rvas if r > t), t + 0x1000)
            print(f"\n=== calls made by {here[1] if here else '?'} "
                  f"0x{t:X}..0x{nxt:X} ===")
            sel = idx[(idx * 4 >= t) & (idx * 4 < nxt)]
            for site in sel:
                dest = int(site) * 4 + int(
                    (words[site] & np.uint32(0x03FFFFFF)).astype(np.int64)
                    - ((1 << 26) if (words[site] & np.uint32(0x02000000)) else 0)) * 4
                o = owner(methods, dest)
                kind = "bl " if (words[site] & BRANCH_MASK) == BL_OP else "b  "
                # Intra-method b's are just control flow, not calls.
                if kind == "b  " and t <= dest < nxt:
                    continue
                name = f"{o[1]} @ 0x{o[0]:X}" if o else "?"
                off = dest - o[0] if o else 0
                print(f"  0x{site * 4:X}  {kind} 0x{dest:X}"
                      f"{'' if off == 0 else f' (+0x{off:X})'}  {name}")
        return

    for t in targets:
        hits = idx[target == t] * 4
        print(f"\n=== callers of 0x{t:X} ({len(hits)}) ===")
        seen = set()
        for site in hits:
            o = owner(methods, int(site))
            kind = "bl " if (words[site // 4] & BRANCH_MASK) == BL_OP else "b  "
            if o is None:
                print(f"  {kind} @ 0x{site:X}  (no enclosing method)")
                continue
            label = f"{o[1]} @ 0x{o[0]:X}"
            if label in seen:
                continue
            seen.add(label)
            print(f"  {kind} @ 0x{site:X}  in {label}")


if __name__ == "__main__":
    main()
