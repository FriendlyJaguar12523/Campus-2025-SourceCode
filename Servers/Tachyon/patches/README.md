# Patches applied to vendored submodules

## `0001-tachyon-packet-layer-hook.patch` → `vendor/LiteNetLibPP`

Applies to: LiteNetLibPP @ `c6625b0652c5930046b75b495db314456c8bc411`
(https://github.com/Revan600/LiteNetLibPP), the pinned submodule under `vendor/LiteNetLibPP`.

### Why

Upstream has no packet-layer abstraction, but the Rec Room client wraps every datagram in a
trailing-marker "encrypt" layer (a `0x11` byte on the plaintext path). This patch adds a generic
datagram-transform hook — the equivalent of the C# LiteNetLib `PacketLayerBase` — so Tachyon can
install the marker without forking the library wholesale. It is deliberately generic (not
RecRoom-specific) so it could be submitted upstream unchanged.

### What it changes

- `include/lnl/net_manager.h` — two public `std::function` members, `outbound_layer` and
  `inbound_layer` (both default null → stock behaviour), plus `<functional>`/`<vector>` includes.
- `src/lnl/net_manager.cpp`
  - `send_raw()` — if `outbound_layer` is set, transform the bytes just before `sendto`.
  - `receive_logic()` — if `inbound_layer` is set, transform/drop each datagram just after
    `recvfrom`, before `on_message_received`.

Tachyon installs the `0x11` marker through these hooks in `src/main.cpp`.

### How it is applied

The top-level `CMakeLists.txt` applies this patch to the submodule working tree at configure time,
idempotently (`git apply --reverse --check` detects an already-patched tree and skips). This leaves
the submodule showing as "dirty" in `git status` — that is expected. To reset it:
`git -C vendor/LiteNetLibPP checkout .`

### Regenerating the patch (if the pin is bumped)

```sh
# from a pristine checkout of the new upstream commit, with the two files edited:
git -C <litenetlibpp> diff > patches/0001-tachyon-packet-layer-hook.patch
```

### Known limitation

The outbound hook appends its marker *after* LiteNetLibPP's MTU/fragmentation math, without
reserving space the way the C# `ExtraPacketSizeForLayer` did. Safe here because every packet the
server *sends* is far below the MTU; revisit if the server ever sends reliable data near the MTU.
