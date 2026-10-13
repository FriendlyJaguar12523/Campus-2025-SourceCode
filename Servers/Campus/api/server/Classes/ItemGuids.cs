using System.Text.Json;

namespace Campus_live_rewrite.api.server.Classes
{
    // Which avatar items the CLIENT BUILD can actually render.
    //
    // The client carries its own item database. Hand it an item whose id isn't in there and
    // it has no asset, so it cannot work out the item's outfit type either - which is the
    // "None does not have an associated browsable avatar item filter" warning in Player.log.
    // The item then belongs to no wardrobe category and never appears while browsing, even
    // though it is present in the /api/avatar/v4/items response and findable by search.
    //
    // No server change makes such an item render. The only thing the server can usefully do
    // is stop listing items the client cannot draw.
    //
    // Data/APIS/items/ClientKnownGuids.json holds the ids, in the same shape the 2023 server
    // used: { "_comment": "...", "Guids": [ ... ] }.
    //
    // IF THE FILE IS ABSENT, NOTHING IS FILTERED. That is the default: the list shipped
    // alongside this was extracted from the 2023 build, and the 2025 client is a newer build
    // that almost certainly knows items 2023 never had. Filtering on the older list would
    // hide items that currently work. Turn it on deliberately, and compare.
    public static class ItemGuids
    {
        private static HashSet<string>? _known;
        private static readonly object _lock = new();

        public static HashSet<string> KnownGuids()
        {
            if (_known != null) return _known;

            lock (_lock)
            {
                if (_known != null) return _known;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "ClientKnownGuids.json");
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        if (doc.RootElement.TryGetProperty("Guids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                            foreach (var g in arr.EnumerateArray())
                            {
                                var s = g.GetString();
                                if (!string.IsNullOrWhiteSpace(s))
                                {
                                    var n = NormalizeGuid(s);
                                    if (n != null) set.Add(n);
                                }
                            }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[items] could not read ClientKnownGuids.json ({ex.Message}) - nothing will be filtered");
                }

                _known = set;
                Console.WriteLine($"[items] client-known GUIDs loaded: {set.Count}" +
                                  (set.Count == 0 ? " (no filter file - nothing will be filtered)" : ""));
                return _known;
            }
        }

        /// <summary>
        /// Rec Room writes item ids two ways: a normal UUID, or a 22-character base64
        /// "short GUID". Both normalise to a plain lowercase UUID so they compare equal.
        /// </summary>
        public static string? NormalizeGuid(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return null;

            if (Guid.TryParse(s, out var g)) return g.ToString().ToLowerInvariant();

            if (s.Length == 22)
            {
                try
                {
                    var bytes = Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + "==");
                    if (bytes.Length == 16) return new Guid(bytes).ToString().ToLowerInvariant();
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Whether the client can render this item. Always true when no filter file is
        /// present, so an absent list can never cost anyone their wardrobe.
        /// </summary>
        // ---- HiddenItemsStorefronts --------------------------------------------------------
        //
        // An ALLOWLIST: while the flag is on, a store lists an avatar item only if its base guid is
        // in Data/APIS/items/ShownItems.json ({ "Guids": [...] }) - or, when that file does not
        // exist, only if it is in the starter wardrobe (DefaultAvatarItems.json), which every avatar
        // on the server wears and which is known to render.
        //
        // ShownItems.json is generated from the reference server's legacy item catalog
        // (recflare apps/econ/static/db/avatar-items.json, 3,061 items / 977 base guids) - the
        // list its 2025 Watch store (sf3-2025.json) sells to this very client build, with nothing
        // but AvatarItemDesc on each row. Regenerate it from that file if the reference updates.
        //
        // History: the 2023 embedded-item list was the gate first, then a hand-grown "seen to
        // render" list, because items on the 2023 list still came up "Can't load item". That was
        // not the embedded set: the captured store rows carried AvatarItemInfo.DownloadableAvatarItemId,
        // which sends the client to download a CAI bundle instead of drawing the legacy item, and
        // that download always fails on this client. StoreGenerator.ShapeRow now strips it, so a
        // legacy item on this list renders the way it does on the reference.
        // ShownItems.json is re-read when it changes.
        private static HashSet<string>? _shown;
        private static string _shownStamp = "";

        private static HashSet<string> ShownGuids()
        {
            string shownPath = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "ShownItems.json");
            string defaultsPath = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "DefaultAvatarItems.json");
            string stamp = (File.Exists(shownPath) ? File.GetLastWriteTimeUtc(shownPath).Ticks.ToString() : "-")
                         + "|" + (File.Exists(defaultsPath) ? File.GetLastWriteTimeUtc(defaultsPath).Ticks.ToString() : "-");
            if (_shown != null && stamp == _shownStamp) return _shown;

            lock (_lock)
            {
                if (_shown != null && stamp == _shownStamp) return _shown;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string source = "nothing";
                try
                {
                    if (File.Exists(shownPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(shownPath));
                        if (doc.RootElement.TryGetProperty("Guids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                            foreach (var g in arr.EnumerateArray())
                            {
                                var n = NormalizeGuid((g.GetString() ?? "").Split(',')[0]);
                                if (n != null) set.Add(n);
                            }
                        source = "ShownItems.json";
                    }
                    else if (File.Exists(defaultsPath))
                    {
                        var arr = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(defaultsPath)) ?? new();
                        foreach (var d in arr)
                        {
                            var n = NormalizeGuid((d ?? "").Split(',')[0]);
                            if (n != null) set.Add(n);
                        }
                        source = "DefaultAvatarItems.json (no ShownItems.json yet)";
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[items] could not read the shown-items list: {ex.Message}"); }

                Console.WriteLine($"[items] HiddenItemsStorefronts: stores list only the {set.Count} item id(s) in {source}");
                _shown = set;
                _shownStamp = stamp;
                return _shown;
            }
        }

        /// <summary>
        /// May a STORE list this avatar item? False only while HiddenItemsStorefronts is on and
        /// the item's base guid is not in the known-embedded set. Items with no descriptor
        /// (consumables, boxes, equipment skins) are never hidden - their art is in the client.
        /// </summary>
        public static bool StoreCanList(string? avatarItemDesc)
        {
            if (!ConfigCampus.HiddenItemsStorefronts) return true;
            if (string.IsNullOrWhiteSpace(avatarItemDesc)) return true;   // consumables, boxes, skins: no bundle
            var shown = ShownGuids();
            var baseGuid = NormalizeGuid(avatarItemDesc.Split(',').FirstOrDefault());
            // An unparseable descriptor is hidden, not waved through - this is an allowlist.
            return baseGuid != null && shown.Contains(baseGuid);
        }

        public static bool ClientCanRender(string? avatarItemDesc)
        {
            var known = KnownGuids();
            if (known.Count == 0) return true;

            var baseGuid = NormalizeGuid((avatarItemDesc ?? "").Split(',').FirstOrDefault());
            return baseGuid == null || known.Contains(baseGuid);
        }
    }
}
