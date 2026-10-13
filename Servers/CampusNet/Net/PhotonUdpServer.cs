using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace CampusNet.Net;

/// <summary>
/// Photon's ENet-style reliable UDP (wire details verified from a capture, docs/photon-wire-findings.md):
/// 12-byte packet header + N commands, per-channel reliable sequencing with acks and retransmission,
/// unreliable sequencing, fragmentation above the MTU, ping/keep-alive, server-time via ack timestamp.
/// One instance per listening port; the service on top receives whole messages per peer.
/// </summary>
public sealed class PhotonUdpServer
{
    public const int Mtu = 1200;
    private const int PacketHeader = 12, CommandHeader = 12;
    private const byte CmdAck = 1, CmdConnect = 2, CmdVerifyConnect = 3, CmdDisconnect = 4, CmdPing = 5,
                       CmdReliable = 6, CmdUnreliable = 7, CmdFragment = 8, CmdServerTime = 12;
    private const byte FlagReliable = 1, FlagUnsequenced = 2;
    private const byte ChannelSystem = 255;
    // How long a peer may go SILENT before it is dropped. It was 12 s, and a reliable command
    // left unacked for ~11 s dropped it too - so a player who took their headset off, tabbed out or
    // froze for a moment was thrown out of the room (2026-09-27: 434 "timeout" and 159 "unacked,
    // last heard >5 s" game drops in a day). Photon itself keeps a paused client alive for a minute
    // (PUN KeepAliveInBackground 60 s). Room owners / co-owners and staff get AfkExemptTimeoutMs
    // (Peer.TimeoutMs, set once the Campus server confirms who they are - see AfkExempt).
    public static readonly int PeerTimeoutMs = EnvInt("CAMPUSNET_PEER_TIMEOUT_MS", 60000);
    public static readonly int AfkExemptTimeoutMs = EnvInt("CAMPUSNET_EXEMPT_TIMEOUT_MS", 600000);
    private const int PingIntervalMs = 1000, MaxRetries = 8;
    // An unanswered reliable command is NOT a reason to drop a peer that is still talking to us unless that peer has acked NOTHING at all for
    // this long. It used to be: 8 retries (~14 s) with the peer heard in the last 3 s. Two things made that throw healthy players out
    // (2026-10-13: 10 "unacked" drops in 6 h, "pending" counts of 20 / 93 / 102 / 376 / 523 with "last heard" a few ms ago):
    //   - a peer that went quiet for a while (a hitch, a tab-out, Wi-Fi) piled up unacked commands - one keep-alive ping a second - and every
    //     one of them had long used up its retries, so the instant its first packet arrived (and it was "heard" again, but had not yet acked
    //     the backlog) the tick dropped it; the client only noticed ~15 s later (ClientTimeout), as a black screen and a reload into the dorm;
    //   - a single keep-alive ping whose ack was lost kept the peer on the hook while every other command was being acked.
    // Now a quiet peer gets a fresh retry budget when it speaks again, an unanswered ping is simply forgotten while the peer acks other
    // things, and the peer is dropped for this only when it keeps talking but has acked nothing for UnackedDropMs.
    public static readonly int UnackedDropMs = EnvInt("CAMPUSNET_UNACKED_DROP_MS", 30000);
    private const int ResumeGapMs = 3000, MaxPendingPings = 20;
    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    public readonly int Port;
    public readonly string Name;
    private readonly Socket _sock;
    private readonly ConcurrentDictionary<EndPoint, Peer> _peers = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Random _rng = new();
    private int ServerTime => (int)_clock.ElapsedMilliseconds;

    public event Action<Peer, byte[], bool>? OnMessage;   // a complete message (0xF3 ...), reliable?
    public event Action<Peer>? OnDisconnect;
    public event Action<Peer>? OnConnect;

    public PhotonUdpServer(string name, int port)
    {
        Name = name; Port = port;
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { _sock.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }   // SIO_UDP_CONNRESET off (Windows)
        _sock.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    public int PeerCount => _peers.Count;
    public IEnumerable<Peer> Peers => _peers.Values;

    // ------------------------------------------------------------ peer

    public sealed class Peer
    {
        public readonly EndPoint Remote;
        public readonly short PeerId;
        public int Challenge;
        public bool Connected;
        public long LastRecv, LastPing, LastSend, LastAck;   // LastAck: when the peer last acknowledged anything of ours
        public int TimeoutMs = PeerTimeoutMs;   // silence allowed before the peer is dropped
        public int Rtt = 200;
        public object? Session;            // the service's per-connection state
        internal readonly Dictionary<byte, ChannelState> Channels = new();
        internal readonly List<PendingAck> AckQueue = new();
        internal readonly List<OutCommand> OutQueue = new();
        internal readonly Dictionary<long, OutCommand> UnackedBySeq = new();
        internal readonly Dictionary<long, Fragmenter> Fragments = new();
        internal int FragmentEvictions;
        internal readonly HashSet<byte> UnknownTypesLogged = new();
        internal int UnreliableOutSeq;
        internal bool Disconnecting;
        internal readonly TokenBucket InBytes = new(Guard.InBytesPerSec, Guard.InBytesBurst);
        // Health ledger, so a disconnect can be explained afterwards: running totals, and every 5 s a snapshot of what changed
        // (packets in / out, acks received from the peer, reliable commands received from it, how many of OUR reliable commands are
        // still unacked and how old the oldest is). Printed by DropPeer when the last 30 s look wrong.
        internal long PktsIn, PktsOut, AcksIn, RelIn;
        internal long SnapAt, SnapPktsIn, SnapPktsOut, SnapAcksIn, SnapRelIn, ConnectedAt;
        internal readonly Queue<(long At, long In, long Out, long Acks, long Rel, int Unacked, long OldestMs)> Snaps = new();
        public readonly string Ip;
        public Peer(EndPoint remote, short id) { Remote = remote; PeerId = id; Ip = Guard.IpOf(remote); }
        public override string ToString() => $"{Remote}#{PeerId}";
    }

    internal sealed class ChannelState
    {
        public int ReliableOut;       // last reliable seq sent
        public int ReliableInExpected = 1;
        public int UnreliableIn;
        // Out-of-order reliable messages, with how many reliable seqs each occupies (1, or a
        // fragmented message's fragment count - it must advance the stream by that much).
        public readonly SortedDictionary<int, (byte[] Message, int Span)> Pending = new();
    }

    internal struct PendingAck { public byte Channel; public int Seq; public int SentTime; }

    internal sealed class OutCommand
    {
        public byte Type, Channel, Flags; public int Seq; public byte[] Payload = Array.Empty<byte>();
        public long FirstSent, LastSent; public int Retries; public bool Reliable => (Flags & FlagReliable) != 0;
        public int Length => CommandHeader + Payload.Length;
    }

    internal sealed class Fragmenter { public int Count, Total; public byte Channel; public int Start; public DateTime Created = DateTime.UtcNow; public readonly SortedDictionary<int, byte[]> Parts = new(); }

    // ------------------------------------------------------------ callbacks outside the peer lock
    //
    // The game loop FROZE for good on 2026-10-03 (every join sat at "18%"; the game socket's receive queue
    // filled and nothing ran again). Cause: a lock-order cycle. The tick thread dropped a silent peer while
    // holding lock(peer), and the disconnect handler then wanted room.Sync; at the same moment the receive
    // thread held room.Sync (relaying a message to that very peer, which needs lock(peer)). Handlers ran
    // INSIDE lock(peer) everywhere, so peer -> room and room -> peer both existed.
    // Now the message and disconnect handlers are queued while a peer lock is held and run once it is
    // released, so a peer lock never waits on a room lock: the only order left is room -> peer.
    [ThreadStatic] private static List<Action>? _post;

    private static void Post(Action a) { if (_post != null) _post.Add(a); else a(); }

    /// <summary>Run the queued callbacks (the thread's peer lock must already be released). Returns how many ran.</summary>
    private static int RunPosted()
    {
        var l = _post; _post = null;
        if (l == null) return 0;
        foreach (var a in l) a();
        return l.Count;
    }

    // ------------------------------------------------------------ run loop

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"[{Name}] listening on udp/{Port}");
        var buf = new byte[65536];
        var tick = Task.Run(() => TickLoop(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await _sock.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (Exception ex) { Console.WriteLine($"[{Name}] recv error: {ex.Message}"); continue; }
            try { HandlePacket(r.RemoteEndPoint, buf.AsSpan(0, r.ReceivedBytes)); }
            catch (Exception ex) { Console.WriteLine($"[{Name}] packet from {r.RemoteEndPoint} failed: {ex}"); }
        }
        await tick;
    }

    private async Task TickLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(20, ct); } catch { break; }
            long now = _clock.ElapsedMilliseconds;
            foreach (var p in _peers.Values)
            {
                _post = new List<Action>();
                try
                {
                    lock (p)
                    {
                        if (p.Connected && now - p.SnapAt >= 5000) TakeSnapshot(p, now);
                        if (now - p.LastRecv > p.TimeoutMs) { DropPeer(p, $"timeout ({p.TimeoutMs / 1000} s silent)"); continue; }
                        if (p.Connected && now - p.LastPing > PingIntervalMs)
                        {
                            p.LastPing = now;
                            // Not while twenty are already waiting: a quiet peer used to collect one a second (523 were seen), all
                            // retransmitted the moment it came back. The retransmissions of the waiting ones keep the line warm.
                            if (p.UnackedBySeq.Values.Count(u => u.Type == CmdPing) < MaxPendingPings)
                                Enqueue(p, new OutCommand { Type = CmdPing, Channel = ChannelSystem, Flags = FlagReliable });
                        }
                        // Retransmit unacked reliable commands.
                        foreach (var c in p.UnackedBySeq.Values.ToList())
                        {
                            int rto = Math.Clamp(p.Rtt * 2 + 50, 200, 2000) << Math.Min(c.Retries, 3);
                            if (now - c.LastSent < rto) continue;
                            // Out of retries only counts when the client is still TALKING to us and acking NOTHING (see UnackedDropMs).
                            // A silent client (paused, AFK, a hiccup) is given its whole timeout instead - it acks everything once it
                            // is back (ResumeAfterSilence also resets the retry count then).
                            bool usedUp = c.Retries++ >= MaxRetries;
                            // A keep-alive ping nobody answered while the peer acks everything else: it carries nothing, forget it.
                            if (usedUp && c.Type == CmdPing && now - p.LastAck < 5000) { p.UnackedBySeq.Remove(Key(c.Channel, c.Seq)); continue; }
                            if (usedUp && now - p.LastRecv < 3000 && now - p.LastAck >= UnackedDropMs)
                            {
                                // Say WHICH command went unanswered and whether the client was still
                                // talking to us. ~1 in 10 disconnects are "unacked", spread across all
                                // players: a recent last-heard means the client is alive but refuses
                                // this one command (a CampusNet bug to fix); an old one means the
                                // network path dropped out.
                                string what = $"type {c.Type} ch {c.Channel} seq {c.Seq} {c.Payload.Length}B";
                                if (c.Payload.Length > 3 && (c.Type == CmdReliable || c.Type == CmdFragment))
                                {
                                    var b = c.Payload; int o = c.Type == CmdFragment ? 20 : 0;
                                    if (b.Length > o + 2 && b[o] == 0xF3) what += $" msg {b[o + 1] & 0x7F} code {b[o + 2]}";
                                    if (c.Type == CmdFragment && b.Length >= 20) what += $" frag {BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(8))}/{BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(4))} total {BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(12))}B";
                                }
                                DropPeer(p, $"unacked command ({what}; pending {p.UnackedBySeq.Count}, last heard {now - p.LastRecv} ms ago, last ack {now - p.LastAck} ms ago, rtt {p.Rtt} ms)");
                                break;
                            }
                            p.OutQueue.Add(c);
                        }
                        Flush(p);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[{Name}] tick {p}: {ex.Message}"); }
                finally { try { RunPosted(); } catch (Exception ex) { Console.WriteLine($"[{Name}] tick {p} callbacks: {ex.Message}"); } }
            }
        }
    }

    private static (long At, long In, long Out, long Acks, long Rel, int Unacked, long OldestMs) Snap(Peer p, long now)
    {
        long oldest = 0;
        foreach (var c in p.UnackedBySeq.Values) if (c.FirstSent > 0) oldest = Math.Max(oldest, now - c.FirstSent);
        var s = (now, p.PktsIn - p.SnapPktsIn, p.PktsOut - p.SnapPktsOut, p.AcksIn - p.SnapAcksIn, p.RelIn - p.SnapRelIn, p.UnackedBySeq.Count, oldest);
        p.SnapPktsIn = p.PktsIn; p.SnapPktsOut = p.PktsOut; p.SnapAcksIn = p.AcksIn; p.SnapRelIn = p.RelIn; p.SnapAt = now;
        return s;
    }

    private static void TakeSnapshot(Peer p, long now)
    {
        p.Snaps.Enqueue(Snap(p, now));
        while (p.Snaps.Count > 6) p.Snaps.Dequeue();
    }

    /// <summary>When a peer goes and the last 30 s look wrong - one of our commands waited 3 s or more for its ack, or the peer sent no acks while we
    /// were sending - say what each 5 s slice looked like, so "the server stopped hearing the client" can be told from "the client stopped hearing
    /// the server" afterwards. Quiet for normal leaves.</summary>
    private void ExplainDrop(Peer p, string why)
    {
        try
        {
            if (!p.Connected || p.ConnectedAt == 0) return;
            long now = _clock.ElapsedMilliseconds;
            var list = p.Snaps.ToList();
            if (now - p.SnapAt > 500) list.Add(Snap(p, now));          // the slice still in progress
            bool odd = list.Any(s => s.OldestMs >= 3000 || (s.Out > 3 && s.Acks == 0 && s.At - p.ConnectedAt > 6000));
            if (!odd) return;
            var sb = new System.Text.StringBuilder();
            foreach (var s in list) sb.Append($" [{(s.At - now) / 1000.0:F0}s: in {s.In} out {s.Out} acks {s.Acks} rel {s.Rel} unacked {s.Unacked}/{s.OldestMs}ms]");
            Console.WriteLine($"[{Name}] {p} ({why}) last slices (in/out = packets, acks = from the peer, rel = reliable commands from the peer, unacked = ours waiting / oldest age):{sb}");
        }
        catch { }
    }

    /// <summary>The peer spoke again after at least ResumeGapMs of silence: whatever piled up unacked while it was away starts over with a
    /// full retry budget and the peer gets UnackedDropMs of grace to acknowledge the backlog. Called with the peer's lock held.</summary>
    private void ResumeAfterSilence(Peer peer, long now)
    {
        int n = 0;
        foreach (var c in peer.UnackedBySeq.Values) { c.Retries = 0; n++; }
        peer.LastAck = now;
        if (n > 0) Console.WriteLine($"[{Name}] {peer} is back after {(now - peer.LastRecv) / 1000.0:F1} s of silence - {n} unacked command(s) get a fresh retry budget");
    }

    // ------------------------------------------------------------ receive

    // Photon's region ping (Photon Realtime PhotonPing / PingMono): a bare 13-byte datagram - twelve
    // 0x7D bytes and a ping id - that the server echoes back unchanged. The client pings the region's
    // master before authenticating and retries 5 x 800 ms when nothing answers. We dropped it (only a
    // CONNECT may create a peer), so EVERY login sat through that ~4-5 s timeout on the name server
    // (key exchange -> authenticate took 4.4-5.4 s in the logs). Echoing it removes the wait.
    private static bool IsPhotonPing(ReadOnlySpan<byte> pkt)
    {
        if (pkt.Length < 9 || pkt.Length > 16) return false;
        for (int i = 0; i < pkt.Length - 1; i++) if (pkt[i] != 0x7D) return false;
        return true;
    }

    private void HandlePacket(EndPoint from, ReadOnlySpan<byte> pkt)
    {
        if (IsPhotonPing(pkt))
        {
            if (!Guard.IsBanned(Guard.IpOf(from)))
                try { _sock.SendTo(pkt.ToArray(), SocketFlags.None, from); } catch { }
            return;
        }
        if (pkt.Length < PacketHeader) return;
        short peerId = BinaryPrimitives.ReadInt16BigEndian(pkt);
        int count = pkt[3];
        int sentTime = BinaryPrimitives.ReadInt32BigEndian(pkt.Slice(4));
        int challenge = BinaryPrimitives.ReadInt32BigEndian(pkt.Slice(8));

        if (!_peers.TryGetValue(from, out var peer))
        {
            // Only a CONNECT may create a peer.
            if (pkt.Length < PacketHeader + CommandHeader || pkt[PacketHeader] != CmdConnect) return;
            string ip = Guard.IpOf(from);
            if (Guard.IsBanned(ip)) return;
            if (!Guard.AllowConnect(ip)) { Guard.BanIp(ip, $"more than {Guard.ConnectsPerIpPer10s} connects in 10s on {Name}"); return; }
            if (_peers.Values.Count(p => p.Ip == ip) >= Guard.MaxPeersPerIp)
            {
                Console.WriteLine($"[{Name}] connect from {from} refused: {Guard.MaxPeersPerIp} peers already from {ip}");
                return;
            }
            short id;
            do { id = (short)_rng.Next(1, short.MaxValue); } while (_peers.Values.Any(p => p.PeerId == id));
            // LastRecv starts NOW, not 0: the peer is visible to the tick thread (_peers) before the lock below
            // stamps it, and with LastRecv = 0 the tick saw "silent for the whole uptime" and dropped the new
            // connection at once ("gone (timeout (60 s silent))" in the same second as its connect) - about one
            // connect in twenty, which left the client at "Failed to connect to region" / stuck at 18%.
            peer = new Peer(from, id) { Challenge = challenge, LastRecv = _clock.ElapsedMilliseconds, LastAck = _clock.ElapsedMilliseconds };
            _peers[from] = peer;
            Console.WriteLine($"[{Name}] connect from {from} -> peer {id}");
        }

        _post = new List<Action>();
        int posted = 0;
        try
        {
        lock (peer)
        {
            long nowMs = _clock.ElapsedMilliseconds;
            if (peer.Connected && nowMs - peer.LastRecv >= ResumeGapMs) ResumeAfterSilence(peer, nowMs);
            peer.LastRecv = nowMs;
            peer.PktsIn++;
            // Inbound flood: over the byte budget the packet is dropped before any decode work;
            // a peer that stays over it is closed and its address cooled.
            if (!peer.InBytes.Take(pkt.Length, nowMs))
            {
                if (peer.InBytes.DroppedSince == 1) Console.WriteLine($"[{Name}] {peer} over {Guard.InBytesPerSec} B/s inbound - dropping packets");
                if (nowMs - peer.InBytes.ViolatingSinceMs > Guard.FloodKickAfterSec * 1000L)
                {
                    Guard.BanIp(peer.Ip, $"inbound flood on {Name} ({peer.InBytes.Dropped} packets dropped)");
                    DropPeer(peer, "inbound flood");
                }
                return;
            }
            int pos = PacketHeader;
            for (int i = 0; i < count && pos + CommandHeader <= pkt.Length; i++)
            {
                byte type = pkt[pos], channel = pkt[pos + 1], flags = pkt[pos + 2];
                int len = BinaryPrimitives.ReadInt32BigEndian(pkt.Slice(pos + 4));
                int seq = BinaryPrimitives.ReadInt32BigEndian(pkt.Slice(pos + 8));
                if (len < CommandHeader || pos + len > pkt.Length) break;
                var body = pkt.Slice(pos + CommandHeader, len - CommandHeader);
                HandleCommand(peer, type, channel, flags, seq, sentTime, body);
                pos += len;
            }
            Flush(peer);
        }
        }
        finally { posted = RunPosted(); }
        if (posted > 0) lock (peer) Flush(peer);       // what the handlers just queued (the 20 ms tick would send it too)
    }

    private void HandleCommand(Peer peer, byte type, byte channel, byte flags, int seq, int sentTime, ReadOnlySpan<byte> body)
    {
        if ((flags & FlagReliable) != 0 && type != CmdAck)
        {
            peer.AckQueue.Add(new PendingAck { Channel = channel, Seq = seq, SentTime = sentTime });
            peer.RelIn++;
        }
        else if (type == CmdAck) peer.AcksIn++;

        switch (type)
        {
            case CmdAck:
            {
                if (body.Length < 8) return;
                int ackedSeq = BinaryPrimitives.ReadInt32BigEndian(body);
                int ackedSent = BinaryPrimitives.ReadInt32BigEndian(body.Slice(4));
                peer.LastAck = _clock.ElapsedMilliseconds;
                if (peer.UnackedBySeq.Remove(Key(channel, ackedSeq), out var cmd))
                {
                    int rtt = ServerTime - ackedSent;
                    if (rtt >= 0 && rtt < 5000) peer.Rtt = (peer.Rtt * 7 + rtt) / 8;
                }
                return;
            }
            case CmdConnect:
            {
                // Answer VerifyConnect with the peer id we assigned; the rest echoes the client's connect block.
                var payload = new byte[32];
                if (body.Length >= 32) body.Slice(0, 32).CopyTo(payload);
                BinaryPrimitives.WriteInt16BigEndian(payload, peer.PeerId);
                for (int i = 12; i < 32; i++) payload[i] = 0;
                if (!peer.Connected)
                {
                    peer.Connected = true;
                    peer.ConnectedAt = peer.SnapAt = _clock.ElapsedMilliseconds;
                    peer.LastPing = _clock.ElapsedMilliseconds;
                    Enqueue(peer, new OutCommand { Type = CmdVerifyConnect, Channel = ChannelSystem, Flags = FlagReliable, Payload = payload });
                    OnConnect?.Invoke(peer);
                }
                return;
            }
            case CmdDisconnect:
                DropPeer(peer, "client disconnect");
                return;
            case CmdPing:
            case CmdServerTime:
                return;   // the ack (with our header sentTime) is the whole answer
            case CmdReliable:
                DeliverReliable(peer, channel, seq, body.ToArray());
                return;
            case CmdUnreliable:
            {
                if (body.Length < 4) return;
                int useq = BinaryPrimitives.ReadInt32BigEndian(body);
                var ch = Channel(peer, channel);
                if (useq <= ch.UnreliableIn) return;   // late/duplicate
                ch.UnreliableIn = useq;
                Dispatch(peer, body.Slice(4).ToArray(), false);
                return;
            }
            case CmdFragment:
            {
                if (body.Length < 20) return;
                int start = BinaryPrimitives.ReadInt32BigEndian(body);
                int fcount = BinaryPrimitives.ReadInt32BigEndian(body.Slice(4));
                int total = BinaryPrimitives.ReadInt32BigEndian(body.Slice(12));
                int offset = BinaryPrimitives.ReadInt32BigEndian(body.Slice(16));
                long key = Key(channel, start);
                // The fragment header is client-supplied: bound the reassembly buffer and the number
                // of half-built messages before either becomes an allocation the client controls.
                if (total <= 0 || total > Guard.MaxFragmentTotal || fcount <= 0 || fcount > Guard.MaxFragmentTotal / 64 || offset < 0 || offset >= total)
                {
                    Guard.BanIp(peer.Ip, $"bad fragment header on {Name} (total {total}, count {fcount}, offset {offset})");
                    DropPeer(peer, "bad fragment");
                    return;
                }
                // A resent fragment of a message that was already rebuilt and delivered (our ack
                // was lost or slow) - it is acked above; it must NOT open a new reassembly. That
                // used to start a half-built message that could never complete; big messages
                // (screen-share frames, ~50 fragments each) leaked them until the peer was dropped
                // for "too many partial fragments".
                var fch = Channel(peer, channel);
                if (start < fch.ReliableInExpected) return;
                if (!peer.Fragments.TryGetValue(key, out var f))
                {
                    if (peer.Fragments.Count >= Guard.MaxFragmentSets)
                    {
                        // Bounded, but never a disconnect: first discard sets the stream has already
                        // moved past or that have been stuck for 30 s, then the oldest one.
                        var now = DateTime.UtcNow;
                        foreach (var kv in peer.Fragments.Where(kv => kv.Value.Start < Channel(peer, kv.Value.Channel).ReliableInExpected
                                                                     || (now - kv.Value.Created).TotalSeconds > 30).ToList())
                            peer.Fragments.Remove(kv.Key);
                        if (peer.Fragments.Count >= Guard.MaxFragmentSets)
                        {
                            var oldest = peer.Fragments.OrderBy(kv => kv.Value.Created).First();
                            peer.Fragments.Remove(oldest.Key);
                            if (peer.FragmentEvictions++ == 0)
                                Console.WriteLine($"[{Name}] {peer} has {Guard.MaxFragmentSets} partial messages in flight - discarding the oldest (logged once per peer)");
                        }
                    }
                    peer.Fragments[key] = f = new Fragmenter { Count = fcount, Total = total, Channel = channel, Start = start };
                }
                f.Parts[offset] = body.Slice(20).ToArray();
                if (f.Parts.Count < f.Count) return;
                peer.Fragments.Remove(key);
                var whole = new byte[f.Total]; int p = 0;
                foreach (var part in f.Parts.Values) { Buffer.BlockCopy(part, 0, whole, p, Math.Min(part.Length, whole.Length - p)); p += part.Length; }
                // Fragments occupy reliable seqs start..start+count-1; deliver in order at `start`.
                DeliverReliable(peer, channel, start, whole, fcount);
                return;
            }
            default:
                // Unknown command type: say so once per (peer, type) - a missing delivery mode
                // (unsequenced variants are 11/14/15/16) shows up here instead of vanishing.
                if (peer.UnknownTypesLogged.Add(type))
                    Console.WriteLine($"[{Name}] {peer} sent command type {type} (channel {channel}, flags {flags}, {body.Length} bytes) - not handled, dropped");
                return;
        }
    }

    private static long Key(byte channel, int seq) => ((long)channel << 32) | (uint)seq;
    private ChannelState Channel(Peer p, byte c) { if (!p.Channels.TryGetValue(c, out var s)) p.Channels[c] = s = new ChannelState(); return s; }

    private void DeliverReliable(Peer peer, byte channel, int seq, byte[] message, int span = 1)
    {
        var ch = Channel(peer, channel);
        if (seq < ch.ReliableInExpected) return;          // duplicate (already acked above)
        if (seq > ch.ReliableInExpected)
        {
            if (ch.Pending.Count >= Guard.MaxPendingReliable) { DropPeer(peer, "reliable window overflow"); return; }
            ch.Pending[seq] = (message, span); return;
        }
        ch.ReliableInExpected = seq + span;
        Dispatch(peer, message, true);
        while (ch.Pending.Count > 0)
        {
            var first = ch.Pending.First();
            if (first.Key != ch.ReliableInExpected) break;
            ch.Pending.Remove(first.Key);
            // Advance by the message's SPAN: a parked fragmented message occupies fcount seqs.
            // Advancing by 1 left the stream waiting forever for a seq that was part of it, so
            // every later reliable message from that player stalled (a frozen screen share).
            ch.ReliableInExpected += first.Value.Span;
            Dispatch(peer, first.Value.Message, true);
        }
    }

    private void Dispatch(Peer peer, byte[] message, bool reliable)
    {
        if (message.Length == 0) return;
        Post(() =>
        {
            try { OnMessage?.Invoke(peer, message, reliable); }
            catch (Exception ex) { Console.WriteLine($"[{Name}] handler error for {peer}: {ex}"); }
        });
    }

    // ------------------------------------------------------------ send

    public void SendReliable(Peer peer, byte[] message, byte channel = 0)
    {
        lock (peer)
        {
            if (peer.Disconnecting) return;
            int maxBody = Mtu - PacketHeader - CommandHeader;
            if (message.Length <= maxBody)
            {
                Enqueue(peer, new OutCommand { Type = CmdReliable, Channel = channel, Flags = FlagReliable, Payload = message });
                return;
            }
            int chunk = maxBody - 20;
            int count = (message.Length + chunk - 1) / chunk;
            var ch = Channel(peer, channel);
            int start = ch.ReliableOut + 1;
            for (int i = 0; i < count; i++)
            {
                int off = i * chunk, len = Math.Min(chunk, message.Length - off);
                var payload = new byte[20 + len];
                BinaryPrimitives.WriteInt32BigEndian(payload, start);
                BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), count);
                BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), i);
                BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12), message.Length);
                BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(16), off);
                Buffer.BlockCopy(message, off, payload, 20, len);
                Enqueue(peer, new OutCommand { Type = CmdFragment, Channel = channel, Flags = FlagReliable, Payload = payload });
            }
        }
    }

    public void SendUnreliable(Peer peer, byte[] message, byte channel = 0)
    {
        lock (peer)
        {
            if (peer.Disconnecting) return;
            if (message.Length > Mtu - PacketHeader - CommandHeader - 4) { SendReliable(peer, message, channel); return; }
            var payload = new byte[4 + message.Length];
            BinaryPrimitives.WriteInt32BigEndian(payload, ++peer.UnreliableOutSeq);
            Buffer.BlockCopy(message, 0, payload, 4, message.Length);
            Enqueue(peer, new OutCommand { Type = CmdUnreliable, Channel = channel, Flags = 0, Payload = payload });
        }
    }

    public void Disconnect(Peer peer, string why)
    {
        bool own = _post == null;
        if (own) _post = new List<Action>();
        try
        {
            lock (peer)
            {
                if (peer.Disconnecting) return;
                Enqueue(peer, new OutCommand { Type = CmdDisconnect, Channel = ChannelSystem, Flags = FlagUnsequenced });
                Flush(peer);
                DropPeer(peer, why);
            }
        }
        finally { if (own) RunPosted(); }
    }

    private void Enqueue(Peer peer, OutCommand c)
    {
        var ch = Channel(peer, c.Channel);
        if (c.Reliable)
        {
            c.Seq = ++ch.ReliableOut;
            peer.UnackedBySeq[Key(c.Channel, c.Seq)] = c;
        }
        else
        {
            // ENet sequences unreliable commands AGAINST the channel's reliable stream: the header's
            // sequence field must carry the last reliable seq sent on the channel. The receiver
            // holds an unreliable command until it has that reliable command, and DISCARDS one whose
            // reliable seq is older than what it already has. Stamping 0 here meant every unreliable
            // event the server relayed - all the avatar movement - was thrown away as stale the
            // moment the client had received its first reliable message (the join response).
            c.Seq = ch.ReliableOut;
        }
        peer.OutQueue.Add(c);
    }

    /// <summary>Pack queued acks + commands into MTU-sized packets and send them.</summary>
    private void Flush(Peer peer)
    {
        if (peer.AckQueue.Count == 0 && peer.OutQueue.Count == 0) return;
        long now = _clock.ElapsedMilliseconds;
        var pkt = new byte[Mtu];
        int pos = PacketHeader, n = 0;

        void Send()
        {
            if (n == 0) return;
            BinaryPrimitives.WriteInt16BigEndian(pkt, 0);
            pkt[2] = 0; pkt[3] = (byte)n;
            BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(4), ServerTime);
            BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(8), peer.Challenge);
            try { _sock.SendTo(pkt, 0, pos, SocketFlags.None, peer.Remote); peer.PktsOut++; } catch (Exception ex) { Console.WriteLine($"[{Name}] send to {peer} failed: {ex.Message}"); }
            pos = PacketHeader; n = 0;
        }
        void Put(byte type, byte channel, byte flags, int seq, ReadOnlySpan<byte> payload)
        {
            int len = CommandHeader + payload.Length;
            if (pos + len > Mtu) Send();
            pkt[pos] = type; pkt[pos + 1] = channel; pkt[pos + 2] = flags; pkt[pos + 3] = 4;
            BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(pos + 4), len);
            BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(pos + 8), seq);
            payload.CopyTo(pkt.AsSpan(pos + CommandHeader));
            pos += len; n++;
            if (n == 255) Send();
        }

        foreach (var a in peer.AckQueue)
        {
            Span<byte> ap = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(ap, a.Seq);
            BinaryPrimitives.WriteInt32BigEndian(ap.Slice(4), a.SentTime);
            Put(CmdAck, a.Channel, 0, 0, ap);
        }
        peer.AckQueue.Clear();
        foreach (var c in peer.OutQueue)
        {
            if (c.Reliable) { if (c.FirstSent == 0) c.FirstSent = now; c.LastSent = now; }
            Put(c.Type, c.Channel, c.Flags, c.Seq, c.Payload);
        }
        peer.OutQueue.Clear();
        Send();
        peer.LastSend = now;
    }

    /// <summary>Close a peer for abuse and cool its address (Guard.IpBanSec).</summary>
    public void Kick(Peer peer, string why, int? banSeconds = null)
    {
        Guard.BanIp(peer.Ip, $"{why} on {Name}", banSeconds);
        Disconnect(peer, why);
    }

    private void DropPeer(Peer peer, string why)
    {
        if (peer.Disconnecting) return;
        peer.Disconnecting = true;
        _peers.TryRemove(peer.Remote, out _);
        Console.WriteLine($"[{Name}] {peer} gone ({why})");
        ExplainDrop(peer, why);
        Post(() => { try { OnDisconnect?.Invoke(peer); } catch (Exception ex) { Console.WriteLine($"[{Name}] disconnect handler: {ex}"); } });
    }
}
