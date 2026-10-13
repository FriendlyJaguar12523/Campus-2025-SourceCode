using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// CampusStudio (the desktop recolour app) talks to the server through these routes only.
    ///
    /// AUTH. Sign-in with a Campus username + password mints a token with surface "studio": it
    /// is accepted here and NOWHERE else (ClientSecurity.DecodeToken refuses "studio" on every
    /// generic path), so a CampusStudio login can't act in the game, the website or the admin
    /// panel. Who may use it = owner / dev accounts and accounts with the Studio role (admin
    /// panel > player > Studio). The role is re-checked on every call, so revoking it takes
    /// effect immediately. Banned accounts are refused.
    ///
    /// PUBLISH. The app sends the bundles it built plus a few fields; the server does NOT take a
    /// record from the client. It copies the SOURCE item's record and changes only the name, the
    /// ids, the file names and the admin-only tag - price, outfit type, accessibility and the
    /// rest always come from the source. Files are checked (names, UnityFS / PNG magic, sizes),
    /// never overwrite an existing file, and the item is registered live (CustomItemsCache.AddStudio
    /// -> StudioUGC.json) - no restart.
    ///
    /// Updating: an item published from CampusStudio can be republished under the same id (so
    /// owners keep it) by the account that made it, or by an owner / dev account. Items made
    /// with the first local build of the tool carry the "campusstudio" tag and count as
    /// CampusStudio items with no recorded maker (owner / dev only).
    /// </summary>
    [ApiController]
    public class StudioController : ControllerBase
    {
        private const string StudioTag = "campusstudio";
        private const string CreatedAt = "2025-07-18T12:00:00Z";   // the client drops items created after its 2025-07-19 build
        private static readonly TimeSpan TokenTtl = TimeSpan.FromDays(7);
        private const long MaxBundle = 40L * 1024 * 1024, MaxImage = 8L * 1024 * 1024, MaxRequest = 300L * 1024 * 1024;

        private static readonly Regex BundleName = new("^[a-z0-9]{25}\\.assetbundle$", RegexOptions.Compiled);
        private static readonly Regex ImageName = new("^[a-z0-9]{25}\\.png$", RegexOptions.Compiled);
        private static readonly object _publishLock = new();

        private static string Root => Environment.CurrentDirectory;
        private static string BlobDir => Path.Combine(Root, "Data", "CDN", "AvatarBlobs");
        private static string QuestDir => Path.Combine(BlobDir, "quest");
        private static string ImageDir => Path.Combine(Root, "Data", "Images");
        private static string MetaPath => Path.Combine(Root, "Data", "APIS", "items", "StudioMeta.json");

        // ---- who ------------------------------------------------------------------------------

        public static bool CanUseStudio(ulong id, out string? why)
        {
            why = null;
            var full = PlayerDB.GetFullAccount(id);
            if (full == null) { why = "Account not found."; return false; }
            if (PlayerDB.HasActiveModerationBlock(id)) { why = "This account is banned."; return false; }
            var f = full.dev_Flag;
            if ((f & (DevFlag.owner | DevFlag.dev | DevFlag.studio)) != 0) return true;
            why = "This account doesn't have the Studio role. Ask an admin to turn on \"Studio\" for you in the admin panel.";
            return false;
        }

        private static bool IsOwnerOrDev(ulong id) =>
            ((PlayerDB.GetFullAccount(id)?.dev_Flag ?? DevFlag.none) & (DevFlag.owner | DevFlag.dev)) != 0;

        private (player_info? user, IActionResult? error) Caller()
        {
            string auth = Request.Headers["Authorization"].ToString();
            string token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth.Substring(7).Trim() : "";
            if (token.Length == 0) return (null, StatusCode(401, new { ok = false, error = "Sign in first." }));
            string data = ClientSecurity.DecodeToken(token, requireSurface: "studio");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_"))
                return (null, StatusCode(401, new { ok = false, error = data == "error_token_expired" ? "Your sign-in has expired - sign in again." : "Sign in again." }));
            player_info? u;
            try { u = JsonConvert.DeserializeObject<player_info>(data); } catch { u = null; }
            if (u == null) return (null, StatusCode(401, new { ok = false, error = "Sign in again." }));
            if (!CanUseStudio(u.Id, out var why)) return (null, StatusCode(403, new { ok = false, error = why }));
            return (u, null);
        }

        // ---- meta (who made which studio item; next save id) ------------------------------------

        private class Meta
        {
            public Dictionary<string, MetaItem> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public int NextSaveId { get; set; }
        }
        private class MetaItem
        {
            public ulong Creator { get; set; }
            public string? CreatorName { get; set; }
            public string? Source { get; set; }
            public DateTime Created { get; set; }
            public DateTime Updated { get; set; }
        }

        private static Meta LoadMeta()
        {
            try
            {
                if (System.IO.File.Exists(MetaPath))
                {
                    var m = JsonConvert.DeserializeObject<Meta>(System.IO.File.ReadAllText(MetaPath));
                    if (m != null) { m.Items = new Dictionary<string, MetaItem>(m.Items, StringComparer.OrdinalIgnoreCase); return m; }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[studio] meta unreadable: {ex.Message}"); }
            return new Meta();
        }

        private static void SaveMeta(Meta m)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MetaPath)!);
            string tmp = MetaPath + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonConvert.SerializeObject(m, Formatting.Indented));
            System.IO.File.Move(tmp, MetaPath, overwrite: true);
        }

        private static bool HasStudioTag(JObject rec) =>
            rec["Tags"] is JArray tags && tags.Any(t => string.Equals((string?)t?["Value"], StudioTag, StringComparison.OrdinalIgnoreCase));

        private static bool IsStudioItem(string id, JObject? rec, Meta meta) =>
            CustomItemsCache.IsStudio(id) || meta.Items.ContainsKey(id) || (rec != null && HasStudioTag(rec));

        // ---- routes ---------------------------------------------------------------------------

        public class LoginBody { public string? username { get; set; } public string? password { get; set; } }

        /// <summary>POST /api/studio/v1/login {username, password} -> a studio token (7 days).</summary>
        [HttpPost("/api/studio/v1/login")]
        public IActionResult Login([FromBody] LoginBody? body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.username) || string.IsNullOrWhiteSpace(body.password))
                return BadRequest(new { ok = false, error = "Enter your username and password." });
            var result = PlayerDB.GetAccountPassword(body.username.Trim(), body.password, isAdminPanel: false);
            if (!result.success)
                return StatusCode(401, new { ok = false, error = result.error ?? "Incorrect username or password." });
            if (!CanUseStudio(result.accid, out var why))
            {
                Console.WriteLine($"[studio] sign-in refused for {result.accid}: {why}");
                return StatusCode(403, new { ok = false, error = why });
            }
            string token = ClientSecurity.RefreshToken_Web(result.accid, TokenTtl, "studio");
            var full = PlayerDB.GetFullAccount(result.accid);
            Console.WriteLine($"[studio] @{full?.player?.Username} ({result.accid}) signed in to CampusStudio");
            return Ok(new { ok = true, token, id = result.accid, username = full?.player?.Username,
                            expires = DateTimeOffset.UtcNow.Add(TokenTtl).ToUnixTimeSeconds() });
        }

        [HttpGet("/api/studio/v1/me")]
        public IActionResult Me()
        {
            var (u, err) = Caller();
            if (err != null) return err;
            return Ok(new { ok = true, id = u!.Id, username = u.Username, admin = IsOwnerOrDev(u.Id) });
        }

        /// <summary>The library: every first-party + CampusStudio modelled item.</summary>
        [HttpGet("/api/studio/v1/items")]
        public IActionResult Items()
        {
            var (u, err) = Caller();
            if (err != null) return err;
            var meta = LoadMeta();
            bool admin = IsOwnerOrDev(u!.Id);
            var list = new List<object>();
            foreach (var r in CustomItemsCache.StudioLibraryRecs())
            {
                JObject rec;
                try { rec = CustomItemsCache.ParseRecord(r.Json); } catch { continue; }
                var saves = rec["CurrentSaves"] as JArray;
                if (saves == null || !saves.Any(s => !string.IsNullOrEmpty((string?)s["UnityAsset2"]))) continue;
                bool studio = IsStudioItem(r.Id, rec, meta);
                meta.Items.TryGetValue(r.Id, out var mi);
                bool mine = studio && (mi != null ? mi.Creator == u.Id : admin);
                list.Add(new
                {
                    id = r.Id, name = r.Name, adminOnly = r.AdminOnly, outfit = (int?)rec["OutfitType"] ?? -1,
                    bodyTypes = saves.Select(s => (int?)s["BodyType"] ?? 0).ToArray(),
                    studio, mine, canUpdate = studio && (admin || (mi != null && mi.Creator == u.Id)),
                    maker = mi?.CreatorName,
                });
            }
            Response.Headers["Cache-Control"] = "no-store";
            return Ok(new { ok = true, items = list });
        }

        /// <summary>One item's full record (what the app needs to fetch and rebuild its bundles).</summary>
        [HttpGet("/api/studio/v1/items/{id}")]
        public IActionResult Item(string id)
        {
            var (u, err) = Caller();
            if (err != null) return err;
            var r = CustomItemsCache.GetRec(id);
            if (r == null || !CustomItemsCache.IsFirstParty(r)) return NotFound(new { ok = false, error = "No such item." });
            Response.Headers["Cache-Control"] = "no-store";
            return Content(r.ToJObject().ToString(Formatting.None), "application/json");
        }

        /// <summary>
        /// Publish a recolour. multipart/form-data:
        ///   sourceId, name, adminOnly (0/1), replaceId (optional), hideHand (optional: 1 = the item
        ///   hides the wearer's hand like a full glove, 0 = the hand shows; empty = as the source item),
        ///   saves = JSON [{BodyType, UnityAssetId, UnityAsset, UnityAsset2, Thumbnail, Quest}],
        ///   files named "pc:&lt;bundle&gt;", "quest:&lt;bundle&gt;", "img:&lt;thumbnail&gt;".
        /// </summary>
        [HttpPost("/api/studio/v1/publish")]
        [RequestSizeLimit(MaxRequest)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxRequest)]
        public async Task<IActionResult> Publish()
        {
            var (u, err) = Caller();
            if (err != null) return err;
            if (!Request.HasFormContentType) return BadRequest(new { ok = false, error = "Send multipart/form-data." });
            var form = await Request.ReadFormAsync();
            IActionResult Bad(string msg) => BadRequest(new { ok = false, error = msg });

            string sourceId = form["sourceId"].ToString().Trim();
            string name = form["name"].ToString().Trim();
            bool adminOnly = form["adminOnly"].ToString() == "1";
            string replaceId = form["replaceId"].ToString().Trim();
            string hideHand = form["hideHand"].ToString().Trim();
            string hideHead = form["hideHead"].ToString().Trim(), hideBody = form["hideBody"].ToString().Trim();

            // name: printable ASCII (the site's plain-text rule), 2..64
            if (name.Length < 2 || name.Length > 64 || name.Any(c => c < 32 || c > 126))
                return Bad("The name must be 2-64 plain characters.");

            var src = CustomItemsCache.GetRec(sourceId);
            if (src == null || !CustomItemsCache.IsFirstParty(src)) return Bad("The source item isn't in the library.");

            // saves
            JArray saves;
            try { saves = JArray.Parse(form["saves"].ToString()); } catch { return Bad("Bad saves list."); }
            if (saves.Count < 1 || saves.Count > 3) return Bad("An item has 1-3 body types.");
            var srcRec = src.ToJObject();
            var srcSaves = (srcRec["CurrentSaves"] as JArray)?.OfType<JObject>().ToList() ?? new();
            var files = form.Files.ToDictionary(f => f.Name, f => f, StringComparer.Ordinal);
            var seenBt = new HashSet<int>();
            var plan = new List<(JObject save, IFormFile pc1, IFormFile pc2, IFormFile? q1, IFormFile? q2, IFormFile img)>();
            foreach (var t in saves.OfType<JObject>())
            {
                int bt = (int?)t["BodyType"] ?? -1;
                string a1 = (string?)t["UnityAsset"] ?? "", a2 = (string?)t["UnityAsset2"] ?? "", th = (string?)t["Thumbnail"] ?? "", aid = (string?)t["UnityAssetId"] ?? "";
                bool quest = (bool?)t["Quest"] ?? false;
                if (bt < 0 || bt > 2 || !seenBt.Add(bt)) return Bad("Bad or repeated body type.");
                if (!srcSaves.Any(s => ((int?)s["BodyType"] ?? 0) == bt)) return Bad($"The source item has no body type {bt}.");
                if (!BundleName.IsMatch(a1) || !BundleName.IsMatch(a2) || !ImageName.IsMatch(th) || !Guid.TryParse(aid, out _))
                    return Bad("Bad file names.");
                if (!files.TryGetValue("pc:" + a1, out var p1) || !files.TryGetValue("pc:" + a2, out var p2) || !files.TryGetValue("img:" + th, out var im))
                    return Bad($"Body type {bt}: files are missing.");
                files.TryGetValue("quest:" + a1, out var q1); files.TryGetValue("quest:" + a2, out var q2);
                if (quest && (q1 == null || q2 == null)) return Bad($"Body type {bt}: the Quest files are missing.");
                plan.Add((t, p1, p2, quest ? q1 : null, quest ? q2 : null, im));
            }

            // file checks: size, magic, never overwrite
            async Task<string?> Check(IFormFile f, bool image, string dir, string fname)
            {
                if (f.Length <= 0 || f.Length > (image ? MaxImage : MaxBundle)) return $"{fname}: bad size.";
                byte[] head = new byte[8];
                using (var s = f.OpenReadStream()) { int n = 0, r; while (n < 8 && (r = await s.ReadAsync(head, n, 8 - n)) > 0) n += r; }
                bool ok = image ? head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G'
                                : Encoding.ASCII.GetString(head, 0, 7) == "UnityFS";
                if (!ok) return $"{fname}: not a {(image ? "PNG" : "Unity bundle")}.";
                if (System.IO.File.Exists(Path.Combine(dir, fname))) return $"{fname}: already exists.";
                return null;
            }
            foreach (var p in plan)
            {
                string a1 = (string)p.save["UnityAsset"]!, a2 = (string)p.save["UnityAsset2"]!, th = (string)p.save["Thumbnail"]!;
                foreach (var (f, img, dir, fn) in new[] { (p.pc1, false, BlobDir, a1), (p.pc2, false, BlobDir, a2), (p.img, true, ImageDir, th) })
                { var e = await Check(f, img, dir, fn); if (e != null) return Bad(e); }
                if (p.q1 != null) { var e = await Check(p.q1, false, QuestDir, a1); if (e != null) return Bad(e); }
                if (p.q2 != null) { var e = await Check(p.q2, false, QuestDir, a2); if (e != null) return Bad(e); }
            }

            lock (_publishLock)
            {
                var meta = LoadMeta();
                string id;
                bool replacing = replaceId.Length > 0;
                if (replacing)
                {
                    var existing = CustomItemsCache.GetRec(replaceId);
                    JObject? existingRec = existing?.ToJObject();
                    if (existing == null || !IsStudioItem(replaceId, existingRec, meta))
                        return Bad("Only items made with CampusStudio can be updated.");
                    meta.Items.TryGetValue(replaceId, out var mi);
                    if (!IsOwnerOrDev(u!.Id) && (mi == null || mi.Creator != u.Id))
                        return StatusCode(403, new { ok = false, error = "You can only update items you made." });
                    id = existing.Id;
                }
                else id = Guid.NewGuid().ToString();

                // name must be unique in the library (except the item being updated)
                if (CustomItemsCache.StudioLibraryRecs().Any(r => !string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)
                                                                  && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return Bad($"An item called \"{name}\" already exists - pick another name.");

                // the record: the SOURCE's, with only names / ids / files / admin-only changed
                var rec = (JObject)srcRec.DeepClone();
                rec["Name"] = name;
                rec["CustomAvatarItemId"] = id;
                rec["RankedEntityId"] = id;
                rec["CreatedAt"] = CreatedAt;
                rec["ModifiedAt"] = CreatedAt;
                rec["IsFeatured"] = false;
                var tags = (rec["Tags"] as JArray)?.OfType<JObject>()
                    .Where(t => !string.Equals((string?)t["Value"], "adminonly", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals((string?)t["Value"], StudioTag, StringComparison.OrdinalIgnoreCase)).ToList() ?? new();
                tags.Add(new JObject { ["TagType"] = 0, ["Value"] = StudioTag });
                if (adminOnly) tags.Add(new JObject { ["TagType"] = 0, ["Value"] = "adminonly" });
                rec["Tags"] = new JArray(tags);

                if (meta.NextSaveId <= 0) meta.NextSaveId = MaxSaveId() + 1;
                var newSaves = new JArray();
                foreach (var p in plan)
                {
                    int bt = (int)p.save["BodyType"]!;
                    var s = (JObject)srcSaves.First(x => ((int?)x["BodyType"] ?? 0) == bt).DeepClone();
                    s["CustomAvatarItemId"] = id;
                    s["CustomAvatarItemSaveId"] = meta.NextSaveId++;
                    s["CreatedAt"] = CreatedAt;
                    s["ModifiedAt"] = CreatedAt;
                    s["UnityAsset"] = (string)p.save["UnityAsset"]!;
                    s["UnityAsset2"] = (string)p.save["UnityAsset2"]!;
                    s["UnityAssetId"] = (string)p.save["UnityAssetId"]!;
                    s["ThumbnailFileName"] = (string)p.save["Thumbnail"]!;
                    if (hideHand == "0" || hideHand == "1") SetHideHand(s, hideHand == "1");
                    if (hideHead == "1" || hideBody == "1") SetHideParts(s, hideHead == "1", hideBody == "1");
                    newSaves.Add(s);
                }
                rec["CurrentSaves"] = newSaves;

                // files first, then the record, so nothing is ever served without its bundles
                Directory.CreateDirectory(BlobDir); Directory.CreateDirectory(QuestDir); Directory.CreateDirectory(ImageDir);
                var written = new List<string>();
                try
                {
                    foreach (var p in plan)
                    {
                        string a1 = (string)p.save["UnityAsset"]!, a2 = (string)p.save["UnityAsset2"]!, th = (string)p.save["Thumbnail"]!;
                        Save(p.pc1, Path.Combine(BlobDir, a1), written);
                        Save(p.pc2, Path.Combine(BlobDir, a2), written);
                        if (p.q1 != null) Save(p.q1, Path.Combine(QuestDir, a1), written);
                        if (p.q2 != null) Save(p.q2, Path.Combine(QuestDir, a2), written);
                        Save(p.img, Path.Combine(ImageDir, th), written);
                    }
                    foreach (JObject s in newSaves)
                    {
                        s["UnityAssetHash"] = Sha(Path.Combine(BlobDir, (string)s["UnityAsset"]!));
                        s["UnityAsset2Hash"] = Sha(Path.Combine(BlobDir, (string)s["UnityAsset2"]!));
                    }
                    if (CustomItemsCache.AddStudio(rec) == null)
                        throw new Exception("the item was refused by the catalog");
                }
                catch (Exception ex)
                {
                    foreach (var w in written) try { System.IO.File.Delete(w); } catch { }
                    Console.WriteLine($"[studio] publish of '{name}' by @{u!.Username} failed: {ex.Message}");
                    return StatusCode(500, new { ok = false, error = "The server couldn't save the item: " + ex.Message });
                }

                var now = DateTime.UtcNow;
                meta.Items.TryGetValue(id, out var prev);
                meta.Items[id] = new MetaItem
                {
                    Creator = prev?.Creator > 0 ? prev.Creator : u!.Id,
                    CreatorName = prev?.CreatorName ?? u!.Username,
                    Source = src.Id,
                    Created = prev?.Created ?? now,
                    Updated = now,
                };
                SaveMeta(meta);
                Console.WriteLine($"[studio] @{u!.Username} ({u.Id}) {(replacing ? "updated" : "published")} '{name}' ({id}) from '{src.Name}': {plan.Count} body type(s), {written.Count} file(s){(adminOnly ? ", admin-only" : "")}");
                return Ok(new { ok = true, id, name, saves = plan.Count, updated = replacing, files = written.Count });
            }
        }

        /// <summary>
        /// Delete an item made with CampusStudio: POST /api/studio/v1/items/{id}/delete (or DELETE .../items/{id}).
        /// The person who made it can delete it; owner and dev accounts can delete any. It goes straight out of the
        /// catalog (live, no restart) and its bundle / thumbnail files are removed unless another item still uses them.
        /// Players who already own it simply stop seeing it (the locker skips ids that no longer exist).
        /// </summary>
        [HttpPost("/api/studio/v1/items/{id}/delete")]
        [HttpDelete("/api/studio/v1/items/{id}")]
        public IActionResult DeleteItem(string id)
        {
            var (u, err) = Caller();
            if (err != null) return err;
            lock (_publishLock)
            {
                var existing = CustomItemsCache.GetRec(id);
                if (existing == null || !CustomItemsCache.IsStudio(id))
                    return NotFound(new { ok = false, error = "That item wasn't made with CampusStudio, or it's already gone." });
                var meta = LoadMeta();
                meta.Items.TryGetValue(existing.Id, out var mi);
                if (!IsOwnerOrDev(u!.Id) && (mi == null || mi.Creator != u.Id))
                    return StatusCode(403, new { ok = false, error = "You can only delete items you made." });

                string name = existing.Name;
                var removed = CustomItemsCache.RemoveStudio(existing.Id);
                if (removed == null) return NotFound(new { ok = false, error = "That item is already gone." });
                meta.Items.Remove(existing.Id);
                SaveMeta(meta);

                // its files, unless another item (an older copy, a hand-made twin) still refers to them
                int files = 0;
                foreach (var s in (removed["CurrentSaves"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    foreach (var key in new[] { "UnityAsset", "UnityAsset2" })
                    {
                        string? n = (string?)s[key];
                        if (string.IsNullOrEmpty(n) || !BundleName.IsMatch(n) || CustomItemsCache.AnyRecordMentions(n)) continue;
                        foreach (var path in new[] { Path.Combine(BlobDir, n), Path.Combine(QuestDir, n) })
                            try { if (System.IO.File.Exists(path)) { System.IO.File.Delete(path); files++; } } catch { }
                    }
                    string? th = (string?)s["ThumbnailFileName"];
                    if (!string.IsNullOrEmpty(th) && ImageName.IsMatch(th) && !CustomItemsCache.AnyRecordMentions(th))
                        try { var p = Path.Combine(ImageDir, th); if (System.IO.File.Exists(p)) { System.IO.File.Delete(p); files++; } } catch { }
                }
                Console.WriteLine($"[studio] @{u.Username} ({u.Id}) deleted '{name}' ({existing.Id}){(mi != null && mi.Creator != u.Id ? $" made by @{mi.CreatorName}" : "")}: {files} file(s) removed");
                return Ok(new { ok = true, id = existing.Id, name, files });
            }
        }

        // "hh":1 in a save's AdditionalConfiguration makes the game hide the wearer's hand (full gloves
        // do it); "m" is the mask that goes with it (98304 on full gloves). Gauntlets and bands have neither.
        // Hide the wearer's head / body under the item. The save's config `d.m` is a bitmask of the body parts the game hides (hands 98304 for a
        // glove). Measured on the game's own items (36,712 saves): a helmet / head costume hides bit 0 + the ears (bits 20, 21) = 3145729; a suit of
        // armour that leaves the head out is 288230376147640846; the animal suits (everything) are 288230376151310863 = that + the head's bits.
        // Only ever ADDS bits to what the template already hides (a shirt must keep hiding its own torso).
        private const long HideHeadMask = 3145729L, HideBodyMask = 288230376147640846L, HideAllMask = 288230376151310863L;
        private static void SetHideParts(JObject save, bool head, bool body)
        {
            JObject cfg;
            try { cfg = JObject.Parse((string?)save["AdditionalConfiguration"] ?? "{}"); } catch { cfg = new JObject(); }
            if (cfg["d"] is not JObject d) { d = new JObject(); cfg["d"] = d; }
            long m = d["m"] != null && d["m"]!.Type == JTokenType.Integer ? (long)d["m"]! : 0L;
            m |= head && body ? HideAllMask : head ? HideHeadMask : HideBodyMask;
            d["m"] = m;
            save["AdditionalConfiguration"] = cfg.ToString(Formatting.None);
        }

        private static void SetHideHand(JObject save, bool hide)
        {
            JObject cfg;
            try { cfg = JObject.Parse((string?)save["AdditionalConfiguration"] ?? "{}"); } catch { cfg = new JObject(); }
            if (cfg["d"] is not JObject d) { d = new JObject(); cfg["d"] = d; }
            if (hide) { d["hh"] = 1; if (d["m"] == null) d["m"] = 98304; }
            else { d.Remove("hh"); d.Remove("m"); }
            save["AdditionalConfiguration"] = cfg.ToString(Formatting.None);
        }

        // ---- engine download ------------------------------------------------------------------

        // The app's engine (signed Python + UnityPy + engine.pyc), one zip per build, named by its
        // SHA-256 - CampusStudio.exe carries the hash and checks the download against it. It isn't
        // embedded in the exe because antivirus ML flags a big exe that unpacks programs and runs
        // them. Staged by CampusStudio\tools\pack_engine.py, uploaded by UploadUpdate.bat.
        private static readonly Regex EngineId = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
        private static string EngineDir => Path.Combine(Root, "Data", "Studio", "engine");

        [HttpGet("/api/studio/v1/engine/{id}")]
        public IActionResult Engine(string id)
        {
            var (_, err) = Caller();
            if (err != null) return err;
            if (!EngineId.IsMatch(id ?? "")) return BadRequest(new { ok = false, error = "Bad engine id." });
            string path = Path.Combine(EngineDir, id + ".zip");
            if (!System.IO.File.Exists(path))
                return NotFound(new { ok = false, error = "The server doesn't have this version of the Campus Studio engine yet - ask the owner to run UploadUpdate." });
            return PhysicalFile(path, "application/zip", enableRangeProcessing: true);
        }

        // ---- app updates ------------------------------------------------------------------------

        // Campus Studio updates itself: after sign-in it asks for the newest version and, when it's
        // newer than itself, downloads the exe by its SHA-256 and checks it. Data/Studio/app holds
        // latest.json ({version, build, sha256, size}) and <sha256>.exe, staged by
        // CampusStudio\tools\release_app.py and uploaded by UploadUpdate.bat (the exe first).
        private static string AppDir => Path.Combine(Root, "Data", "Studio", "app");

        [HttpGet("/api/studio/v1/app")]
        public IActionResult AppLatest()
        {
            var (_, err) = Caller();
            if (err != null) return err;
            try
            {
                var j = JObject.Parse(System.IO.File.ReadAllText(Path.Combine(AppDir, "latest.json")));
                string sha = (string?)j["sha256"] ?? "";
                if (!EngineId.IsMatch(sha) || !System.IO.File.Exists(Path.Combine(AppDir, sha + ".exe")))
                    return Ok(new { ok = true, build = 0 });            // nothing released (or the exe isn't up yet)
                return Ok(new { ok = true, version = (string?)j["version"], build = (int?)j["build"] ?? 0, sha256 = sha, size = (long?)j["size"] ?? 0 });
            }
            catch { return Ok(new { ok = true, build = 0 }); }
        }

        [HttpGet("/api/studio/v1/app/{id}")]
        public IActionResult AppDownload(string id)
        {
            var (_, err) = Caller();
            if (err != null) return err;
            if (!EngineId.IsMatch(id ?? "")) return BadRequest(new { ok = false, error = "Bad version id." });
            string path = Path.Combine(AppDir, id + ".exe");
            if (!System.IO.File.Exists(path))
                return NotFound(new { ok = false, error = "The server doesn't have this version of Campus Studio." });
            return PhysicalFile(path, "application/octet-stream", enableRangeProcessing: true);
        }

        // ---- restart ----------------------------------------------------------------------------

        private static readonly object _restartLock = new();
        private static readonly TimeSpan RestartCooldown = TimeSpan.FromMinutes(5);
        // On disk: the process that sets it is about to exit, so memory wouldn't survive the restart.
        private static string RestartStampPath => Path.Combine(Root, "Data", "APIS", "items", "StudioLastRestart.txt");
        private static DateTime LastRestart()
        {
            try { return DateTime.Parse(System.IO.File.ReadAllText(RestartStampPath).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// Restart the game server (owner's call: Studio users may do this). The process exits and
        /// systemd (Restart=always) brings it back in a few seconds - everyone online is dropped for
        /// ~15 s. At most once per 5 minutes, whoever asks, and every restart is logged with the account.
        /// </summary>
        [HttpPost("/api/studio/v1/restart")]
        public IActionResult Restart()
        {
            var (u, err) = Caller();
            if (err != null) return err;
            lock (_restartLock)
            {
                var since = DateTime.UtcNow - LastRestart();
                if (since < RestartCooldown)
                {
                    int wait = (int)Math.Ceiling((RestartCooldown - since).TotalSeconds);
                    return StatusCode(429, new { ok = false, error = $"The server was restarted less than 5 minutes ago - try again in {wait / 60}m {wait % 60}s." });
                }
                try { System.IO.File.WriteAllText(RestartStampPath, DateTime.UtcNow.ToString("o")); }
                catch (Exception ex) { return StatusCode(500, new { ok = false, error = "Couldn't record the restart: " + ex.Message }); }
            }
            Console.WriteLine($"[studio] @{u!.Username} ({u.Id}) restarted the game server from CampusStudio");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);   // let this response reach the app first
                Console.WriteLine("[studio] restart requested - exiting for systemd to relaunch");
                Environment.Exit(0);
            });
            return Ok(new { ok = true, message = "Restarting - the game server will be back in about 15 seconds." });
        }

        private static void Save(IFormFile f, string path, List<string> written)
        {
            string tmp = path + ".part";
            using (var fs = System.IO.File.Create(tmp)) f.CopyTo(fs);
            System.IO.File.Move(tmp, path, overwrite: false);
            written.Add(path);
        }

        private static string Sha(string path)
        {
            using var fs = System.IO.File.OpenRead(path);
            return Convert.ToBase64String(SHA256.HashData(fs));
        }

        // Highest CustomAvatarItemSaveId in the catalog (once; the meta file keeps the counter after).
        private static int MaxSaveId()
        {
            int max = 0;
            foreach (var r in CustomItemsCache.StudioLibraryRecs())
            {
                try
                {
                    if (CustomItemsCache.ParseRecord(r.Json)["CurrentSaves"] is JArray ss)
                        foreach (var s in ss) max = Math.Max(max, (int?)s["CustomAvatarItemSaveId"] ?? 0);
                }
                catch { }
            }
            return Math.Max(max, 5_000_000);   // well clear of every catalog save id (max ~102k)
        }
    }
}
