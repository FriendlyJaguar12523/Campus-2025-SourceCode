using System.Collections.Concurrent;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Short-lived codes for linking a Discord account to a Campus account.
    ///
    /// FLOW: the website Settings > Discord tab (SiteController, site-cookie authed) calls
    /// <see cref="Generate"/> for the signed-in account and shows the code; the player runs
    /// `/link &lt;code&gt;` with the Discord bot (BotCommands), which calls <see cref="TryResolve"/>
    /// then <see cref="Consume"/> to bind their Discord id.
    ///
    /// This is DELIBERATELY its own single-purpose store, isolated from the admin session gate
    /// (AdminGate) and everything else: redeeming a code ONLY records a Discord&lt;-&gt;account link
    /// (PlayerDB.SetDiscordLink) and can never mint an admin session or grant any privilege. That is
    /// the whole point of keeping it separate - linking Discord must never be a path into the admin
    /// panel.
    /// </summary>
    public static class DiscordLinkCodes
    {
        private const int TtlSeconds = 600;   // 10 minutes
        // Unambiguous alphabet (no 0/O/1/I) so a 6-char code is easy to read off a screen and type.
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private static readonly ConcurrentDictionary<string, (ulong accountId, long expires)> _codes = new();
        private static readonly Random _rng = new();

        /// <summary>Mint a fresh code for this account (replacing any it already had), valid for 10 min.</summary>
        public static (string code, int ttlSeconds) Generate(ulong accountId)
        {
            Sweep();
            // Drop any outstanding code for this account so only the newest one works.
            foreach (var kv in _codes)
                if (kv.Value.accountId == accountId) _codes.TryRemove(kv.Key, out _);

            string code;
            do
            {
                var chars = new char[6];
                lock (_rng)
                    for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[_rng.Next(Alphabet.Length)];
                code = new string(chars);
            } while (_codes.ContainsKey(code));

            long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlSeconds;
            _codes[code] = (accountId, expires);
            return (code, TtlSeconds);
        }

        /// <summary>Look up (without consuming) the account a live code belongs to. False if unknown/expired.</summary>
        public static bool TryResolve(string? code, out ulong accountId)
        {
            accountId = 0;
            if (string.IsNullOrWhiteSpace(code)) return false;
            code = code.Trim().ToUpperInvariant();
            if (!_codes.TryGetValue(code, out var entry)) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > entry.expires)
            {
                _codes.TryRemove(code, out _);
                return false;
            }
            accountId = entry.accountId;
            return true;
        }

        /// <summary>Burn a code after a successful link so it can't be reused.</summary>
        public static void Consume(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            _codes.TryRemove(code.Trim().ToUpperInvariant(), out _);
        }

        private static long _lastSweep;
        private static void Sweep()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - Interlocked.Read(ref _lastSweep) < 60) return;
            Interlocked.Exchange(ref _lastSweep, now);
            foreach (var kv in _codes)
                if (now > kv.Value.expires) _codes.TryRemove(kv.Key, out _);
        }
    }
}
