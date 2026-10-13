#!/usr/bin/env python3
"""
Minimal il2cpp v27 string-literal repointer.

Unlike the 0xLatte editor (which rewrites and inflates the whole literal blob),
this appends the replacement string at EOF and repoints exactly one entry in the
string-literal table. No section offsets move, so the file grows by len(new)+pad.

il2cpp resolves a literal as `stringLiteralData + dataIndex`, and the whole file
is loaded into memory, so data living past the declared stringLiteralData extent
still resolves fine. Release builds do not bounds-check per literal.
"""
import argparse
import struct
import sys

SANITY = 0xFAB11BAF


def read_header(buf):
    sanity, version = struct.unpack_from("<Ii", buf, 0)
    if sanity != SANITY:
        sys.exit(f"bad sanity 0x{sanity:08X} (expected 0x{SANITY:08X})")
    if version != 27:
        sys.exit(f"unsupported metadata version {version} (this tool targets 27)")
    # v27 header: sanity, version, then (offset,size) u32 pairs.
    lit_off, lit_size, data_off, data_size = struct.unpack_from("<4I", buf, 8)
    return version, lit_off, lit_size, data_off, data_size


def find_literal(buf, lit_off, lit_size, data_off, target):
    """Table entries are {u32 length; u32 dataIndex}."""
    target_b = target.encode()
    for i in range(lit_size // 8):
        ent = lit_off + i * 8
        length, data_index = struct.unpack_from("<2I", buf, ent)
        if length != len(target_b):
            continue
        start = data_off + data_index
        if buf[start:start + length] == target_b:
            return i, ent, length, data_index
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("metadata")
    ap.add_argument("--old", default="https://ns.rec.net")
    ap.add_argument("--new", default="https://ns.recflare.net")
    ap.add_argument("-o", "--output")
    ap.add_argument("--apply", action="store_true", help="write output (default is dry run)")
    ap.add_argument("--inplace", action="store_true",
                    help="write into the existing data slot (requires new <= old); "
                         "file size and all offsets stay identical")
    args = ap.parse_args()

    buf = bytearray(open(args.metadata, "rb").read())
    version, lit_off, lit_size, data_off, data_size = read_header(buf)

    print(f"metadata      : {args.metadata}")
    print(f"size          : {len(buf):,} bytes")
    print(f"version       : {version}")
    print(f"literal table : offset=0x{lit_off:X} size={lit_size:,} ({lit_size // 8:,} entries)")
    print(f"literal data  : offset=0x{data_off:X} size={data_size:,}")

    hit = find_literal(buf, lit_off, lit_size, data_off, args.old)
    if not hit:
        sys.exit(f"literal {args.old!r} not found in the string-literal table")
    idx, ent, length, data_index = hit
    print(f"\nfound literal : index={idx} entry@0x{ent:X}")
    print(f"                length={length} dataIndex=0x{data_index:X} "
          f"(file offset 0x{data_off + data_index:X})")
    print(f"                value={args.old!r}")

    new_b = args.new.encode()

    if args.inplace:
        # Overwrite the existing slot. il2cpp refuses to follow a dataIndex past
        # stringLiteralDataCount, so the replacement has to live in-section.
        if len(new_b) > length:
            sys.exit(f"--inplace needs new <= old ({len(new_b)} > {length} bytes)")
        print(f"\nrewrite to    : {args.new!r}  (in place)")
        print(f"                length={len(new_b)} dataIndex=0x{data_index:X} (unchanged)")
        print(f"growth        : +0 bytes -- file size and all offsets unchanged")
        if not args.apply:
            print("\n(dry run -- pass --apply to write)")
            return
        if not args.output:
            sys.exit("--apply requires -o/--output")
        start = data_off + data_index
        buf[start:start + len(new_b)] = new_b
        struct.pack_into("<2I", buf, ent, len(new_b), data_index)
        with open(args.output, "wb") as f:
            f.write(buf)
        print(f"\nwrote {args.output} ({len(buf):,} bytes)")
        return

    # Append at EOF, 4-byte aligned.
    pad = (-len(buf)) % 4
    new_file_off = len(buf) + pad
    new_data_index = new_file_off - data_off

    print(f"\nrewrite to    : {args.new!r}")
    print(f"                length={len(new_b)} dataIndex=0x{new_data_index:X} "
          f"(file offset 0x{new_file_off:X})")
    print(f"growth        : +{pad + len(new_b)} bytes "
          f"({len(buf):,} -> {len(buf) + pad + len(new_b):,})")

    if not args.apply:
        print("\n(dry run -- pass --apply to write)")
        return

    if not args.output:
        sys.exit("--apply requires -o/--output")

    buf.extend(b"\x00" * pad)
    buf.extend(new_b)
    struct.pack_into("<2I", buf, ent, len(new_b), new_data_index)

    with open(args.output, "wb") as f:
        f.write(buf)
    print(f"\nwrote {args.output} ({len(buf):,} bytes)")

    # Verify by re-reading through the same path the runtime would use.
    check = bytearray(open(args.output, "rb").read())
    _, l_off, l_size, d_off, _ = read_header(check)
    n_len, n_idx = struct.unpack_from("<2I", check, l_off + idx * 8)
    got = check[d_off + n_idx:d_off + n_idx + n_len].decode()
    print(f"verify        : entry {idx} resolves to {got!r}", "OK" if got == args.new else "MISMATCH")


if __name__ == "__main__":
    main()
