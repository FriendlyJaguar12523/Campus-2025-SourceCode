using LiteDB;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// Website-only per-account preferences: the nickname a player gives a friend and which
    /// friends are pinned to their home page. Neither exists in the game, so they live here
    /// (Data/DBs/SitePrefs.db) instead of on the relationship row, keyed by the OWNER's account.
    /// </summary>
    public static class SitePrefsDB
    {
        private static readonly Lazy<LiteDatabase> _db = new(() =>
            new LiteDatabase(Path.Combine(Environment.CurrentDirectory, "Data", "DBs", "SitePrefs.db")));

        public class Prefs
        {
            [BsonId] public ulong AccountId { get; set; }
            public Dictionary<string, string> Nicknames { get; set; } = new();   // other account id -> nickname
            public List<ulong> Pinned { get; set; } = new();
            public Dictionary<string, List<string>> QuickChat { get; set; } = new();   // set key (CustomA...) -> the player's own phrases
            // Do Not Disturb runs until this moment, as Unix seconds (0 = off). Not a DateTime: LiteDB hands DateTimes back as
            // local time, which would shift the end by the server's UTC offset.
            public long DndUntilUnix { get; set; }
        }

        private static ILiteCollection<Prefs> Col() => _db.Value.GetCollection<Prefs>("prefs");

        public static Prefs Get(ulong accountId) => Col().FindById(accountId) ?? new Prefs { AccountId = accountId };

        public static string? NicknameFor(ulong owner, ulong other) =>
            Get(owner).Nicknames.TryGetValue(other.ToString(), out var n) && !string.IsNullOrWhiteSpace(n) ? n : null;

        /// <summary>Store one personal quick chat set (empty phrases = stock slot). An all-empty set removes it.</summary>
        public static void SetQuickChat(ulong owner, string setKey, List<string> phrases)
        {
            var p = Get(owner);
            p.QuickChat ??= new();
            if (phrases == null || phrases.All(string.IsNullOrWhiteSpace)) p.QuickChat.Remove(setKey);
            else p.QuickChat[setKey] = phrases;
            Col().Upsert(p);
        }

        /// <summary>Do Not Disturb until the given UTC time; null clears it.</summary>
        public static void SetDnd(ulong owner, DateTime? until)
        {
            var p = Get(owner);
            p.DndUntilUnix = until == null ? 0 : new DateTimeOffset(DateTime.SpecifyKind(until.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
            Col().Upsert(p);
        }

        public static bool IsPinned(ulong owner, ulong other) => Get(owner).Pinned.Contains(other);

        public static string? SetNickname(ulong owner, ulong other, string? nickname)
        {
            var p = Get(owner);
            nickname = (nickname ?? "").Trim();
            if (nickname.Length > 24) nickname = nickname[..24];
            if (nickname.Length == 0) p.Nicknames.Remove(other.ToString()); else p.Nicknames[other.ToString()] = nickname;
            Col().Upsert(p);
            return nickname.Length == 0 ? null : nickname;
        }

        public static bool SetPinned(ulong owner, ulong other, bool on)
        {
            var p = Get(owner);
            p.Pinned.Remove(other);
            if (on) { p.Pinned.Insert(0, other); if (p.Pinned.Count > 12) p.Pinned = p.Pinned.Take(12).ToList(); }
            Col().Upsert(p);
            return on;
        }

        /// <summary>Drop a friend's nickname and pin when the friendship ends.</summary>
        public static void Forget(ulong owner, ulong other)
        {
            var p = Col().FindById(owner);
            if (p == null) return;
            bool changed = p.Nicknames.Remove(other.ToString()) | p.Pinned.Remove(other);
            if (changed) Col().Update(p);
        }
    }
}
