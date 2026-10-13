using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The "launched by the current launcher" gate. The PATCH proves it is the current DLL
    /// (PatchProofs); THIS proves the current Campus launcher was the one that started the
    /// game. The launcher mints an HMAC-signed token at Play time ("launch|accountId|steamId|ts"
    /// signed with the per-build LauncherKey secret) and passes it to the game via the
    /// CAMPUS_LAUNCH_TOKEN environment variable. The patch reads that env var, forwards it to
    /// the server with /api/patch/proof. A good token is remembered here for
    /// ConfigCampus.PatchProofMinutes by the caller's IP and Steam id - the same window as
    /// patch proofs. The login gate then refuses PC logins without a fresh LAUNCHER-launch
    /// proof, and when a legit patch proof is seen WITHOUT a launcher-launch proof (classic
    /// "someone cloned the DLL into their own tool") it yanks the Steam id from the normal-
    /// player whitelist so they have to come back through the launcher.
    /// </summary>
    public static class LauncherLaunchProofs
    {
        private static readonly ConcurrentDictionary<string, DateTime> _byIp = new();
        private static readonly ConcurrentDictionary<ulong, DateTime> _bySteam = new();

        public readonly record struct Check(bool Ok, string Why, ulong AccountId, ulong SteamId);

        // Per-build secret. The launcher's release_app.py mints one into Deploy\launcher-app
        // as <sha>.launchkey (next to the exe), uploaded with the exe. Fallback: a long-lived
        // one in ConfigCampus.LauncherLaunchKey - fine for dev, used in prod only if an upload
        // ever ships without the .launchkey file next to it.
        private static string _keySha = "", _key = "";
        private static readonly object _keyLock = new();

        private static string KeyFor(string sha)
        {
            lock (_keyLock)
            {
                if (sha == _keySha) return _key;
                string k = "";
                try
                {
                    string f = Path.Combine(Environment.CurrentDirectory, "Data", "Launcher", "app", sha + ".launchkey");
                    if (sha.Length == 64 && File.Exists(f)) k = File.ReadAllText(f).Trim();
                }
                catch { }
                if (k.Length == 0) k = ConfigCampus.LauncherLaunchKey ?? "";
                _keySha = sha; _key = k;
                return k;
            }
        }

        private static string CurrentLauncherSha()
        {
            try
            {
                string f = Path.Combine(Environment.CurrentDirectory, "Data", "Launcher", "app", "latest.json");
                if (!File.Exists(f)) return "";
                var j = JObject.Parse(File.ReadAllText(f));
                return (j.Value<string>("sha256") ?? "").ToLowerInvariant();
            }
            catch { return ""; }
        }

        /// <summary>
        /// Verify a launch token "accountId|steamId|ts|hmac". Returns Ok=true + the token's
        /// accountId / steamId on success so the caller can cross-check (the login endpoint
        /// confirms it matches the actual Steam ticket id and the account that's signing in).
        /// </summary>
        public static Check Verify(string token)
        {
            if (string.IsNullOrEmpty(token)) return new(false, "no token", 0, 0);
            var parts = token.Split('|');
            if (parts.Length != 4) return new(false, "malformed token", 0, 0);
            if (!ulong.TryParse(parts[0], out ulong acc)) return new(false, "bad accountId", 0, 0);
            if (!ulong.TryParse(parts[1], out ulong steam)) return new(false, "bad steamId", 0, 0);
            if (!long.TryParse(parts[2], out long ts)) return new(false, "bad ts", 0, 0);
            string mac = (parts[3] ?? "").ToLowerInvariant();

            string sha = CurrentLauncherSha();
            if (sha.Length == 0) return new(false, "no launcher staged on the server", acc, steam);
            string key = KeyFor(sha);
            if (key.Length == 0) return new(false, "no launcher-launch key configured", acc, steam);
            long skew = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
            if (Math.Abs(skew) > ConfigCampus.ProofMaxSkewSeconds)
                return new(false, $"clock off by {Math.Abs(skew) / 3600.0:0.#} hours - set the PC clock to automatic time", acc, steam);

            string msg = $"launch|{acc}|{steam}|{ts}";
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            string want = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(msg))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(mac)))
                return new(false, "bad launch signature", acc, steam);
            return new(true, "", acc, steam);
        }

        public static void Record(string ip, ulong steamId)
        {
            var now = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(ip)) _byIp[ip] = now;
            if (steamId != 0) _bySteam[steamId] = now;
            if (_byIp.Count > 20000) Prune();
        }

        public static bool HasFresh(string? ip, ulong verifiedSteamId)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, ConfigCampus.PatchProofMinutes));
            if (!string.IsNullOrEmpty(ip) && _byIp.TryGetValue(ip, out var a) && a >= cutoff) return true;
            if (verifiedSteamId != 0 && _bySteam.TryGetValue(verifiedSteamId, out var b) && b >= cutoff) return true;
            return false;
        }

        private static void Prune()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, ConfigCampus.PatchProofMinutes));
            foreach (var kv in _byIp) if (kv.Value < cutoff) _byIp.TryRemove(kv.Key, out _);
            foreach (var kv in _bySteam) if (kv.Value < cutoff) _bySteam.TryRemove(kv.Key, out _);
        }
    }
}
