using System.Collections.Concurrent;
using System.Net;
using CampusNet.Services;

namespace CampusNet.Net;

/// <summary>
/// Abuse limits for the whole stack. Every number has an environment override (CAMPUSNET_*) so a
/// limit can be tuned on the box without a rebuild. The numbers are sized so a real client never
/// meets them: the Rec Room client's steady state is a few dozen events a second and well under
/// 100 KB/s, and a burst of RPCs on a scene change is hundreds, not thousands.
///
/// 2026-09-17: the op/byte budgets were raised ~10x. The maker pen's Clone and Split send one
/// RPC per object in a single burst (a big selection is thousands of ops in a second), and the
/// 200 ops/s bucket was silently dropping the tail of that burst ("over budget (op 253)") -
/// the other clients never saw the clones and the pen looked broken. A budget must sit far
/// above anything a builder can do; these only stop a flood, not a build session.
///
///   Transport (per peer / per IP, PhotonUdpServer)
///     MaxPeersPerIp          simultaneous connections from one address (a household behind NAT)
///     ConnectsPerIpPer10s    new CONNECTs per address per 10 s (a bot spinning up peers)
///     InBytesPerSec / InBytesBurst   inbound bytes per peer, token bucket - excess packets are
///                            dropped before decryption/decoding; a peer over it for
///                            FloodKickAfterSec straight is dropped and its IP cooled for IpBanSec
///     MaxFragmentTotal       largest reassembled message (the fragment header's "total" is
///                            client-supplied - unchecked it is an allocation of any size)
///     MaxFragmentSets        concurrent reassemblies per peer
///     MaxPendingReliable     out-of-order reliable commands buffered per channel
///   Game (per connection, GameService)
///     OpsPerSec / OpsBurst   operations (RaiseEvent, SetProperties, ...) per connection
///     EventBytesPerSec / EventBytesBurst   RaiseEvent payload bytes per connection
///     MaxEventBytes          one event's payload
///     MaxRoomCache / MaxActorCache   cached events per room / per actor
///     OpKickAfter            dropped operations in a row before the connection is closed and
///                            its IP cooled for IpBanSec
/// </summary>
public static class Guard
{
    public static readonly int MaxPeersPerIp        = Env("CAMPUSNET_MAX_PEERS_PER_IP", 16);
    public static readonly int ConnectsPerIpPer10s  = Env("CAMPUSNET_CONNECTS_PER_IP_10S", 30);
    public static readonly int InBytesPerSec        = Env("CAMPUSNET_IN_BYTES_PER_SEC", 2048 * 1024);
    public static readonly int InBytesBurst         = Env("CAMPUSNET_IN_BYTES_BURST", 8192 * 1024);
    public static readonly int FloodKickAfterSec    = Env("CAMPUSNET_FLOOD_KICK_AFTER_SEC", 5);
    public static readonly int MaxFragmentTotal     = Env("CAMPUSNET_MAX_FRAGMENT_TOTAL", 8 * 1024 * 1024);
    public static readonly int MaxFragmentSets      = Env("CAMPUSNET_MAX_FRAGMENT_SETS", 16);
    public static readonly int MaxPendingReliable   = Env("CAMPUSNET_MAX_PENDING_RELIABLE", 4096);
    public static readonly int IpBanSec             = Env("CAMPUSNET_IP_BAN_SEC", 300);

    public static readonly int OpsPerSec            = Env("CAMPUSNET_OPS_PER_SEC", 2000);
    public static readonly int OpsBurst             = Env("CAMPUSNET_OPS_BURST", 10000);
    public static readonly int EventBytesPerSec     = Env("CAMPUSNET_EVENT_BYTES_PER_SEC", 2048 * 1024);
    public static readonly int EventBytesBurst      = Env("CAMPUSNET_EVENT_BYTES_BURST", 8192 * 1024);
    public static readonly int MaxEventBytes        = Env("CAMPUSNET_MAX_EVENT_BYTES", 4096 * 1024);
    public static readonly int MaxRoomCache         = Env("CAMPUSNET_MAX_ROOM_CACHE", 100000);
    public static readonly int MaxActorCache        = Env("CAMPUSNET_MAX_ACTOR_CACHE", 8192);
    public static readonly int OpKickAfter          = Env("CAMPUSNET_OP_KICK_AFTER", 1500);
    public static readonly int OpFloodBanSec        = Env("CAMPUSNET_OP_FLOOD_BAN_SEC", 60);

    private static int Env(string name, int dflt) => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : dflt;

    // ---------------------------------------------------------------- ip cooldown

    private static readonly ConcurrentDictionary<string, long> _banned = new();
    private static readonly ConcurrentDictionary<string, (int count, long windowStart)> _connects = new();

    public static string IpOf(EndPoint ep) => ep is IPEndPoint ip ? ip.Address.ToString() : ep.ToString() ?? "";

    public static void BanIp(string ip, string why, int? seconds = null)
    {
        long until = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (seconds ?? IpBanSec);
        _banned.AddOrUpdate(ip, until, (_, cur) => Math.Max(cur, until));
        Log.Warn($"guard: ip {ip} cooled for {(seconds ?? IpBanSec)}s - {why}");
    }

    public static bool IsBanned(string ip)
    {
        if (!_banned.TryGetValue(ip, out long until)) return false;
        if (until > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return true;
        _banned.TryRemove(ip, out _);
        return false;
    }

    /// <summary>Count a CONNECT from this IP; false when it is over ConnectsPerIpPer10s.</summary>
    public static bool AllowConnect(string ip)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var e = _connects.AddOrUpdate(ip, _ => (1, now), (_, cur) => now - cur.windowStart >= 10 ? (1, now) : (cur.count + 1, cur.windowStart));
        if (_connects.Count > 10000) foreach (var kv in _connects) if (now - kv.Value.windowStart > 60) _connects.TryRemove(kv.Key, out _);
        return e.count <= ConnectsPerIpPer10s;
    }
}

/// <summary>Token bucket: <c>rate</c> tokens/s refilled continuously, at most <c>burst</c> banked.</summary>
public sealed class TokenBucket
{
    private readonly double _rate, _burst;
    private double _tokens;
    private long _last;
    public long Dropped;                 // total refused
    public long DroppedSince;            // refusals in the current violation run
    public long ViolatingSinceMs;        // when the current run started, 0 = clean
    private long _lastDropMs = long.MinValue;
    private const int RunGapMs = 2000;   // a run ends after this long without a refusal

    public TokenBucket(int ratePerSec, int burst) { _rate = ratePerSec; _burst = burst; _tokens = burst; _last = long.MinValue; }

    /// <param name="nowMs">any monotonic millisecond clock - the bucket adopts the first one it sees</param>
    public bool Take(double n, long nowMs)
    {
        if (_last == long.MinValue) _last = nowMs;
        double dt = (nowMs - _last) / 1000.0;
        if (dt > 0) { _tokens = Math.Min(_burst, _tokens + dt * _rate); _last = nowMs; }
        if (_tokens >= n)
        {
            _tokens -= n;
            if (ViolatingSinceMs != 0 && nowMs - _lastDropMs > RunGapMs) { DroppedSince = 0; ViolatingSinceMs = 0; }
            return true;
        }
        // A run of refusals: a client that stays over budget keeps the run alive even though
        // the trickle of refilled tokens lets the odd operation through in between.
        if (ViolatingSinceMs == 0 || nowMs - _lastDropMs > RunGapMs) { DroppedSince = 0; ViolatingSinceMs = nowMs; }
        Dropped++; DroppedSince++; _lastDropMs = nowMs;
        return false;
    }
}
