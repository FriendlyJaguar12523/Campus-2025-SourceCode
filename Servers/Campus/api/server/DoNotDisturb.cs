using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Do Not Disturb, set on the website for a chosen length of time. While it is on:
    ///  - direct game invites from anyone the player has NOT favourited are refused (the sender is told the player isn't taking
    ///    invites right now), so favourites can always still reach them;
    ///  - the player isn't sent "friend X is online" notices.
    /// It expires by itself (the time is stored, nothing has to switch it off) and is capped at <see cref="MaxMinutes"/>.
    /// Party moves and room follows are deliberately NOT covered: those are a party leader taking their party along, not an invite.
    /// </summary>
    public static class DoNotDisturb
    {
        public const int MaxMinutes = 24 * 60;

        public static DateTime? Until(ulong accountId)
        {
            long unix = SitePrefsDB.Get(accountId).DndUntilUnix;
            if (unix <= 0) return null;
            var until = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            return until > DateTime.UtcNow ? until : null;
        }

        public static bool Active(ulong accountId) => accountId != 0 && Until(accountId) != null;

        /// <summary>Start (minutes &gt; 0, capped) or stop (0) it. Returns when it ends, or null when it is off.</summary>
        public static DateTime? Set(ulong accountId, int minutes)
        {
            if (minutes <= 0) { SitePrefsDB.SetDnd(accountId, null); return null; }
            var until = DateTime.UtcNow.AddMinutes(Math.Min(minutes, MaxMinutes));
            SitePrefsDB.SetDnd(accountId, until);
            return until;
        }

        /// <summary>May <paramref name="sender"/> invite <paramref name="target"/> right now? True unless the target has it on and
        /// has not favourited the sender.</summary>
        public static bool InviteAllowed(ulong target, ulong sender)
        {
            if (!Active(target)) return true;
            try
            {
                return FriendsDB.GetRelationships(target).Any(r =>
                    r.PlayerID == sender && (r.Favorited == ReciprocalStatus.Local || r.Favorited == ReciprocalStatus.Mutual));
            }
            catch { return true; }   // never lock a player out because a lookup failed
        }
    }
}
