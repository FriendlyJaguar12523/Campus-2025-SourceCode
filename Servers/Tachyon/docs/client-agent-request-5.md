# Request #5: exact Dissonance packet field encodings (client's PacketReader)

Request #4 landed it: NamedMessage = `u16 NamedMessageType LE` + `i32 length LE` + Dissonance
packet; reply on type 2 (DissonanceToClient); Dissonance is stock v8.x, magic `0x8bc7`
big-endian. We've implemented a best-effort HandshakeResponse from the docs' field lists and
are testing it live. This request is the authoritative fallback for the byte-exact encodings the
docs don't specify — read from the CLIENT's `PacketReader` (what parses what we send), which is
the ground truth for what we must produce.

Dissonance networking is in the un-indexed regions you noted; the reader methods are
`AJLLGKJMHIO` (PacketReader) / `BKJPKJLIHFE` @ 0x3136430 (magic check) and the `BaseClient`
handshake handler.

**Q1 — `ReadHandshakeResponse` exact layout.** After magic + type(5): confirm the order and
encoding of session id (u32?), assigned client id (u16?), and the three lists (clients, rooms,
listeners). For each list: is the count a **byte, u16, or u32** (big-endian?), and what is each
entry's layout? For the client list entry: name + codec settings + id — in what order, and how
is the **string (name)** encoded (length prefix width + UTF8? null sentinel?). Does the client
list include the joining client itself or only others?

**Q2 — `ReadClientState` layout.** Fields: name, client id, codec settings, rooms list. Same
questions — string encoding, list count width, field order. This is what we broadcast when a
second client joins so the two see each other.

**Q3 — `ReadVoiceData` header.** After magic+type(2)+session: sender client id (u16?), the
8-bit flags bitfield, the sequence number (u16?), and the "channels this voice is addressed to"
list (count width + per-channel layout). We want to relay the body verbatim — confirm the whole
packet after the session id can be forwarded unchanged to other clients, or if the server must
rewrite the sender id / channel list.

**Q4 — the `CodecSettings` block.** Confirm `byte codec, u32 frameSize, u32 sampleRate` (BE),
matching the captured HandshakeRequest (`01`, 960, 48000). Is there a trailing field? The
captured request had `00 02 32` after the codec settings that we read as the player name —
confirm the name encoding from `ReadHandshakeRequest`.

**Q5 — client id / session semantics.** Does the server pick the session id freely (any nonzero
u32)? What range/rule for assigned client ids (start value, reserved ids)? The client kicks on
"wrong session ID" for subsequent packets — confirm it stores the session from HandshakeResponse
and echoes it, so our chosen value just has to be consistent.

## Output

Byte offsets, field order, count widths, endianness, string encoding. This is the last layer —
once these are exact, plaintext voice relay is a direct implementation.
