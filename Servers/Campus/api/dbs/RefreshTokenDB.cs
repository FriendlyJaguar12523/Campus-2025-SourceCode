using System.Security.Cryptography;
using System.Text;
using LiteDB;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// Game refresh tokens (grant_type=refresh_token on /Auth/connect/token), ported from the
    /// reference's apps/auth/src/refresh-db.ts. Every login hands out one; the client redeems it to
    /// renew its access token IN PLACE - no logout, no reload. That is what a RefreshLogin push
    /// asks for, and how a mic ban (the token's rn.privilege claim) lands instantly.
    ///
    /// Only a SHA-256 hash of each token is stored, with its account and an absolute expiry.
    /// Tokens are single-use: redeeming one deletes it, and the grant issues a fresh one
    /// (rotation), so a replayed token stops working.
    ///
    /// Before this the grant answered an empty body: the client logged "RecNet login returned a
    /// blank response" on every refresh and fell back to a full logout + relogin.
    /// </summary>
    public static class RefreshTokenDB
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

        private static readonly Lazy<LiteDatabase> _db = new(() => new LiteDatabase(Path.Combine("Data", "DBs", "RefreshTokens.db")));
        private static readonly object Sync = new();

        public class Entry
        {
            [BsonId] public string Hash { get; set; } = "";
            public ulong AccountId { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime ExpiresAt { get; set; }
        }

        private static ILiteCollection<Entry> Col()
        {
            var c = _db.Value.GetCollection<Entry>("tokens");
            c.EnsureIndex(x => x.AccountId);
            c.EnsureIndex(x => x.ExpiresAt);
            return c;
        }

        public static void Checkpoint() => _db.Value.Checkpoint();

        private static string Hash(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        /// <summary>Mint and store a new refresh token for the account; returns the raw token (the only time it exists).</summary>
        public static string Issue(ulong accountId)
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var now = DateTime.UtcNow;
            lock (Sync)
            {
                var col = Col();
                col.Insert(new Entry { Hash = Hash(token), AccountId = accountId, CreatedAt = now, ExpiresAt = now + Lifetime });
                // Housekeeping: expired tokens go as new ones come in.
                col.DeleteMany(x => x.ExpiresAt < now);
            }
            return token;
        }

        /// <summary>Redeem a refresh token: its account when valid (the token is used up), else null.</summary>
        public static ulong? Consume(string? token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
            string h = Hash(token.Trim());
            lock (Sync)
            {
                var col = Col();
                var e = col.FindById(h);
                if (e == null) return null;
                col.Delete(h);
                return e.ExpiresAt < DateTime.UtcNow ? null : e.AccountId;
            }
        }

        /// <summary>Revoke every refresh token of an account (bans / password changes).</summary>
        public static int RevokeAll(ulong accountId)
        {
            lock (Sync) return Col().DeleteMany(x => x.AccountId == accountId);
        }
    }
}
