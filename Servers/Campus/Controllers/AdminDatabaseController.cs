using System.Reflection;
using System.Text;
using LiteDB;
using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    // ---------------------------------------------------------------------------------
    // Admin panel "Database" tab: browse, search, edit, add and delete documents in the server's
    // LiteDB databases (Data/DBs/*.db).
    //
    // Every database is reached through the LiteDatabase the server ALREADY holds open (read from
    // the owning class's static field) - LiteDB locks its file, so opening a second handle would
    // fail or fight the live one. Links.db is the exception: LinkDB opens it per call, so it is
    // opened per request here too.
    //
    // Safety:
    //   * dev token + admin session + admin IP allowlist, like every /admin/api route;
    //   * secrets (password, tokens, API keys, hashes) are shown masked and put back on save, so
    //     editing a document never overwrites them with the mask;
    //   * every save and delete is appended to Data/db-editor-audit.jsonl with the document as it
    //     was before and after - the undo trail;
    //   * no dropping collections or databases, and a document's _id can't be changed.
    // ---------------------------------------------------------------------------------
    [ApiController]
    public class AdminDatabaseController : ControllerBase
    {
        private sealed record Entry(string Name, Type? Owner, string? Field, string? Path, string About);

        private static readonly Entry[] Dbs =
        {
            new("Players", typeof(PlayerDB), "PlayerDBFile", null, "accounts, profiles, balances, bans"),
            new("Rooms", typeof(RoomDB), "rooms_db", null, "rooms and subrooms"),
            new("PlayersFriends", typeof(FriendsDB), "PlayerFriendDBFile", null, "friends, blocks, favourites"),
            new("Heartbeats", typeof(HeartbeatDB), "_db", null, "presence (who is where)"),
            new("Images", typeof(ImageMetadataDB), "_dbInstance", null, "photos, cheers, comments"),
            new("Chat", typeof(ChatDB), "ChatDBFile", null, "chat threads and messages"),
            new("Events", typeof(EventDB), "EventDBFile", null, "player events"),
            new("Clubs", typeof(ClubDB), "_db", null, "clubs"),
            new("Inventions", typeof(InventionDB), "_db", null, "inventions"),
            new("Settings", typeof(SettingsDB), "_db", null, "player settings"),
            new("Leaderboard", typeof(LeaderboardDB), "DB", null, "leaderboards"),
            new("Support", typeof(SupportDB), "DB", null, "support tickets"),
            new("Reports", typeof(ReportsDB), "_db", null, "player reports"),
            new("ModerationLog", typeof(ModerationLogDB), "_db", null, "ban logs"),
            new("BugReports", typeof(BugReportDB), "_db", null, "bug reports"),
            new("CuratedLists", typeof(CuratedListDB), "_db", null, "curated room lists"),
            new("Earnings", typeof(EarningsDB), "_db", null, "creator earnings"),
            new("RoomEcon", typeof(RoomEconDB), "_db", null, "room currencies, keys, offers"),
            new("Wishlists", typeof(WishlistDB), "_db", null, "wishlists"),
            new("SitePrefs", typeof(SitePrefsDB), "_db", null, "website preferences"),
            new("RefreshTokens", typeof(RefreshTokenDB), "_db", null, "sign-in refresh tokens"),
            new("ProgressionEvents", typeof(ProgressionEvents), "_db", null, "limited-time challenge progress"),
            new("RewardSelections", typeof(RewardSelections), "_db", null, "reward card picks"),
            new("Socials", typeof(SocialLinks), "_db", null, "linked social accounts"),
            new("Spotify", typeof(SpotifyLink), "_db", null, "linked Spotify accounts"),
            new("Links", null, null, "Data/DBs/Links.db", "links"),
        };

        private const string Mask = "(hidden)";
        private static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "passwordhash", "hashedpassword", "salt", "hash", "tokenhash", "secret", "clientsecret",
            "accesstoken", "refreshtoken", "authtoken", "apikey",
        };

        private static string AuditPath => System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "db-editor-audit.jsonl");
        private static readonly object _auditLock = new();

        // The live handle for a database; `owned` is non-null when this request opened it itself.
        private static LiteDatabase? Open(Entry e, out IDisposable? owned)
        {
            owned = null;
            if (e.Path != null)
            {
                var db = new LiteDatabase(e.Path);
                owned = db;
                return db;
            }
            var fi = e.Owner!.GetField(e.Field!, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return fi?.GetValue(null) switch
            {
                LiteDatabase d => d,
                Lazy<LiteDatabase> l => l.Value,
                _ => null
            };
        }

        private static Entry? Find(string name) => Dbs.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static ContentResult J(object o, int status = 200) => new()
        {
            Content = Newtonsoft.Json.JsonConvert.SerializeObject(o),
            ContentType = "application/json",
            StatusCode = status
        };

        private static string Pretty(BsonValue v)
        {
            var sw = new StringWriter();
            new JsonWriter(sw) { Pretty = true, Indent = 2 }.Serialize(v);
            return sw.ToString();
        }

        private static BsonValue MaskSecrets(BsonValue v)
        {
            if (v.IsDocument)
            {
                var d = new BsonDocument();
                foreach (var kv in v.AsDocument)
                    d[kv.Key] = Secret.Contains(kv.Key) && !kv.Value.IsNull ? Mask : MaskSecrets(kv.Value);
                return d;
            }
            if (v.IsArray) return new BsonArray(v.AsArray.Select(MaskSecrets));
            return v;
        }

        // Put the real value back wherever the edited document still carries the mask.
        private static void Unmask(BsonValue edited, BsonValue? original)
        {
            if (edited.IsDocument && original != null && original.IsDocument)
            {
                var e = edited.AsDocument; var o = original.AsDocument;
                foreach (var key in e.Keys.ToList())
                {
                    if (Secret.Contains(key) && e[key].IsString && e[key].AsString == Mask)
                        e[key] = o.ContainsKey(key) ? o[key] : BsonValue.Null;
                    else if (o.ContainsKey(key)) Unmask(e[key], o[key]);
                }
            }
            else if (edited.IsArray && original != null && original.IsArray)
            {
                var e = edited.AsArray; var o = original.AsArray;
                for (int i = 0; i < e.Count && i < o.Count; i++) Unmask(e[i], o[i]);
            }
        }

        private static void Audit(player_info admin, string db, string col, string op, BsonValue id, BsonValue? before, BsonValue? after)
        {
            var line = new BsonDocument
            {
                ["at"] = DateTime.UtcNow.ToString("o"),
                ["admin"] = (long)admin.Id,
                ["adminName"] = admin.Username ?? "",
                ["db"] = db, ["collection"] = col, ["op"] = op, ["id"] = id,
                ["before"] = before ?? BsonValue.Null, ["after"] = after ?? BsonValue.Null
            };
            lock (_auditLock) System.IO.File.AppendAllText(AuditPath, LiteDB.JsonSerializer.Serialize(line) + "\n");
        }

        private static BsonValue ParseId(string? raw)
        {
            raw = (raw ?? "").Trim();
            if (raw.Length == 0) throw new ArgumentException("No document id.");
            return LiteDB.JsonSerializer.Deserialize(raw);
        }

        // ---- list ---------------------------------------------------------------------------
        [HttpGet("/admin/api/db/list")]
        public ContentResult List()
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var outList = new List<object>();
            foreach (var e in Dbs)
            {
                string file = System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "DBs", e.Name + ".db");
                long size = System.IO.File.Exists(file) ? new FileInfo(file).Length : 0;
                try
                {
                    var db = Open(e, out var owned);
                    using (owned)
                    {
                        if (db == null) { outList.Add(new { name = e.Name, about = e.About, size, error = "not reachable" }); continue; }
                        var cols = db.GetCollectionNames().Where(c => !c.StartsWith("$")).OrderBy(c => c)
                            .Select(c => new { name = c, count = db.GetCollection(c).LongCount() }).ToList();
                        outList.Add(new { name = e.Name, about = e.About, size, collections = cols });
                    }
                }
                catch (Exception ex) { outList.Add(new { name = e.Name, about = e.About, size, error = ex.Message }); }
            }
            return J(new { success = true, databases = outList });
        }

        // ---- browse / search ----------------------------------------------------------------
        [HttpGet("/admin/api/db/{db}/{col}")]
        public ContentResult Browse(string db, string col, [FromQuery] int skip = 0, [FromQuery] int limit = 50,
                                    [FromQuery] string? q = null, [FromQuery] string? where = null)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var e = Find(db);
            if (e == null) return J(new { success = false, message = "No such database." });
            skip = Math.Max(0, skip); limit = Math.Clamp(limit, 1, 200);
            try
            {
                var ldb = Open(e, out var owned);
                using (owned)
                {
                    if (ldb == null || !ldb.CollectionExists(col)) return J(new { success = false, message = "No such collection." });
                    var c = ldb.GetCollection(col);
                    List<BsonDocument> page; long total; bool capped = false;
                    if (!string.IsNullOrWhiteSpace(where))
                    {
                        var expr = BsonExpression.Create(where);
                        total = c.LongCount(expr);
                        page = c.Find(expr, skip, limit).ToList();
                    }
                    else if (!string.IsNullOrWhiteSpace(q))
                    {
                        // Text search over each document's JSON (secrets excluded), capped so a huge
                        // collection can't stall the server.
                        const int scanCap = 50000;
                        var hits = new List<BsonDocument>(); int scanned = 0;
                        foreach (var d in c.FindAll())
                        {
                            if (++scanned > scanCap) { capped = true; break; }
                            if (LiteDB.JsonSerializer.Serialize(MaskSecrets(d)).Contains(q, StringComparison.OrdinalIgnoreCase)) hits.Add(d);
                        }
                        total = hits.Count;
                        page = hits.Skip(skip).Take(limit).ToList();
                    }
                    else
                    {
                        total = c.LongCount();
                        page = c.Find(Query.All(), skip, limit).ToList();
                    }
                    var rows = page.Select(d =>
                    {
                        string json = LiteDB.JsonSerializer.Serialize(MaskSecrets(d));
                        return new { id = LiteDB.JsonSerializer.Serialize(d["_id"]), preview = json.Length > 240 ? json.Substring(0, 240) + "…" : json };
                    }).ToList();
                    return J(new { success = true, total, skip, limit, capped, rows });
                }
            }
            catch (Exception ex) { return J(new { success = false, message = ex.Message }); }
        }

        [HttpGet("/admin/api/db/{db}/{col}/doc")]
        public ContentResult GetDoc(string db, string col, [FromQuery] string? id)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var e = Find(db);
            if (e == null) return J(new { success = false, message = "No such database." });
            try
            {
                var ldb = Open(e, out var owned);
                using (owned)
                {
                    if (ldb == null || !ldb.CollectionExists(col)) return J(new { success = false, message = "No such collection." });
                    var d = ldb.GetCollection(col).FindById(ParseId(id));
                    if (d == null) return J(new { success = false, message = "That document no longer exists." });
                    return J(new { success = true, id = LiteDB.JsonSerializer.Serialize(d["_id"]), json = Pretty(MaskSecrets(d)) });
                }
            }
            catch (Exception ex) { return J(new { success = false, message = ex.Message }); }
        }

        // ---- write --------------------------------------------------------------------------
        public sealed class SaveBody { public string? id { get; set; } public string? json { get; set; } }

        [HttpPost("/admin/api/db/{db}/{col}/save")]
        public ContentResult Save(string db, string col, [FromBody] SaveBody body)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var e = Find(db);
            if (e == null) return J(new { success = false, message = "No such database." });
            BsonDocument doc;
            try
            {
                var v = LiteDB.JsonSerializer.Deserialize(body?.json ?? "");
                if (!v.IsDocument) return J(new { success = false, message = "The JSON must be one object { ... }." });
                doc = v.AsDocument;
            }
            catch (Exception ex) { return J(new { success = false, message = "That isn't valid JSON: " + ex.Message }); }
            try
            {
                var ldb = Open(e, out var owned);
                using (owned)
                {
                    if (ldb == null || !ldb.CollectionExists(col)) return J(new { success = false, message = "No such collection." });
                    var c = ldb.GetCollection(col);
                    bool isNew = string.IsNullOrWhiteSpace(body!.id);
                    BsonDocument? before = null;
                    if (!isNew)
                    {
                        var id = ParseId(body.id);
                        before = c.FindById(id);
                        if (before == null) return J(new { success = false, message = "That document no longer exists - reload." });
                        if (!doc.ContainsKey("_id")) doc["_id"] = id;
                        if (doc["_id"] != id) return J(new { success = false, message = "The _id can't be changed. Add a new document and delete this one instead." });
                        Unmask(doc, before);
                        c.Update(doc);
                    }
                    else
                    {
                        if (doc.ContainsKey("_id") && c.FindById(doc["_id"]) != null)
                            return J(new { success = false, message = "A document with that _id already exists." });
                        var newId = c.Insert(doc);
                        doc["_id"] = newId;
                    }
                    Audit(admin!, e.Name, col, isNew ? "insert" : "update", doc["_id"], before, doc);
                    Console.WriteLine($"[db-editor] {admin!.Username} ({admin.Id}) {(isNew ? "added" : "saved")} {e.Name}.{col} {LiteDB.JsonSerializer.Serialize(doc["_id"])}");
                    return J(new { success = true, id = LiteDB.JsonSerializer.Serialize(doc["_id"]), message = isNew ? "Added." : "Saved." });
                }
            }
            catch (Exception ex) { return J(new { success = false, message = ex.Message }); }
        }

        public sealed class DeleteBody { public string? id { get; set; } }

        [HttpPost("/admin/api/db/{db}/{col}/delete")]
        public ContentResult Delete(string db, string col, [FromBody] DeleteBody body)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var e = Find(db);
            if (e == null) return J(new { success = false, message = "No such database." });
            try
            {
                var ldb = Open(e, out var owned);
                using (owned)
                {
                    if (ldb == null || !ldb.CollectionExists(col)) return J(new { success = false, message = "No such collection." });
                    var c = ldb.GetCollection(col);
                    var id = ParseId(body?.id);
                    var before = c.FindById(id);
                    if (before == null) return J(new { success = false, message = "That document no longer exists." });
                    c.Delete(id);
                    Audit(admin!, e.Name, col, "delete", id, before, null);
                    Console.WriteLine($"[db-editor] {admin!.Username} ({admin.Id}) deleted {e.Name}.{col} {LiteDB.JsonSerializer.Serialize(id)}");
                    return J(new { success = true, message = "Deleted." });
                }
            }
            catch (Exception ex) { return J(new { success = false, message = ex.Message }); }
        }
    }
}
