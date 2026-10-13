# Follow-up request: the RecRoomClientEncryptLayer 9-byte format

Your Tachyon findings resolved the handshake failure. Reconciled against the server side:

- The client's inbound layer strips 9 bytes off **every** datagram before LiteNetLib parses it.
- Our stock server sent a 15-byte ConnectAccept; after the client strips 9 it is 6 bytes, so
  `NetConnectAcceptPacket.FromData` fails `Size != 15` and returns null — the exact silent
  ignore-and-retry-to-timeout we observed.
- Confirmed on the server: a stock server absorbs the client's over-long ConnectRequest (the 9
  extra bytes fold into variable-length connect data), which is why the server logged
  "peer connected" while the client never did.

So the server needs the matching layer. We have built it as a configurable
`PacketLayerBase` with `ExtraPacketSizeForLayer = 9`, and proved the plumbing: with the same
9-byte framing on both ends the handshake completes; with it on the server only, a stock
client fails identically to the game today.

**The one thing blocking a live test is the exact 9-byte format and the null-key behaviour.**
This is §3 / step 1 of your own task doc, and it is now the whole critical path.

## Q1 — `RecRoomClientEncryptLayer.ProcessOutBoundPacket` (RVA `0x735D760`)

Full disassembly / decompile. Specifically:

- Are the 9 bytes **prepended** (written at the front, payload shifted) or **appended**
  (written after the existing payload)? The offset math in the method answers this.
- What are the 9 bytes when the session key field (`DAKIIEIEKNI`, `NetPacket`+`0x18`) is
  **null** — the state during the whole LiteNetLib + NGO handshake, before
  `RecRoom_SetEncryptionInfo` runs? Zero fill? A counter? A magic constant? Copied from
  somewhere in the packet?
- When the key is **non-null**, what transforms the payload — AES over the body, and the 9
  bytes carry a nonce/tag? We need the outbound path, but the null-key branch is what unblocks
  the handshake.
- Does it branch on packet property (e.g. treat ConnectRequest/Accept differently from
  Channeled data), or is it uniform for every datagram?

## Q2 — `RecRoomClientEncryptLayer.ProcessInboundPacket` (RVA `0x735DF90`)

Full disassembly / decompile. Specifically:

- Does it **validate** the 9 bytes (compare a MAC/magic/counter and drop on mismatch), or just
  **strip** them? This is the single most important question: if it only strips when the key is
  null, a zero-fill server reply connects immediately; if it validates, we must reproduce the
  exact bytes.
- Same prepend-vs-append question from the read side (which 9 bytes are removed, front or back).
- The null-key branch specifically — what happens to a datagram that arrives before a key is set
  (which is every handshake packet). Does it early-out, strip-only, or reject?
- Any length guard (e.g. drop if `size < 9`).

## Q3 — the two unidentified layer methods

`0x735DC20` and `0x735E140` are listed as unidentified members of the layer. One-line summary
of each — likely a key setter (the sink for `RecRoom_SetEncryptionInfo`) and/or a
nonce/counter helper. If one installs the key into `DAKIIEIEKNI`, note its signature.

## Q4 — the `.cctor` at `0x735E220`

If the layer has static state (a fixed magic, a base nonce, an RNG seed), it is initialised
here. Dump any constants it writes.

## Why this precise scoping

We are one bounded read from a live handshake. If Q2's null-key path is strip-only, the layer
we have built connects the real client as-is and we move straight to the NGO ConnectionRequest
(where your Tachyon JSON / `"Carrot"` findings take over). If it validates, we need the byte
recipe before any wire test is meaningful — a guess here is the "plausible-but-wrong costs more
than not-found" trap you flagged.

Report bytes and offsets, not prose. Where the null-key path is genuinely absent (i.e. the
layer assumes a key is always set), say so — that itself would be a finding, implying the key
is established out-of-band before the first datagram.
