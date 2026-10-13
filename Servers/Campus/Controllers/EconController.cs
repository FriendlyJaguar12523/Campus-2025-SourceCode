using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.dbs;

namespace Campus_live_rewrite.Controllers
{
    [ApiController]
    public class EconController : ControllerBase
    {
        [HttpGet("/econ/customAvatarItems/v1/owned")]
        public IActionResult OwnedCustomAvatarItems([FromQuery] int skip = 0, [FromQuery] int take = 100)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // PAGINATED. The client downloads its locker in pages of 100 (skip/take) and keeps asking
            // until it has TotalResults. Ignoring skip/take handed EVERY page the whole 3k-item dev
            // locker (7.5 MB per request) - the headset timed out, retried a few times and gave up
            // with "Failed to download unlocked avatar items", so the imported items never showed.
            if (take > 1000) take = 1000;
            if (take <= 0) take = 100;
            if (skip < 0) skip = 0;

            // The caller's OWN custom items (the ones they created), from the in-memory UGC index.
            // Was streaming the ENTIRE catalog file - fine when empty, an 87MB response now. Items
            // a player equips but didn't create still resolve via /api/customAvatarItems/v1/bulk.
            //
            // DEV LOCKER: a dev owns every IMPORTED UGC item (the ones whose assets we host, so they
            // actually render) on top of their own creations - so imported clothing shows up in the
            // wardrobe and equips like a normal item. Non-devs still only own what they created.
            // Compact records; only the requested page is turned into JSON objects.
            var mine = CustomItemsCache.ByCreatorRecs(account.Id);
            // Bought / gifted UGC items.
            {
                var seenB = new HashSet<string>(mine.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var caid in PlayerDB.OwnedCustomItems(account.Id))
                {
                    var r = CustomItemsCache.GetRec(caid);
                    if (r != null && seenB.Add(r.Id)) mine.Add(r);
                }
            }
            // The dev locker used to add every IMPORTED first-party item here as well. Off by
            // default now: those records render only through a downloaded asset bundle, and the
            // 20250718 PC client fails that download for every bundle we serve (the wardrobe
            // tile shows, equipping dies in a NullReferenceException). The reference server has
            // no such locker - its wardrobe is the LEGACY catalog (/api/avatar/v4/items), which the
            // PC client renders from its own built-in assets, and a dev already owns all of that.
            // So a dev's "Winter Scarf" that works is the legacy one; the imported twin only ever
            // produced the broken duplicate tile. ConfigCampus.DevLockerImportedUgc turns it back on.
            if (ConfigCampus.DevLockerImportedUgc && PlayerDB.CheckDevFlag(account.Id))
            {
                // Every custom item that can render (CustomItemsCache.DevLockerRecs), after their own.
                var seen = new HashSet<string>(mine.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var it in CustomItemsCache.DevLockerRecs())
                    if (seen.Add(it.Id)) mine.Add(it);
            }
            var page = mine.Skip(skip).Take(take).Select(r => r.ToJObject()).ToList();
            int extra = 0;
            if (ConfigCampus.HoldNewAccountCreation)
            {
                // The starter avatar wears two custom items we have no record of; "Can't get purchase info" for an item the account
                // does not own is what broke the customize screen. Listing them as owned (stand-in records) removes the price check.
                foreach (var sid in Campus_live_rewrite.api.Controllers.APIController.StarterCustomItemIds)
                {
                    if (mine.Any(m => string.Equals(m.Id, sid, StringComparison.OrdinalIgnoreCase))) continue;
                    extra++;
                    if (skip == 0) { var st = Campus_live_rewrite.api.Controllers.APIController.StandInCustomItem(sid); if (st != null) page.Add(st); }
                }
            }
            if (skip == 0) Console.WriteLine($"[ugc] {account.Id} owns {mine.Count} custom item(s) (page size {take})");
            return new ContentResult
            {
                Content = new Newtonsoft.Json.Linq.JObject
                {
                    ["Results"] = new Newtonsoft.Json.Linq.JArray(page),
                    ["TotalResults"] = mine.Count + extra
                }.ToString(Newtonsoft.Json.Formatting.None),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/econ/roomInventory/room/{roomId}")]
        public IActionResult GetRoomInventoryForRoom(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        [HttpGet("/econ/roomInventory/room/{roomId}/player")]
        public IActionResult GetRoomInventoryForPlayer(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        [HttpGet("/econ/roomInventoryItemTags/room/{roomId}")]
        public IActionResult GetRoomInventoryItemTagsForRoom(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        [HttpGet("/econ/roomEconConfig/{roomId}")]
        public IActionResult GetRoomEconConfig(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                RoomId = roomId,
                EnableSortingTabs = false
            });
        }

        [HttpGet("/econ/roomOffer/room/{roomId}")]
        public IActionResult GetRoomOffer(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        [HttpGet("/econ/roomOffer/room/{roomId}/purchaseCounts")]
        public IActionResult GetRoomOfferPurchaseCounts(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                value = Array.Empty<object>(),
                success = true
            });
        }

        [HttpGet("/econ/roomGiftDropShops/room/{roomId}")]
        public IActionResult GetRoomGiftDropShops(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        // A room's "reward outfit to players in the room" call. It 404'd (7 times, September) because
        // nothing served it, so the room's reward silently never arrived. The request/response shape
        // is not pinned yet (not in the catalog, nothing on the reference server), so this ONLY logs the
        // raw fields - the first real call names them in the journal - and answers 200 with an empty
        // object (it is not a list/scalar route, so {} cannot raise "Malformed Response"). It grants
        // nothing: a room owner naming a custom item here must be checked for owning it first, and
        // that needs the real field names.
        [HttpPost("/econ/customAvatarItems/outfitRewards/rewardPlayersInRoom")]
        public IActionResult RewardPlayersInRoomOutfit()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var f = RoomModeration.Fields(Request);
            string raw = "";
            try
            {
                Request.EnableBuffering();
                Request.Body.Position = 0;
                using var sr = new StreamReader(Request.Body, leaveOpen: true);
                raw = sr.ReadToEndAsync().GetAwaiter().GetResult();
                Request.Body.Position = 0;
            }
            catch { }
            Console.WriteLine($"[outfit-reward] {account.Id} rewardPlayersInRoom (not granted - shape not pinned): {RoomModeration.Dump(f)} body={(raw.Length > 600 ? raw[..600] : raw)}");
            return Ok(new { });
        }

        [HttpPost("/econ/roomInventory/v2/award")]
        public IActionResult AwardRoomInventory([FromBody] List<AwardRoomInventoryRequest> request)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // An empty/missing list used to throw on FirstOrDefault().X (a 500 the client reads as a failed award).
            if (request == null || request.Count == 0)
                return Ok(new { Result = 0 });

            return Ok(new
            {
                Result = 0,
                PlayerRoomInventoryItem = new
                {
                    AccountId = account.Id,
                    RoomInventoryItemId = request.FirstOrDefault().RoomInventoryItemOriginId,
                    RoomId = request.FirstOrDefault().RoomId,
                    RoomInventoryItemOriginId = request.FirstOrDefault().RoomInventoryItemOriginId,
                    Count = 0,
                    ConcurrencyCode = request.FirstOrDefault().ConcurrencyCodes.NewConcurrencyCode,
                    FirstReceivedAt = DateTime.UtcNow,
                    ModifiedAt = DateTime.UtcNow,
                    QuantityAwarded = request.FirstOrDefault().Quantity
                }
            });
        }

        public class AwardRoomInventoryRequest
        {
            public ConcurrencyCodes ConcurrencyCodes { get; set; }
            public int Quantity { get; set; }
            public ulong RoomId { get; set; }
            public string RoomInventoryItemOriginId { get; set; }
        }

        public class ConcurrencyCodes
        {
            public string? CurrentConcurrencyCode { get; set; }
            public string? NewConcurrencyCode { get; set; }
        }
    }
}
