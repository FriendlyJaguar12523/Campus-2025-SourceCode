using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.dbs;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Safe stubs for endpoints the 2025 client calls that had NO handler (live 404s), found by
    /// diffing the client's full endpoint list against the server's routes (2026-09-10). Each
    /// returns the most conservative valid shape for its kind - an empty list, a scalar, or the
    /// { error, success, value } ack envelope the rest of this codebase uses - so a missing panel
    /// loads empty instead of erroring. Handlers here are intentionally param-less (no [FromBody])
    /// so a non-JSON/empty body can't 415/400 under [ApiController]. Ones that need REAL behaviour
    /// later (purchases, invention publishing, room-setting writes) are grouped + noted.
    /// </summary>
    [ApiController]
    public class GapEndpointsController : ControllerBase
    {
        private static ContentResult Json(string s) => new() { Content = s, ContentType = "application/json", StatusCode = 200 };
        private static ContentResult EmptyArray() => Json("[]");
        private IActionResult Ack() => Ok(new { error = "", success = true, value = (object?)null });

        // ---------------- Room Settings tabs (Game Roles / Moderation / Instances / Earnings) --
        // These back the "This Room → Room Settings" tabs and were live 404s. No custom role /
        // ban / earnings-split storage yet, so they answer the conservative empty shape so the
        // tab loads instead of erroring.
        // NOTE: rooms/{id}/roles + roles/{pid}/invite are now REAL (persist + return roles) in
        // RoomController - removed the empty-array/ack stubs here to avoid duplicate routes.
        // Earnings distribution: an object (not a list), so the Earnings tab can render "distribute
        // equally = off, no splits, 100% unallocated" rather than showing "Unable to retrieve…".
        [HttpGet("/api/roomEarningsDistributions/v1/earningsDistribution/{roomId}")]
        public ContentResult RoomEarningsDistribution(long roomId) => Json(
            "{\"RoomId\":" + roomId + ",\"DistributeEarningsEqually\":false,\"Distributions\":[],\"EarningsDistributions\":[],\"TotalPercentage\":0}");

        // Keepsake categories — the client parses this as a RESULT SET object, not a list, and
        // chokes on an array. Empty catalog.
        [HttpGet("/api/keepsakes/categories")] public ContentResult KeepsakeCategories() => Json("{\"Results\":[],\"TotalResults\":0}");

        // ---------------- Rooms discovery lists (empty carousels are safe) ----------------
        [HttpGet("/Room_server/rooms/contestwinners")]   public ContentResult RoomsContestWinners()   => EmptyArray();
        [HttpGet("/Room_server/rooms/curated_playlists")] public ContentResult RoomsCuratedPlaylists() => EmptyArray();
        [HttpGet("/Room_server/rooms/topcreators")]      public ContentResult RoomsTopCreators()      => EmptyArray();
        [HttpGet("/Room_server/rooms/magic_door")]       public ContentResult RoomsMagicDoor()        => EmptyArray();
        [HttpGet("/Room_server/rooms/rro_ids")]          public ContentResult RoomsRroIds()           => EmptyArray();
        [HttpGet("/Room_server/rooms/{roomId}/promo_images")]   public ContentResult RoomPromoImages(string roomId)   => EmptyArray();
        [HttpGet("/Room_server/rooms/{roomId}/promo_external")] public ContentResult RoomPromoExternal(string roomId) => Json("{}");

        // ---------------- Room setting writes (ack; real persistence is TODO) ----------------
        [HttpPut("/Room_server/rooms/{roomId}/allow_new_users")]  [HttpPost("/Room_server/rooms/{roomId}/allow_new_users")]
        public IActionResult RoomAllowNewUsers(string roomId) => Ack();
        [HttpPut("/Room_server/rooms/{roomId}/automute")]  [HttpPost("/Room_server/rooms/{roomId}/automute")]
        public IActionResult RoomAutomute(string roomId) => Ack();
        // max_player_calculation_mode: real now - RoomController.SetMaxPlayerCalculationMode.
        [HttpPut("/Room_server/rooms/{roomId}/voice_chatencryption")]  [HttpPost("/Room_server/rooms/{roomId}/voice_chatencryption")]
        public IActionResult RoomVoiceEncryption(string roomId) => Ack();
        [HttpPost("/Room_server/rooms/{roomId}/requestManualModeration")]
        public IActionResult RoomRequestManualMod(string roomId) => Ack();
        [HttpPost("/Room_server/rooms/{roomId}/bans/import")]
        public IActionResult RoomBansImport(string roomId) => Ack();

        // ---------------- Account ----------------
        [HttpGet("/Accounts/account/me/haspassword")]  public ContentResult HasPassword() => Json("true");
        [HttpPost("/Accounts/account/me/createlogintoken")]
        public IActionResult CreateLoginToken() => Ok(new { token = System.Guid.NewGuid().ToString("N"), success = true });
        [HttpPost("/Accounts/account/recoverpassword")]  public IActionResult RecoverPassword() => Ack();

        // ---------------- Objectives / consumables / images ----------------
        [HttpPost("/api/objectives/v1/completegroup")]
        // The group's REAL state. It answered isCompleted=true for anyone; completion (and the daily
        // game request) is decided server-side in updateobjective, never by this call.
        public IActionResult CompleteObjectivesGroup()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var g = Campus_live_rewrite.api.dbs.PlayerDB.GetObjectivesProgress(account.Id).groups.FirstOrDefault(x => x.Group == 0);
            return Ok(new { group = 0, isCompleted = g?.IsCompleted ?? false, clearedAt = g?.ClearedAt ?? System.DateTime.UtcNow.ToString("o") });
        }
        [HttpPost("/api/consumables/v1/transfer")]      public IActionResult ConsumablesTransfer() => Ack();
        [HttpGet("/api/images/v1/listsaved")]           public ContentResult ImagesListSaved() => EmptyArray();
        // ---- photo edits: were acks that did nothing, so a deleted photo came back and a photo
        // made private stayed public. The field names the client uses were never captured, so
        // every plausible spelling is read (form, query or JSON) and the raw keys are logged the
        // first few times so the real ones name themselves.
        private static int _imgLogged;
        private async Task<(int id, string? accessibility, string? description, List<int> ids)> ReadImageEdit()
        {
            var keys = new List<string>();
            string Get(params string[] names)
            {
                foreach (var n in names)
                {
                    string v = Request.HasFormContentType ? Request.Form[n].ToString() : "";
                    if (string.IsNullOrEmpty(v)) v = Request.Query[n].ToString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                return "";
            }
            Newtonsoft.Json.Linq.JObject? body = null;
            if (!Request.HasFormContentType)
            {
                try
                {
                    using var r = new StreamReader(Request.Body);
                    string raw = await r.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(raw)) body = Newtonsoft.Json.Linq.JObject.Parse(raw);
                }
                catch { }
            }
            string J(params string[] names)
            {
                if (body == null) return "";
                foreach (var n in names)
                {
                    var p = body.Properties().FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                    if (p != null && p.Value.Type != Newtonsoft.Json.Linq.JTokenType.Null) return p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Array ? string.Join(",", p.Value) : p.Value.ToString();
                }
                return "";
            }
            if (Request.HasFormContentType) keys.AddRange(Request.Form.Keys);
            if (body != null) keys.AddRange(body.Properties().Select(p => p.Name));
            if (_imgLogged++ < 5) Console.WriteLine($"[images] {Request.Path} fields=[{string.Join(",", keys)}]");

            string idRaw = Get("id", "imageId", "savedImageId", "SavedImageId", "ImageId", "Id");
            if (idRaw == "") idRaw = J("id", "imageId", "savedImageId", "ImageId");
            string idsRaw = Get("ids", "imageIds", "savedImageIds", "SavedImageIds");
            if (idsRaw == "") idsRaw = J("ids", "imageIds", "savedImageIds", "SavedImageIds");
            var ids = idsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x.Trim(), out var n) ? n : 0).Where(n => n != 0).ToList();
            int id = int.TryParse(idRaw, out var one) ? one : (ids.Count == 1 ? ids[0] : 0);
            if (id != 0 && !ids.Contains(id)) ids.Add(id);
            // resolve ImageName: the 2025 client names the photo by its IMAGE NAME, not its id
            // (deletesaved / modifyaccessibility / modifydescription all sent fields=[ImageName]
            // and "deleted 0 of 0" every time).
            string namesRaw = Get("ImageName", "imageName", "ImageNames", "imageNames", "name"); if (namesRaw == "") namesRaw = J("ImageName", "imageName", "ImageNames", "imageNames", "name");
            foreach (var nm in namesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var rec = ImageMetadataDB.GetByName(nm.Trim());
                if (rec != null && !ids.Contains(rec.Id)) ids.Add(rec.Id);
            }
            if (id == 0 && ids.Count == 1) id = ids[0];
            string acc = Get("accessibility", "Accessibility", "imageAccessibility"); if (acc == "") acc = J("accessibility", "imageAccessibility");
            string desc = Get("description", "Description"); if (desc == "") desc = J("description");
            return (id, acc == "" ? null : acc, desc == "" ? null : desc, ids);
        }

        [HttpPost("/api/images/v1/deletesaved")]
        [HttpPost("/api/images/v2/deletesaved")]
        [HttpDelete("/api/images/v1/deletesaved")]
        public async Task<IActionResult> ImagesDeleteSaved()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var (_, _, _, ids) = await ReadImageEdit();
            int done = 0;
            foreach (var id in ids)
            {
                var img = ImageMetadataDB.GetById(id);
                if (img == null) continue;
                if (img.PlayerId != account.Id && !PlayerDB.CheckDevFlag(account.Id)) continue;
                if (ImageMetadataDB.DeleteImage(id) != null) done++;
            }
            Console.WriteLine($"[images] {account.Id} deleted {done} of {ids.Count} photo(s)");
            return Ack();
        }

        [HttpPost("/api/images/v1/modifyaccessibility")]
        [HttpPost("/api/images/v2/modifyaccessibility")]
        [HttpPut("/api/images/v2/modifyaccessibility")]
        public async Task<IActionResult> ImagesModifyAccessibility()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var (_, accRaw, _, ids) = await ReadImageEdit();
            ImageMetadataDB.ImageAccessibility acc;
            if (int.TryParse(accRaw, out var ai)) acc = ai == 0 ? ImageMetadataDB.ImageAccessibility.Private : ImageMetadataDB.ImageAccessibility.Public;
            else if (string.Equals(accRaw, "Private", StringComparison.OrdinalIgnoreCase)) acc = ImageMetadataDB.ImageAccessibility.Private;
            else if (string.Equals(accRaw, "Public", StringComparison.OrdinalIgnoreCase)) acc = ImageMetadataDB.ImageAccessibility.Public;
            else { Console.WriteLine($"[images] {account.Id} modifyaccessibility with no readable accessibility ('{accRaw}')"); return Ack(); }
            int done = 0;
            foreach (var id in ids)
            {
                var img = ImageMetadataDB.GetById(id);
                if (img == null || (img.PlayerId != account.Id && !PlayerDB.CheckDevFlag(account.Id))) continue;
                if (ImageMetadataDB.SetVisibility(id, acc, friendsOnly: false)) done++;
            }
            Console.WriteLine($"[images] {account.Id} set {done} photo(s) {acc}");
            return Ack();
        }

        [HttpPost("/api/images/v1/modifydescription")]
        [HttpPost("/api/images/v2/modifydescription")]
        [HttpPut("/api/images/v1/modifydescription")]
        public async Task<IActionResult> ImagesModifyDescription()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var (id, _, desc, ids) = await ReadImageEdit();
            foreach (var one in ids)
            {
                var img = ImageMetadataDB.GetById(one);
                if (img == null || (img.PlayerId != account.Id && !PlayerDB.CheckDevFlag(account.Id))) continue;
                ImageMetadataDB.SetDescription(one, desc ?? "");
            }
            return Ack();
        }

        // ---- small gaps the 2025 client hits on every session -------------------------------
        // Matchmaking "avoid juniors" preference: off, and a PUT is accepted.
        // Rooms 2.0 staged (unsaved-to-publish) entities: none.
        // The room's staged ENTITY CHANGE LIST (Rooms 2.0 entity edits waiting to publish). The
        // client reads an OBJECT here - `[]` threw "expected:'{', actual:'['" and "Failed to get
        // room entity change list" on every Rooms 2.0 load (Player.log). Nothing here stages
        // entity edits, so the list is empty.
        [HttpGet("/Room_server/rooms/{roomId}/entities/staged")] public ContentResult StagedEntities(string roomId) => Json("{\"RoomId\":" + (long.TryParse(roomId, out var rid) ? rid : 0) + ",\"Entities\":[],\"Changes\":[],\"StagedEntities\":[],\"Version\":0}");
        // Invention browse filters: the category tags the create flow uses (UGC.Room2InventionCategoryTagConcatenatedList).
        [HttpGet("/api/inventions/v1/tagfilters")]
        public ContentResult InventionTagFilters() => Json(Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            PinnedFilters = new[] { "weapon", "gameobject", "decoration", "environment", "gamemechanic" },
            PopularFilters = new[] { "weapon", "gameobject", "decoration", "environment", "gamemechanic" },
        }));
        // Website share-link page views: nothing to consume.
        [HttpPost("/link/pageview/consume")] public IActionResult LinkPageviewConsume() => Ok(new { });
        [HttpPost("/api/images/v1/sendlink")]           public IActionResult ImagesSendLink() => Ack();

        // ---------------- Custom avatar items (UGC) ----------------
        [HttpGet("/api/customAvatarItems/v1/itemOwnershipLimit")] public ContentResult ItemOwnershipLimit() => Json("10000");
        [HttpGet("/econ/customAvatarItems/v1/itemOwnershipLimit")] public ContentResult ItemOwnershipLimitEcon() => Json("10000");
        // Live 404s from the 2025 client (API log): the costume maker's "items you used to own"
        // list, the consumable "who can I give this to" lookup, the PUT spelling of the design
        // save, the GameSight analytics sink, the batched telemetry post and the commerce probes.
        [HttpGet("/econ/customAvatarItems/v1/allPastOwnedIds")]  public ContentResult AllPastOwnedIds() => EmptyArray();
        [HttpGet("/api/consumables/v1/getTransferable/{id}")]      public ContentResult ConsumableTransferable(string id) => EmptyArray();
        [HttpPut("/api/customAvatarItems/v1/design")]              public ContentResult CustomItemDesignPut() => CustomItemDesign();
        [HttpPost("/api/gamesight/event")]                          public IActionResult GamesightEvent() => Ok();
        [HttpPost("/data/events")]                                  public IActionResult DataEvents() => Ok();
        [HttpGet("/Commerce/purchase/v1/hasspentmoney")]           public ContentResult HasSpentMoney() => Json("false");
        [HttpPost("/Commerce/purchase/v1/initiatepurchase")]       public IActionResult InitiatePurchase() => Ok(new { success = true, error = "", value = new { TransactionId = "campus-no-store" } });
        // Per-room XP progress the 2025 client POSTs on leaving a room (was a 405); nothing
        // tracks per-room XP, so it is acknowledged like the GET's "not enabled" answer.
        [HttpPost("/Room_server/rooms/{roomId}/experience/player")] public IActionResult RoomExperienceReport(string roomId) => Ok(new { Enabled = false, Experience = (object?)null, ConcurrencyCode = (string?)null });
        // NOTE: /cdn/config/* is NOT routed here. Program.cs serves it as static files from
        // Data/CDN/Configs (RRPlusConfig_v3.json, SkuConfig_v1.json, Prod/*, and the opaque
        // launch-validation blob 1b057e6e-...). A controller route added here for it shadowed
        // that middleware and read a folder that doesn't exist, so the blob 404'd and every PC
        // client failed boot with "Launch validation failed. Is Rec Room installed correctly?".
        // Anti-cheat signature blobs: served from Data/CDN/Sigs when a file of that name exists.
        [HttpGet("/cdn/sigs/{name}")]
        public IActionResult CdnSig(string name)
        {
            string p = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "Sigs", Path.GetFileName(name));
            return System.IO.File.Exists(p) ? PhysicalFile(p, "application/octet-stream") : NotFound();
        }
        // The costume maker's saved-designs lookup. Its exact shape is still being learned: the
        // client rejected "{}" ("Malformed Response"), so it is answered as an empty LIST and every
        // call is logged with its query so the real contract can be filled in from a capture.
        [HttpGet("/api/customAvatarItems/v1/design")] [HttpPost("/api/customAvatarItems/v1/design")]
        public ContentResult CustomItemDesign()
        {
            Console.WriteLine($"[ugc-design] {Request.Method} {Request.Path}{Request.QueryString} ct='{Request.ContentType}' len={Request.ContentLength} accept='{Request.Headers["Accept"]}'");
            return EmptyArray();
        }
        [HttpPost("/api/customAvatarItems/GetCustomAvatarItemCurrentSavesForLegacyAvatarItemDescs")]
        public IActionResult LegacyCustomItemSavesByDesc() => Ok(new { customAvatarItemSavesByAvatarItemDesc = new { } });

        // ---------------- Room currencies ----------------

        // ---------------- Room keys ----------------

        // ---------------- Inventions (lists empty; writes ack) ----------------
        // /api/inventions/v1/version is REAL now (InventionController.GetVersion): it answers a
        // bare RRInventionVersion OBJECT, and the [] this stub returned was one of the client's
        // "Malformed Response: '[]'" - the room load stalled on it on every invention spawn.
        [HttpGet("/api/inventions/v1/versions")]          public ContentResult InvVersions()        => EmptyArray();
        [HttpGet("/api/inventions/v1/featureddormskins")] public ContentResult InvFeaturedDorm()    => EmptyArray();
        [HttpGet("/api/inventions/v1/dormskinsfromids")] [HttpPost("/api/inventions/v1/dormskinsfromids")]
        public ContentResult InvDormFromIds() => EmptyArray();
        // v*/addversion is REAL now - see InventionController.AddVersion.
        // NOTE: v6/save, v9/save and v3/publish are REAL now — see InventionController.

        // ---------------- Player events ----------------
        [HttpPost("/api/playerevents/v1/broadcast")]      public IActionResult PeBroadcast()     => Ack();
        // bulkInvite: real now - APIController.BulkInviteToEvent.
        [HttpPost("/api/playerevents/v1/deleteResponse")] public IActionResult PeDeleteResponse() => Ack();
        [HttpGet("/api/playerevents/v1/r")] [HttpPost("/api/playerevents/v1/r")]
        public ContentResult PeR() => EmptyArray();

        // ---------------- Storefront purchases (ack envelope; real charge is TODO) ----------------
        [HttpPost("/api/storefronts/v1/buyForFreeGiftButton")]        public IActionResult SfBuyFreeGift()      => Ack();
        [HttpPost("/api/storefronts/v1/buyProgressionEventXpBoost")]  public IActionResult SfBuyXpBoost()       => Ack();
        [HttpPost("/api/storefronts/v1/buyPurchaseReminder")]         public IActionResult SfBuyReminder()      => Ack();
        [HttpPost("/api/storefronts/v2/buyElite")]                    public IActionResult SfBuyElite()         => Ack();
        [HttpPost("/api/storefronts/v2/buyTier")]                     public IActionResult SfBuyTier()          => Ack();
        [HttpGet("/api/storefronts/v1/toptoday")]                     public ContentResult SfTopToday()         => EmptyArray();
        [HttpGet("/api/storefronts/v1/trialInvention")]               public ContentResult SfTrialInvention()   => Json("{}");
        [HttpGet("/api/storefronts/v1/trialInvention/duration")]      public ContentResult SfTrialDuration()    => Json("0");

        // ---------------- Reporting / platform / misc ----------------
        [HttpPost("/api/PlayerReporting/v1/deviceId")]    public IActionResult ReportDeviceId()  => Ack();
        [HttpPost("/api/AppIntegrity/v1/iospaymentqueuefailed")] public IActionResult IosPayFailed() => Ack();
        [HttpGet("/api/AppIntegrity/v1/iosproducts")]     public ContentResult IosProducts()      => EmptyArray();
        [HttpGet("/api/CampusCard/PS5RecRoomPlusEnabledForAllPlayers")] public ContentResult Ps5Plus() => Json("false");
        [HttpGet("/api/apple/musicpromotion/code")]       public ContentResult AppleMusicCode()   => Json("{}");
        [HttpGet("/api/config/v1/freegiftbutton")]        public ContentResult FreeGiftButtonConfig() => Json("{}");
        [HttpGet("/api/royale/v2/matchcomplete")] [HttpPost("/api/royale/v2/matchcomplete")]
        public IActionResult RoyaleMatchComplete() => Ack();
        // No Rec Royale season is running on Campus - return an empty "no current season" object
        // (was 404, which the client surfaced as an error) instead of a real season.
        [HttpGet("/api/royale/v1/current")] public ContentResult RoyaleCurrent() => Json("{}");
        // Season storefront (battle-pass-style season store) by id - we don't run seasons, so an
        // empty store instead of a 404. Shape mirrors the giftdrop store: an id + empty item list.
        [HttpGet("/api/storefronts/v1/season/{seasonId}")]
        public IActionResult SeasonStorefront(int seasonId) => Ok(new { SeasonId = seasonId, StorefrontType = seasonId, StoreItems = System.Array.Empty<object>(), Prices = System.Array.Empty<object>() });

        // ---------------- External friend invite / referrals ----------------
        [HttpPost("/api/externalfriendinvite/v1/createplatforminvite")]   public IActionResult ExtCreatePlatformInvite() => Json("{}");
        [HttpGet("/api/externalfriendinvite/v1/gettextmessagereferrers")] public ContentResult ExtTextReferrers()        => EmptyArray();
        [HttpPost("/api/externalfriendinvite/v1/sendtextmessageinvite")]  public IActionResult ExtSendTextInvite()       => Ack();
        [HttpPost("/api/incentivizedreferrals/claim")]  public IActionResult ReferralsClaim()     => Ack();
        [HttpGet("/api/incentivizedreferrals/referrals")] public ContentResult ReferralsList()    => EmptyArray();
    }
}
