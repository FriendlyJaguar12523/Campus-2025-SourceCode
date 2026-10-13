using System.Collections.Concurrent;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// In-memory game invites: "player A invited player B into their room". Backs POST
    /// /Matchmaking/invite (create + a MessageReceived frame) and the two redeem paths the 2025
    /// client uses - /matchmake/invite/{inviteId} (by row id) and /matchmake/v2/player/{fromId}
    /// (by sender, when the frame's InviteId isn't used). Mirrors RecFlare's room_invite table but
    /// kept in memory: invites are ephemeral, single-use, and short-lived, so a restart dropping
    /// them is fine (the sender just re-invites). Redeeming follows the inviter's LIVE presence, so
    /// only the association (who invited whom) needs storing, not the room.
    /// </summary>
    public static class RoomInvites
    {
        private const int TtlSeconds = 900; // 15 min

        public sealed class Invite
        {
            public long RoomInviteId { get; init; }
            public ulong FromPlayerId { get; init; }
            public ulong ToPlayerId { get; init; }
            public long Expires { get; init; }
        }

        private static long _nextId = 1;
        private static readonly ConcurrentDictionary<long, Invite> _byId = new();

        public static Invite Create(ulong fromPlayerId, ulong toPlayerId)
        {
            Sweep();
            long id = Interlocked.Increment(ref _nextId);
            var inv = new Invite
            {
                RoomInviteId = id,
                FromPlayerId = fromPlayerId,
                ToPlayerId = toPlayerId,
                Expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlSeconds
            };
            _byId[id] = inv;
            return inv;
        }

        public static Invite? Get(long inviteId)
        {
            if (!_byId.TryGetValue(inviteId, out var inv)) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > inv.Expires) { _byId.TryRemove(inviteId, out _); return null; }
            return inv;
        }

        /// <summary>True if a live invite exists from <paramref name="fromPlayerId"/> to
        /// <paramref name="toPlayerId"/> (the by-sender redeem path).</summary>
        public static bool HasInviteFromTo(ulong fromPlayerId, ulong toPlayerId)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var kv in _byId)
            {
                var i = kv.Value;
                if (i.Expires < now) { _byId.TryRemove(kv.Key, out _); continue; }
                if (i.FromPlayerId == fromPlayerId && i.ToPlayerId == toPlayerId) return true;
            }
            return false;
        }

        public static void Consume(long inviteId) => _byId.TryRemove(inviteId, out _);

        /// <summary>Consume every live invite from a sender to a recipient (used when the join is
        /// redeemed by sender rather than by a specific row id).</summary>
        public static void ConsumeFromTo(ulong fromPlayerId, ulong toPlayerId)
        {
            foreach (var kv in _byId)
                if (kv.Value.FromPlayerId == fromPlayerId && kv.Value.ToPlayerId == toPlayerId)
                    _byId.TryRemove(kv.Key, out _);
        }

        private static long _lastSweep;
        private static void Sweep()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - Interlocked.Read(ref _lastSweep) < 120) return;
            Interlocked.Exchange(ref _lastSweep, now);
            foreach (var kv in _byId)
                if (now > kv.Value.Expires) _byId.TryRemove(kv.Key, out _);
        }
    }
}
