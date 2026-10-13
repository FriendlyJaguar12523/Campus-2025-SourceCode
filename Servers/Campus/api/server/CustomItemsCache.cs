using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// In-memory index of the UGC custom-avatar-item catalog (Data/APIS/items/UGC.json, ~24k items,
    /// ~87MB), the imported overlay (ImportedUGC.json - the dev locker) and the items players made
    /// here (CreatedUGC.json). Loaded once at startup.
    ///
    /// MEMORY. Each record is kept as its compact UTF-8 JSON (the whole catalog is ~45 MB that
    /// way) plus a small typed header - <see cref="Rec"/> - with the handful of fields the routes
    /// filter and sort on. A <see cref="JObject"/> is only built when a record is actually being
    /// served (a search page, a /bulk lookup, a locker page), and thrown away after. Holding the
    /// catalog as JObjects cost ~640 MB for the raw file alone (measured), and loading it as one
    /// parsed tree put the peak well over a gigabyte; the server sat at 2.3 GB RSS. The file is
    /// also read item by item (JsonTextReader), never as one parsed tree, so the peak is one item.
    ///
    /// Dates are kept as the strings they arrived as (DateParseHandling.None) - the catalog's own
    /// spelling is what the client got before, and ISO strings sort correctly as text.
    /// </summary>
    public static class CustomItemsCache
    {
        /// <summary>One catalog record: its JSON bytes and the fields the server reasons about.</summary>
        public sealed class Rec
        {
            public string Id = "";
            public byte[] Json = Array.Empty<byte>();
            public ulong Creator;
            public int Accessibility;
            public int OutfitType = -1;
            public int? BaseAvatarItemId;
            public bool Featured;
            public string Created = "";
            public int Price;
            public string Name = "";
            public string Description = "";
            /// <summary>Has asset-bundle saves (a modelled item) as opposed to a PNG shirt design.</summary>
            public bool BundleBacked;
            /// <summary>
            /// Exclusive: tagged "adminonly" (Tags[].Value). Never listed in any store / profile and
            /// never sold - only the admin panel's gift gives it. Owners still see and wear it normally.
            /// </summary>
            public bool AdminOnly;

            /// <summary>The record as the client receives it. A fresh object every call.</summary>
            public JObject ToJObject() => BundleHashes.Stamp(ParseRecord(Json));

            public static Rec From(JObject o)
            {
                // Clothing floor (owner rule): a custom item that is for sale costs at least
                // StoreGenerator.DefaultPrice. Applied to the record itself so the price the client
                // displays and the price the buy charges are the same number.
                int rawPrice = (int?)o["Price"] ?? 0;
                if (rawPrice > 0 && rawPrice < StoreGenerator.DefaultPrice) o["Price"] = StoreGenerator.DefaultPrice;
                var r = new Rec
                {
                    Id = (string?)o["CustomAvatarItemId"] ?? "",
                    Creator = (ulong?)o["CreatorAccountId"] ?? 0,
                    Accessibility = (int?)o["Accessibility"] ?? 0,
                    OutfitType = (int?)o["OutfitType"] ?? -1,
                    BaseAvatarItemId = (int?)o["BaseAvatarItemId"],
                    Featured = (bool?)o["IsFeatured"] == true,
                    Created = (string?)o["CreatedAt"] ?? "",
                    Price = (int?)o["Price"] ?? 0,
                    Name = (string?)o["Name"] ?? "",
                    Description = (string?)o["Description"] ?? "",
                    BundleBacked = o["CurrentSaves"] is JArray saves && saves.Count > 0,
                    AdminOnly = o["Tags"] is JArray tags && tags.Any(t => string.Equals((string?)t?["Value"], "adminonly", StringComparison.OrdinalIgnoreCase)),
                };
                r.Json = Encoding.UTF8.GetBytes(o.ToString(Formatting.None));
                return r;
            }
        }

        private static readonly JsonSerializerSettings NoDateParsing = new() { DateParseHandling = DateParseHandling.None };

        public static JObject ParseRecord(byte[] json)
        {
            using var sr = new StreamReader(new MemoryStream(json, writable: false), Encoding.UTF8);
            using var jr = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(jr);
        }

        private static Dictionary<string, Rec> _byId = new(StringComparer.OrdinalIgnoreCase);
        private static List<Rec> _all = new();
        private static Dictionary<ulong, List<Rec>> _byCreator = new();

        // IMPORTED UGC = items whose real .assetbundle/.png assets have been hosted on THIS server
        // (via the admin importer). These are what a dev "owns" in the UGC dev-locker. Kept as a
        // distinct set so devs don't get the whole catalog dumped into their wardrobe.
        private static readonly List<Rec> _imported = new();
        private static readonly HashSet<string> _importedIds = new(StringComparer.OrdinalIgnoreCase);
        // The official first-party items (FirstPartyUGC.json) - the Coach's stock, account 1.
        private static readonly HashSet<string> _firstPartyIds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        public static string ImportedPath =>
            Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "ImportedUGC.json");

        // FIRST-PARTY (Coach) custom items from the reference export, priced from a real Watch
        // storefront - ImportRealStore.py writes it. Loaded over UGC.json like part of the base
        // catalog (its record wins for an id both hold, which is how the prices land); NOT the
        // dev locker, so nobody's wardrobe grows by 2700 items.
        public static string FirstPartyPath =>
            Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "FirstPartyUGC.json");

        // PLAYER-CREATED items (the in-game costume maker, POST /api/customAvatarItems/v1): their own
        // file so a deploy can never clobber them, indexed like the rest but NOT marked imported.
        public static string CreatedPath =>
            Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "CreatedUGC.json");
        private static readonly List<Rec> _created = new();

        // CAMPUSSTUDIO items (recolours published from the CampusStudio app through
        // StudioController): their own file, loaded after FirstPartyUGC.json and treated as
        // first-party (Coach) stock. Written only by the server, so a deploy never clobbers it.
        public static string StudioPath =>
            Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "StudioUGC.json");
        private static readonly List<Rec> _studio = new();

        public static int Count => _all.Count;
        public static int ImportedCount { get { lock (_lock) return _imported.Count; } }

        /// <summary>Every record (catalog + imported + created), as compact records.</summary>
        public static IReadOnlyList<Rec> AllRecs => _all;

        /// <summary>The imported (dev locker) records.</summary>
        public static List<Rec> ImportedRecs { get { lock (_lock) return new List<Rec>(_imported); } }
        public static HashSet<string> ImportedIds { get { lock (_lock) return new HashSet<string>(_importedIds, StringComparer.OrdinalIgnoreCase); } }

        /// <summary>Walk a {"Results":[...]} file one object at a time - never the whole tree.</summary>
        private static IEnumerable<JObject> ReadResults(string path)
        {
            using var sr = new StreamReader(path, Encoding.UTF8);
            using var jr = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None };
            // Find the Results array, then yield each object in it.
            while (jr.Read())
            {
                if (jr.TokenType == JsonToken.PropertyName && string.Equals((string?)jr.Value, "Results", StringComparison.OrdinalIgnoreCase))
                {
                    if (!jr.Read() || jr.TokenType != JsonToken.StartArray) yield break;
                    while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                    {
                        if (jr.TokenType == JsonToken.StartObject)
                            yield return JObject.Load(jr);
                    }
                    yield break;
                }
            }
        }

        public static void Load()
        {
            _droppedItems = 0; _droppedSaves = 0;
            try
            {
                string ugc = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "UGC.json");
                string legacy = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "customitems.json");
                string path = File.Exists(ugc) ? ugc : legacy;

                var byId = new Dictionary<string, Rec>(StringComparer.OrdinalIgnoreCase);
                var all = new List<Rec>();

                if (File.Exists(path))
                {
                    foreach (var o in ReadResults(path))
                    {
                        string? id = (string?)o["CustomAvatarItemId"];
                        if (string.IsNullOrEmpty(id)) continue;
                        if (!PassesCutoff(o)) continue;
                        SanitizeForClient(o);
                        var rec = Rec.From(o);
                        if (byId.TryGetValue(id, out var existing)) all.Remove(existing);
                        byId[id] = rec;
                        all.Add(rec);
                        // NOT indexed by creator: the catalog's CreatorAccountId values are the ORIGINAL
                        // RecNet creators' ids, which are not accounts here. Only CreatedUGC (made on
                        // this server) maps to local accounts; see AddOrUpdate.
                    }
                    Console.WriteLine($"[ugc] loaded {all.Count} custom avatar items from {Path.GetFileName(path)}");
                }
                else Console.WriteLine("[ugc] no UGC.json / customitems.json found - base custom items empty");

                _byId = byId; _all = all; _byCreator = new Dictionary<ulong, List<Rec>>();
                lock (_lock) { _imported.Clear(); _importedIds.Clear(); }

                // Merge the imported overlay LAST so its records win (they carry hosted-asset refs).
                if (File.Exists(ImportedPath))
                {
                    try
                    {
                        int n = 0;
                        foreach (var o in ReadResults(ImportedPath)) { AddOrUpdate(o, persist: false); n++; }
                        Console.WriteLine($"[ugc] merged {n} imported UGC item(s) from ImportedUGC.json (dev locker)");
                    }
                    catch (Exception iex) { Console.WriteLine($"[ugc] ImportedUGC.json load failed: {iex.Message}"); }
                }

                // First-party items LAST: the same 2830 Coach items the dev locker was imported
                // from, now carrying the real store's price (ImportRealStore.py). A record that is
                // already in the locker stays in it - only the record is swapped.
                if (File.Exists(FirstPartyPath))
                {
                    try
                    {
                        int added = 0, replaced = 0;
                        lock (_lock) _firstPartyIds.Clear();
                        foreach (var o in ReadResults(FirstPartyPath))
                        {
                            string? id = (string?)o["CustomAvatarItemId"];
                            if (string.IsNullOrEmpty(id)) continue;
                            if (!PassesCutoff(o)) continue;
                            SanitizeForClient(o);
                            var rec = Rec.From(o);
                            lock (_lock)
                            {
                                if (_byId.TryGetValue(id, out var existing))
                                {
                                    _all.Remove(existing);
                                    if (_imported.Remove(existing)) _imported.Add(rec);
                                    replaced++;
                                }
                                else added++;
                                _byId[id] = rec;
                                _all.Add(rec);
                                _firstPartyIds.Add(id);
                            }
                        }
                        Console.WriteLine($"[ugc] FirstPartyUGC.json: {added} first-party item(s) added, {replaced} replaced (priced)");
                    }
                    catch (Exception fex) { Console.WriteLine($"[ugc] FirstPartyUGC.json load failed: {fex.Message}"); }
                }

                lock (_lock) _studio.Clear();
                if (File.Exists(StudioPath))
                {
                    try
                    {
                        int n = 0;
                        foreach (var o in ReadResults(StudioPath)) if (AddStudio(o, persist: false) != null) n++;
                        Console.WriteLine($"[ugc] StudioUGC.json: {n} CampusStudio item(s)");
                    }
                    catch (Exception sex) { Console.WriteLine($"[ugc] StudioUGC.json load failed: {sex.Message}"); }
                }

                lock (_lock) _created.Clear();
                if (File.Exists(CreatedPath))
                {
                    try
                    {
                        int n = 0;
                        foreach (var o in ReadResults(CreatedPath))
                        {
                            var rec = AddOrUpdate(o, persist: false, markImported: false);
                            if (rec != null) { lock (_lock) _created.Add(rec); n++; }
                        }
                        Console.WriteLine($"[ugc] loaded {n} player-created item(s) from CreatedUGC.json");
                    }
                    catch (Exception cex) { Console.WriteLine($"[ugc] CreatedUGC.json load failed: {cex.Message}"); }
                }
                if (ConfigCampus.CustomItemCreatedCutoffUtc != null)
                    Console.WriteLine($"[ugc] cutoff {ConfigCampus.CustomItemCreatedCutoffUtc:u}: dropped {_droppedItems} item(s) and {_droppedSaves} newer save(s) the clients can't read; {_all.Count} item(s) served");

                long bytes = 0; foreach (var r in _all) bytes += r.Json.Length;
                Console.WriteLine($"[ugc] catalog held as {bytes / (1024 * 1024)} MB of compact JSON ({_all.Count} record(s))");
                GC.Collect();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ugc] load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Upsert one custom item into every index and mark it IMPORTED (dev-locker eligible).
        /// Thread-safe; used both at Load (persist:false) and live from the admin importer
        /// (persist:true appends it to ImportedUGC.json so it survives a restart). Returns the
        /// stored record, or null when the item was refused by the cutoff.
        /// </summary>
        public static Rec? AddOrUpdate(JObject item, bool persist = true, bool markImported = true)
        {
            string? id = (string?)item["CustomAvatarItemId"];
            if (string.IsNullOrEmpty(id)) return null;
            if (markImported && !PassesCutoff(item)) return null;   // a player's own creation is always current
            SanitizeForClient(item);
            var rec = Rec.From(item);

            lock (_lock)
            {
                if (_byId.TryGetValue(id, out var existing))
                {
                    _all.Remove(existing);
                    if (_byCreator.TryGetValue(existing.Creator, out var oldList)) oldList.Remove(existing);
                    _imported.Remove(existing);
                    _created.Remove(existing);
                }
                _byId[id] = rec;
                _all.Add(rec);
                if (rec.Creator != 0 && !markImported)   // imported catalog records keep their RecNet creator id; only local creations are attributed
                {
                    if (!_byCreator.TryGetValue(rec.Creator, out var list)) _byCreator[rec.Creator] = list = new List<Rec>();
                    list.Add(rec);
                }
                if (markImported)
                {
                    _importedIds.Add(id);
                    _imported.Add(rec);
                }
            }

            if (persist && markImported) PersistImported();
            return rec;
        }

        /// <summary>
        /// Upsert a CampusStudio item: indexed exactly like a first-party item (so the store, the
        /// dev locker and gifting all see it) and persisted to StudioUGC.json. Live - no restart.
        /// </summary>
        public static Rec? AddStudio(JObject item, bool persist = true)
        {
            string? id = (string?)item["CustomAvatarItemId"];
            if (string.IsNullOrEmpty(id)) return null;
            if (!PassesCutoff(item)) return null;
            SanitizeForClient(item);
            var rec = Rec.From(item);
            lock (_lock)
            {
                if (_byId.TryGetValue(id, out var existing))
                {
                    _all.Remove(existing);
                    if (_imported.Remove(existing)) _imported.Add(rec);
                }
                _byId[id] = rec;
                _all.Add(rec);
                _firstPartyIds.Add(id);
                _studio.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                _studio.Add(rec);
            }
            if (persist) PersistStudio();
            return rec;
        }

        /// <summary>
        /// Delete a CampusStudio item from every index and from StudioUGC.json (live - no restart). Only items that
        /// were published through Studio are in that list; anything else answers null. Returns the removed record.
        /// </summary>
        public static JObject? RemoveStudio(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Rec? rec;
            lock (_lock)
            {
                if (!_studio.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) return null;
                if (!_byId.TryGetValue(id!, out rec)) return null;
                _byId.Remove(id!);
                _all.Remove(rec);
                _firstPartyIds.Remove(id!);
                _studio.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (_byCreator.TryGetValue(rec.Creator, out var list)) list.Remove(rec);
                _importedIds.Remove(id!);
                _imported.Remove(rec);
                _created.Remove(rec);
            }
            PersistStudio();
            return rec.ToJObject();
        }

        /// <summary>Does any catalog record's JSON still contain this text (a bundle or thumbnail file name)?</summary>
        public static bool AnyRecordMentions(string needle)
        {
            var nb = Encoding.UTF8.GetBytes(needle);
            lock (_lock) foreach (var r in _all) if (r.Json.AsSpan().IndexOf(nb) >= 0) return true;
            return false;
        }

        public static bool IsStudio(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_lock) return _studio.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static void PersistStudio()
        {
            try
            {
                List<Rec> list;
                lock (_lock) list = new List<Rec>(_studio);
                WriteResults(StudioPath, list);
            }
            catch (Exception ex) { Console.WriteLine($"[ugc] persist studio failed: {ex.Message}"); }
        }

        /// <summary>First-party (Coach + CampusStudio) modelled items, admin-only included - the CampusStudio library.</summary>
        public static List<Rec> StudioLibraryRecs()
        {
            List<Rec> all; HashSet<string> ids;
            lock (_lock) { all = new List<Rec>(_all); ids = new HashSet<string>(_firstPartyIds, StringComparer.OrdinalIgnoreCase); }
            return all.Where(r => ids.Contains(r.Id) && r.BundleBacked).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Register a player-created item (costume maker) and persist it to CreatedUGC.json.</summary>
        public static void AddCreated(JObject item)
        {
            var rec = AddOrUpdate(item, persist: false, markImported: false);
            if (rec == null) return;
            lock (_lock)
            {
                _created.RemoveAll(x => string.Equals(x.Id, rec.Id, StringComparison.OrdinalIgnoreCase));
                _created.Add(rec);
            }
            PersistCreated();
        }

        /// <summary>Store an edited created item (PUT) and re-persist. The record is replaced, not patched in place.</summary>
        public static void CreatedChanged(JObject item) => AddCreated(item);

        /// <summary>Remove an item from every index (DELETE). Returns the record, or null.</summary>
        public static JObject? Remove(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Rec? rec;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id!, out rec)) return null;
                _byId.Remove(id!);
                _all.Remove(rec);
                if (_byCreator.TryGetValue(rec.Creator, out var list)) list.Remove(rec);
                _importedIds.Remove(id!);
                _imported.Remove(rec);
                _created.Remove(rec);
            }
            PersistCreated();
            return rec.ToJObject();
        }

        // {"Results":[<record bytes>,...],"TotalResults":N}, streamed - no tree is built to write it.
        private static void WriteResults(string path, List<Rec> recs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var head = Encoding.UTF8.GetBytes("{\"Results\":[");
                fs.Write(head, 0, head.Length);
                for (int i = 0; i < recs.Count; i++)
                {
                    if (i > 0) fs.WriteByte((byte)',');
                    fs.Write(recs[i].Json, 0, recs[i].Json.Length);
                }
                var tail = Encoding.UTF8.GetBytes($"],\"TotalResults\":{recs.Count}}}");
                fs.Write(tail, 0, tail.Length);
            }
            File.Move(tmp, path, overwrite: true);
        }

        private static void PersistCreated()
        {
            try
            {
                List<Rec> list;
                lock (_lock) list = new List<Rec>(_created);
                WriteResults(CreatedPath, list);
            }
            catch (Exception ex) { Console.WriteLine($"[ugc] persist created failed: {ex.Message}"); }
        }

        /// <summary>Write the imported overlay to disk once (use after a batch of AddOrUpdate(persist:false)).</summary>
        public static void FlushImported() => PersistImported();

        private static void PersistImported()
        {
            try
            {
                List<Rec> list;
                lock (_lock) list = new List<Rec>(_imported);
                WriteResults(ImportedPath, list);
            }
            catch (Exception ex) { Console.WriteLine($"[ugc] persist imported failed: {ex.Message}"); }
        }

        // Keys of a save's AdditionalConfiguration envelope the 2021.3 client (PC + Quest
        // 20250718.01) can read: {"v":1,"d":{...}}. Newer exports add top-level keys ("ls":1 on
        // every 2024+ save); the client's SerializedAvatarItemData converter reads v and d and then
        // expects the object to END, so an extra key throws "The converter read too much or not
        // enough. Path: $ ... BytePositionInLine: 53" inside ApplyDownloadableAvatarItem and the
        // item never gets as far as its bundle. Unknown keys INSIDE d are only warned about
        // ("unhandled property ... (et)"), so d is passed through untouched.
        private static readonly HashSet<string> ConfigEnvelopeKeys = new(StringComparer.Ordinal) { "v", "d" };

        private static int _droppedItems, _droppedSaves;

        /// <summary>
        /// Apply ConfigCampus.CustomItemCreatedCutoffUtc: false when the ITEM was created after the
        /// cutoff (caller skips it). Saves created after the cutoff are removed from the record; an
        /// item left with no saves at all is dropped too (nothing renderable would remain).
        /// </summary>
        private static bool PassesCutoff(JObject item)
        {
            var cutoff = ConfigCampus.CustomItemCreatedCutoffUtc;
            if (cutoff == null) return true;
            if (After(item["CreatedAt"], cutoff.Value)) { _droppedItems++; return false; }
            if (item["CurrentSaves"] is JArray saves && saves.Count > 0)
            {
                var late = saves.Where(s => After(s["CreatedAt"], cutoff.Value)).ToList();
                foreach (var s in late) { s.Remove(); _droppedSaves++; }
                if (saves.Count == 0) { _droppedItems++; return false; }
            }
            return true;
        }

        private static bool After(JToken? createdAt, DateTime cutoffUtc)
        {
            string? s = (string?)createdAt;
            return !string.IsNullOrEmpty(s)
                && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                && dt > cutoffUtc;
        }

        // Keys the client's converter accepts INSIDE d.d (the per-slot attach/render block). This is
        // every key the 2024-era catalogs use; a key it doesn't know is first warned about ("Found
        // an unhandled property when parsing SerializedAvatarItemData | propertyName(et)") and then
        // the SAME "read too much or not enough" exception follows - and because the client applies
        // the whole owned list in one pass, one bad item (a sword exported in 2025-08 carrying
        // "et") aborts registration of every item after it, which surfaces as bundles that download
        // but "can't load". Unknown nested keys are stripped and logged.
        private static readonly HashSet<string> ConfigNestedKnownKeys = new(StringComparer.Ordinal)
            { "ap", "fp", "af", "uu", "hh", "fr", "uv", "c", "r", "ho", "hp", "l", "dc", "p", "hr", "ha", "hs", "t" };
        private static int _strippedNested;

        /// <summary>Strip the parts of an item record the clients' converters can't parse (see ConfigEnvelopeKeys).</summary>
        public static void SanitizeForClient(JObject item)
        {
            if (item["CurrentSaves"] is not JArray saves) return;
            foreach (var t in saves)
            {
                if (t is not JObject save) continue;
                string? ac = (string?)save["AdditionalConfiguration"];
                if (string.IsNullOrWhiteSpace(ac)) continue;
                try
                {
                    var cfg = JObject.Parse(ac);
                    bool changed = false;
                    var extra = cfg.Properties().Where(pr => !ConfigEnvelopeKeys.Contains(pr.Name)).ToList();
                    foreach (var pr in extra) { pr.Remove(); changed = true; }
                    if (cfg["d"] is JObject d && d["d"] is JObject nested)
                    {
                        var unknown = nested.Properties().Where(pr => !ConfigNestedKnownKeys.Contains(pr.Name)).ToList();
                        foreach (var pr in unknown)
                        {
                            if (_strippedNested++ < 20)
                                Console.WriteLine($"[ugc] '{(string?)item["Name"]}' ({(string?)item["CustomAvatarItemId"]}): stripped unknown config key d.d.{pr.Name} (client can't parse it)");
                            pr.Remove(); changed = true;
                        }
                    }
                    if (!changed) continue;
                    save["AdditionalConfiguration"] = cfg.ToString(Formatting.None);
                }
                catch { /* not JSON - leave it for the client to reject */ }
            }
        }

        /// <summary>
        /// A custom item the client renders from an ASSET BUNDLE (CurrentSaves carry UnityAsset
        /// files), as opposed to a custom SHIRT - a PNG design (DesignFilename) drawn onto a stock
        /// base item, which the client paints itself and needs no bundle for.
        ///
        /// The two are what the store search's tabs are: the CLOTHING tab asks for the modelled
        /// outfit types (0, 2, 3, 10, 20, 100-103, 200, 300, 301) and the CUSTOM CLOTHING tab asks
        /// for 105, the shirt designs. Every bundle-backed item in the catalog was built for a
        /// newer Unity than this client and comes up "Can't load item", so while
        /// ConfigCampus.HiddenItemsStorefronts is on they are kept out of the store (see
        /// StoreListable) - the Clothing tab goes empty, and the Custom Clothing tab keeps the
        /// shirts, which do render.
        /// </summary>
        public static bool IsBundleBacked(Rec r) => r.BundleBacked;

        /// <summary>Whether the store (search, featured, hot) may list this item right now.</summary>
        public static bool StoreListable(Rec r) =>
            !r.AdminOnly && (!ConfigCampus.HiddenItemsStorefronts || !r.BundleBacked)
            || (!r.AdminOnly && ConfigCampus.ServeFirstPartyClothing && IsFirstParty(r));

        /// <summary>An official first-party item (FirstPartyUGC.json) - the Coach's stock.</summary>
        public static bool IsFirstParty(Rec r) { lock (_lock) return _firstPartyIds.Contains(r.Id); }

        /// <summary>The Coach's stock (account 1): every published first-party item, newest first.</summary>
        /// <summary>Published, modelled custom items whose name contains q (admin gift search).</summary>
        public static List<Rec> SearchRecs(string q, int cap)
        {
            if (string.IsNullOrWhiteSpace(q)) return new List<Rec>();
            List<Rec> all; lock (_lock) all = _all.ToList();
            return all.Where(r => r.Accessibility != 0 && r.BundleBacked && r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(r => r.Name.Length).ThenBy(r => r.Name).Take(cap).ToList();
        }

        public static List<Rec> FirstPartyRecs()
        {
            HashSet<string> ids; lock (_lock) ids = new HashSet<string>(_firstPartyIds, StringComparer.OrdinalIgnoreCase);
            return _all.Where(r => ids.Contains(r.Id) && r.Accessibility != 0 && !r.AdminOnly)
                       .OrderByDescending(r => r.Created).ThenBy(r => r.Id).ToList();
        }

        // ---- the developer locker (UGC half) --------------------------------------------------
        private static List<Rec>? _devLocker;
        private static DateTime _devLockerAt;
        private static int _devLockerSource = -1;
        private static readonly object _devLockerLock = new();

        /// <summary>
        /// Every custom item a developer owns: every first-party (Coach) item and every published
        /// player-made / imported item - modelled ones whose bundles we don't host included (they
        /// show but can't load). Rebuilt every 10 minutes (or when the item count changes).
        /// </summary>
        public static List<Rec> DevLockerRecs()
        {
            lock (_devLockerLock)
            {
                int count; lock (_lock) count = _all.Count;
                if (_devLocker != null && count == _devLockerSource && DateTime.UtcNow - _devLockerAt < TimeSpan.FromMinutes(10))
                    return new List<Rec>(_devLocker);

                List<Rec> all; HashSet<string> firstParty;
                lock (_lock) { all = new List<Rec>(_all); firstParty = new HashSet<string>(_firstPartyIds, StringComparer.OrdinalIgnoreCase); }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var locker = new List<Rec>();
                int skipped = 0;
                // EVERY published item (owner's call 2026-09-26: "every single item") - including
                // imported modelled items whose bundles we don't host; `skipped` only counts those
                // for the log. Other players' unpublished drafts stay private.
                foreach (var r in all)
                {
                    if (firstParty.Contains(r.Id)) { locker.Add(r); continue; }
                    if (r.Accessibility == 0) continue;                 // someone's unpublished draft
                    if (r.BundleBacked && !BundlesHeld(r)) skipped++;
                    locker.Add(r);
                }
                // First-party first (the Coach's stock), then the rest newest first.
                locker = locker.OrderByDescending(r => firstParty.Contains(r.Id)).ThenByDescending(r => r.Created).ToList();
                Console.WriteLine($"[ugc] dev locker: {locker.Count} custom item(s) ({skipped} of them modelled items without hosted bundles) in {sw.ElapsedMilliseconds} ms");
                _devLocker = locker; _devLockerAt = DateTime.UtcNow; _devLockerSource = count;
                return new List<Rec>(locker);
            }
        }

        /// <summary>Does this server hold every bundle this item's saves name?</summary>
        private static bool BundlesHeld(Rec r)
        {
            try
            {
                var saves = ParseRecord(r.Json)["CurrentSaves"] as JArray;
                if (saves == null || saves.Count == 0) return true;
                foreach (var s in saves)
                    foreach (var k in new[] { "UnityAsset", "UnityAsset2" })
                    {
                        var n = (string?)s[k];
                        if (!string.IsNullOrWhiteSpace(n) && BundleHashes.Resolve(n) == null) return false;
                    }
                return true;
            }
            catch { return false; }
        }

        /// <summary>The Coach's account - who first-party items are filed under (reference COACH_ACCOUNT_ID).</summary>
        public const ulong CoachAccountId = 1;

        /// <summary>All imported UGC (the dev-locker set), materialised. Prefer ImportedRecs + paging.</summary>
        public static List<JObject> Imported => ImportedRecs.Select(r => r.ToJObject()).ToList();
        public static bool IsImported(string? id) { if (string.IsNullOrEmpty(id)) return false; lock (_lock) return _importedIds.Contains(id!); }

        /// <summary>Resolve specific ids (the outfit-build /bulk lookup). Order/duplicates follow input.</summary>
        public static List<JObject> GetByIds(IEnumerable<string?> ids)
        {
            // Every id resolves that exists - bundle-backed ones included. Withholding those here
            // (tried 2026-09-22, to keep the client off the CAI bundle path) emptied the locker
            // and the store: the client re-resolves its whole cached custom-item set through this
            // call at login (~5,000 ids in batches of 500) and does not cope with misses.
            var res = new List<JObject>();
            foreach (var id in ids)
                if (!string.IsNullOrEmpty(id) && _byId.TryGetValue(id!, out var r)) res.Add(r.ToJObject());
            return res;
        }

        public static JObject? Get(string? id) =>
            !string.IsNullOrEmpty(id) && _byId.TryGetValue(id!, out var r) ? r.ToJObject() : null;

        public static Rec? GetRec(string? id) =>
            !string.IsNullOrEmpty(id) && _byId.TryGetValue(id!, out var r) ? r : null;

        public static List<Rec> ByCreatorRecs(ulong creatorAccountId)
        {
            lock (_lock) return _byCreator.TryGetValue(creatorAccountId, out var l) ? new List<Rec>(l) : new List<Rec>();
        }

        public static List<JObject> ByCreator(ulong creatorAccountId) =>
            ByCreatorRecs(creatorAccountId).Where(r => !r.AdminOnly).Select(r => r.ToJObject()).ToList();   // profile lists: exclusives stay hidden
    }
}
