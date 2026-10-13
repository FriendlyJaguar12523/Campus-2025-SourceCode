using System.Collections;
using CampusNet.Net;
using CampusNet.Protocol;

namespace CampusNet.Services;

/// <summary>The game-server side: rooms, actors, events (with PUN's event cache), properties, groups.</summary>
public static class GameService
{
    public static void Handle(Connection c, GpBinary16.OperationRequest op, bool reliable)
    {
        int payload = op.Code == Op.RaiseEvent ? op.PayloadBytes : 0;
        if (payload > Guard.MaxEventBytes)
        {
            Log.Warn($"{c.Tag}: event of {payload} bytes refused (cap {Guard.MaxEventBytes})");
            c.SendError(op.Code, Rc.InvalidOperation, "Event too large");
            return;
        }
        if (!c.Charge(payload, $"op {op.Code}")) return;
        switch (op.Code)
        {
            case Op.CreateGame:
            case Op.JoinGame: Join(c, op); return;
            case Op.Leave: Leave(c, op); return;
            case Op.RaiseEvent: RaiseEvent(c, op, reliable); return;
            case Op.SetProperties: SetProperties(c, op); return;
            case Op.GetProperties: GetProperties(c, op); return;
            case Op.ChangeGroups: ChangeGroups(c, op); return;
            case Op.ServerSettings: c.SendResponse(op.Code, new()); return;
            default:
                Log.Warn($"{c.Tag}: game op {op.Code} not implemented {LoadBalancingApp.DescribeParams(op.Params)}");
                c.SendError(op.Code, Rc.InvalidOperation, "Operation not supported");
                return;
        }
    }

    // ---------------------------------------------------------------- join / create

    private static void Join(Connection c, GpBinary16.OperationRequest op)
    {
        if (c.Room != null) { c.SendError(op.Code, Rc.OperationNotAllowedInCurrentState, "Already in a game"); return; }
        string name = op.Params.GetValueOrDefault(P.RoomName) as string ?? "";
        if (name.Length == 0) { c.SendError(op.Code, Rc.InvalidOperation, "Missing game id"); return; }
        // Kicked from this instance (Campus InstanceBlocks): the client reconnects to its old room
        // on its own, past matchmaking - refuse it here as well as on the master (JoinBlocks).
        if (JoinBlocks.IsBlocked(c.UserId, name))
        {
            Log.Info($"{c.Tag}: refused join to '{name}' - kicked from that instance");
            c.SendError(op.Code, Rc.GameClosed, "Game closed"); return;
        }
        int joinMode = LoadBalancingApp.ToInt(op.Params.GetValueOrDefault(P.JoinMode));   // 0 default, 1 CreateIfNotExists, 2 JoinOrRejoin, 3 RejoinOnly

        Room room;
        bool created;
        if (op.Code == Op.CreateGame) room = Rooms.GetOrCreate(name, out created);
        else
        {
            var existing = Rooms.Get(name);
            if (existing == null && joinMode != 1) { c.SendError(op.Code, Rc.GameDoesNotExist, "Game does not exist"); return; }
            room = existing ?? Rooms.GetOrCreate(name, out _);
            created = existing == null;
        }

        Actor actor;
        var joinerProps = op.Params.GetValueOrDefault(P.PlayerProperties) as Hashtable ?? new Hashtable();
        var cachedEvents = new List<CachedEvent>();
        var others = new List<Connection>();
        lock (room.Sync)
        {
            // A NEW session (any join but RejoinOnly) from a user who still has an actor here: the
            // old game never disconnected (a hard quit or crash - staff connections are kept through
            // 10 min of silence by AfkExempt, so they outlive a game restart). That ghost is gone,
            // the way Photon would have timed it out: taking over its actor handed the new game the
            // ghost's own cached spawns, so it built a second Maker Pen with the same view id
            // ("NetworkView ID duplicate found: 100029"), whose teardown then threw on every leave
            // - the client never finished leaving and every room load after the dorm hung at
            // "PREPARING 0%" (u6, 2026-09-27 14:43-14:59). An explicit rejoin (JoinMode 3) is the
            // one case that legitimately resumes its actor.
            var ghost = joinMode == 3 ? null : room.FindByUserId(c.UserId!);
            if (ghost != null)
            {
                if (ghost.Conn != null && ghost.Conn != c)
                {
                    var old = ghost.Conn; old.Room = null; old.Actor = null;
                    try { old.Disconnect("replaced by a new session"); } catch { }
                }
                ghost.Conn = null;
                room.Actors.Remove(ghost.Nr);
                int dropped = room.Cache.RemoveAll(e => !e.Global && e.ActorNr == ghost.Nr);
                if (room.Actors.Count == 0)
                {
                    // It was alone: Photon would have closed the empty room - start it fresh.
                    room.Cache.Clear(); room.Props.Clear(); room.NextActorNr = 1; room.MasterOverride = 0;
                    room.PlayerTtl = room.EmptyRoomTtl = 0; room.CleanupCacheOnLeave = false;
                    Log.Warn($"{c.Tag}: user already in '{name}' as actor {ghost.Nr} from an old session - removed it and reset the room (it was alone)");
                }
                else
                {
                    // Everyone else sees the ghost leave (before the new actor's join event below).
                    var leaveEv = new Dictionary<byte, object?> { [P.ActorNr] = ghost.Nr, [P.IsInactive] = false, [P.MasterClientId] = room.MasterClientId };
                    foreach (var a in room.Actors.Values)
                        if (a.Conn != null && !a.Inactive) a.Conn.SendEvent(Ev.Leave, leaveEv);
                    Log.Warn($"{c.Tag}: user already in '{name}' as actor {ghost.Nr} from an old session - removed it ({dropped} of its cached events) and joining as a new actor");
                }
            }

            if (created || room.Actors.Count == 0 && room.Props.Count == 0)
            {
                if (op.Params.GetValueOrDefault(P.GameProperties) is Hashtable gp) Room.Merge(room.Props, gp);
                room.PlayerTtl = LoadBalancingApp.ToInt(op.Params.GetValueOrDefault(P.PlayerTtl));
                room.EmptyRoomTtl = LoadBalancingApp.ToInt(op.Params.GetValueOrDefault(P.EmptyRoomTtl));
                if (room.Props[GameProp.PlayerTtl] is int pt) room.PlayerTtl = pt;
                if (room.Props[GameProp.EmptyRoomTtl] is int et) room.EmptyRoomTtl = et;
                if (op.Params.GetValueOrDefault(P.CleanupCacheOnLeave) is bool cl) room.CleanupCacheOnLeave = cl;
                if (room.Props[GameProp.CleanupCacheOnLeave] is bool cl2) room.CleanupCacheOnLeave = cl2;
                Log.Info($"{c.Tag}: created room '{name}' props={GpBinary16.Describe(room.Props)}");
            }

            var rejoin = room.FindByUserId(c.UserId!);
            if (rejoin != null)
            {
                if (!rejoin.Inactive && rejoin.Conn != null && rejoin.Conn != c)
                {
                    // Same user connected twice: the newer connection wins (the old one is stale).
                    Log.Warn($"{c.Tag}: user already in '{name}' as actor {rejoin.Nr} - replacing the old connection");
                    var old = rejoin.Conn; old.Room = null; old.Actor = null;
                    try { old.Disconnect("replaced by a new connection"); } catch { }
                }
                actor = rejoin;
                actor.Inactive = false; actor.Conn = c;
                actor.ActiveSince = DateTime.UtcNow; actor.LoadSignal = false; actor.StuckReported = false;
                Room.Merge(actor.Props, joinerProps);
            }
            else
            {
                if (!room.IsOpen && joinMode != 3) { c.SendError(op.Code, Rc.GameClosed, "Game closed"); return; }
                if (room.MaxPlayers > 0 && room.ActiveCount >= room.MaxPlayers) { c.SendError(op.Code, Rc.GameFull, "Game full"); return; }
                actor = new Actor { Nr = room.NextActorNr++, UserId = c.UserId!, Conn = c, CreatedRoom = room.ActiveCount == 0 };
                Room.Merge(actor.Props, joinerProps);
                if (!actor.Props.ContainsKey(PlayerProp.NickName) && c.NickName.Length > 0) actor.Props[PlayerProp.NickName] = c.NickName;
                room.Actors[actor.Nr] = actor;
            }
            c.Room = room; c.Actor = actor;

            foreach (var a in room.Actors.Values)
                if (a.Conn != null && a.Conn != c && !a.Inactive) others.Add(a.Conn);
            cachedEvents.AddRange(room.Cache);
            foreach (var a in room.Actors.Values) cachedEvents.AddRange(a.OwnCache);

            // Response: my actor number, everyone's properties, the room's, and the active actor list.
            c.SendResponse(op.Code, new Dictionary<byte, object?>
            {
                [P.ActorNr] = actor.Nr,
                [P.ActorList] = room.ActorList(),
                [P.PlayerProperties] = room.AllPlayerProps(),
                [P.GameProperties] = room.GamePropsForClient(),
                [P.RoomName] = name
            });

            // Join event to everyone including the joiner (PUN sets up its own player from it).
            var joinEv = new Dictionary<byte, object?>
            {
                [P.ActorNr] = actor.Nr,
                [P.PlayerProperties] = Room.PlayerPropsForClient(actor),
                [P.ActorList] = room.ActorList()
            };
            c.SendEvent(Ev.Join, joinEv);
            foreach (var o in others) o.SendEvent(Ev.Join, joinEv);
        }

        // Replay the cache so the late joiner sees every instantiate / cached RPC in order.
        // EVERY global event goes out as coming from the room (actor 0), the way Photon owns global
        // events. Rec Room's buffered room load (all global, cache=5) only accepts messages from the
        // current master or from actor 0 ("Received buffered message from non-master client [1]
        // Current Master is [2]. Discarding..."), so replaying under the real sender hung the
        // joiner at 25% whenever that sender was no longer master:
        //   * the sender had LEFT (2026-09-27 11:11, room-2-2-461102059, 53 events discarded);
        //   * the sender was still there but had HANDED master on (2026-09-27 10:20 EDT,
        //     room-2-2-49039038: a1 -> a2 when a player dropped, 54 events discarded).
        // Only the replay's sender changes: the cache keeps the real sender, so RemoveFromRoomCache
        // filters still match (see CachedEvent.ActorNr). Non-global events keep their sender.
        foreach (var ev in cachedEvents)
            c.SendEvent(ev.Code, EventParams(ev.Global ? 0 : ev.ActorNr, ev.Data));
        Log.Info($"{c.Tag}: joined '{name}' as actor {actor.Nr} ({room.ActiveCount} active, {cachedEvents.Count} cached events replayed)");
        JoinCheck.Observe(c.UserId, name, c.Tag);
        AfkExempt.Apply(c, name);
    }

    private static Dictionary<byte, object?> EventParams(int senderActor, object? data)
    {
        var p = new Dictionary<byte, object?> { [P.ActorNr] = senderActor };
        if (data != null) p[P.Data] = data;
        return p;
    }

    // ---------------------------------------------------------------- leave

    private static void Leave(Connection c, GpBinary16.OperationRequest op)
    {
        bool inactive = op.Params.GetValueOrDefault(P.IsInactive) is bool b && b;
        RemoveFromRoom(c, inactive, "leave");
        c.SendResponse(op.Code, new());
    }

    public static void OnConnectionLost(Connection c) => RemoveFromRoom(c, becomeInactive: true, why: "disconnect");

    private static void RemoveFromRoom(Connection c, bool becomeInactive, string why)
    {
        var room = c.Room; var actor = c.Actor;
        if (room == null || actor == null) return;
        c.Room = null; c.Actor = null;
        List<Connection> others;
        int master; bool removed;
        lock (room.Sync)
        {
            if (actor.Conn != c) return;   // already superseded
            actor.Conn = null;
            bool keep = becomeInactive && room.PlayerTtl != 0;
            if (keep) { actor.Inactive = true; actor.InactiveSince = DateTime.UtcNow; removed = false; }
            else
            {
                room.Actors.Remove(actor.Nr); removed = true;
                // Cached events by this actor go only if the room asked for CleanupCacheOnLeave
                // (Rec Room never does - see Room.CleanupCacheOnLeave) and they weren't "global".
                if (room.CleanupCacheOnLeave) room.Cache.RemoveAll(e => !e.Global && e.ActorNr == actor.Nr);
            }
            master = room.MasterClientId;
            others = room.Actors.Values.Where(a => a.Conn != null && !a.Inactive).Select(a => a.Conn!).ToList();
            if (room.ActiveCount == 0) room.EmptySince = DateTime.UtcNow;
        }
        var ev = new Dictionary<byte, object?>
        {
            [P.ActorNr] = actor.Nr,
            [P.IsInactive] = !removed,
            [P.MasterClientId] = master
        };
        foreach (var o in others) o.SendEvent(Ev.Leave, ev);
        Log.Info($"{c.Tag}: left '{room.Name}' ({why}, {(removed ? "removed" : "inactive")}), master now {master}");
        MaybeRemoveRoom(room);
    }

    private static void MaybeRemoveRoom(Room room)
    {
        lock (room.Sync)
        {
            if (room.Actors.Values.Any(a => !a.Inactive)) return;
            if (room.EmptyRoomTtl > 0 && (DateTime.UtcNow - room.EmptySince).TotalMilliseconds < room.EmptyRoomTtl) return;
            if (room.Actors.Values.Any(a => a.Inactive) && room.PlayerTtl < 0) return;   // PlayerTtl -1 = keep forever
        }
        Rooms.Remove(room.Name);
        Log.Info($"room '{room.Name}' removed (empty)");
    }

    private static DateTime _lastStats = DateTime.MinValue;

    /// <summary>Periodic: expire inactive actors past PlayerTtl and empty rooms past EmptyRoomTtl.</summary>
    public static void Sweep()
    {
        bool stats = (DateTime.UtcNow - _lastStats).TotalSeconds >= 15;
        if (stats) _lastStats = DateTime.UtcNow;
        foreach (var room in Rooms.All())
        {
            // Every 15 s, for rooms with company: who raised what, and where it went. This is the
            // trace for "the other player doesn't move" - it shows whether the mover's events
            // arrive at all, whether they are unreliable, which interest group they carry, and
            // whether anyone was subscribed to receive them.
            if (stats)
            {
                lock (room.Sync)
                {
                    if (room.ActiveCount >= 2)
                    {
                        foreach (var a in room.Actors.Values)
                        {
                            if (a.Raised == 0 && a.Conn == null) continue;
                            string codes = string.Join(" ", a.RaisedByCode.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}:{kv.Value}"));
                            string groups = a.RaisedByGroup.Count == 0 ? "-" : string.Join(" ", a.RaisedByGroup.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"g{kv.Key}:{kv.Value}"));
                            string subs = a.Groups.Count == 0 ? "none" : (a.Groups.Count > 12 ? $"{a.Groups.Count} groups" : string.Join(",", a.Groups.OrderBy(g => g)));
                            Log.Info($"stats '{room.Name}' actor {a.Nr} u{a.UserId}{(a.Inactive ? " (inactive)" : "")}: raised {a.Raised} ({a.RaisedUnreliable} unreliable), delivered {a.Delivered}, no-recipient {a.DroppedNoRecipient}, codes [{codes}], sent-in-groups [{groups}], subscribed [{subs}], recv {a.Conn?.Sent ?? 0} msgs, rtt {a.Conn?.Peer.Rtt ?? 0}ms");
                            a.Raised = a.RaisedUnreliable = a.Delivered = a.DroppedNoRecipient = 0;
                            a.RaisedByCode.Clear(); a.RaisedByGroup.Clear();
                        }
                    }
                }
            }
            // Stuck-load watchdog: a joiner that has shown no sign of finishing its load (StuckLoad).
            List<(string user, int secs)>? stuck = null;
            lock (room.Sync)
            {
                foreach (var a in room.Actors.Values)
                {
                    if (a.Inactive || a.Conn == null || a.LoadSignal || a.StuckReported || a.CreatedRoom) continue;
                    int secs = (int)(DateTime.UtcNow - a.ActiveSince).TotalSeconds;
                    if (secs < StuckLoad.AfterSeconds) continue;
                    a.StuckReported = true;
                    (stuck ??= new()).Add((a.UserId, secs));
                }
            }
            if (stuck != null) foreach (var (user, secs) in stuck) StuckLoad.Report(user, room.Name, secs);

            List<Connection> others = new(); List<int> expired = new(); int master;
            lock (room.Sync)
            {
                foreach (var a in room.Actors.Values.Where(a => a.Inactive).ToList())
                {
                    if (room.PlayerTtl > 0 && (DateTime.UtcNow - a.InactiveSince).TotalMilliseconds > room.PlayerTtl)
                    {
                        room.Actors.Remove(a.Nr); if (room.CleanupCacheOnLeave) room.Cache.RemoveAll(e => !e.Global && e.ActorNr == a.Nr); expired.Add(a.Nr);
                    }
                }
                master = room.MasterClientId;
                if (expired.Count > 0) others = room.Actors.Values.Where(a => a.Conn != null && !a.Inactive).Select(a => a.Conn!).ToList();
            }
            foreach (var nr in expired)
                foreach (var o in others)
                    o.SendEvent(Ev.Leave, new Dictionary<byte, object?> { [P.ActorNr] = nr, [P.IsInactive] = false, [P.MasterClientId] = master });
            MaybeRemoveRoom(room);
        }
    }

    // ---------------------------------------------------------------- events

    private static readonly string[] TraceRooms = (Environment.GetEnvironmentVariable("CAMPUSNET_TRACE_ROOM") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static readonly HashSet<string> TraceUsers = new((Environment.GetEnvironmentVariable("CAMPUSNET_TRACE_USER") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private static readonly string TraceRoom = (TraceRooms.Length > 0 || TraceUsers.Count > 0) ? "on" : "";
    private static bool Traced(Room room) => TraceRooms.Any(t => room.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
    // A traced user's rooms are traced too (private instances carry no room id in their name).
    private static bool Traced(Room room, Actor me) => Traced(room) || TraceUsers.Contains(me.UserId ?? "") || room.Actors.Values.Any(a => TraceUsers.Contains(a.UserId ?? ""));

    /// <summary>The actor numbers in a RaiseEvent's target list, whatever container they arrived in.</summary>
    /// <summary>Whether an event payload carries readable UTF-16 text (typed chat) - checked on RPCs
    /// (code 36) only, so any room's chat can be traced without logging every event.</summary>
    private static bool HasUtf16Text(object? o, int depth = 0)
    {
        if (o == null || depth > 4) return false;
        if (o is string str) return str.Length >= 3 && str.Any(char.IsLetter) && str.All(ch => ch >= 32);
        if (o is byte[] b)
        {
            int run = 0;
            for (int i = 0; i + 1 < b.Length; i += 2)
            {
                if (b[i + 1] == 0 && b[i] >= 32 && b[i] < 127) { if (++run >= 4) return true; }
                else run = 0;
            }
            return false;
        }
        if (o is Hashtable ht) { foreach (DictionaryEntry e in ht) if (HasUtf16Text(e.Value, depth + 1)) return true; return false; }
        if (o is System.Collections.IEnumerable en) { int n = 0; foreach (var x in en) { if (n++ > 64) break; if (HasUtf16Text(x, depth + 1)) return true; } }
        return false;
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private static int[]? ActorTargets(object? v)
    {
        switch (v)
        {
            case null: return null;
            case int[] n: return n;
            case byte[] b: return b.Select(x => (int)x).ToArray();
            case short[] sh: return sh.Select(x => (int)x).ToArray();
            case TypedArray ta: return ta.Items.Select(LoadBalancingApp.ToInt).ToArray();
            case object?[] oa: return oa.Select(LoadBalancingApp.ToInt).ToArray();
            default:
                Log.Warn($"RaiseEvent: unrecognised target-actor list type {v.GetType().Name} - treated as none");
                return null;
        }
    }

    private static void RaiseEvent(Connection c, GpBinary16.OperationRequest op, bool reliable)
    {
        var room = c.Room; var me = c.Actor;
        if (room == null || me == null) { c.SendError(op.Code, Rc.OperationNotAllowedInCurrentState, "Not in a game"); return; }
        byte code = (byte)LoadBalancingApp.ToInt(op.Params.GetValueOrDefault(P.Code));
        var data = op.Params.GetValueOrDefault(P.Data);
        byte cache = (byte)LoadBalancingApp.ToInt(op.Params.GetValueOrDefault(P.Cache));
        byte receivers = (byte)LoadBalancingApp.ToInt(op.Params.GetValueOrDefault(P.ReceiverGroup));
        byte group = (byte)LoadBalancingApp.ToInt(op.Params.GetValueOrDefault(P.Group));
        // Target actors (252). PUN spells this int[] ('n'), which is all that was matched before;
        // a typed array of ints / an object[] / a byte[] decodes to something else and used to be
        // silently treated as "no targets" - the event then went by receiver group instead.
        var targets = ActorTargets(op.Params.GetValueOrDefault(P.ActorList));

        // A gift box spawned into the room: let every other client show its message and box type
        // (they only do when the package says IsSignatureValid - see GiftBoxRelay).
        if (code == 29 && StuckLoad.ContainsString(data, "(GiftBox)"))
        {
            GiftBoxRelay.Capture(data, room.Name, c.UserId);
            int flipped = GiftBoxRelay.MarkValid(data);
            if (flipped > 0 && !room.LoggedGiftBoxValid) { room.LoggedGiftBoxValid = true; Log.Info($"{c.Tag}: gift box spawn in '{room.Name}' marked valid for the other players"); }
        }

        // A loaded client streams transforms (28); a joiner's first act once the room state is in
        // is a request to the master client (receiver group 2). Either one means it is not stuck.
        if (code == 28 || receivers == 2) me.LoadSignal = true;

        var recipients = new List<Connection>();
        lock (room.Sync)
        {
            bool isMaster = me.Nr == room.MasterClientId;
            switch (cache)
            {
                case Cache.AddToRoomCache:
                case Cache.AddToRoomCacheGlobal:
                    // A gift box someone opens is spawned into the room (ev 29 "(GiftBox)") and kept in
                    // the cache, so every later joiner replays it while loading - and on their client
                    // another player's box fails its package signature ("Bad signature for remote gift
                    // package"). Joins stuck at 25% started right after boxes were cached (Rec Center,
                    // 2026-09-26). Boxes are short-lived: deliver them live, never keep them.
                    if (code == 29 && StuckLoad.ContainsString(data, "(GiftBox)"))
                    {
                        if (!room.LoggedGiftBoxSkip) { room.LoggedGiftBoxSkip = true; Log.Info($"{c.Tag}: gift box spawns in '{room.Name}' are delivered live but not kept in the room cache"); }
                        break;
                    }
                    if (room.Cache.Count >= Guard.MaxRoomCache)
                    {
                        if (room.Cache.Count == Guard.MaxRoomCache) Log.Warn($"{c.Tag}: room '{room.Name}' cache full ({Guard.MaxRoomCache}) - new cached events are delivered but not kept");
                        break;
                    }
                    room.Cache.Add(new CachedEvent { ActorNr = me.Nr, Global = cache == Cache.AddToRoomCacheGlobal, Code = code, Data = data, Group = group }); break;
                case Cache.RemoveFromRoomCache:
                {
                    // Photon semantics: ANY actor removes the cached events matching the filter (code,
                    // data keys, and the target list read as SENDERS). This used to let only the
                    // master remove other actors' events - and Rec Room's buffered-event system clears
                    // a game-state slice from whichever client ends it, so those clears were silently
                    // dropped and a late joiner got the old round / team / spawn state replayed (the
                    // Dodgeball "join mid-game and spawn on top of everyone" bug).
                    // Event code 0 on a removal means "any code" (PUN's remove-all-of-a-player).
                    byte? codeFilter = op.Params.ContainsKey(P.Code) && code != 0 ? code : null;
                    int foreign = isMaster ? 0 : room.Cache.Count(e => e.ActorNr != me.Nr && CacheFilterMatch(e, targets, codeFilter, data as Hashtable));
                    int removed = room.Cache.RemoveAll(e => CacheFilterMatch(e, targets, codeFilter, data as Hashtable));
                    if (foreign > 0 && !room.LoggedForeignRemove)
                    {
                        room.LoggedForeignRemove = true;
                        Log.Info($"{c.Tag}: non-master a{me.Nr} removed {foreign} cached event(s) raised by others in '{room.Name}' (ev {code}; {removed} removed in all) - was refused before");
                    }
                    break;
                }
                case Cache.RemoveFromRoomCacheForActorsLeft:
                {
                    // NOT global events. In Photon a global event belongs to the room (it is cached
                    // as actor 0, which never "leaves"), so this cleanup only drops the per-actor
                    // AddToRoomCache events of players who are gone. Global events keep their real
                    // sender here only so replays and RemoveFromRoomCache filters match them.
                    // This used to delete them too: when a room's creator left, the next master's
                    // cleanup wiped the BufferedEventHelper "RoomLoad" slices (all global, cache=5)
                    // and every later joiner got ~3 cached events instead of ~50 and hung on the
                    // loading screen at 25% (2026-09-26 22:51, room-2-2-741109222: 51 -> 3).
                    int before = room.Cache.Count;
                    int globalsKept = room.Cache.Count(e => e.Global && !room.Actors.ContainsKey(e.ActorNr));
                    int dropped = room.Cache.RemoveAll(e => !e.Global && !room.Actors.ContainsKey(e.ActorNr));
                    if (dropped > 0 || globalsKept > 0)
                        Log.Info($"{c.Tag}: '{room.Name}' cleanup for departed players: removed {dropped} of {before} cached event(s), kept {globalsKept} global");
                    break;
                }
                case Cache.MergeCache:
                {
                    var existing = me.OwnCache.FirstOrDefault(e => e.Code == code);
                    if (existing != null && existing.Data is Hashtable eh && data is Hashtable dh) Room.Merge(eh, dh);
                    else if (existing != null) existing.Data = data;
                    else if (me.OwnCache.Count < Guard.MaxActorCache) me.OwnCache.Add(new CachedEvent { ActorNr = me.Nr, Code = code, Data = data, Group = group });
                    break;
                }
                case Cache.ReplaceCache:
                    me.OwnCache.RemoveAll(e => e.Code == code);
                    if (data != null && me.OwnCache.Count < Guard.MaxActorCache) me.OwnCache.Add(new CachedEvent { ActorNr = me.Nr, Code = code, Data = data, Group = group });
                    break;
                case Cache.RemoveCache:
                    me.OwnCache.RemoveAll(e => e.Code == code); break;
            }

            if (targets != null)
            {
                foreach (var nr in targets)
                    if (room.Actors.TryGetValue(nr, out var a) && a.Conn != null && !a.Inactive) recipients.Add(a.Conn);
            }
            else if (receivers == Receivers.MasterClient)
            {
                if (room.Actors.TryGetValue(room.MasterClientId, out var m) && m.Conn != null) recipients.Add(m.Conn);
            }
            else
            {
                foreach (var a in room.Actors.Values)
                {
                    if (a.Conn == null || a.Inactive) continue;
                    if (a == me && receivers != Receivers.All) continue;
                    if (group != 0 && !a.Groups.Contains(group)) continue;
                    recipients.Add(a.Conn);
                }
            }
        }

        // Cache removals are NEVER delivered, data or not: on a RemoveFromRoomCache the data is the
        // FILTER, not a payload (Photon only edits the cache). This used to deliver any removal that
        // carried a filter, so every time Rec Room cleared a cached RPC (ev 36) or state event (ev
        // 29) everyone in the room received it as a brand-new one and re-ran it - thousands a day.
        bool deliver = op.Params.ContainsKey(P.Code) && !(cache >= Cache.RemoveFromRoomCache && cache <= Cache.RemoveFromRoomCacheForActorsLeft);
        // Room trace (CAMPUSNET_TRACE_ROOM=<substring of the room name>): every non-transform event
        // with its size, routing and - for RPC-shaped payloads - the readable strings inside (RR RPC
        // method names are plain text on the wire). Used to see what a "Rooms 2.0" authority
        // handshake actually sends, without the firehose of CAMPUSNET_DEBUG.
        if (code != 28 && ((TraceRoom.Length > 0 && Traced(room, me)) || (code == 36 && HasUtf16Text(data))))
        {
            string strs = "";
            try
            {
                var found = new List<string>();
                void Scan(object? o, int depth)
                {
                    if (o == null || depth > 4 || found.Count >= 6) return;
                    if (o is CampusNet.Protocol.CustomValue cvx) o = cvx.Data;   // custom types carry their payload as raw bytes (gift packages ride in one)
                    if (o is string str) { if (str.Length >= 3 && str.Length <= 80) found.Add(str); return; }
                    if (o is byte[] bytes)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var bt in bytes)
                        {
                            if (bt >= 32 && bt < 127) sb.Append((char)bt);
                            else { if (sb.Length >= 5 && found.Count < 6) found.Add(sb.ToString()); sb.Clear(); }
                        }
                        if (sb.Length >= 5 && found.Count < 6) found.Add(sb.ToString());
                        // UTF-16LE text (a C# string written raw: every other byte 0) reads as
                        // single characters above and is dropped, which hid typed chat entirely.
                        var u16 = new System.Text.StringBuilder();
                        for (int i = 0; i + 1 < bytes.Length; i++)
                        {
                            if (bytes[i] >= 32 && bytes[i] < 127 && bytes[i + 1] == 0) { u16.Append((char)bytes[i]); i++; continue; }
                            if (u16.Length >= 3 && found.Count < 6) found.Add("u16:" + u16);
                            u16.Clear();
                        }
                        if (u16.Length >= 3 && found.Count < 6) found.Add("u16:" + u16);
                        return;
                    }
                    if (o is Hashtable ht) { foreach (DictionaryEntry e in ht) { Scan(e.Key, depth + 1); Scan(e.Value, depth + 1); } return; }
                    if (o is System.Collections.IEnumerable en) { int n = 0; foreach (var x in en) { if (n++ > 64) break; Scan(x, depth + 1); } }
                }
                Scan(data, 0);
                if (found.Count > 0) strs = " strs=[" + string.Join(" | ", found) + "]";
            }
            catch { }
            // Every op parameter with its decoded CLR type, so a receiver-group / target-list field
            // that arrives in an unexpected encoding is visible instead of silently ignored.
            string keys = "[" + string.Join(",", op.Params.Select(kv => $"{kv.Key}:{(kv.Value == null ? "null" : kv.Value is TypedArray ta ? $"TypedArray<{(char)ta.ElemType}>" : kv.Value.GetType().Name)}")) + "]";
            Log.Info($"trace '{room.Name}' a{me.Nr} u{me.UserId}: ev {code} {op.PayloadBytes}B cache={cache} recv={receivers} group={group} targets=[{(targets == null ? "-" : string.Join(",", targets))}] -> {recipients.Count} recipient(s){(deliver ? "" : " (cache-only)")} keys={keys}{strs}");
        }
        // Room chat (RPC 36 carrying "<noparse>text</noparse>") is held until the Campus server
        // confirms the text went through its chat filter (ChatGuard) - text that skipped it is
        // dropped and the sender disconnected. Everything else is delivered as before.
        string? chatText = deliver && code == 36 && ChatGuard.Enabled ? ChatGuard.ChatText(data) : null;
        if (chatText != null)
        {
            var msg = GpBinary16.EventMessage(code, EventParams(me.Nr, data));
            var to = recipients.ToList();
            string user = me.UserId, roomName = room.Name;
            _ = Task.Run(async () =>
            {
                if (await ChatGuard.IsBypass(user, roomName, chatText))
                {
                    Log.Warn($"[chat-guard] u{user} in '{roomName}': unfiltered chat dropped and sender disconnected");
                    c.Disconnect("chat filter bypass");
                    return;
                }
                foreach (var r in to) r.Send(msg, reliable);
            });
        }
        else if (deliver)
        {
            var msg = GpBinary16.EventMessage(code, EventParams(me.Nr, data));
            foreach (var r in recipients) r.Send(msg, reliable);
        }
        lock (room.Sync)
        {
            me.Raised++; if (!reliable) me.RaisedUnreliable++;
            me.RaisedByCode[code] = me.RaisedByCode.GetValueOrDefault(code) + 1;
            if (group != 0) me.RaisedByGroup[group] = me.RaisedByGroup.GetValueOrDefault(group) + 1;
            if (deliver) { me.Delivered += recipients.Count; if (recipients.Count == 0) me.DroppedNoRecipient++; }
        }
        if (reliable) c.SendResponse(op.Code, new());   // PUN expects the ack'd response for reliable raises
    }

    private static bool CacheFilterMatch(CachedEvent e, int[]? actors, byte? code, Hashtable? dataFilter)
    {
        if (actors != null && actors.Length > 0 && !actors.Contains(e.ActorNr)) return false;
        if (code != null && e.Code != code) return false;
        if (dataFilter != null)
        {
            if (e.Data is not Hashtable h) return false;
            foreach (DictionaryEntry f in dataFilter)
                if (!h.ContainsKey(f.Key) || !Room.ValuesEqual(h[f.Key], f.Value)) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- properties

    private static void SetProperties(Connection c, GpBinary16.OperationRequest op)
    {
        var room = c.Room; var me = c.Actor;
        if (room == null || me == null) { c.SendError(op.Code, Rc.OperationNotAllowedInCurrentState, "Not in a game"); return; }
        var props = op.Params.GetValueOrDefault(P.Properties) as Hashtable ?? new Hashtable();
        var expected = op.Params.GetValueOrDefault(P.ExpectedValues) as Hashtable;
        bool broadcast = op.Params.GetValueOrDefault(P.Broadcast) is bool b && b;
        int target = LoadBalancingApp.ToInt(op.Params.GetValueOrDefault(P.ActorNr));   // 0 = room properties

        Hashtable changed; List<Connection> recipients = new();
        lock (room.Sync)
        {
            Hashtable store;
            if (target == 0) store = room.Props;
            else
            {
                if (!room.Actors.TryGetValue(target, out var a)) { c.SendError(op.Code, Rc.InvalidOperation, "No such actor"); return; }
                // A client writes its own player properties; only the master client may write
                // another actor's (PUN never does this from a plain client, a modified one would -
                // it is how a "bot" renames or re-flags other players).
                if (target != me.Nr && me.Nr != room.MasterClientId)
                {
                    Log.Warn($"{c.Tag}: tried to set actor {target}'s properties - refused");
                    c.SendError(op.Code, Rc.InvalidOperation, "Not your actor");
                    return;
                }
                store = a.Props;
            }

            // Master client hand-off (PUN SetMasterClient): room prop 248 = new master, with the
            // CURRENT master as the expected value. 248 is never stored - MasterClientId is live -
            // so the plain CAS below always read "stored null" and refused every hand-off, leaving
            // game authority (rounds, teams) with whoever had the lowest actor number.
            if (target == 0 && props.ContainsKey(GameProp.MasterClientId))
            {
                int wanted = LoadBalancingApp.ToInt(props[GameProp.MasterClientId]);
                int current = room.MasterClientId;
                bool casOk = expected == null || !expected.ContainsKey(GameProp.MasterClientId)
                             || LoadBalancingApp.ToInt(expected[GameProp.MasterClientId]) == current;
                bool valid = room.Actors.TryGetValue(wanted, out var nm) && !nm.Inactive;
                if (!casOk || !valid)
                {
                    Log.Warn($"{c.Tag}: master hand-off to a{wanted} refused in '{room.Name}' ({(!valid ? "no such active actor" : $"expected master {GpBinary16.Describe(expected![GameProp.MasterClientId])}, is {current}")})");
                    c.SendError(op.Code, Rc.InvalidOperation, "CAS failed");
                    return;
                }
                props = (Hashtable)props.Clone(); props.Remove(GameProp.MasterClientId);
                if (expected != null) { expected = (Hashtable)expected.Clone(); expected.Remove(GameProp.MasterClientId); }
                room.MasterOverride = wanted;
                Log.Info($"{c.Tag}: master client a{current} -> a{wanted} in '{room.Name}'");
                // Everyone (the sender included) hears it, as Photon does: PUN switches master off
                // the 248 PropertiesChanged event.
                var mev = new Dictionary<byte, object?> { [P.TargetActorNr] = 0, [P.Properties] = new Hashtable { [GameProp.MasterClientId] = wanted }, [P.ActorNr] = me.Nr };
                var mmsg = GpBinary16.EventMessage(Ev.PropertiesChanged, mev);
                foreach (var a in room.Actors.Values) if (a.Conn != null && !a.Inactive) a.Conn.Send(mmsg);
                if (props.Count == 0) { c.SendResponse(op.Code, new()); return; }
            }

            if (!Room.CasMatches(store, expected))
            {
                // Always logged: a refused compare-and-set silently drops that client's state change
                // for everyone (it is how per-player objects - chat, typing, held items - sync).
                var why = string.Join(", ", expected!.Keys.Cast<object>().Select(k => $"{k}: expected {GpBinary16.Describe(expected[k])} stored {GpBinary16.Describe(store[k])}"));
                Log.Warn($"{c.Tag}: CAS refused on {(target == 0 ? "room" : "actor " + target)} in '{room.Name}' - {Trunc(why, 400)} | wanted [{Trunc(string.Join(", ", props.Keys.Cast<object>()), 200)}]");
                c.SendError(op.Code, Rc.InvalidOperation, "CAS failed");   // PUN treats this rc as the CAS miss
                return;
            }
            changed = Room.Merge(store, props);
            if (target == 0 && changed.ContainsKey(GameProp.PlayerTtl) && changed[GameProp.PlayerTtl] is int pt) room.PlayerTtl = pt;
            if (target == 0 && changed.ContainsKey(GameProp.EmptyRoomTtl) && changed[GameProp.EmptyRoomTtl] is int et) room.EmptyRoomTtl = et;
            foreach (var a in room.Actors.Values)
                if (a.Conn != null && !a.Inactive && (broadcast || a.Conn != c)) recipients.Add(a.Conn);
        }
        c.SendResponse(op.Code, new());
        // Player-property writes are rare (join, avatar/state changes) - log the keys and sizes so a
        // "others don't see my new outfit" report can be matched against what actually crossed the wire.
        bool chatKeys = changed.Keys.Cast<object>().Any(k => k is string ks && (ks.Contains("TYPING") || ks.Contains("CHAT", StringComparison.OrdinalIgnoreCase)));
        if (target != 0 || chatKeys || (TraceRoom.Length > 0 && Traced(room, me)))
        {
            var desc = string.Join(", ", changed.Keys.Cast<object>().Select(k => $"{k}={(changed[k] is byte[] ba ? ba.Length + "B" : changed[k] is string str ? str.Length + "ch" : changed[k] is Hashtable ? "table" : changed[k] is object?[] arr ? Trunc(GpBinary16.Describe(arr), 240) : changed[k]?.ToString() ?? "null")}"));
            Log.Info($"{c.Tag}: {(target == 0 ? "room" : "player")} props on {(target == 0 ? "room" : "actor " + target)} [{desc}] cas={(expected != null)} -> {recipients.Count} recipient(s){(broadcast ? " (broadcast)" : "")}");
        }
        if (broadcast || recipients.Count > 0)
        {
            var ev = new Dictionary<byte, object?> { [P.TargetActorNr] = target, [P.Properties] = changed, [P.ActorNr] = me.Nr };
            var msg = GpBinary16.EventMessage(Ev.PropertiesChanged, ev);
            foreach (var r in recipients) if (broadcast || r != c) r.Send(msg);
        }
    }

    private static void GetProperties(Connection c, GpBinary16.OperationRequest op)
    {
        var room = c.Room;
        if (room == null) { c.SendError(op.Code, Rc.OperationNotAllowedInCurrentState, "Not in a game"); return; }
        var resp = new Dictionary<byte, object?>();
        lock (room.Sync)
        {
            resp[P.GameProperties] = room.GamePropsForClient();
            resp[P.PlayerProperties] = room.AllPlayerProps();
        }
        c.SendResponse(op.Code, resp);
    }

    private static void ChangeGroups(Connection c, GpBinary16.OperationRequest op)
    {
        var me = c.Actor; var room = c.Room;
        if (room == null || me == null) { c.SendError(op.Code, Rc.OperationNotAllowedInCurrentState, "Not in a game"); return; }
        lock (room.Sync)
        {
            // null = all groups; empty array = none
            if (op.Params.TryGetValue(P.Remove, out var rem))
            {
                if (rem == null) me.Groups.Clear();
                else foreach (var g in AsBytes(rem)) me.Groups.Remove(g);
            }
            if (op.Params.TryGetValue(P.Add, out var add))
            {
                if (add == null) for (int g = 1; g < 256; g++) me.Groups.Add((byte)g);
                else foreach (var g in AsBytes(add)) me.Groups.Add(g);
            }
            Log.Info($"{c.Tag}: groups now [{(me.Groups.Count > 12 ? me.Groups.Count + " groups" : string.Join(",", me.Groups.OrderBy(g => g)))}] (remove={(op.Params.TryGetValue(P.Remove, out var rr) ? (rr == null ? "all" : GpBinary16.Describe(rr)) : "-")} add={(op.Params.TryGetValue(P.Add, out var aa) ? (aa == null ? "all" : GpBinary16.Describe(aa)) : "-")})");
        }
        c.SendResponse(op.Code, new());
    }

    private static IEnumerable<byte> AsBytes(object v) => v switch
    {
        byte[] b => b,
        TypedArray ta => ta.Items.Select(x => (byte)LoadBalancingApp.ToInt(x)),
        int[] n => n.Select(x => (byte)x),
        _ => Array.Empty<byte>()
    };
}
