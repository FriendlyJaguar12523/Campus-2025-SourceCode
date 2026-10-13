using System.Security.Cryptography;
using System.Text;
using LiteDB;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// The short-lived ticket issued between "password was correct" and "2FA code was correct" on EITHER login surface
    /// (AdminController's admin login, SiteController's website login). It proves nothing but the account id, that it was
    /// minted here seconds ago, and WHICH surface it's for - the `surface` string is baked into the signed payload, so a
    /// ticket from the website login can never be replayed against the admin 2FA endpoint (which would hand out an admin
    /// session) or vice versa. The browser carries it; the server never stores it. Same HMAC-over-a-payload shape as
    /// AdminGate (Middleware/PatchGateMiddleware.cs).
    /// </summary>
    public static class PendingTwoFactorLogin
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

        private static string Sign(string payload)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(ConfigCampus.AdminWebToken ?? ""));
            return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes("2fa-pending:" + payload)));
        }

        public static string Issue(ulong accountId, string surface)
        {
            string payload = accountId + "." + DateTimeOffset.UtcNow.Add(Ttl).ToUnixTimeSeconds() + "." + surface;
            return payload + "." + Sign(payload);
        }

        /// <summary>The account id the ticket names, or 0 if it's missing, expired, for the wrong surface, or doesn't check out.</summary>
        public static ulong Validate(string? ticket, string expectedSurface)
        {
            if (string.IsNullOrWhiteSpace(ticket)) return 0;
            var parts = ticket.Split('.');
            if (parts.Length != 4) return 0;
            string payload = parts[0] + "." + parts[1] + "." + parts[2];
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(payload)), Encoding.UTF8.GetBytes(parts[3]))) return 0;
            if (!ulong.TryParse(parts[0], out var accId) || !long.TryParse(parts[1], out var exp)) return 0;
            if (parts[2] != expectedSurface) return 0;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return 0;
            return accId;
        }
    }

    /// <summary>
    /// TOTP 2FA (see api/server/Totp.cs for the algorithm), one row per ACCOUNT - shared by the admin panel login
    /// (AdminController.cs) and the website login (SiteController.cs), since Campus has one account/player id space.
    /// The secret is AES-encrypted at rest (Utils.EncryptAes, same helper BanDB uses for IPs) - LiteDB is a plain file,
    /// and this secret is "the thing you have" half of the whole feature. Recovery-code hashes use ClientSecurity's own
    /// PBKDF2 helper (the one account passwords use) - no new hashing dependency either.
    /// </summary>
    public class TotpRow
    {
        [BsonId] public ulong AccountId { get; set; }
        public string EncryptedSecret { get; set; } = "";
        public bool Enabled { get; set; }
        public List<string> RecoveryCodeHashes { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime? EnabledAt { get; set; }
    }

    public static class TwoFactorDB
    {
        private static readonly LiteDatabase Db = new(Path.Combine(Environment.CurrentDirectory, "Data", "DBs", "AdminTwoFactor.db"));
        private static ILiteCollection<TotpRow> Col => Db.GetCollection<TotpRow>("totp");

        public static bool IsEnabled(ulong accountId) => Col.FindById(accountId)?.Enabled ?? false;

        /// <summary>Starts (or restarts) enrollment: a fresh secret, stored but NOT enabled until ConfirmEnroll proves the
        /// account actually has it in an authenticator app. Returns the secret to show (base32, for manual entry or a QR code).</summary>
        public static string BeginEnroll(ulong accountId)
        {
            string secret = Totp.GenerateSecret();
            var row = Col.FindById(accountId) ?? new TotpRow { AccountId = accountId, CreatedAt = DateTime.UtcNow };
            row.EncryptedSecret = Utils.EncryptAes(secret);
            row.Enabled = false;
            row.RecoveryCodeHashes = new();
            Col.Upsert(row);
            return secret;
        }

        /// <summary>Proves the pending secret with a real code from the app, turns 2FA on, and mints recovery codes
        /// (shown to the account exactly once - only their hash is kept).</summary>
        public static List<string>? ConfirmEnroll(ulong accountId, string code)
        {
            var row = Col.FindById(accountId);
            if (row == null || row.Enabled) return null;
            string secret = Utils.DecryptAes(row.EncryptedSecret);
            if (!Totp.Verify(secret, code)) return null;

            var plain = new List<string>();
            var hashes = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                string c = RecoveryCode();
                plain.Add(c);
                hashes.Add(ClientSecurity.HashPassword(c));
            }
            row.Enabled = true; row.EnabledAt = DateTime.UtcNow; row.RecoveryCodeHashes = hashes;
            Col.Update(row);
            return plain;
        }

        /// <summary>Turns 2FA off. Returns false if it wasn't on (nothing to do, not an error).</summary>
        public static bool Disable(ulong accountId) => Col.Delete(accountId);

        /// <summary>Checks a login-time code: a live TOTP code, OR (consuming it) one of the one-time recovery codes.</summary>
        public static bool VerifyLogin(ulong accountId, string? code)
        {
            var row = Col.FindById(accountId);
            if (row == null || !row.Enabled || string.IsNullOrWhiteSpace(code)) return false;
            code = code.Trim();

            string secret = Utils.DecryptAes(row.EncryptedSecret);
            if (Totp.Verify(secret, code)) return true;

            for (int i = 0; i < row.RecoveryCodeHashes.Count; i++)
            {
                if (ClientSecurity.VerifyPassword(code, row.RecoveryCodeHashes[i]))
                {
                    row.RecoveryCodeHashes.RemoveAt(i);
                    Col.Update(row);
                    Console.WriteLine($"[2fa] account {accountId} signed in with a recovery code - {row.RecoveryCodeHashes.Count} left");
                    return true;
                }
            }
            return false;
        }

        private static readonly char[] RecoveryChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();   // no 0/O/1/I - written down, not typed by a scanner
        private static string RecoveryCode()
        {
            var bytes = RandomNumberGenerator.GetBytes(10);
            var sb = new StringBuilder(10);
            foreach (var b in bytes) sb.Append(RecoveryChars[b % RecoveryChars.Length]);
            return sb.ToString().Insert(5, "-");
        }
    }
}
