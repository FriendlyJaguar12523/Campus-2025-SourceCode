using LiteDB;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// Device and IP bans.
    ///
    /// An account ban (player_extra.ModerationBlockDetails) only stops that one account -
    /// the player makes a new one and is straight back in. A device or IP ban is checked
    /// at LOGIN, before a token is issued, so every account from that machine or address
    /// is refused.
    ///
    /// Lives in the existing Players.db so it is covered by the WAL checkpointing in
    /// DbMaintenance rather than adding another file that never checkpoints.
    /// </summary>
    public static class BanDB
    {
        public const string Device = "device";
        public const string Ip = "ip";

        public class BanEntry
        {
            [BsonId]
            public string Key { get; set; } = "";   // "device:<id>" or "ip:<encrypted>"

            public string Type { get; set; } = "";  // Device | Ip
            public string Value { get; set; } = ""; // device id, or AES-encrypted IP

            public ulong PlayerId { get; set; }     // account the ban originated from
            public ulong BannedBy { get; set; }
            public string? Reason { get; set; }

            public long CreatedAt { get; set; }
            /// <summary>Unix seconds when this lapses. 0 = permanent.</summary>
            public long ExpiresAt { get; set; }
        }

        private static ILiteCollection<BanEntry> Col
        {
            get
            {
                var col = PlayerDB.PlayerDBFile.GetCollection<BanEntry>("bans");
                col.EnsureIndex(x => x.Type);
                col.EnsureIndex(x => x.PlayerId);
                return col;
            }
        }

        private static string KeyFor(string type, string value) => $"{type}:{value}";

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// IPs are stored AES-encrypted, matching player_extra.IpAddresses. Utils.EncryptAes
        /// uses a fixed IV, so the same address always produces the same ciphertext - which
        /// is what makes a stored IP comparable at all.
        /// </summary>
        private static string NormalizeIp(string ip) => Utils.EncryptAes((ip ?? "").Trim());

        // ---- writing -----------------------------------------------------------

        /// <summary>Bans a device id. durationSeconds &lt;= 0 (or int.MaxValue) = permanent.</summary>
        public static bool BanDevice(string deviceId, ulong playerId, ulong bannedBy, string? reason, int durationSeconds)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            return Upsert(Device, deviceId.Trim(), playerId, bannedBy, reason, durationSeconds);
        }

        /// <summary>Bans an IP address (plaintext in; stored encrypted).</summary>
        public static bool BanIp(string ip, ulong playerId, ulong bannedBy, string? reason, int durationSeconds)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            return Upsert(Ip, NormalizeIp(ip), playerId, bannedBy, reason, durationSeconds);
        }

        private static bool Upsert(string type, string value, ulong playerId, ulong bannedBy, string? reason, int durationSeconds)
        {
            long expires = (durationSeconds <= 0 || durationSeconds == int.MaxValue)
                ? 0
                : Now + durationSeconds;

            // Drop any cached "not banned" decision so the 502 applies immediately.
            if (type == Ip) BannedIpMiddleware.Forget(value == null ? null : Utils.DecryptAes(value));

            Col.Upsert(new BanEntry
            {
                Key = KeyFor(type, value),
                Type = type,
                Value = value,
                PlayerId = playerId,
                BannedBy = bannedBy,
                Reason = reason,
                CreatedAt = Now,
                ExpiresAt = expires
            });
            return true;
        }

        // ---- checking ----------------------------------------------------------

        public static BanEntry? GetDeviceBan(string? deviceId)
            => string.IsNullOrWhiteSpace(deviceId) ? null : GetActive(Device, deviceId.Trim());

        public static BanEntry? GetIpBan(string? ip)
            => string.IsNullOrWhiteSpace(ip) ? null : GetActive(Ip, NormalizeIp(ip));

        public static bool IsDeviceBanned(string? deviceId) => GetDeviceBan(deviceId) != null;
        public static bool IsIpBanned(string? ip) => GetIpBan(ip) != null;

        /// <summary>Returns the entry only if it is still in force; expired entries are removed.</summary>
        private static BanEntry? GetActive(string type, string value)
        {
            var entry = Col.FindById(KeyFor(type, value));
            if (entry == null) return null;

            if (entry.ExpiresAt > 0 && entry.ExpiresAt <= Now)
            {
                Col.Delete(entry.Key);   // self-cleaning: a lapsed ban stops applying
                return null;
            }

            return entry;
        }

        // ---- lifting -----------------------------------------------------------

        public static bool UnbanDevice(string? deviceId)
            => !string.IsNullOrWhiteSpace(deviceId) && Col.Delete(KeyFor(Device, deviceId!.Trim()));

        public static bool UnbanIp(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            BannedIpMiddleware.Forget(ip);          // release immediately, don't wait for the cache
            return Col.Delete(KeyFor(Ip, NormalizeIp(ip!)));
        }

        /// <summary>
        /// Lifts every device/IP ban that originated from this account. Used by the admin
        /// panel's Unban so lifting a ban actually lets the player back in, instead of
        /// clearing the account block while their device stays blacklisted.
        /// </summary>
        public static int UnbanAllForPlayer(ulong playerId)
        {
            var entries = Col.Find(x => x.PlayerId == playerId).ToList();
            int n = 0;
            foreach (var e in entries)
                if (Col.Delete(e.Key)) n++;

            // An unban must let them straight back in, not after the cache expires.
            if (n > 0) BannedIpMiddleware.ForgetAll();
            return n;
        }

        // ---- listing -----------------------------------------------------------

        public static List<BanEntry> ForPlayer(ulong playerId)
            => Col.Find(x => x.PlayerId == playerId).ToList();

        public static List<BanEntry> All()
            => Col.FindAll().OrderByDescending(x => x.CreatedAt).ToList();
    }
}
