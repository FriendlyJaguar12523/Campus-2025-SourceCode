# Request to the client-side agent (dumped game image)

## Context

We have a third-party NGO/LiteNetLib server that the game connects to. The LiteNetLib
transport handshake **fails in a specific, reproducible way**, and we've narrowed it to one
thing: the client silently ignores our `ConnectAccept`.

Evidence, so you know what's already excluded:

- The client's `ConnectRequest` is byte-identical in shape to a stock LiteNetLib 1.x request:
  `protocolId = 13`, `HeaderSize = 18`, `addrSize = 16`, plus **3 bytes of connect data
  `01 00 11`**. So the client's outbound path is stock or near-stock.
- Our server replies with a stock 15-byte `ConnectAccept` whose `connectionTime` matches.
- The client ignores it and resends `ConnectRequest` ~11 times, all with the *same*
  `connectionTime` — i.e. one attempt being retried, never leaving `ConnectionState.Outgoing`.
- Meanwhile our server thinks it is connected, pings, gets no Pong, and times out at 5s.
- A synthetic stock-LiteNetLib-1.2.0 client, run from the *same machine and NAT* as the game,
  connects to the same server successfully. So this is not the network, not NAT, and not our
  server. It is client-side.
- We reproduced the exact symptom against a stock client by making the accept the wrong
  length: `NetConnectAcceptPacket.FromData` begins `if (packet.Size != Size) return null`,
  which yields precisely this silent-ignore-and-retry behaviour.

**Working hypothesis: the game's bundled LiteNetLib is forked and expects a `ConnectAccept`
that differs from stock — most likely a different length.** We need the exact layout.

The bundled LiteNetLib lives inside `Netcode.Transports.LiteNetLib.dll` (reported earlier as
116 types, "LiteNetLib itself bundled inside"). Third-party assemblies appear to have kept
their real names, so these types should be findable by name rather than by obfuscated slug.

## Stock reference (LiteNetLib 1.2.0, MIT) — diff against this

```csharp
internal sealed class NetConnectAcceptPacket
{
    public const int Size = 15;
    public readonly long ConnectionTime;
    public readonly byte ConnectionNumber;
    public readonly int  PeerId;
    public readonly bool PeerNetworkChanged;

    public static NetConnectAcceptPacket FromData(NetPacket packet)
    {
        if (packet.Size != Size) return null;
        long connectionId = BitConverter.ToInt64(packet.RawData, 1);
        byte connectionNumber = packet.RawData[9];
        if (connectionNumber >= NetConstants.MaxConnectionNumber) return null;
        byte isReused = packet.RawData[10];
        if (isReused > 1) return null;
        int peerId = BitConverter.ToInt32(packet.RawData, 11);
        if (peerId < 0) return null;
        return new NetConnectAcceptPacket(connectionId, connectionNumber, peerId, isReused == 1);
    }

    public static NetPacket Make(long connectTime, byte connectNum, int localPeerId)
    {
        var packet = new NetPacket(PacketProperty.ConnectAccept, 0);
        FastBitConverter.GetBytes(packet.RawData, 1, connectTime);
        packet.RawData[9] = connectNum;
        FastBitConverter.GetBytes(packet.RawData, 11, localPeerId);
        return packet;
    }
}
```

Stock wire layout: `[0]` property (6) | `[1..8]` connectionTime | `[9]` connectionNumber |
`[10]` isReused | `[11..14]` peerId.

## What we need

**Q1 (highest value — this likely ends the investigation).**
`LiteNetLib.NetConnectAcceptPacket` in the bundled copy:
- the value of `Size`;
- every field and its byte offset;
- the full body of `FromData`, especially every early-return/rejection condition;
- the full signature and body of `Make`.
If `Size != 15`, tell us what the extra bytes are and where `Make` writes them.

**Q2.** `LiteNetLib.NetConstants.ProtocolId`. We derived 13 from the wire; confirm from the
image. Also `MaxConnectionNumber` if it differs from stock (4).

**Q3.** Is `NetManager` constructed with a `PacketLayerBase` (a second constructor argument,
or a non-null `_extraPacketLayer` field)? If yes, name the layer type and give
`ExtraPacketSizeForLayer` plus the bodies of `ProcessInboundPacket` / `ProcessOutBoundPacket`.
We ruled a layer out by inference from wire bytes; we want it confirmed from the image.

**Q4.** `LiteNetLib.NetConnectRequestPacket.HeaderSize`, and **what writes the 3 connect-data
bytes `01 00 11`**. Find the `NetManager.Connect(...)` call site — expected to be in the NGO
voice session wrapper (`BAOFAOBLAMJ`) or in `LiteNetLibTransport` — and show how that
`NetDataWriter` is populated. If those bytes are a version/handshake field, we need to know
whether the server is expected to validate or echo them.

**Q5 (only if cheap).** In `NetPeer`, the method handling an inbound accept (stock name
`ProcessConnectAccept`): list every condition that returns false. That is the definitive list
of reasons our accept could be dropped.

## Output format

For each question: the decompiled source if you have it, otherwise field offsets and constant
values. Give **byte offsets and exact integer values** rather than prose. Where the fork
matches stock, just say "matches stock" — we only need the diff.

If something genuinely isn't present in the image, say so explicitly. Do not infer the layout
from stock LiteNetLib or reason about what it "should" be — we already have stock, and a
plausible-but-wrong layout costs us more than a "not found".
