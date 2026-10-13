using System.Collections.Concurrent;
using Newtonsoft.Json;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The NORMAL PLAYER whitelist: every Steam id that has been through the Campus launcher.
    ///
    /// The launcher proves a player's Steam account with a ticket from Steam itself
    /// (LauncherController /api/launcher/v1/steam, SteamTicket checks Steam's signature), and that
    /// Steam id is added here automatically. With Required on, a PC (Steam) game login is let in
    /// only when its - Steam-verified - id is on this list or on the hand-made allowlist
    /// (SteamAllowlist, Data/steam_allowlist.txt: staff / special entries). So playing on PC means
    /// running Campus through our launcher at least once. Quest/Meta is never affected.
    ///
    /// Stored in Data/steam_launcher_players.json; managed from the admin panel (Settings).
    /// </summary>
    public static class LauncherPlayers
    {
        public class Entry
        {
            public string SteamId { get; set; } = "";
            public ulong AccountId { get; set; }
            public string Username { get; set; } = "";
            public DateTime AddedAt { get; set; }
            public DateTime LastSeen { get; set; }
        }

        private class FileData
        {
            public bool Required { get; set; } = true;
            public List<Entry> Players { get; set; } = new();
        }

        private static string Path_ => Path.Combine(Environment.CurrentDirectory, "Data", "steam_launcher_players.json");
        private static readonly object _lock = new();
        private static FileData? _data;
        private static ConcurrentDictionary<ulong, Entry> _byId = new();

        private static FileData Data()
        {
            if (_data != null) return _data;
            lock (_lock)
            {
                if (_data != null) return _data;
                FileData d;
                try { d = File.Exists(Path_) ? JsonConvert.DeserializeObject<FileData>(File.ReadAllText(Path_)) ?? new FileData() : new FileData(); }
                catch (Exception ex)
                {
                    // Unreadable: keep the gate on (Required defaults true) but start empty - the
                    // players re-add themselves on their next launcher start. Not saved over the
                    // broken file, so it can still be recovered by hand.
                    Console.WriteLine($"[launcher-players] could not read {Path_}: {ex.Message}");
                    d = new FileData();
                }
                var map = new ConcurrentDictionary<ulong, Entry>();
                foreach (var e in d.Players)
                    if (ulong.TryParse(e.SteamId, out ulong id) && id != 0) map[id] = e;
                _byId = map;
                _data = d;
                Console.WriteLine($"[launcher-players] {map.Count} Steam id(s) whitelisted through the launcher; " +
                                  $"launcher required for Steam logins: {(d.Required ? "YES" : "no")}");
                return d;
            }
        }

        private static void Save()
        {
            try
            {
                var d = Data();
                d.Players = _byId.Values.OrderBy(e => e.AddedAt).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
                string tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(d, Formatting.Indented));
                File.Move(tmp, Path_, true);
            }
            catch (Exception ex) { Console.WriteLine($"[launcher-players] could not write {Path_}: {ex.Message}"); }
        }

        /// <summary>Must a Steam login come through the launcher (this list or the hand-made allowlist)?</summary>
        public static bool Required => !ConfigCampus.LocalDev && Data().Required;

        public static void SetRequired(bool on)
        {
            lock (_lock) { Data().Required = on; Save(); }
            Console.WriteLine($"[launcher-players] launcher required for Steam logins: {(on ? "YES" : "no")}");
        }

        public static bool Contains(ulong steamId) { Data(); return steamId != 0 && _byId.ContainsKey(steamId); }

        /// <summary>Add (or refresh) a Steam id the launcher just proved with a Steam ticket.</summary>
        public static bool Add(ulong steamId, ulong accountId, string username)
        {
            if (steamId == 0) return false;
            Data();
            lock (_lock)
            {
                bool added = !_byId.TryGetValue(steamId, out var e);
                e ??= new Entry { SteamId = steamId.ToString(), AddedAt = DateTime.UtcNow };
                e.AccountId = accountId; e.Username = username ?? ""; e.LastSeen = DateTime.UtcNow;
                _byId[steamId] = e;
                Save();
                if (added) Console.WriteLine($"[launcher-players] whitelisted Steam {steamId} (@{username}, {accountId}) - {_byId.Count} total");
                return added;
            }
        }

        public static bool Remove(ulong steamId)
        {
            Data();
            lock (_lock)
            {
                if (!_byId.TryRemove(steamId, out _)) return false;
                Save();
            }
            Console.WriteLine($"[launcher-players] removed Steam {steamId} - {_byId.Count} left");
            return true;
        }

        public static List<Entry> All() { Data(); return _byId.Values.OrderByDescending(e => e.AddedAt).ToList(); }

        /// <summary>The whitelisted Steam ids an account added through the launcher (the launcher's /me).</summary>
        public static List<string> ForAccount(ulong accountId) { Data(); return _byId.Values.Where(e => e.AccountId == accountId).Select(e => e.SteamId).ToList(); }
    }

    /// <summary>
    /// The one question the login asks about a Steam id: may it play? With the launcher required,
    /// only ids on the normal-player whitelist (LauncherPlayers) or the hand-made allowlist
    /// (SteamAllowlist) - an empty hand-made list does NOT open the gate then. Without it, the old
    /// allowlist rule (empty = everyone).
    /// </summary>
    public static class SteamGate
    {
        public static bool Active => LauncherPlayers.Required || SteamAllowlist.IsActive;

        public static bool Allowed(ulong steamId) =>
            LauncherPlayers.Required
                ? steamId != 0 && (SteamAllowlist.All().Contains(steamId) || LauncherPlayers.Contains(steamId))
                : SteamAllowlist.IsAllowed(steamId);
    }
}
