using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The login patch gate (see ConfigCampus.PatchProofKey). The PC patch proves it is the CURRENT
    /// build by POSTing /api/patch/proof - the SHA-256 of its own DLL file, its Steam id and a time,
    /// HMAC-signed with the shared key - at startup and right before every game login. A good proof
    /// is remembered here for ConfigCampus.PatchProofMinutes by the caller's IP and by that Steam id;
    /// /Auth/connect/token then lets a PC login through only with a fresh one. In memory only: the
    /// patch re-proves before each login, so a restart costs nothing.
    /// </summary>
    public static class PatchProofs
    {
        private static readonly ConcurrentDictionary<string, DateTime> _byIp = new();
        private static readonly ConcurrentDictionary<ulong, DateTime> _bySteam = new();

        public readonly record struct Check(bool Ok, string Why, string Channel = "");

        // Every patch release has its OWN key (ReleaseGame.bat makes a new one): it is uploaded next to
        // the DLL as Data/Launcher/patch/<sha256>.key. Without one, ConfigCampus.PatchProofKey.
        private static string _keySha = "", _key = "", _keyCh = "";
        private static string KeyFor(string sha, string channel = "")
        {
            lock (_keyLock)
            {
                if (sha == _keySha && channel == _keyCh) return _key;
                string k = "";
                try
                {
                    string f = Path.Combine(Campus_live_rewrite.Controllers.LauncherController.PatchDirFor(channel), sha + ".key");
                    if (sha.Length == 64 && File.Exists(f)) k = File.ReadAllText(f).Trim();
                }
                catch { }
                if (k.Length == 0) k = ConfigCampus.PatchProofKey ?? "";
                _keySha = sha; _key = k; _keyCh = channel; _keyCh = channel;
                return k;
            }
        }
        private static readonly object _keyLock = new();

        /// <summary>Verify a proof: signature, clock, and that it is the current patch.</summary>
        public static Check Verify(long ts, string sha, ulong steamId, string mac)
        {
            sha = (sha ?? "").ToLowerInvariant();
            // A developer's own server (CAMPUS_LOCAL_DEV=1) has no uploaded patch to compare with: any patch is fine there.
            if (ConfigCampus.LocalDev) return new(true, "");
            // The patch must be the newest of ANY channel: public, or the beta / dev patch a developer was handed.
            string current = "", channel = "";
            foreach (string ch in Campus_live_rewrite.Controllers.LauncherController.Channels)
            {
                string c = Campus_live_rewrite.Controllers.LauncherController.CurrentPatchSha(ch);
                if (c.Length > 0 && c == sha) { current = c; channel = ch; break; }
            }
            if (current.Length == 0)
            {
                // not any channel's newest: an old patch (or no patch uploaded at all)
                current = Campus_live_rewrite.Controllers.LauncherController.CurrentPatchSha();
                if (current.Length == 0) return new(false, "no patch uploaded to compare with");
            }
            string key = KeyFor(current, channel);
            if (key.Length == 0) return new(false, "no proof key configured");
            long skew = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
            if (Math.Abs(skew) > ConfigCampus.ProofMaxSkewSeconds)
                return new(false, $"clock off by {Math.Abs(skew) / 3600.0:0.#} hours - set the PC clock to automatic time");
            string msg = $"{ts}|{sha}|{steamId}";
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            string want = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(msg))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes((mac ?? "").ToLowerInvariant())))
                // an older build signs with its own (older) key: tell it apart from a forgery
                return new(false, sha != current ? "old patch " + (sha.Length >= 12 ? sha.Substring(0, 12) : sha) : "bad signature");
            if (sha != current) return new(false, "old patch " + (sha.Length >= 12 ? sha.Substring(0, 12) : sha));
            return new(true, "", channel);
        }

        public static void Record(string ip, ulong steamId)
        {
            var now = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(ip)) _byIp[ip] = now;
            if (steamId != 0) _bySteam[steamId] = now;
            if (_byIp.Count > 20000) Prune();
        }

        /// <summary>Is there a fresh proof from this IP or (when verified) this Steam id?</summary>
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
