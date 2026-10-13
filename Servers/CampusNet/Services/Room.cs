using System.Collections;
using CampusNet.Protocol;

namespace CampusNet.Services;

public sealed class Actor
{
    public int Nr;
    public string UserId = "";
    public Connection? Conn;          // null while inactive (disconnected with PlayerTtl)
    public readonly Hashtable Props = new();
    public bool Inactive;
    public DateTime InactiveSince;
    public readonly HashSet<byte> Groups = new();   // interest groups (0 = always)
    public readonly List<CachedEvent> OwnCache = new();   // MergeCache / ReplaceCache per actor (legacy)

    // Traffic counters for the periodic room summary (see GameService.Sweep): what this actor
    // raised, how much of it was unreliable, per event code, and how many recipients it reached.
    public long Raised, RaisedUnreliable, Delivered, DroppedNoRecipient;
    public readonly Dictionary<byte, int> RaisedByCode = new();
    public readonly Dictionary<byte, int> RaisedByGroup = new();

    // Stuck-load watchdog (GameService.Sweep / StuckLoad): when this actor last became active,
    // whether it has shown it finished loading (streamed a transform, ev 28, or sent its request
    // to the master client), and whether it has already been reported as stuck.
    public DateTime ActiveSince = DateTime.UtcNow;
    public bool LoadSignal, StuckReported, CreatedRoom;
}

public sealed class CachedEvent
{
    // ActorNr is the REAL sender, also for AddToRoomCacheGlobal: Photon keeps the sender on a
    // global event (it is replayed as coming from them and a RemoveFromRoomCache actor filter
    // matches it); "global" only means it survives its sender leaving. This used to store 0
    // for global events, so non-master removals never matched them (stale game state piled up
    // and was replayed to late joiners) and replays arrived from actor 0.
    public int ActorNr; public byte Code; public object? Data; public byte Group;
    public bool Global;
}

/// <summary>A game room: actors, properties, and the event cache PUN relies on for late joiners.</summary>
public sealed class Room
{
    public readonly string Name;
    public readonly Hashtable Props = new();   // byte system keys + custom keys
    public readonly SortedDictionary<int, Actor> Actors = new();
    public readonly List<CachedEvent> Cache = new();     // AddToRoomCache / AddToRoomCacheGlobal, in order
    public int NextActorNr = 1;
    public int PlayerTtl, EmptyRoomTtl;
    // OFF unless the creating client asks for it. PUN sends op param 241 / game prop 249 ONLY when
    // RoomOptions.CleanupCacheOnLeave is true, so their absence means the client wants the cache
    // KEPT - and Rec Room's rooms are created without them ({"BUG":1, 254:False, 250:[], 253:True}).
    // This used to default to true, which deleted the room CREATOR's cached events when they left:
    // those hold the room's buffered state (BufferedEventHelper "RoomLoad" slices), so every later
    // joiner got 3 cached events instead of ~40 and hung on the loading screen at 25% forever
    // ("Waiting to receive buffered message"; seen 2026-09-26 in Campus-Dorm-404635683). Rec Room
    // clears departed players' events itself (RemoveFromRoomCacheForActorsLeft) once it has moved on.
    public bool CleanupCacheOnLeave = false;
    public bool LoggedForeignRemove;
    public bool LoggedGiftBoxValid;                       // first gift-box spawn marked valid (GiftBoxRelay), logged once
    public bool LoggedGiftBoxSkip;                        // first uncached gift-box spawn, logged once                      // first non-master removal of others' events, logged once
    public DateTime EmptySince = DateTime.UtcNow;
    public readonly object Sync = new();
    public readonly DateTime CreatedAt = DateTime.UtcNow;

    public Room(string name) { Name = name; }

    public int MaxPlayers => Props[GameProp.MaxPlayers] is byte b ? b : (Props[GameProp.MaxPlayers] is int i ? i : 0);
    public bool IsOpen => Props[GameProp.IsOpen] is bool o ? o : true;
    public bool IsVisible => Props[GameProp.IsVisible] is bool v ? v : true;
    public int ActiveCount => Actors.Values.Count(a => !a.Inactive);
    // Photon's SetMasterClient: a room-property write of 248 guarded by the current master. The
    // chosen actor stays master while active; otherwise the lowest active actor number is.
    public int MasterOverride;
    public int MasterClientId => MasterOverride > 0 && Actors.TryGetValue(MasterOverride, out var mo) && !mo.Inactive
        ? MasterOverride
        : Actors.Values.Where(a => !a.Inactive).Select(a => a.Nr).DefaultIfEmpty(0).Min();

    public Actor? FindByUserId(string userId) => Actors.Values.FirstOrDefault(a => a.UserId == userId);

    /// <summary>Game properties as sent to clients: stored props plus the live system values.</summary>
    public Hashtable GamePropsForClient()
    {
        var h = new Hashtable();
        foreach (DictionaryEntry e in Props) h[e.Key] = e.Value;
        h[GameProp.MasterClientId] = MasterClientId;
        h[GameProp.PlayerCount] = (byte)ActiveCount;
        if (!h.ContainsKey(GameProp.IsOpen)) h[GameProp.IsOpen] = true;
        if (!h.ContainsKey(GameProp.IsVisible)) h[GameProp.IsVisible] = true;
        return h;
    }

    /// <summary>All players' properties keyed by actor number (what the join response carries).</summary>
    public Hashtable AllPlayerProps()
    {
        var h = new Hashtable();
        foreach (var a in Actors.Values) h[a.Nr] = PlayerPropsForClient(a);
        return h;
    }

    public static Hashtable PlayerPropsForClient(Actor a)
    {
        var h = new Hashtable();
        foreach (DictionaryEntry e in a.Props) h[e.Key] = e.Value;
        h[PlayerProp.UserId] = a.UserId;
        h[PlayerProp.IsInactive] = a.Inactive;
        if (!h.ContainsKey(PlayerProp.NickName)) h[PlayerProp.NickName] = "";
        return h;
    }

    public int[] ActorList() => Actors.Values.Where(a => !a.Inactive).Select(a => a.Nr).ToArray();

    /// <summary>Copy props from a client hashtable into a store, honouring the "null removes" rule; returns the changed subset.</summary>
    public static Hashtable Merge(Hashtable target, Hashtable incoming)
    {
        var changed = new Hashtable();
        foreach (DictionaryEntry e in incoming)
        {
            if (e.Value == null) { if (target.ContainsKey(e.Key)) { target.Remove(e.Key); changed[e.Key] = null; } }
            else { target[e.Key] = e.Value; changed[e.Key] = e.Value; }
        }
        return changed;
    }

    /// <summary>Check-and-set: every expected value must match the current one (both absent counts as match).</summary>
    public static bool CasMatches(Hashtable target, Hashtable? expected)
    {
        if (expected == null) return true;
        foreach (DictionaryEntry e in expected)
        {
            var cur = target[e.Key];
            if (!ValuesEqual(cur, e.Value)) return false;
        }
        return true;
    }

    public static bool ValuesEqual(object? a, object? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is byte[] ab && b is byte[] bb) return ab.AsSpan().SequenceEqual(bb);
        if (a is Array aa && b is Array ba)
        {
            if (aa.Length != ba.Length) return false;
            for (int i = 0; i < aa.Length; i++) if (!ValuesEqual(aa.GetValue(i), ba.GetValue(i))) return false;
            return true;
        }
        if (a is CustomValue ca && b is CustomValue cb) return ca.Code == cb.Code && ca.Data.AsSpan().SequenceEqual(cb.Data);
        try { return Convert.ToDouble(a) == Convert.ToDouble(b); } catch { }
        return a.Equals(b);
    }
}

/// <summary>Every room this process hosts (master and game share it - one process).</summary>
public static class Rooms
{
    private static readonly Dictionary<string, Room> _rooms = new();
    public static readonly object Sync = new();

    public static Room? Get(string name) { lock (Sync) return _rooms.TryGetValue(name, out var r) ? r : null; }
    public static Room GetOrCreate(string name, out bool created)
    {
        lock (Sync)
        {
            if (_rooms.TryGetValue(name, out var r)) { created = false; return r; }
            r = new Room(name); _rooms[name] = r; created = true; return r;
        }
    }
    public static void Remove(string name) { lock (Sync) _rooms.Remove(name); }
    public static List<Room> All() { lock (Sync) return _rooms.Values.ToList(); }
    public static int Count { get { lock (Sync) return _rooms.Count; } }
}
