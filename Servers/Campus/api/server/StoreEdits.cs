using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Admin-panel edits to the gift-drop storefronts (Stores tab), kept OUT of the store files in
    /// Data/APIS/store-edits.json and laid over them wherever a store is served or bought from:
    ///   - GET /api/storefronts/v3/giftdropstore/{id} (ApplyJson),
    ///   - StoreCatalog.FindStoreItem, so an added row is buyable at the admin's price,
    ///   - the Wishlist rows.
    /// The store files themselves (3.json is re-uploaded by UploadUpdate.bat on every deploy)
    /// are never written, so a deploy can't wipe the edits.
    ///
    /// Per storefront: "Rows" = full store rows (the client's own StoreItem shape) that are either
    /// NEW (their own PurchasableItemId, from AdminIdBase up) or REPLACE the base row with the same
    /// id (a price / set / featured edit), and "Removed" = base PurchasableItemIds taken off sale.
    /// </summary>
    public static class StoreEdits
    {
        /// <summary>
        /// Ids for admin-added rows (PurchasableItemId and GiftDropId both). Far above the captured
        /// ids and the generated Watch ranges (StoreGenerator: catalog 100000+, skins 140000+), and
        /// unique across ALL storefronts, since FindStoreItem scans every store when the client
        /// does not say which one it is buying from.
        /// </summary>
        public const int AdminIdBase = 900_000;

        private static readonly string FilePath =
            Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "store-edits.json");

        private static readonly object _lock = new();
        private static JObject? _root;
        private static int _version;

        public static int Version { get { Load(); return _version; } }

        private static JObject Load()
        {
            lock (_lock)
            {
                if (_root != null) return _root;
                try
                {
                    _root = File.Exists(FilePath) ? JObject.Parse(File.ReadAllText(FilePath)) : new JObject();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[store-edits] could not read store-edits.json ({ex.Message}) - starting empty; the bad file is kept as .broken");
                    try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
                    _root = new JObject();
                }
                if (_root["Stores"] is not JObject) _root["Stores"] = new JObject();
                if (_root["NextId"]?.Type != JTokenType.Integer) _root["NextId"] = AdminIdBase;
                MigrateTo2025StoreIds(_root);
                _version++;
                return _root;
            }
        }

        /// <summary>
        /// One-time fix-up: the Stores tab first used the 2023 client's storefront ids, where
        /// 2000-2004 were "PopUpShop_1..5". In the 2025 client those are the progression-event
        /// shops and the pop-up shops are 1700-1704, so anything stocked there moves across.
        /// </summary>
        private static void MigrateTo2025StoreIds(JObject root)
        {
            if ((int?)root["StoreIds"] == 2025) return;
            // No file = nothing stocked yet: nothing to move, and writing one here would leave a
            // stray store-edits.json on a dev box for a full package to ship over the live copy.
            if (!File.Exists(FilePath)) { root["StoreIds"] = 2025; return; }
            var stores = (JObject)root["Stores"]!;
            int moved = 0;
            for (int i = 0; i < 5; i++)
            {
                if (stores[(2000 + i).ToString()] is not JObject from) continue;
                stores.Remove((2000 + i).ToString());
                string to = (1700 + i).ToString();
                if (stores[to] is not JObject dest) { stores[to] = from; }
                else
                {
                    foreach (var r in (from["Rows"] as JArray ?? new JArray())) ((JArray)(dest["Rows"] ??= new JArray())).Add(r);
                    foreach (var r in (from["Removed"] as JArray ?? new JArray())) ((JArray)(dest["Removed"] ??= new JArray())).Add(r);
                }
                moved += (from["Rows"] as JArray)?.Count ?? 0;
                Console.WriteLine($"[store-edits] moved store {2000 + i} edits to {to} (PopUpShop_{i + 1} is {to} in the 2025 client)");
            }
            root["StoreIds"] = 2025;
            try
            {
                var tmp = FilePath + ".tmp";
                if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak-2023ids", true);
                File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.Indented));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex) { Console.WriteLine($"[store-edits] could not save the store id migration: {ex.Message}"); }
            if (moved > 0) Console.WriteLine($"[store-edits] {moved} pop-up shop item(s) moved to the 2025 store ids");
        }

        private static void Save()
        {
            // caller holds _lock
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, _root!.ToString(Newtonsoft.Json.Formatting.Indented));
            File.Move(tmp, FilePath, true);
            _version++;
            lock (_applied) _applied.Clear();
        }

        private static JObject StoreNode(int store, bool create)
        {
            var stores = (JObject)Load()["Stores"]!;
            if (stores[store.ToString()] is JObject s) return s;
            var n = new JObject { ["Removed"] = new JArray(), ["Rows"] = new JArray() };
            if (create) stores[store.ToString()] = n;
            return n;
        }

        private static int Pid(JToken row) => (int?)row["PurchasableItemId"] ?? 0;

        // ---- reads --------------------------------------------------------------------------

        public static bool HasEdits(int store)
        {
            lock (_lock)
            {
                var s = StoreNode(store, false);
                return ((s["Rows"] as JArray)?.Count ?? 0) > 0 || ((s["Removed"] as JArray)?.Count ?? 0) > 0;
            }
        }

        /// <summary>The admin row for this id in this store (added or replacing), cloned; null if none.</summary>
        public static JObject? RowFor(int store, int pid)
        {
            lock (_lock)
            {
                var rows = StoreNode(store, false)["Rows"] as JArray;
                return rows?.FirstOrDefault(r => Pid(r) == pid)?.DeepClone() as JObject;
            }
        }

        public static bool IsRemoved(int store, int pid)
        {
            lock (_lock)
                return (StoreNode(store, false)["Removed"] as JArray)?.Any(x => (int?)x == pid) == true;
        }

        /// <summary>Every admin row in any store with this id (store it belongs to first).</summary>
        public static List<(int store, JObject row)> RowsAnywhere(int pid)
        {
            var list = new List<(int, JObject)>();
            lock (_lock)
                foreach (var p in (JObject)Load()["Stores"]!)
                    if (int.TryParse(p.Key, out int st) && p.Value?["Rows"] is JArray rows)
                        foreach (var r in rows)
                            if (Pid(r) == pid) list.Add((st, (JObject)r.DeepClone()));
            return list;
        }

        /// <summary>(added, edited, removed) counts for one store; "edited" = rows replacing a base id.</summary>
        public static (int added, int edited, int removed) Counts(int store)
        {
            lock (_lock)
            {
                var s = StoreNode(store, false);
                int added = 0, edited = 0;
                foreach (var r in s["Rows"] as JArray ?? new JArray())
                    if (Pid(r) >= AdminIdBase) added++; else edited++;
                return (added, edited, (s["Removed"] as JArray)?.Count ?? 0);
            }
        }

        public static HashSet<int> RemovedIds(int store)
        {
            lock (_lock)
                return new HashSet<int>((StoreNode(store, false)["Removed"] as JArray ?? new JArray()).Select(x => (int?)x ?? 0));
        }

        public static List<JObject> Rows(int store)
        {
            lock (_lock)
                return (StoreNode(store, false)["Rows"] as JArray ?? new JArray()).Select(r => (JObject)r.DeepClone()).ToList();
        }

        // ---- laying the edits over a served store -------------------------------------------

        /// <summary>Apply this store's edits to a store object in place: drop removed rows, swap in edited ones, append added ones.</summary>
        public static void ApplyTo(int store, JObject root)
        {
            List<JObject> rows; HashSet<int> removed;
            lock (_lock)
            {
                var s = StoreNode(store, false);
                rows = (s["Rows"] as JArray ?? new JArray()).Select(r => (JObject)r.DeepClone()).ToList();
                removed = new HashSet<int>((s["Removed"] as JArray ?? new JArray()).Select(x => (int?)x ?? 0));
            }
            if (rows.Count == 0 && removed.Count == 0) return;

            foreach (var r in rows)
            {
                StoreGenerator.ShapeRow(r);
                r["CanBeGifted"] = StoreCatalog.PricedInTokens(r);
            }
            var byPid = rows.GroupBy(Pid).ToDictionary(g => g.Key, g => g.Last());
            var used = new HashSet<int>();
            var outRows = new JArray();
            foreach (var it in root["StoreItems"] as JArray ?? new JArray())
            {
                int pid = Pid(it);
                if (removed.Contains(pid)) continue;
                if (byPid.TryGetValue(pid, out var rep)) { outRows.Add(rep); used.Add(pid); }
                else outRows.Add(it);
            }
            foreach (var r in rows)
                if (used.Add(Pid(r))) outRows.Add(r);
            root["StoreItems"] = outRows;
        }

        private static readonly Dictionary<string, string> _applied = new();

        /// <summary>The served store JSON with this store's edits applied (unchanged string when it has none). Cached.</summary>
        public static string ApplyJson(int store, string json)
        {
            if (!HasEdits(store)) return json;
            string key = $"{store}|{_version}|{json.Length}|{json.GetHashCode()}";
            lock (_applied)
                if (_applied.TryGetValue(key, out var hit)) return hit;
            try
            {
                var root = JObject.Parse(json);
                ApplyTo(store, root);
                string outJson = root.ToString(Newtonsoft.Json.Formatting.None);
                lock (_applied)
                {
                    if (_applied.Count > 64) _applied.Clear();
                    _applied[key] = outJson;
                }
                return outJson;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[store-edits] could not apply edits to store {store}: {ex.Message}");
                return json;
            }
        }

        // ---- writes (admin) -----------------------------------------------------------------

        public static int NewId()
        {
            lock (_lock)
            {
                var root = Load();
                int id = Math.Max(AdminIdBase, (int?)root["NextId"] ?? AdminIdBase);
                root["NextId"] = id + 1;
                Save();
                return id;
            }
        }

        /// <summary>Store a row (added or replacing a base row), and un-remove its id.</summary>
        public static void Put(int store, JObject row)
        {
            lock (_lock)
            {
                var s = StoreNode(store, true);
                var rows = (JArray)s["Rows"]!;
                int pid = Pid(row);
                foreach (var old in rows.Where(r => Pid(r) == pid).ToList()) old.Remove();
                rows.Add(row);
                foreach (var x in ((JArray)s["Removed"]!).Where(x => (int?)x == pid).ToList()) x.Remove();
                Save();
            }
        }

        /// <summary>Put several rows in one write (set assignment / rename).</summary>
        public static void PutMany(int store, IEnumerable<JObject> newRows)
        {
            lock (_lock)
            {
                var s = StoreNode(store, true);
                var rows = (JArray)s["Rows"]!;
                var removed = (JArray)s["Removed"]!;
                foreach (var row in newRows)
                {
                    int pid = Pid(row);
                    foreach (var old in rows.Where(r => Pid(r) == pid).ToList()) old.Remove();
                    rows.Add(row);
                    foreach (var x in removed.Where(x => (int?)x == pid).ToList()) x.Remove();
                }
                Save();
            }
        }

        /// <summary>Take an id off sale: an added row is deleted, a base row is listed as removed.</summary>
        public static void Remove(int store, int pid)
        {
            lock (_lock)
            {
                var s = StoreNode(store, true);
                foreach (var old in ((JArray)s["Rows"]!).Where(r => Pid(r) == pid).ToList()) old.Remove();
                var removed = (JArray)s["Removed"]!;
                if (pid < AdminIdBase && !removed.Any(x => (int?)x == pid)) removed.Add(pid);
                Save();
            }
        }

        /// <summary>Undo every edit to a base id: back on sale, original price and set.</summary>
        public static void Restore(int store, int pid)
        {
            lock (_lock)
            {
                var s = StoreNode(store, true);
                foreach (var old in ((JArray)s["Rows"]!).Where(r => Pid(r) == pid && pid < AdminIdBase).ToList()) old.Remove();
                foreach (var x in ((JArray)s["Removed"]!).Where(x => (int?)x == pid).ToList()) x.Remove();
                Save();
            }
        }
    }
}
