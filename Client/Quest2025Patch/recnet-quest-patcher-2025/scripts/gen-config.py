#!/usr/bin/env python3
"""
Generate config_generated.h from a recflare.cfg so the config is baked into
libvrsupport.so at build time (no device-side file needed). Keys match recflare.cfg /
the PC plugin. Keys absent from the cfg emit no #define, leaving the compiled-in
default in recflare_hook.cpp.

Usage: gen-config.py <recflare.cfg> <out config_generated.h>
"""
import re
import sys

# cfg key (lowercased) -> (macro, kind)
KEYS = {
    "package name":            ("RF_PACKAGE",         "str"),
    "recnet nameserver host":  ("RF_NAMESERVER_HOST", "host"),
    "oculus app id":           ("RF_OCULUS_APPID",    "str"),
    "app id realtime":         ("RF_PHOTON_REALTIME", "str"),
    "app id voice":            ("RF_PHOTON_VOICE",    "str"),
    "app id chat":             ("RF_PHOTON_CHAT",     "str"),
    "photon nameserver":       ("RF_PHOTON_SERVER",   "str"),
    "photon nameserver port":  ("RF_PHOTON_PORT",     "int"),
    "log http":                ("RF_LOG_HTTP",        "bool"),
    "debug":                   ("RF_DEBUG_HTTP",      "bool"),
    "log http body max":       ("RF_LOG_BODY_MAX",    "int"),
    "log noisy endpoints":     ("RF_LOG_NOISY",       "bool"),
}

TRUE_VALUES = {"true", "1", "yes", "on"}

# Effective values when a key is absent from the cfg (must match recflare_hook.cpp).
DEFAULTS = {
    "recnet nameserver host": "ns.recflare.net",
    "photon nameserver": "",
}

# The stock deployment's hosts. Pointing the client at someone else's RecNet while
# leaving Photon on ours is always a half-finished edit, so refuse to bake it.
STOCK_NAMESERVER = "ns.recflare.net"
STOCK_PHOTON = "photon.recflare.net"


def c_string(val: str) -> str:
    return '"' + val.replace("\\", "\\\\").replace('"', '\\"') + '"'


def check_hosts(values: dict):
    ns = values.get("recnet nameserver host", DEFAULTS["recnet nameserver host"])
    photon = values.get("photon nameserver", DEFAULTS["photon nameserver"])
    if ns.lower() != STOCK_NAMESERVER and photon.lower() == STOCK_PHOTON:
        sys.exit(
            f"gen-config: mismatched servers -- RecNet NameServer Host is "
            f"'{ns}' but Photon NameServer is still '{photon}'.\n"
            f"  Point Photon at the matching deployment, or blank it for Photon Cloud."
        )


def main():
    if len(sys.argv) != 3:
        sys.exit(f"usage: {sys.argv[0]} <recflare.cfg> <config_generated.h>")
    cfg, out = sys.argv[1], sys.argv[2]

    defines = []
    values = {}
    for raw in open(cfg, encoding="utf-8"):
        line = raw.split("#", 1)[0]
        if "=" not in line:
            continue
        key, val = line.split("=", 1)
        key, val = key.strip().lower(), val.strip()
        if key not in KEYS:
            continue
        macro, kind = KEYS[key]
        if kind == "host":
            val = re.sub(r"^\w+://", "", val).split("/")[0]  # bare host
            defines.append(f"#define {macro} {c_string(val)}")
        elif kind == "int":
            defines.append(f"#define {macro} {int(val) if val else 0}")
        elif kind == "bool":
            # Blank means "use the compiled-in default", so emit nothing.
            if val:
                defines.append(f"#define {macro} {1 if val.lower() in TRUE_VALUES else 0}")
        else:
            defines.append(f"#define {macro} {c_string(val)}")
        values[key] = val

    check_hosts(values)

    with open(out, "w", encoding="utf-8") as f:
        f.write(f"// generated from {cfg} -- do not edit\n")
        f.write("\n".join(defines) + ("\n" if defines else ""))
    print(f"gen-config: wrote {out} ({len(defines)} defines)")


if __name__ == "__main__":
    main()
