using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    // Real invention SAVE (Maker Pen "save selection as invention") + publish + my-list.
    // The save endpoints used to ack with the wrong shape, so the client reported
    // "Error uploading invention data". The client's decoder is lenient (it reads
    // Invention.InventionId + the Status/Success envelope and drops unknown fields), so a
    // correctly-STRUCTURED reply is what it needs. The circuit blob is uploaded separately and
    // referenced here by inventionDataFilename.
    [ApiController]
    public class InventionController : ControllerBase
    {
        private static ContentResult J(object o, int status = 200) =>
            new() { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = status };

        private async Task<JObject?> ReadBody()
        {
            try
            {
                using var r = new StreamReader(Request.Body);
                var s = await r.ReadToEndAsync();
                return string.IsNullOrWhiteSpace(s) ? new JObject() : JObject.Parse(s);
            }
            catch { return null; }
        }

        private static InventionDB.Invention BuildFromBody(ulong creator, JObject b)
        {
            string name = (b.Value<string>("name") ?? "").Trim(); if (name.Length == 0) name = "Untitled";
            string desc = (b.Value<string>("description") ?? "").Trim(); if (desc.Length == 0) desc = "No description yet";
            var inv = new InventionDB.Invention
            {
                CreatorPlayerId = creator,
                Name = name.Length > 24 ? name.Substring(0, 24) : name,
                Description = desc.Length > 512 ? desc.Substring(0, 512) : desc,
                ImageName = b.Value<string>("imageName") ?? "",
                BlobName = b.Value<string>("inventionDataFilename") ?? "",
                InstantiationCost = b.Value<int?>("instantiationCost") ?? 0,
                LightsCost = b.Value<int?>("lightsCost") ?? 0,
                ChipsCost = b.Value<int?>("chipsCost") ?? 0,
                CloudVariablesCost = b.Value<int?>("cloudVariablesCost") ?? 0,
                AICost = b.Value<int?>("aiCost") ?? 0,
                CreationRoomId = b.Value<int?>("creationRoomId") ?? 0,
                UgcVersion = b.Value<int?>("ugcVersion") ?? 0,
                HasBetaContent = b.Value<bool?>("hasBetaContent") ?? false,
                LongDescription = b.Value<string>("longDescription"),
                DisplayMetadataJson = b.Value<string>("displayMetadataJson"),
                ReferencedInventions = b["referencedInventions"]?.ToObject<List<int>>() ?? new(),
                ReferencedUnityAssetIds = b["referencedUnityAssetIds"]?.ToObject<List<string>>() ?? new(),
            };
            if (b["tagsRequest"] is JObject tr)
            {
                var custom = tr["CustomTags"]?.ToObject<List<string>>() ?? new();
                var auto = tr["AutoTags"]?.ToObject<List<string>>() ?? new();
                inv.Tags = custom.Concat(auto).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
            }
            return InventionDB.Create(inv);
        }

        // GET /api/inventions/v1/version?inventionId=N&version=V - the bare RRInventionVersion,
        // which carries the blob name the client downloads. The current version, or an earlier
        // one kept by addversion; a number never saved 404s rather than naming a missing blob. version=0 means "whichever is current". (Reference: api/routes/avatar.ts.)
        [HttpGet("/api/inventions/v1/version")]
        public ContentResult GetVersion([FromQuery] int inventionId = 0, [FromQuery] int version = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (inventionId <= 0) return J(new { error = "inventionId is required" }, 400);

            var inv = InventionDB.GetById(inventionId);
            if (inv == null) return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
            if (version != 0 && version != inv.CurrentVersionNumber)
            {
                // An earlier version (addversion keeps them): rooms saved with it still load it.
                var old = inv.VersionHistory?.LastOrDefault(v => v.VersionNumber == version);
                if (old == null) return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
                return J(new
                {
                    InventionId = inv.InventionId,
                    ReplicationId = inv.ReplicationId,
                    VersionNumber = old.VersionNumber,
                    BlobName = old.BlobName,
                    BlobHash = (string?)null,
                    InstantiationCost = old.InstantiationCost,
                    LightsCost = old.LightsCost,
                    ChipsCost = old.ChipsCost,
                    CloudVariablesCost = old.CloudVariablesCost,
                    AICost = old.AICost,
                    HasBetaContent = old.HasBetaContent,
                });
            }

            return J(VersionDto(inv));
        }

        private static object VersionDto(InventionDB.Invention i) => new
        {
            InventionId = i.InventionId,
            ReplicationId = i.ReplicationId,
            VersionNumber = i.CurrentVersionNumber,
            BlobName = i.BlobName,
            BlobHash = (string?)null,
            InstantiationCost = i.InstantiationCost,
            LightsCost = i.LightsCost,
            ChipsCost = i.ChipsCost,
            CloudVariablesCost = i.CloudVariablesCost,
            AICost = i.AICost,
            HasBetaContent = i.HasBetaContent,
        };

        // The stored-record shape the read endpoints (v2/mine) + v6 save envelope use.
        public static object InventionDto(InventionDB.Invention i) => new
        {
            InventionId = i.InventionId,
            ReplicationId = i.ReplicationId,
            CreatorPlayerId = i.CreatorPlayerId,
            Name = i.Name,
            Description = i.Description,
            ImageName = i.ImageName,
            CurrentVersionNumber = i.CurrentVersionNumber,
            CurrentVersion = VersionDto(i),
            Accessibility = i.Accessibility,
            IsPublished = i.IsPublished,
            IsFeatured = i.IsFeatured,
            ModifiedAt = i.ModifiedAt,
            CreatedAt = i.CreatedAt,
            FirstPublishedAt = i.FirstPublishedAt,
            CreationRoomId = i.CreationRoomId,
            NumPlayersHaveUsedInRoom = i.NumDownloads,
            NumDownloads = i.NumDownloads,
            CheerCount = i.CheerCount,
            // Permissions: KGCKBAIOBNL - Unassigned 0, LimitedOneUseOnly 10, DisallowKeyLock 15,
            // UseOnly 20, EditAndSave 40, Publish 60, Charge 80, Unlimited 100. Both were 0
            // (Unassigned), which is not a permission the maker pen will spawn from. The creator
            // gets Unlimited over their own invention; everyone else gets what publishing implies.
            CreatorPermission = 100,
            GeneralPermission = i.IsPublished ? 20 : 0,
            IsAGInvention = false,
            IsCertifiedInvention = false,
            Price = i.Price,
            AllowTrial = false,
            HideFromPlayer = false,
            ReferencedInventions = i.ReferencedInventions ?? new(),

            // The six keys the client's 28-key Invention DTO carries that this DTO never sent
            // (wire-shapes: inventions-v2-search). UgcVersion is the important one: the maker pen
            // palette filters its list by it, so an invention that arrives without one matches
            // neither CV1 nor CV2 and never appears in the Inventions tab. 0 is not a valid
            // version - default to 1 (CV1) for records saved before the field existed.
            UgcVersion = i.UgcVersion > 0 ? i.UgcVersion : 1,
            LatestVersionNumber = i.CurrentVersionNumber,
            LongDescription = i.LongDescription ?? "",
            ForceCannotPublish = false,
            IsRecRoomApproved = false,
            DisplayMetadataJson = i.DisplayMetadataJson,
        };

        private static object SaveResultV6(InventionDB.Invention i) => new
        {
            Status = 0,
            Invention = InventionDto(i),
            InventionVersion = VersionDto(i),
        };

        // POST /api/inventions/v6/save — JSON body (camelCase); returns { Status, Invention, InventionVersion }.
        [HttpPost("/api/inventions/v6/save")]
        public async Task<ContentResult> SaveV6()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(new { error = "Unauthorized" }, 401);
            var b = await ReadBody();
            if (b == null) return J(new { error = "Invalid request body" }, 400);
            var inv = BuildFromBody(account.Id, b);
            Console.WriteLine($"[invention] {account.Id} saved '{inv.Name}' (#{inv.InventionId}) blob={inv.BlobName}");
            return J(SaveResultV6(inv));
        }

        // POST /api/inventions/v9/save — same save, ENVELOPED { Value, Success, Error, error_id }.
        [HttpPost("/api/inventions/v9/save")]
        public async Task<ContentResult> SaveV9()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(new { Value = (object?)null, Success = false, Error = "Unauthorized", error_id = (string?)null });
            var b = await ReadBody();
            if (b == null) return J(new { Value = (object?)null, Success = false, Error = "Invalid request body", error_id = (string?)null });
            var inv = BuildFromBody(account.Id, b);
            Console.WriteLine($"[invention] {account.Id} saved (v9) '{inv.Name}' (#{inv.InventionId}) blob={inv.BlobName}");
            return J(new
            {
                Value = new { Status = 0, Invention = InventionDto(inv), InventionVersion = VersionDto(inv), TagsResponse = new { Result = 0, Tags = inv.Tags ?? new List<string>() } },
                Success = true,
                Error = "",
                error_id = (string?)null,
            });
        }

        // POST /api/inventions/v8/addversion - re-saving an invention you already own (edit it
        // with the Maker Pen, save): a NEW VERSION of the same invention rather than a new one.
        // It 404'd, so every edit of an existing invention failed to save. The body is the save
        // body plus the invention's id (field names read tolerantly - the first bodies are
        // logged so the exact spelling is on record); the new blob, costs and circuit info
        // replace the current version and the version number goes up by one, so clients holding
        // the old version re-download. Creator (or staff) only. Answers the v9 save envelope.
        [HttpPost("/api/inventions/v4/addversion")]
        [HttpPost("/api/inventions/v5/addversion")]
        [HttpPost("/api/inventions/v6/addversion")]
        [HttpPost("/api/inventions/v7/addversion")]
        [HttpPost("/api/inventions/v8/addversion")]
        [HttpPost("/api/inventions/v9/addversion")]
        public async Task<ContentResult> AddVersion()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(V9(null, "Unauthorized"));
            var b = await ReadBody();
            if (b == null) return J(V9(null, "Invalid request body"));
            if (System.Threading.Interlocked.Increment(ref _addVersionLogged) <= 5)
            {
                string raw = b.ToString(Formatting.None);
                Console.WriteLine($"[invention] addversion body: {(raw.Length > 600 ? raw[..600] + "..." : raw)}");
            }

            JToken? F(params string[] keys)
            {
                foreach (var k in keys)
                    foreach (var p in b.Properties())
                        if (string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase) && p.Value.Type != JTokenType.Null) return p.Value;
                return null;
            }
            int id = F("inventionId", "InventionId", "id")?.Value<int?>() ?? 0;
            if (id == 0 && int.TryParse(Request.Query["inventionId"], out var qid)) id = qid;
            string blob = F("inventionDataFilename", "blobName", "BlobName", "dataFilename")?.ToString() ?? "";
            if (id <= 0) return J(V9(null, "inventionId is required"));
            if (blob.Length == 0) return J(V9(null, "inventionDataFilename is required"));

            var inv = InventionStore.Edit(id, account.Id, i =>
            {
                (i.VersionHistory ??= new()).Add(new InventionDB.OldVersion
                {
                    VersionNumber = Math.Max(1, i.CurrentVersionNumber), BlobName = i.BlobName,
                    InstantiationCost = i.InstantiationCost, LightsCost = i.LightsCost, ChipsCost = i.ChipsCost,
                    CloudVariablesCost = i.CloudVariablesCost, AICost = i.AICost, HasBetaContent = i.HasBetaContent,
                });
                i.BlobName = blob;
                i.CurrentVersionNumber = Math.Max(1, i.CurrentVersionNumber) + 1;
                int? N(params string[] k) => F(k)?.Value<int?>();
                i.InstantiationCost = N("instantiationCost") ?? i.InstantiationCost;
                i.LightsCost = N("lightsCost") ?? i.LightsCost;
                i.ChipsCost = N("chipsCost") ?? i.ChipsCost;
                i.CloudVariablesCost = N("cloudVariablesCost") ?? i.CloudVariablesCost;
                i.AICost = N("aiCost") ?? i.AICost;
                i.UgcVersion = N("ugcVersion") ?? i.UgcVersion;
                i.HasBetaContent = F("hasBetaContent")?.Value<bool?>() ?? i.HasBetaContent;
                var img = F("imageName")?.ToString();
                if (!string.IsNullOrEmpty(img)) i.ImageName = img;
                if (F("referencedInventions") is JArray ri) i.ReferencedInventions = ri.ToObject<List<int>>() ?? new();
                if (F("referencedUnityAssetIds") is JArray ra) i.ReferencedUnityAssetIds = ra.ToObject<List<string>>() ?? new();
                var dm = F("displayMetadataJson")?.ToString();
                if (dm != null) i.DisplayMetadataJson = dm;
            }, Staff(account.Id));
            if (inv == null)
            {
                Console.WriteLine($"[invention] {account.Id} addversion #{id} refused: no such invention, or not theirs");
                return J(V9(null, "No such invention, or not yours"));
            }
            Console.WriteLine($"[invention] {account.Id} saved version {inv.CurrentVersionNumber} of #{id} '{inv.Name}' blob={inv.BlobName}");
            return J(V9(new { Status = 0, Invention = InventionDto(inv), InventionVersion = VersionDto(inv), TagsResponse = new { Result = 0, Tags = inv.Tags ?? new List<string>() } }));
        }
        private static int _addVersionLogged;

        // POST /api/storefronts/v2/trialInvention - "Try it" on an invention in the store. It
        // 404'd (12 times in five days). Trying is free and grants nothing permanent: the client
        // spawns the current version it is handed. Answered like buyInvention (same pair), with
        // no charge and no ownership recorded. Body logged once so its fields are on record.
        [HttpPost("/api/storefronts/v2/trialInvention")]
        [HttpPost("/api/storefronts/v3/trialInvention")]
        public async Task<ContentResult> TrialInvention()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var b = await ReadBody() ?? new JObject();
            if (System.Threading.Interlocked.Increment(ref _trialLogged) <= 5)
                Console.WriteLine($"[invention] trialInvention body: {b.ToString(Formatting.None)} query: {Request.QueryString}");
            int id = b.Value<int?>("InventionId") ?? b.Value<int?>("inventionId") ?? 0;
            if (id == 0 && int.TryParse(Request.Query["inventionId"], out var qid)) id = qid;
            var inv = id > 0 ? InventionDB.GetById(id) : null;
            if (inv == null || (!inv.IsPublished && inv.CreatorPlayerId != account.Id))
                return J(new { InventionResponse = V9(null, "No such invention"), BalanceUpdateResponse = (object?)null });
            const int Tokens = (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens;
            return J(new
            {
                InventionResponse = V9(new { Status = 0, Invention = InventionDto(inv), InventionVersion = VersionDto(inv), TagsResponse = new { Result = 0, Tags = inv.Tags ?? new List<string>() } }),
                BalanceUpdateResponse = new
                {
                    BalanceUpdates = Array.Empty<object>(),
                    Balance = PlayerDB.GetBalance(account.Id, Tokens),
                    CurrencyType = Tokens,
                    Platform = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket
                }
            });
        }
        private static int _trialLogged;

        // GET /api/inventions/v1/details?inventionId=X — the invention's tags card. Fetched right
        // after a save to open the invention; was 404ing. Returns { Tags:[{Tag,Type}] } (404 if none).
        [HttpGet("/api/inventions/v1/details")]
        public ContentResult Details()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (!int.TryParse(Request.Query["inventionId"], out var id))
                return J(new { error = "inventionId is required" }, 400);
            var inv = InventionDB.GetById(id);
            if (inv == null) return J(new { error = "No such invention" }, 404);
            var tags = (inv.Tags ?? new List<string>()).Select(t => new { Tag = t, Type = 0 }).ToList();
            return J(new { Tags = tags });
        }

        private static object V9(object? value, string? error = null) => new
        {
            Value = value, Success = error == null, Error = error ?? "", error_id = (string?)null
        };

        private static bool Staff(ulong id) => PlayerDB.CheckDevFlag(id) || PlayerDB.CheckModFlag(id);

        // ---- browse / store -----------------------------------------------------------------
        // Every one of these answered "[]" (search 468 times a day), so the invention store, the
        // "inventions in this room" list and every batch lookup (room offers, wishlists, nested
        // inventions) came up empty even with inventions published.

        // GET /api/inventions/v2/search?value=&ugcVersion=&skip=&take= - bare array (wire-shapes:
        // inventions-v2-search). "#name" is a tag or a creator's @name.
        [HttpGet("/api/inventions/v2/search")]
        public ContentResult Search([FromQuery] string? value, [FromQuery] int? ugcVersion, [FromQuery] int skip = 0, [FromQuery] int take = 100)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var hits = InventionStore.Search(value, skip, take);
            if (ugcVersion.HasValue && ugcVersion.Value > 0)
                hits = hits.Where(i => (i.UgcVersion > 0 ? i.UgcVersion : 1) == ugcVersion.Value).ToList();
            return J(hits.Select(InventionDto).ToList());
        }

        // GET /api/inventions/v2/batch?id=1&id=2 - the ones the caller may see (published, or theirs).
        [HttpGet("/api/inventions/v2/batch")]
        public ContentResult Batch([FromQuery] List<long> id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var ids = (id ?? new()).Where(x => x > 0 && x <= int.MaxValue).Select(x => (int)x);
            var owned = InventionStore.OwnedIds(account.Id);
            var list = InventionStore.GetByIds(ids)
                .Where(i => i.IsPublished || i.CreatorPlayerId == account.Id || owned.Contains(i.InventionId) || Staff(account.Id))
                .Select(InventionDto).ToList();
            return J(list);
        }

        // GET /api/inventions/v2/mine - created + bought, newest first.
        [HttpGet("/api/inventions/v2/mine")]
        public ContentResult Mine()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return J(InventionStore.Mine(account.Id).Select(InventionDto).ToList());
        }

        // GET /api/inventions/v1/room?id=N - published inventions made in that room.
        [HttpGet("/api/inventions/v1/room")]
        public ContentResult ByRoom([FromQuery] long id, [FromQuery] int skip = 0, [FromQuery] int take = 100)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return J(InventionStore.ByRoom(id).Skip(Math.Max(0, skip)).Take(take <= 0 ? 100 : take).Select(InventionDto).ToList());
        }

        [HttpGet("/api/inventions/v1/featured")]
        [HttpGet("/api/inventions/v1/toptoday")]
        public ContentResult Featured([FromQuery] int skip = 0, [FromQuery] int take = 50)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return J(InventionStore.Top(skip, take).Select(InventionDto).ToList());
        }

        // GET /api/inventions/v1/personaldetails/{id} - the caller's own relation (cheering?). Was 404.
        [HttpGet("/api/inventions/v1/personaldetails/{inventionId:int}")]
        public ContentResult PersonalDetails(int inventionId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            ulong me = error == null && account != null ? account.Id : 0;
            return J(new { IsCheering = me != 0 && InventionStore.IsCheering(me, inventionId), IsOwned = me != 0 && InventionStore.Owns(me, inventionId) });
        }

        // GET /api/inventions/v1/fulllineageowner?id=1&id=2 - does the caller own every piece? bare bool.
        [HttpGet("/api/inventions/v1/fulllineageowner")]
        public ContentResult FullLineageOwner([FromQuery] List<string> id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(false);
            var ids = (id ?? new()).SelectMany(x => x.Split(',')).Select(x => int.TryParse(x.Trim(), out var n) ? n : 0).Where(n => n > 0).ToList();
            return J(ids.Count > 0 && InventionStore.OwnsAll(account.Id, ids));
        }

        // POST /api/inventions/v2/cheer - {InventionId, Cheer?} (form or JSON). Toggles when no flag.
        [HttpPost("/api/inventions/v2/cheer")]
        [HttpPost("/api/inventions/v1/cheer")]
        public ContentResult CheerInvention()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = Campus_live_rewrite.api.server.RoomModeration.Fields(Request);
            int id = (int)Campus_live_rewrite.api.server.RoomModeration.Long(f, "InventionId", "inventionId", "id");
            string flag = Campus_live_rewrite.api.server.RoomModeration.Str(f, "Cheer", "cheer", "IsCheering", "value") ?? "";
            bool cheer = flag.Length == 0 ? !InventionStore.IsCheering(account.Id, id) : (flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag == "1");
            var inv = InventionStore.SetCheer(account.Id, id, cheer);
            if (inv == null) return J(V9(null, "No such invention"));
            return J(V9(new { Status = 0, Invention = InventionDto(inv), InventionVersion = VersionDto(inv), IsCheering = cheer, CheerCount = inv.CheerCount }));
        }

        // ---- creator writes -----------------------------------------------------------------

        // POST /api/inventions/v1/updateprice {InventionId, Price} -> the v6 save result.
        [HttpPost("/api/inventions/v1/updateprice")]
        [HttpPost("/api/inventions/v2/updateprice")]
        public async Task<ContentResult> UpdatePrice()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var b = await ReadBody() ?? new JObject();
            int id = b.Value<int?>("InventionId") ?? b.Value<int?>("inventionId") ?? 0;
            if (id == 0 && int.TryParse(Request.Query["inventionId"], out var qid)) id = qid;
            int price = b.Value<int?>("Price") ?? b.Value<int?>("price") ?? (int.TryParse(Request.Query["price"], out var qp) ? qp : -1);
            if (price < 0) return J(new { error = "Price must be >= 0" }, 400);
            var inv = InventionStore.Edit(id, account.Id, i => { i.Price = price; }, Staff(account.Id));
            if (inv == null) return J(new { error = "No such invention" }, 404);
            Console.WriteLine($"[invention] {account.Id} priced #{id} at {price}");
            return J(SaveResultV6(inv));
        }

        // PUT /api/inventions/v2/metadata {InventionId, Name?, Description?, LongDescription?, ImageName?, TagsRequest?} -> v9 envelope.
        [HttpPut("/api/inventions/v2/metadata")]
        [HttpPost("/api/inventions/v2/metadata")]
        [HttpPut("/api/inventions/v2/update")]     // the editor's "save details" - same body (was a 404)
        [HttpPost("/api/inventions/v2/update")]
        public async Task<ContentResult> Metadata()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(V9(null, "Unauthorized"), 401);
            var b = await ReadBody();
            if (b == null) return J(V9(null, "Invalid request body"));
            int id = b.Value<int?>("InventionId") ?? b.Value<int?>("inventionId") ?? 0;
            var inv = InventionStore.Edit(id, account.Id, i =>
            {
                string? S(string k) => b[k]?.Type == JTokenType.String ? b.Value<string>(k) : null;
                var name = (S("Name") ?? S("name"))?.Trim();
                if (!string.IsNullOrEmpty(name)) i.Name = name.Length > 24 ? name[..24] : name;
                var desc = S("Description") ?? S("description");
                if (desc != null) i.Description = desc.Length > 512 ? desc[..512] : desc;
                var ld = S("LongDescription") ?? S("longDescription");
                if (ld != null) i.LongDescription = ld;
                var img = S("ImageName") ?? S("imageName");
                if (!string.IsNullOrEmpty(img)) i.ImageName = img;
                var dm = S("DisplayMetadataJson") ?? S("displayMetadataJson");
                if (dm != null) i.DisplayMetadataJson = dm;
                var price = b.Value<int?>("Price") ?? b.Value<int?>("price");
                if (price is >= 0) i.Price = price.Value;
                if ((b["TagsRequest"] ?? b["tagsRequest"]) is JObject tr)
                {
                    var custom = tr["CustomTags"]?.ToObject<List<string>>() ?? new();
                    var auto = tr["AutoTags"]?.ToObject<List<string>>() ?? new();
                    i.Tags = custom.Concat(auto).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
                }
            }, Staff(account.Id));
            if (inv == null) return J(V9(null, "No such invention, or not yours"));
            return J(V9(new { Status = 0, Invention = InventionDto(inv), InventionVersion = VersionDto(inv), TagsResponse = new { Result = 0, Tags = inv.Tags ?? new List<string>() } }));
        }

        // POST /api/inventions/v1/settags {InventionId, CustomTags[], AutoTags[]} -> {Result, Tags}.
        [HttpPost("/api/inventions/v1/settags")]
        public async Task<ContentResult> SetTags()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var b = await ReadBody() ?? new JObject();
            int id = b.Value<int?>("InventionId") ?? b.Value<int?>("inventionId") ?? 0;
            var custom = (b["CustomTags"] ?? b["customTags"])?.ToObject<List<string>>() ?? new();
            var auto = (b["AutoTags"] ?? b["autoTags"])?.ToObject<List<string>>() ?? new();
            var inv = InventionStore.Edit(id, account.Id, i => { i.Tags = auto.Concat(custom).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList(); }, Staff(account.Id));
            if (inv == null) return J(new { Result = 2, Tags = new List<string>() });
            return J(new { Result = 0, Tags = inv.Tags ?? new List<string>() });
        }

        // GET /api/inventions/v1?inventionId= - one invention, bare (reference). Unpublished ones
        // are served too, the way the reference does.
        [HttpGet("/api/inventions/v1")]
        public ContentResult One([FromQuery] int inventionId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var inv = inventionId > 0 ? InventionStore.GetByIds(new[] { inventionId }).FirstOrDefault() : null;
            if (inv == null) return J(new { error = "No such invention" }, 404);
            return J(InventionDto(inv));
        }

        // GET|POST /api/inventions/v1/update - the older edit: the fields ride as QUERY params
        // (inventionId, name, description, imageName); absent ones keep their value, an empty
        // description clears it, an empty name / imageName is ignored (reference).
        [HttpGet("/api/inventions/v1/update")]
        [HttpPost("/api/inventions/v1/update")]
        public ContentResult UpdateV1()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = RoomModeration.Fields(Request);
            int id = (int)RoomModeration.Long(f, "inventionId", "InventionId", "id");
            var inv = InventionStore.Edit(id, account.Id, i =>
            {
                string? name = RoomModeration.Str(f, "name", "Name")?.Trim();
                if (!string.IsNullOrEmpty(name)) i.Name = name.Length > 24 ? name[..24] : name;
                if (f.TryGetValue("description", out var d) || f.TryGetValue("Description", out d)) i.Description = (d ?? "").Length > 512 ? d![..512] : (d ?? "");
                string? img = RoomModeration.Str(f, "imageName", "ImageName");
                if (!string.IsNullOrEmpty(img)) i.ImageName = img;
            }, Staff(account.Id));
            if (inv == null) return J(new { Success = false, Error = "No such invention, or not yours" });
            return J(InventionDto(inv));
        }

        // POST /api/inventions/v1/report - filed against the invention's CREATOR (reference).
        [HttpPost("/api/inventions/v1/report")]
        public async Task<ContentResult> Report()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var b = await ReadBody() ?? new JObject();
            var f = RoomModeration.Fields(Request);
            int id = b.Value<int?>("InventionId") ?? b.Value<int?>("inventionId") ?? (int)RoomModeration.Long(f, "InventionId", "inventionId");
            var inv = id > 0 ? InventionStore.GetByIds(new[] { id }).FirstOrDefault() : null;
            if (inv == null) return J(new { success = false, error = "No such invention" });
            int category = b.Value<int?>("ReportCategory") ?? (int)RoomModeration.Long(f, "ReportCategory");
            string details = b.Value<string>("Details") ?? b.Value<string>("ReportDescription") ?? RoomModeration.Str(f, "Details", "ReportDescription") ?? "";
            Campus_live_rewrite.api.dbs.ReportsDB.Create(new Campus_live_rewrite.api.dbs.ReportsDB.Report
            {
                ReporterPlayerId = account.Id,
                ReportedPlayerId = inv.CreatorPlayerId,
                ReportCategory = category,
                Details = $"[invention {inv.InventionId} '{inv.Name}'] {details}".Trim(),
            });
            Console.WriteLine($"[report] {account.Id} reported invention {inv.InventionId} by {inv.CreatorPlayerId}");
            return J(new { success = true, error = "" });
        }

        // POST /api/inventions/v2/unpublish | v1/unpublish {InventionId} -> v9 envelope.
        [HttpPost("/api/inventions/v2/unpublish")]
        [HttpPost("/api/inventions/v1/unpublish")]
        public async Task<ContentResult> Unpublish()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(V9(null, "Unauthorized"), 401);
            var b = await ReadBody() ?? new JObject();
            int id = b.Value<int?>("InventionId") ?? b.Value<int?>("inventionId") ?? 0;
            if (id == 0 && int.TryParse(Request.Query["inventionId"], out var qid)) id = qid;
            var inv = InventionStore.Edit(id, account.Id, i => { i.IsPublished = false; i.Accessibility = 0; }, Staff(account.Id));
            if (inv == null) return J(V9(null, "No such invention"));
            Console.WriteLine($"[invention] {account.Id} unpublished #{id}");
            return J(V9(SaveResultV6(inv)));
        }

        // POST /api/inventions/v2/accessibility {InventionId, Accessibility} (0 private, 1 public, 2 unlisted).
        [HttpPost("/api/inventions/v2/accessibility")]
        [HttpPut("/api/inventions/v2/accessibility")]
        public async Task<ContentResult> SetAccessibility()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(V9(null, "Unauthorized"), 401);
            var b = await ReadBody() ?? new JObject();
            int id = b.Value<int?>("InventionId") ?? b.Value<int?>("inventionId") ?? 0;
            int acc = b.Value<int?>("Accessibility") ?? b.Value<int?>("accessibility") ?? 0;
            var inv = InventionStore.Edit(id, account.Id, i =>
            {
                i.Accessibility = acc;
                i.IsPublished = acc != 0;
                if (i.IsPublished && i.FirstPublishedAt == null) i.FirstPublishedAt = DateTime.UtcNow;
            }, Staff(account.Id));
            if (inv == null) return J(V9(null, "No such invention"));
            return J(V9(SaveResultV6(inv)));
        }

        // POST /api/inventions/v2/delete {InventionId} -> {Value:null, Success, Error, error_id}.
        [HttpPost("/api/inventions/v2/delete")]
        [HttpDelete("/api/inventions/v2/delete")]
        public async Task<ContentResult> DeleteInvention()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(V9(null, "Unauthorized"), 401);
            var b = await ReadBody() ?? new JObject();
            int id = b.Value<int?>("InventionId") ?? b.Value<int?>("inventionId") ?? 0;
            if (id == 0 && int.TryParse(Request.Query["inventionId"], out var qid)) id = qid;
            bool ok = InventionStore.Delete(id, account.Id, Staff(account.Id));
            Console.WriteLine($"[invention] {account.Id} delete #{id}: {(ok ? "deleted" : "refused")}");
            return J(V9(null, ok ? null : "No such invention, or not yours"));
        }

        // POST /api/inventions/v4/publish - the same publish, v9 envelope.
        [HttpPost("/api/inventions/v4/publish")]
        public Task<ContentResult> PublishV4() => PublishV3();

        // ---- buying ---------------------------------------------------------------------------
        // POST /api/storefronts/v3/buyInvention {InventionId, RequestedPrice} (2025) and
        // GET /api/storefronts/v2/buyInvention?inventionId=&requestedPrice= (2023). Charges Rec
        // Center Tokens at the STORED price, records ownership (it lands in v2/mine and counts as
        // a download) and answers the bare {InventionResponse, BalanceUpdateResponse} pair
        // (wire-shapes: storefronts-v3-buyinvention). Neither route existed - the 2025 one 404'd.
        [HttpPost("/api/storefronts/v3/buyInvention")]
        [HttpGet("/api/storefronts/v2/buyInvention")]
        [HttpPost("/api/storefronts/v2/buyInvention")]
        public async Task<ContentResult> BuyInvention()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var b = await ReadBody() ?? new JObject();
            int id = b.Value<int?>("InventionId") ?? b.Value<int?>("inventionId") ?? 0;
            if (id == 0 && int.TryParse(Request.Query["inventionId"], out var qid)) id = qid;
            int requested = b.Value<int?>("RequestedPrice") ?? b.Value<int?>("requestedPrice") ?? (int.TryParse(Request.Query["requestedPrice"], out var qp) ? qp : 0);
            const int Tokens = (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens;
            long balance = PlayerDB.GetBalance(account.Id, Tokens);

            ContentResult Refuse(string why, int status = 200)
            {
                Console.WriteLine($"[invention-buy] {account.Id} #{id}: {why}");
                return J(new { InventionResponse = V9(null, why), BalanceUpdateResponse = (object?)null }, status);
            }

            var inv = InventionDB.GetById(id);
            if (inv == null) return Refuse("No such invention");
            if (!inv.IsPublished && inv.CreatorPlayerId != account.Id) return Refuse("This invention is not for sale");
            if (inv.CreatorPlayerId == account.Id) return Refuse("You already own your own invention");
            if (InventionStore.Owns(account.Id, id)) return Refuse("Already owned");
            int price = Math.Max(0, inv.Price);
            if (requested != price)
                Console.WriteLine($"[invention-buy] {account.Id} #{id}: client rendered {requested}, charging the stored {price}");
            if (price > 0 && balance < price) return Refuse("Not enough tokens");
            // Claim the invention FIRST (it is atomic): a second, simultaneous buy now loses here instead of being
            // charged again and then failing on the duplicate key (2026-10-02: an unhandled LiteException).
            if (!InventionStore.AddOwned(account.Id, id, price)) return Refuse("Already owned");
            if (price > 0) balance = PlayerDB.AddBalance(account.Id, Tokens, -price);
            EarningsDB.Record(inv.CreatorPlayerId, account.Id, "invention", id.ToString(), inv.Name, Tokens, price);
            inv = InventionDB.GetById(id) ?? inv;
            Campus_live_rewrite.api.server.GiftBoxes.PushBalance(account.Id, Tokens, balance, "invention purchase");
            Console.WriteLine($"[invention-buy] @{account.Username} bought #{id} '{inv.Name}' for {price} -> balance {balance}");
            return J(new
            {
                InventionResponse = V9(new { Status = 0, Invention = InventionDto(inv), InventionVersion = VersionDto(inv), TagsResponse = new { Result = 0, Tags = inv.Tags ?? new List<string>() } }),
                BalanceUpdateResponse = new
                {
                    BalanceUpdates = new[] { new { UpdateResponse = 0, Data = InventionDto(inv) } },
                    Balance = balance,
                    CurrencyType = Tokens,
                    Platform = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket
                }
            });
        }

        // POST /api/inventions/v3/publish — flip the invention public. Body/query carries the id.
        [HttpPost("/api/inventions/v3/publish")]
        public async Task<ContentResult> PublishV3()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(new { Value = (object?)null, Success = false, Error = "Unauthorized", error_id = (string?)null });
            var b = await ReadBody() ?? new JObject();
            int id = b.Value<int?>("inventionId") ?? b.Value<int?>("InventionId") ?? 0;
            if (id == 0 && int.TryParse(Request.Query["inventionId"], out var qid)) id = qid;
            var inv = InventionDB.Publish(id, account.Id, true);
            if (inv == null) return J(new { Value = (object?)null, Success = false, Error = "No such invention", error_id = (string?)null });
            Console.WriteLine($"[invention] {account.Id} published #{id}");
            return J(new { Value = SaveResultV6(inv), Success = true, Error = "", error_id = (string?)null });
        }
    }
}
