# Request #4: the Dissonance session handshake (server side)

Milestone reached: the real client completes the full LiteNetLib + encrypt-layer + forked-NGO +
Tachyon handshake against our server, connects without crashing, and reports "connected to
voice" — all in plaintext (marker `0x11`; omitting `KT` left the client key-null and its
post-connect RSA callback is evidently caught).

Now the client sends a **Dissonance handshake** over an NGO `NamedMessage` and repeats it every
~4 seconds because our server does not answer:

```
NGO NamedMessage, hash = 0xc78b0000000f0001
Dissonance payload (13 bytes) = 04 01 00 00 03 c0 00 00 bb 80 00 02 32
                                ^^ type?    ^^^^^      ^^^^^
                                            0x03c0=960 0xbb80=48000  (Opus frame / sample rate)
```

**Confirmed empirically:** with a real client connected and the user actively speaking, the
client sends ONLY this handshake, repeating every ~4s, and no voice/UnnamedMessage/other
channel ever appears. So voice transmission is hard-gated on this handshake being answered —
this is the sole remaining blocker to plaintext voice.

Confident partial decode of the 13-byte payload (unverified field names): leading `0x04` =
message type; `0x03c0` = 960 = Opus frame size; `0xbb80` = 48000 = sample rate. The rest
(`01 00 00`, `00 00`, `00 02 32`) is unidentified.

To progress to actual voice, the server must send the Dissonance handshake reply. Questions,
from `DissonanceVoip.dll` / `Dissonance.Integrations.Unity_NFGO.NfgoCommsNetwork` /
`DissonanceNetworkManagerInjector` (`0x826A370`):

**Q1 — the NamedMessage channel.** What is `0xc78b0000000f0001`? Is it the hash of a Dissonance
channel name that NGO's `CustomMessagingManager` registered, and does the server reply on the
same channel/hash? Give the name if you can recover it.

**Q2 — the handshake message format.** Decode the 13-byte payload: what is the leading `0x04`
(a Dissonance `MessageTypes` enum?), and the full field layout. Confirm the 960/48000 reading
and identify the rest.

**Q3 — the server reply.** What does the client expect back — a Dissonance `HandshakeResponse`
that assigns it a player/session id? Give that message's type byte and field layout, and how
the client matches it to its request. This is the specific thing that will stop the 4s retry
loop and move the client into its connected voice state.

**Q4 — steady-state voice.** Once handshked, what does a voice packet look like (type byte,
whether it carries the player id and a sequence, and whether the audio body is opaque Opus we
can relay without parsing)? For proximity voice a relay ideally forwards voice bodies verbatim
between clients in a room — confirm that is viable, i.e. the server does not need to re-sign or
re-encode.

**Q5 — session/room membership.** How does the client learn about other players (join/leave)?
Is there a server broadcast the relay must send when a second client connects, so the two
clients hear each other?

## Output

Message-type bytes, field offsets, and the exact server reply bytes where possible. Dissonance
is a known asset (Placeholder Software) — if the protocol matches a documented/derivable
version, say which. Distinguish read from inferred.
