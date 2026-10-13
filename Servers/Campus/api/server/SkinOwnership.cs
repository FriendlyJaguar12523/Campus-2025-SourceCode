using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.dbs;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Who may use an equipment skin. A skin is OWNED only when it was bought from a store (for
    /// yourself or as a gift) or given through the admin panel - PlayerDB.AddPendingGiftBoxFull
    /// with grantSkin, or PlayerDB.GrantEquipmentSkin. Dev-flagged accounts (the dev locker) have
    /// every skin. Anything Equipments.json does not list (a tool's default look) is not a skin
    /// and is always allowed.
    /// </summary>
    public static class SkinOwnership
    {
        private static readonly object _lock = new();
        private static long _ticks = -1;
        private static HashSet<string> _catalog = new(StringComparer.OrdinalIgnoreCase);

        public static string CatalogPath =>
            Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "Equipments.json");

        // Skin key -> the ModificationGuid exactly as Equipments.json spells it.
        private static Dictionary<string, string> _catalogGuid = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every skin key (PlayerDB.EquipmentSkinKey) in Equipments.json.</summary>
        public static HashSet<string> CatalogKeys()
        {
            try
            {
                long t = File.Exists(CatalogPath) ? File.GetLastWriteTimeUtc(CatalogPath).Ticks : 0;
                lock (_lock)
                {
                    if (t == _ticks) return _catalog;
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (t != 0)
                        foreach (var e in JArray.Parse(File.ReadAllText(CatalogPath)))
                        {
                            string key = PlayerDB.EquipmentSkinKey((string?)e["PrefabName"], (string?)e["ModificationGuid"]);
                            set.Add(key);
                            guids.TryAdd(key, (string?)e["ModificationGuid"] ?? "");
                        }
                    _catalog = set;
                    _catalogGuid = guids;
                    _ticks = t;
                    return set;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[skins] Equipments.json unreadable: {ex.Message}");
                lock (_lock) return _catalog;
            }
        }

        /// <summary>
        /// The skin's ModificationGuid spelled the way the locker list (Equipments.json /
        /// getUnlocked) spells it. 112 of the store's skin rows spell the same skins as 22-char
        /// short guids, and the client compares them as TEXT: a skin bought from such a row came
        /// out of its box as a second copy of the one already in the locker (2026-09-27). Anything
        /// the catalog does not know is handed back unchanged.
        /// </summary>
        public static string? CanonicalGuid(string? prefab, string? guid)
        {
            if (string.IsNullOrWhiteSpace(prefab) || string.IsNullOrWhiteSpace(guid)) return guid;
            CatalogKeys();
            Dictionary<string, string> map;
            lock (_lock) map = _catalogGuid;
            return map.TryGetValue(PlayerDB.EquipmentSkinKey(prefab, guid), out var g) && g.Length > 0 ? g : guid;
        }

        /// <summary>
        /// Equipping a skin the database says they do not own (not bought, not given by an admin):
        /// an automatic ban of ConfigCampus.UnownedSkinBanDays (30). Only a modified client can do
        /// it - the locker it is served lists owned skins only. Developers are never checked
        /// (CheckerFor), and a player already under a block is not banned again.
        /// </summary>
        public static void BanForUnownedSkin(ulong playerId, string skins)
        {
            if (!ConfigCampus.UnownedSkinBanEnabled || PlayerDB.HasDevLocker(playerId)) return;
            if (PlayerDB.HasActiveModerationBlock(playerId)) return;
            int days = Math.Max(1, ConfigCampus.UnownedSkinBanDays);
            try
            {
                PlayerDB.Set_moderation_block(playerId, Moderation_Detail.ReportCategory.Cheating, days * 86400, 0, false,
                    $"Using skins you do not own. Banned for {days} days.", null, true, 0,
                    logKind: "skin-ban", logNote: $"equipped unowned skin(s): {skins}");
                HeartbeatDB.UpdatePlayerHeartbeat(playerId, null, online: false);
                Console.WriteLine($"[skins] BANNED {playerId} for {days} days - equipped unowned skin(s): {skins}");
                Webhook.Send(playerId, "mod", "Skin ban", $"({playerId}) banned for {days} days: equipped skin(s) they do not own - {skins}", 10038562);
            }
            catch (Exception ex) { Console.WriteLine($"[skins] ban of {playerId} failed: {ex.Message}"); }
        }

        /// <summary>
        /// The check for one player: null = everything is allowed (dev locker), otherwise a
        /// predicate that is true for an owned skin or for something that is not a skin at all.
        /// </summary>
        public static Func<string?, string?, bool>? CheckerFor(ulong playerId)
        {
            if (ConfigCampus.SkinsForEveryone || PlayerDB.HasDevLocker(playerId)) return null;
            var owned = PlayerDB.OwnedEquipmentSkins(playerId);
            var catalog = CatalogKeys();
            return (prefab, guid) =>
            {
                string key = PlayerDB.EquipmentSkinKey(prefab, guid);
                return !catalog.Contains(key) || owned.Contains(key);
            };
        }
    }
}
