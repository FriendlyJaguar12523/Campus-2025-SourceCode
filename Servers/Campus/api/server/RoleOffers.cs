using System.Collections.Concurrent;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Who may hand a player a room role (co-owner, contributor, moderator, host).
    ///
    /// Until 2026-10-07 anyone who owned a room could offer any account a role. The invite then reached the
    /// invitee as a notification, and in practice the invitee's client answered it within ~2 seconds
    /// (APILog: 393 invite->answer pairs, median 2.7 s, 11 invitees with six or more all clustered at 2.0-2.5 s)
    /// - so strangers were making players co-owners of rooms they had never seen. Now an offer to a player
    /// who is not already on the room needs the two to be FRIENDS (or the offerer to be staff), and a
    /// block either way refuses it. Taking a role away is never refused.
    /// </summary>
    public static class RoleOffers
    {
        /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are friends (both rows say so).</summary>
        public static bool AreFriends(ulong a, ulong b)
        {
            if (a == 0 || b == 0 || a == b) return false;
            var col = FriendsDB.PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");
            var mine = col.FindOne(x => x.PlayerID == a && x.Relationship.OtherPlayerID == b);
            return mine?.Relationship != null && mine.Relationship.RelationshipType == RelationshipType.Friend;
        }

        /// <summary>May <paramref name="from"/> give <paramref name="to"/> a role on this room? <paramref name="why"/> is the reason when not.</summary>
        public static bool MayOffer(ulong roomId, ulong from, ulong to, out string why)
        {
            why = "";
            if (!ConfigCampus.RoomRoleOffersFriendsOnly) return true;
            if (PlayerDB.CheckDevFlag(from)) return true;                                   // staff set people up on purpose
            if (FriendsDB.IsBlockedEitherWay(from, to)) { why = "You can't offer that player a role."; return false; }
            var room = RoomDB.GetRoom(roomId);
            var held = room?.Roles?.FirstOrDefault(r => (ulong)r.AccountId == to);
            if (held != null && held.Role != RoomDBClasses.Role_data.None && held.Role != RoomDBClasses.Role_data.Banned) return true;   // already on the room: promote / change
            if (AreFriends(to, from)) return true;
            why = "You can only offer a room role to a friend. Add them as a friend first.";
            return false;
        }

        // When each role invite was pushed, so the answer's delay can be logged (is a person pressing Accept, or the client?).
        private static readonly ConcurrentDictionary<string, DateTime> _pushedAt = new();
        private static string Key(ulong roomId, ulong player) => roomId + ":" + player;
        public static void Pushed(ulong roomId, ulong player) { _pushedAt[Key(roomId, player)] = DateTime.UtcNow; if (_pushedAt.Count > 5000) _pushedAt.Clear(); }
        /// <summary>Seconds since the invite went out, or -1 when not known (before a restart / never pushed).</summary>
        public static double SecondsSincePushed(ulong roomId, ulong player) =>
            _pushedAt.TryGetValue(Key(roomId, player), out var t) ? (DateTime.UtcNow - t).TotalSeconds : -1;
    }
}
