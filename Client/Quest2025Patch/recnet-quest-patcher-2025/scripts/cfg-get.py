#!/usr/bin/env python3
"""Print one key's value from a recflare.cfg (empty if the file or key is absent).

Usage: cfg-get.py <recflare.cfg> <Key Name>
"""
import sys


def main():
    if len(sys.argv) != 3:
        sys.exit(f"usage: {sys.argv[0]} <recflare.cfg> <key>")
    cfg, key = sys.argv[1], sys.argv[2].strip().lower()
    val = ""
    try:
        for raw in open(cfg, encoding="utf-8"):
            line = raw.split("#", 1)[0]
            if "=" not in line:
                continue
            k, v = line.split("=", 1)
            if k.strip().lower() == key:
                val = v.strip()
                break
    except FileNotFoundError:
        pass
    print(val)


if __name__ == "__main__":
    main()
