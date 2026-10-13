using System.Collections.Concurrent;
using Campus_live_rewrite.api.dbs;
using static Campus_live_rewrite.api.dbs.HeartbeatDB;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Keeps a GROUP MOVE together: when the game moves everyone in an instance on at once (a
    /// Laser Tag round ends and the whole lobby goes to a new instance), every client matchmakes
    /// on its own, a few seconds apart. Seen 2026-09-21 22:02 in ^LaserTag: 617 posted
    /// room/18/28 JoinMode=2 AdditionalPlayersAutoFollow=True AdditionalPlayerIds=[618] and got a
    /// new private instance; 618 posted the same 10 s later with AdditionalPlayerIds=[] and got a
    /// DIFFERENT new instance, so the lobby split up.
    ///
    /// Two records, each good for <see cref="Window"/>:
    ///   - a reservation per player the first mover listed in AdditionalPlayerIds;
    ///   - the instance the group came FROM + the room it asked for, for an auto-follow move,
    ///     so it works whichever client asks first and even when the list is empty.
    /// The next of them to ask for that room lands in the same instance while it has space.
    /// </summary>
    public static class GroupMoves
    {
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

        private static readonly ConcurrentDictionary<ulong, (RoomInstance inst, DateTime at)> _reserved = new();
        private static readonly ConcurrentDictionary<string, (RoomInstance inst, DateTime at)> _fromInstance = new();

        private static string Key(long fromInstanceId, ulong roomId, long subRoomId) => $"{fromInstanceId}|{roomId}|{subRoomId}";

        /// <summary>
        /// Where this player's group went, if they are part of one: the room (and subroom, when
        /// the request names one; -1 = any) must match, the instance must still have space and
        /// must not be one they were kicked from. Null = matchmake normally.
        /// </summary>
        public static RoomInstance? TakeFor(ulong player, ulong roomId, long subRoomId, long fromInstanceId, bool autoFollow)
        {
            var now = DateTime.UtcNow;
            bool Fits(RoomInstance i) =>
                i.roomId == (long)roomId
                && (subRoomId < 0 || i.subRoomId == subRoomId)
                && i.roomInstanceId != fromInstanceId
                && !InstanceIsFull(i)
                && !IsInstanceBroken(i)
                && !InstanceBlocks.IsBlocked(player, i);

            if (_reserved.TryRemove(player, out var r) && now - r.at <= Window && Fits(r.inst))
            {
                Console.WriteLine($"[group-move] {player} follows their group into instance {r.inst.roomInstanceId} of room {roomId} (listed by the first mover)");
                return r.inst;
            }
            if (autoFollow && fromInstanceId != 0)
            {
                foreach (var sub in subRoomId < 0 ? new long[] { -1 } : new[] { subRoomId, -1 })
                    if (_fromInstance.TryGetValue(Key(fromInstanceId, roomId, sub), out var g) && now - g.at <= Window && Fits(g.inst))
                    {
                        Console.WriteLine($"[group-move] {player} follows their group from instance {fromInstanceId} into instance {g.inst.roomInstanceId} of room {roomId}");
                        return g.inst;
                    }
            }
            return null;
        }

        /// <summary>Remember where a mover landed, so the rest of their group can follow.</summary>
        public static void Record(ulong mover, long fromInstanceId, RoomInstance landed, IEnumerable<long>? additional, bool autoFollow, long requestedSubRoomId)
        {
            if (landed == null || landed.roomInstanceId == 0) return;
            var now = DateTime.UtcNow;
            int listed = 0;
            foreach (var id in additional ?? Enumerable.Empty<long>())
                if (id > 0 && (ulong)id != mover) { _reserved[(ulong)id] = (landed, now); listed++; }
            if (autoFollow && fromInstanceId != 0 && fromInstanceId != landed.roomInstanceId)
                _fromInstance.TryAdd(Key(fromInstanceId, (ulong)landed.roomId, requestedSubRoomId), (landed, now));
            if (listed > 0 || autoFollow)
                Console.WriteLine($"[group-move] {mover} moved from instance {fromInstanceId} to {landed.roomInstanceId} (room {landed.roomId}); {listed} listed player(s) will follow{(autoFollow ? ", auto-follow group" : "")}");
            Sweep(now);
        }

        private static void Sweep(DateTime now)
        {
            foreach (var kv in _reserved) if (now - kv.Value.at > Window) _reserved.TryRemove(kv.Key, out _);
            foreach (var kv in _fromInstance) if (now - kv.Value.at > Window) _fromInstance.TryRemove(kv.Key, out _);
        }
    }
}
