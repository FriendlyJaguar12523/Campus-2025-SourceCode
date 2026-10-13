using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Classes = Campus_live_rewrite.api.server.Classes;
using static Campus_live_rewrite.api.dbs.RoomEconDB;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Room keys, room currencies and room consumables - created in Room Settings, sold in-room,
    /// paid for with tokens (or with a room currency), owned afterwards. Every one of these
    /// routes answered "[]" / an ack before, so a price set in-game was never charged and nothing
    /// was ever kept.
    ///
    /// Purchases with tokens pay the ROOM'S CREATOR their cut (EarningsDB). Request fields are
    /// read by name from form, query or JSON (RoomModeration.Fields) - the exact spellings this
    /// build posts are logged on every write so they can be pinned. Purchase answers carry BOTH
    /// the bare balance-response keys and the {Value,Success,Error,error_id} envelope, so either
    /// reader finds what it needs.
    /// </summary>
    [ApiController]
    public class RoomEconController : ControllerBase
    {
        private const int Tokens = (int)Classes.CurrencyType.RecCenterTokens;

        private static ContentResult J(object o, int status = 200) =>
            new() { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = status };

        private static object Envelope(object? value, string? error = null) => new
        {
            Value = value, Success = error == null, Error = error, error_id = (string?)null
        };

        private static bool CanEdit(long roomId, ulong accountId) =>
            RoomDB.UserCanEditRoom((ulong)roomId, accountId) || PlayerDB.CheckDevFlag(accountId);

        private static ulong RoomCreator(long roomId) => RoomDB.GetRoom((ulong)roomId)?.CreatorAccountId ?? 0;

        // Who a NON-editor (a player standing in the room - its circuits run on a player's client)
        // may award room currency / consumables to. It used to be anyone at all: a player merely in
        // a room could award that room's currency or items to ANY account, in any amount, and a
        // negative amount drained other players' balances wherever they were. Now the target must
        // be in the SAME room instance as the caller, and the caller is rate-limited. The room's
        // owners, co-owners and developers are unchanged (CanEdit).
        private static long InstanceOf(ulong accountId) => HeartbeatDB.GetPlayerHeartbeat(accountId)?.roomInstance?.roomInstanceId ?? 0;
        private const int MaxAwardsPerMinute = 120;
        private const int MaxConsumablesPerAward = 100;
        private static string? AwardRefusal(long roomId, ulong caller, ulong target, int awardsInRequest)
        {
            if (CanEdit(roomId, caller)) return null;
            var hb = HeartbeatDB.GetPlayerHeartbeat(caller);
            if (hb?.roomInstance == null || hb.roomInstance.roomId != roomId) return "Not allowed";
            if (target != caller && InstanceOf(target) != hb.roomInstance.roomInstanceId) return "Player is not in this room";
            if (!RateLimit.TryConsumeQuota($"roomaward:{caller}", MaxAwardsPerMinute, 60, out _)) return "Too many awards - slow down";
            return null;
        }

        private Dictionary<string, string> F() => RoomModeration.Fields(Request);
        private static long L(Dictionary<string, string> f, params string[] n) => RoomModeration.Long(f, n);
        private static string? S(Dictionary<string, string> f, params string[] n) => RoomModeration.Str(f, n);
        private static Guid? G(Dictionary<string, string> f, params string[] n)
        {
            var s = S(f, n);
            return s != null && Guid.TryParse(s.Trim(), out var g) && g != Guid.Empty ? g : null;
        }

        private object BalanceAnswer(long balance, object? data, int updateResponse = 0)
        {
            var bare = new
            {
                Balance = balance,
                CurrencyType = Tokens,
                Platform = Classes.GameCurrency.AllPlatformsBucket,
                BalanceType = Classes.GameCurrency.AllPlatformsBucket,
                BalanceUpdates = new[] { new { UpdateResponse = updateResponse, Data = data } }
            };
            return new { Value = bare, Success = true, Error = (string?)null, error_id = (string?)null, bare.Balance, bare.CurrencyType, bare.Platform, bare.BalanceType, bare.BalanceUpdates };
        }

        private ContentResult Refuse(string why, int status = 200) =>
            J(new { Value = (object?)null, Success = false, Error = why, error_id = (string?)null, Balance = 0, BalanceUpdates = Array.Empty<object>() }, status);

        // ======================================================================= KEYS

        [HttpGet("/api/roomkeys/v1/mine")]
        public ContentResult MyKeys()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return J(KeysOwnedBy(account.Id).Select(KeyDto).ToList());
        }

        [HttpGet("/api/roomkeys/v1/room")]
        public ContentResult KeysOfRoom([FromQuery] long roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return J(KeysForRoom(roomId).Select(KeyDto).ToList());
        }

        // ?roomKeyId= -> that key; ?roomId= -> any key of that room. Bare bool.
        [HttpGet("/api/roomkeys/v1/owns")]
        public ContentResult OwnsKeyRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(false);
            var f = F();
            long keyId = L(f, "roomKeyId", "RoomKeyId", "id");
            long roomId = L(f, "roomId", "RoomId");
            bool owns = keyId > 0 ? OwnsKey(account.Id, keyId)
                      : roomId > 0 && KeysForRoom(roomId).Any(k => OwnsKey(account.Id, k.RoomKeyId));
            return J(owns);
        }

        [HttpGet("/api/roomkeys/v1/owns/bulk")]
        [HttpPost("/api/roomkeys/v1/owns/bulk")]
        public ContentResult OwnsKeysBulk()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var ids = RoomModeration.ULongs(F(), "id", "ids", "roomKeyIds", "RoomKeyIds", "roomKeyId");
            return J(ids.Select(id => new { RoomKeyId = (long)id, IsOwned = OwnsKey(account.Id, (long)id), Owned = OwnsKey(account.Id, (long)id) }).ToList());
        }

        [HttpPost("/api/roomkeys/v1/create")]
        [HttpPut("/api/roomkeys/v1/create")]
        public ContentResult CreateKeyRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            Console.WriteLine($"[roomkeys] {account.Id} create: {RoomModeration.Dump(f)}");
            long roomId = L(f, "RoomId", "roomId");
            if (roomId <= 0) return J(Envelope(null, "RoomId is required"), 400);
            if (!CanEdit(roomId, account.Id)) return J(Envelope(null, "You cannot edit this room"), 403);
            var k = CreateKey(new RoomKey
            {
                RoomId = roomId, CreatorId = RoomCreator(roomId),
                Name = (S(f, "Name", "name") ?? "Room Key").Trim(),
                Description = S(f, "Description", "description") ?? "",
                Price = (int)Math.Max(0, L(f, "Price", "price")),
                PurchaseCurrencyId = G(f, "PurchaseCurrencyId", "purchaseCurrencyId", "CurrencyId"),
                ImageName = S(f, "ImageName", "imageName"),
                Type = (int)L(f, "Type", "type")
            });
            Console.WriteLine($"[roomkeys] room {roomId}: key #{k.RoomKeyId} '{k.Name}' for {k.Price} {(k.PurchaseCurrencyId.HasValue ? "room currency" : "tokens")}");
            var dto = KeyDto(k);
            return J(new { Value = dto, Success = true, Error = (string?)null, error_id = (string?)null, RoomKey = dto,
                           k.RoomKeyId, k.ReplicationId, k.RoomId, k.Name, k.Description, k.Price, k.PurchaseCurrencyId, CreatedAt = k.CreatedAt.ToString("o"), ImageName = k.ImageName ?? "", k.Type });
        }

        [HttpPost("/api/roomkeys/v1/update")]
        [HttpPut("/api/roomkeys/v1/update")]
        [HttpPut("/api/roomkeys/v1/{roomKeyId:long}")]
        [HttpPost("/api/roomkeys/v1/{roomKeyId:long}")]
        public ContentResult UpdateKeyRoute(long roomKeyId = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            if (roomKeyId == 0) roomKeyId = L(f, "RoomKeyId", "roomKeyId", "id");
            var k = GetKey(roomKeyId);
            if (k == null) return J(Envelope(null, "No such key"), 404);
            if (!CanEdit(k.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"), 403);
            Console.WriteLine($"[roomkeys] {account.Id} update #{roomKeyId}: {RoomModeration.Dump(f)}");
            k = UpdateKey(roomKeyId, x =>
            {
                if (S(f, "Name", "name") is string n && n.Trim().Length > 0) x.Name = n.Trim();
                if (S(f, "Description", "description") is string d) x.Description = d;
                if (f.ContainsKey("Price") || f.ContainsKey("price")) x.Price = (int)Math.Max(0, L(f, "Price", "price"));
                if (f.ContainsKey("PurchaseCurrencyId") || f.ContainsKey("purchaseCurrencyId")) x.PurchaseCurrencyId = G(f, "PurchaseCurrencyId", "purchaseCurrencyId");
                if (S(f, "ImageName", "imageName") is string img) x.ImageName = img;
            })!;
            return J(Envelope(KeyDto(k)));
        }

        [HttpDelete("/api/roomkeys/v1/{roomKeyId:long}")]
        [HttpPost("/api/roomkeys/v1/delete")]
        public ContentResult DeleteKeyRoute(long roomKeyId = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (roomKeyId == 0) roomKeyId = L(F(), "RoomKeyId", "roomKeyId", "id");
            var k = GetKey(roomKeyId);
            if (k == null) return J(Envelope(null, "No such key"), 404);
            if (!CanEdit(k.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"), 403);
            UpdateKey(roomKeyId, x => x.Deleted = true);
            return J(Envelope(null));
        }

        // Owner hands keys out / takes them back (form or JSON: RoomKeyId + AccountIds).
        [HttpPost("/api/roomkeys/v1/awardbulk")]
        public ContentResult AwardKeys()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            long keyId = L(f, "RoomKeyId", "roomKeyId", "id");
            var k = GetKey(keyId);
            if (k == null) return J(Envelope(null, "No such key"), 404);
            if (!CanEdit(k.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"), 403);
            var ids = RoomModeration.ULongs(f, "AccountIds", "accountIds", "PlayerIds", "playerIds", "AccountId", "accountId");
            int n = 0; foreach (var pid in ids) if (GrantKey(pid, k)) n++;
            Console.WriteLine($"[roomkeys] {account.Id} awarded key #{keyId} to {n}/{ids.Count} player(s)");
            return J(Envelope(ids.Select(pid => new { AccountId = pid, RoomKeyId = keyId, Success = true, Error = (string?)null }).ToList()));
        }

        [HttpPost("/api/roomkeys/v1/revoke")]
        public ContentResult RevokeKeys()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            long keyId = L(f, "RoomKeyId", "roomKeyId", "id");
            var k = GetKey(keyId);
            if (k == null) return J(Envelope(null, "No such key"), 404);
            if (!CanEdit(k.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"), 403);
            var ids = RoomModeration.ULongs(f, "AccountIds", "accountIds", "PlayerIds", "playerIds", "AccountId", "accountId");
            foreach (var pid in ids) RevokeKey(pid, keyId);
            return J(Envelope(null));
        }

        // GET /api/roomkeys/v1/purchased/{roomKeyId} - does the caller already own this key? Bare bool (the client's own table says bool; 3 x 404 in
        // two days). The same answer as /owns?roomKeyId=.
        [HttpGet("/api/roomkeys/v1/purchased/{roomKeyId:long}")]
        public ContentResult KeyPurchased(long roomKeyId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(false);
            return J(roomKeyId > 0 && OwnsKey(account.Id, roomKeyId));
        }

        // Buy a key with tokens ({RoomKeyId, RequestedPrice}) or with the room currency it is priced in.
        // The client sends buyRoomKey as a GET (RoomKeyId / RequestedPrice in the query; the handler reads query, form and JSON alike) - it was
        // POST-only, so those buys answered 405.
        [HttpGet("/api/storefronts/v1/buyRoomKey")]
        [HttpPost("/api/storefronts/v1/buyRoomKey")]
        [HttpPost("/api/storefronts/v1/PurchaseRoomKeyWithCurrency")]
        [HttpPost("/api/roomkeys/v1/purchase")]
        public ContentResult BuyKey()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            Console.WriteLine($"[roomkeys] {account.Id} buy {Request.Path}: {RoomModeration.Dump(f)}");
            long keyId = L(f, "RoomKeyId", "roomKeyId", "id", "Id");
            var k = GetKey(keyId);
            if (k == null) return Refuse("No such key");
            if (OwnsKey(account.Id, keyId)) return Refuse("You already own this key");
            long requested = L(f, "RequestedPrice", "requestedPrice", "Price");
            if (requested > 0 && requested != k.Price)
                Console.WriteLine($"[roomkeys] client rendered {requested} for key #{keyId}, charging the stored {k.Price}");

            if (k.PurchaseCurrencyId.HasValue)
            {
                var cur = GetCurrency(k.PurchaseCurrencyId.Value);
                if (cur == null) return Refuse("This key's currency no longer exists");
                var left = AddCurrency(account.Id, cur, -k.Price);
                if (left == null) return Refuse($"Not enough {cur.Name}");
                GrantKey(account.Id, k);
                Console.WriteLine($"[roomkeys] @{account.Username} bought key #{keyId} '{k.Name}' for {k.Price} {cur.Name} -> {left}");
                return J(new { Value = new { Balance = left.Value, CurrencyId = cur.CurrencyId, CurrencyType = 300, RoomKey = KeyDto(k) }, Success = true, Error = (string?)null, error_id = (string?)null,
                               Balance = left.Value, CurrencyType = 300, Platform = Classes.GameCurrency.AllPlatformsBucket, BalanceUpdates = new[] { new { UpdateResponse = 0, Data = KeyDto(k) } } });
            }

            long balance = PlayerDB.GetBalance(account.Id, Tokens);
            if (k.Price > 0 && balance < k.Price) return Refuse("Not enough tokens");
            if (k.Price > 0) balance = PlayerDB.AddBalance(account.Id, Tokens, -k.Price);
            GrantKey(account.Id, k);
            EarningsDB.Record(k.CreatorId != 0 ? k.CreatorId : RoomCreator(k.RoomId), account.Id, "roomkey", k.RoomKeyId.ToString(), k.Name, Tokens, k.Price);
            GiftBoxes.PushBalance(account.Id, Tokens, balance, "room key");
            Console.WriteLine($"[roomkeys] @{account.Username} bought key #{keyId} '{k.Name}' for {k.Price} tokens -> {balance}");
            return J(BalanceAnswer(balance, KeyDto(k)));
        }

        // ======================================================================= CURRENCIES

        [HttpGet("/api/roomcurrencies/v1/currencies")]
        public ContentResult CurrenciesOfRoom([FromQuery] long roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return J(CurrenciesForRoom(roomId).Select(CurrencyDto).ToList());
        }

        // 7 form fields: RoomId, Name, Description, Limit, Shape, Color, ImageName -> ENVELOPED.
        [HttpPost("/api/roomcurrencies/v1/createCurrency")]
        public ContentResult CreateCurrencyRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            Console.WriteLine($"[roomcurrency] {account.Id} create: {RoomModeration.Dump(f)}");
            long roomId = L(f, "RoomId", "roomId");
            if (roomId <= 0) return J(Envelope(null, "RoomId is required"));
            if (!CanEdit(roomId, account.Id)) return J(Envelope(null, "You cannot edit this room"));
            var c = CreateCurrency(new RoomCurrency
            {
                RoomId = roomId, CreatorId = RoomCreator(roomId),
                Name = (S(f, "Name", "name") ?? "Coins").Trim(),
                Description = S(f, "Description", "description") ?? "",
                Limit = f.ContainsKey("Limit") || f.ContainsKey("limit") ? L(f, "Limit", "limit") : 1_000_000,
                Shape = (int)L(f, "Shape", "shape"),
                Color = f.ContainsKey("Color") || f.ContainsKey("color") ? (int)L(f, "Color", "color") : -1,
                ImageName = S(f, "ImageName", "imageName")
            });
            Console.WriteLine($"[roomcurrency] room {roomId}: currency {c.CurrencyId} '{c.Name}'");
            return J(Envelope(CurrencyDto(c)));
        }

        [HttpPost("/api/roomcurrencies/v1/updateCurrency")]
        [HttpPut("/api/roomcurrencies/v1/updateCurrency")]
        public ContentResult UpdateCurrencyRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            var id = G(f, "CurrencyId", "currencyId", "id");
            var c = id.HasValue ? GetCurrency(id.Value) : null;
            if (c == null) return J(Envelope(null, "No such currency"));
            if (!CanEdit(c.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"));
            Console.WriteLine($"[roomcurrency] {account.Id} update {id}: {RoomModeration.Dump(f)}");
            c = UpdateCurrency(id!.Value, x =>
            {
                if (S(f, "Name", "name") is string n && n.Trim().Length > 0) x.Name = n.Trim();
                if (S(f, "Description", "description") is string d) x.Description = d;
                if (f.ContainsKey("Limit") || f.ContainsKey("limit")) x.Limit = L(f, "Limit", "limit");
                if (f.ContainsKey("Shape") || f.ContainsKey("shape")) x.Shape = (int)L(f, "Shape", "shape");
                if (f.ContainsKey("Color") || f.ContainsKey("color")) x.Color = (int)L(f, "Color", "color");
                if (S(f, "ImageName", "imageName") is string img) x.ImageName = img;
            })!;
            return J(Envelope(CurrencyDto(c)));
        }

        [HttpPost("/api/roomcurrencies/v1/deleteCurrency")]
        [HttpDelete("/api/roomcurrencies/v1/deleteCurrency")]
        public ContentResult DeleteCurrencyRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            var id = G(f, "CurrencyId", "currencyId", "id");
            var c = id.HasValue ? GetCurrency(id.Value) : null;
            if (c == null) return J(Envelope(null, "No such currency"));
            if (!CanEdit(c.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"));
            UpdateCurrency(id!.Value, x => x.Deleted = true);
            return J(Envelope(null));
        }

        [HttpGet("/api/roomcurrencies/v1/getBalance")]
        [HttpPost("/api/roomcurrencies/v1/getBalance")]
        public ContentResult GetBalanceRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            var id = G(f, "CurrencyId", "currencyId", "id");
            ulong who = RoomModeration.ULong(f, "AccountId", "accountId", "playerId");
            if (who == 0) who = account.Id;
            long bal = id.HasValue ? GetCurrencyBalance(who, id.Value) : 0;
            return J(new { CurrencyId = id, AccountId = who, Balance = bal, Amount = bal });
        }

        [HttpGet("/api/roomcurrencies/v1/getAllBalances")]
        public ContentResult GetAllBalancesRoute([FromQuery] long roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var list = CurrenciesForRoom(roomId).Select(c => new
            {
                c.CurrencyId, AccountId = account.Id, RoomId = c.RoomId, Balance = GetCurrencyBalance(account.Id, c.CurrencyId), Amount = GetCurrencyBalance(account.Id, c.CurrencyId)
            }).ToList();
            return J(list);
        }

        // JSON array [{AccountId, CurrencyId, Amount}] -> per-award results (circuits / owner grants).
        [HttpPost("/api/roomcurrencies/v1/awardCurrency/bulk")]
        public async Task<ContentResult> AwardCurrencyBulk()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            string raw = ""; try { using var r = new StreamReader(Request.Body); raw = await r.ReadToEndAsync(); } catch { }
            var results = new List<object>();
            try
            {
                var tok = Newtonsoft.Json.Linq.JToken.Parse(string.IsNullOrWhiteSpace(raw) ? "[]" : raw);
                var arr = tok as Newtonsoft.Json.Linq.JArray ?? (tok["Awards"] ?? tok["awards"] ?? tok["Requests"]) as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray();
                foreach (var a in arr)
                {
                    ulong pid = (ulong?)a["AccountId"] ?? (ulong?)a["accountId"] ?? (ulong?)a["PlayerId"] ?? 0;
                    Guid.TryParse((string?)a["CurrencyId"] ?? (string?)a["currencyId"] ?? "", out var cid);
                    long amount = (long?)a["Amount"] ?? (long?)a["amount"] ?? (long?)a["CurrencyAmount"] ?? 0;
                    var cur = cid != Guid.Empty ? GetCurrency(cid) : null;
                    if (cur == null || pid == 0) { results.Add(new { AccountId = pid, CurrencyId = cid, Success = false, Error = "No such currency", ResultingBalance = 0L }); continue; }
                    // The room's owners - or a player IN this instance (the room's circuits run as one),
                    // awarding someone in the same instance. See AwardRefusal.
                    var refused = AwardRefusal(cur.RoomId, account.Id, pid, arr.Count);
                    if (refused != null) { results.Add(new { AccountId = pid, CurrencyId = cid, Success = false, Error = refused, ResultingBalance = 0L }); continue; }
                    var nb = AddCurrency(pid, cur, amount);
                    results.Add(new { AccountId = pid, CurrencyId = cid, Success = nb != null, Error = nb == null ? "Insufficient balance" : (string?)null, ResultingBalance = nb ?? GetCurrencyBalance(pid, cid) });
                }
            }
            catch (Exception ex) { Console.WriteLine($"[roomcurrency] award parse failed: {ex.Message} body={raw[..Math.Min(200, raw.Length)]}"); }
            return J(results);
        }

        [HttpPost("/api/roomcurrencies/v1/createPurchaseOffer")]
        public ContentResult CreateOfferRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            Console.WriteLine($"[roomcurrency] {account.Id} create offer: {RoomModeration.Dump(f)}");
            var cid = G(f, "CurrencyId", "currencyId");
            var cur = cid.HasValue ? GetCurrency(cid.Value) : null;
            if (cur == null) return J(Envelope(null, "No such currency"));
            if (!CanEdit(cur.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"));
            var o = CreateOffer(new CurrencyOffer
            {
                CurrencyId = cur.CurrencyId, RoomId = cur.RoomId,
                Order = (int)L(f, "Order", "order"),
                Name = (S(f, "Name", "name") ?? $"{cur.Name} pack").Trim(),
                CurrencyAmount = Math.Max(0, L(f, "CurrencyAmount", "currencyAmount", "Amount")),
                Price = Math.Max(0, L(f, "Price", "price"))
            });
            return J(Envelope(OfferDto(o)));
        }

        [HttpPost("/api/roomcurrencies/v1/updatePurchaseOffer")]
        [HttpPut("/api/roomcurrencies/v1/updatePurchaseOffer")]
        public ContentResult UpdateOfferRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            var oid = G(f, "CurrencyPurchaseOfferId", "currencyPurchaseOfferId", "OfferId", "id");
            var o = oid.HasValue ? GetOffer(oid.Value) : null;
            if (o == null) return J(Envelope(null, "No such offer"));
            if (!CanEdit(o.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"));
            Console.WriteLine($"[roomcurrency] {account.Id} update offer {oid}: {RoomModeration.Dump(f)}");
            o = UpdateOffer(oid!.Value, x =>
            {
                if (S(f, "Name", "name") is string n && n.Trim().Length > 0) x.Name = n.Trim();
                if (f.ContainsKey("Order") || f.ContainsKey("order")) x.Order = (int)L(f, "Order", "order");
                if (f.ContainsKey("CurrencyAmount") || f.ContainsKey("currencyAmount")) x.CurrencyAmount = Math.Max(0, L(f, "CurrencyAmount", "currencyAmount"));
                if (f.ContainsKey("Price") || f.ContainsKey("price")) x.Price = Math.Max(0, L(f, "Price", "price"));
            })!;
            return J(Envelope(OfferDto(o)));
        }

        [HttpPost("/api/roomcurrencies/v1/deletePurchaseOffer")]
        [HttpDelete("/api/roomcurrencies/v1/deletePurchaseOffer")]
        public ContentResult DeleteOfferRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            var oid = G(f, "CurrencyPurchaseOfferId", "currencyPurchaseOfferId", "OfferId", "id");
            var o = oid.HasValue ? GetOffer(oid.Value) : null;
            if (o == null) return J(Envelope(null, "No such offer"));
            if (!CanEdit(o.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"));
            UpdateOffer(oid!.Value, x => x.Deleted = true);
            return J(Envelope(null));
        }

        // GET ?ids=<guid>&ids=... (<100) or POST form ids= (>=100). One element PER REQUESTED id.
        [HttpGet("/api/roomcurrencies/v1/getPurchaseOffersBatch")]
        [HttpPost("/api/roomcurrencies/v1/getPurchaseOffersBatch")]
        public ContentResult OffersBatch()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            var raw = (S(f, "ids", "Ids", "id", "currencyIds") ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<object>();
            foreach (var s in raw)
            {
                if (!Guid.TryParse(s.Trim(), out var cid)) continue;
                list.Add(new { CurrencyId = cid, PurchaseOffers = OffersFor(cid).Select(OfferDto).ToList() });
            }
            return J(list);
        }

        // Buy a currency offer with tokens: {CurrencyPurchaseOfferId, RequestedPrice}.
        [HttpPost("/api/roomCurrencies/v2/purchase")]
        [HttpPost("/api/roomcurrencies/v1/purchase")]
        public ContentResult BuyCurrency()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            Console.WriteLine($"[roomcurrency] {account.Id} buy: {RoomModeration.Dump(f)}");
            var oid = G(f, "CurrencyPurchaseOfferId", "currencyPurchaseOfferId", "OfferId", "offerId", "PurchaseOfferId", "id");
            var o = oid.HasValue ? GetOffer(oid.Value) : null;
            if (o == null) return Refuse("No such offer");
            var cur = GetCurrency(o.CurrencyId);
            if (cur == null) return Refuse("This currency no longer exists");
            long requested = L(f, "RequestedPrice", "requestedPrice", "Price");
            if (requested > 0 && requested != o.Price) Console.WriteLine($"[roomcurrency] client rendered {requested} for offer {oid}, charging the stored {o.Price}");
            long balance = PlayerDB.GetBalance(account.Id, Tokens);
            if (o.Price > 0 && balance < o.Price) return Refuse("Not enough tokens");
            if (o.Price > 0) balance = PlayerDB.AddBalance(account.Id, Tokens, -o.Price);
            long got = AddCurrency(account.Id, cur, o.CurrencyAmount) ?? GetCurrencyBalance(account.Id, cur.CurrencyId);
            EarningsDB.Record(cur.CreatorId != 0 ? cur.CreatorId : RoomCreator(cur.RoomId), account.Id, "roomcurrency", cur.CurrencyId.ToString(), $"{o.CurrencyAmount} {cur.Name}", Tokens, o.Price);
            GiftBoxes.PushBalance(account.Id, Tokens, balance, "room currency");
            Console.WriteLine($"[roomcurrency] @{account.Username} bought {o.CurrencyAmount} {cur.Name} for {o.Price} tokens -> {balance} tokens, {got} {cur.Name}");
            var data = new { CurrencyId = cur.CurrencyId, Balance = got, Amount = o.CurrencyAmount, PurchaseOffer = OfferDto(o), Currency = CurrencyDto(cur) };
            return J(new
            {
                Value = new { Balance = balance, CurrencyType = Tokens, Platform = Classes.GameCurrency.AllPlatformsBucket, BalanceUpdates = new[] { new { UpdateResponse = 0, Data = data } }, CurrencyBalance = got, CurrencyId = cur.CurrencyId },
                Success = true, Error = (string?)null, error_id = (string?)null,
                Balance = balance, CurrencyType = Tokens, Platform = Classes.GameCurrency.AllPlatformsBucket, BalanceType = Classes.GameCurrency.AllPlatformsBucket,
                BalanceUpdates = new[] { new { UpdateResponse = 0, Data = data } },
                CurrencyBalance = got, CurrencyId = cur.CurrencyId
            });
        }

        // ======================================================================= CONSUMABLES

        [HttpGet("/api/roomconsumables/v1/roomConsumable/room/{roomId:long}")]
        public ContentResult ConsumablesOfRoom(long roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return J(ConsumablesForRoom(roomId).Select(ConsumableDto).ToList());
        }

        [HttpGet("/api/roomconsumables/v1/roomConsumable/room/{roomId:long}/me")]
        public ContentResult MyConsumablesInRoom(long roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var list = ConsumablesOwnedInRoom(account.Id, roomId).Select(o =>
            {
                var c = GetConsumable(o.RoomConsumableId);
                return new
                {
                    o.RoomConsumableId, o.RoomId, AccountId = account.Id, PlayerId = account.Id, o.Count, Quantity = o.Count,
                    ModifiedAt = o.ModifiedAt.ToString("o"), RoomConsumable = c != null ? ConsumableDto(c) : null
                };
            }).ToList();
            return J(list);
        }

        [HttpGet("/api/roomconsumables/v1/roomConsumable/{id:guid}")]
        public ContentResult GetConsumableRoute(Guid id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var c = GetConsumable(id);
            return c == null ? J(Envelope(null, "No such consumable"), 404) : J(ConsumableDto(c));
        }

        [HttpGet("/api/roomconsumables/v1/roomConsumable/{id:guid}/isOwned")]
        public ContentResult ConsumableOwned(Guid id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return J(false);
            return J(ConsumableCount(account.Id, id) > 0);
        }

        // Create (PUT/POST roomConsumable) - form/JSON: RoomId, Name, Description, ImageName, Price, PurchaseCurrencyId, MaximumCountPerPurchase.
        [HttpPut("/api/roomconsumables/v1/roomConsumable")]
        [HttpPost("/api/roomconsumables/v1/roomConsumable")]
        [HttpPut("/roomconsumables/v1/roomConsumable")]
        [HttpPost("/api/roomconsumables/v1/roomConsumable/create")]
        public ContentResult CreateConsumableRoute()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            Console.WriteLine($"[roomconsumable] {account.Id} create: {RoomModeration.Dump(f)}");
            long roomId = L(f, "RoomId", "roomId");
            if (roomId <= 0) return J(Envelope(null, "RoomId is required"), 400);
            if (!CanEdit(roomId, account.Id)) return J(Envelope(null, "You cannot edit this room"), 403);
            var c = CreateConsumable(new RoomConsumable
            {
                RoomId = roomId, CreatorId = RoomCreator(roomId),
                Name = (S(f, "Name", "name") ?? "Consumable").Trim(),
                Description = S(f, "Description", "description") ?? "",
                ImageName = S(f, "ImageName", "imageName"),
                Price = Math.Max(0, L(f, "Price", "price")),
                PurchaseCurrencyId = G(f, "PurchaseCurrencyId", "purchaseCurrencyId"),
                MaximumCountPerPurchase = f.ContainsKey("MaximumCountPerPurchase") || f.ContainsKey("maximumCountPerPurchase") ? (int)Math.Max(1, L(f, "MaximumCountPerPurchase", "maximumCountPerPurchase")) : 10
            });
            var dto = ConsumableDto(c);
            return J(new { Value = dto, Success = true, Error = (string?)null, error_id = (string?)null,
                           c.RoomConsumableId, c.RoomId, c.Name, c.Description, ImageName = c.ImageName ?? "", c.Price, c.PurchaseCurrencyId, ModifiedAt = c.ModifiedAt.ToString("o"), c.MaximumCountPerPurchase });
        }

        [HttpPut("/api/roomconsumables/v1/roomConsumable/{id:guid}")]
        [HttpPost("/api/roomconsumables/v1/roomConsumable/{id:guid}")]
        [HttpPost("/api/roomconsumables/v1/roomConsumable/{id:guid}/update")]
        public ContentResult UpdateConsumableRoute(Guid id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var c = GetConsumable(id);
            if (c == null) return J(Envelope(null, "No such consumable"), 404);
            if (!CanEdit(c.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"), 403);
            var f = F();
            Console.WriteLine($"[roomconsumable] {account.Id} update {id}: {RoomModeration.Dump(f)}");
            c = UpdateConsumable(id, x =>
            {
                if (S(f, "Name", "name") is string n && n.Trim().Length > 0) x.Name = n.Trim();
                if (S(f, "Description", "description") is string d) x.Description = d;
                if (S(f, "ImageName", "imageName") is string img) x.ImageName = img;
                if (f.ContainsKey("Price") || f.ContainsKey("price")) x.Price = Math.Max(0, L(f, "Price", "price"));
                if (f.ContainsKey("PurchaseCurrencyId") || f.ContainsKey("purchaseCurrencyId")) x.PurchaseCurrencyId = G(f, "PurchaseCurrencyId", "purchaseCurrencyId");
                if (f.ContainsKey("MaximumCountPerPurchase") || f.ContainsKey("maximumCountPerPurchase")) x.MaximumCountPerPurchase = (int)Math.Max(1, L(f, "MaximumCountPerPurchase", "maximumCountPerPurchase"));
            })!;
            return J(Envelope(ConsumableDto(c)));
        }

        [HttpDelete("/api/roomconsumables/v1/roomConsumable/{id:guid}")]
        [HttpPost("/api/roomconsumables/v1/roomConsumable/{id:guid}/delete")]
        public ContentResult DeleteConsumableRoute(Guid id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var c = GetConsumable(id);
            if (c == null) return J(Envelope(null, "No such consumable"), 404);
            if (!CanEdit(c.RoomId, account.Id)) return J(Envelope(null, "You cannot edit this room"), 403);
            UpdateConsumable(id, x => x.Deleted = true);
            return J(Envelope(null));
        }

        // Use one (or Count) - the room's circuits consume on the player's behalf.
        [HttpPost("/api/roomconsumables/v1/roomConsumable/{id:guid}/consume")]
        public ContentResult ConsumeRoute(Guid id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var c = GetConsumable(id);
            if (c == null) return J(Envelope(null, "No such consumable"), 404);
            var f = F();
            int n = (int)Math.Max(1, L(f, "Count", "count", "Amount", "amount"));
            var left = AddConsumable(account.Id, c, -n);
            if (left == null) return J(Envelope(null, "You do not have enough of that"));
            return J(new { Value = new { RoomConsumableId = id, Count = left.Value, Quantity = left.Value }, Success = true, Error = (string?)null, error_id = (string?)null, RoomConsumableId = id, Count = left.Value });
        }

        [HttpPost("/api/roomconsumables/v1/roomConsumable/awardBulk")]
        public async Task<ContentResult> AwardConsumables()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            string raw = ""; try { using var r = new StreamReader(Request.Body); raw = await r.ReadToEndAsync(); } catch { }
            var results = new List<object>();
            try
            {
                var tok = Newtonsoft.Json.Linq.JToken.Parse(string.IsNullOrWhiteSpace(raw) ? "[]" : raw);
                var arr = tok as Newtonsoft.Json.Linq.JArray ?? (tok["Awards"] ?? tok["awards"]) as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray();
                foreach (var a in arr)
                {
                    ulong pid = (ulong?)a["AccountId"] ?? (ulong?)a["accountId"] ?? (ulong?)a["PlayerId"] ?? 0;
                    Guid.TryParse((string?)a["RoomConsumableId"] ?? (string?)a["roomConsumableId"] ?? "", out var cid);
                    int count = (int?)a["Count"] ?? (int?)a["count"] ?? (int?)a["Amount"] ?? 1;
                    var c = cid != Guid.Empty ? GetConsumable(cid) : null;
                    if (c == null || pid == 0) { results.Add(new { AccountId = pid, RoomConsumableId = cid, Success = false, Error = "No such consumable" }); continue; }
                    var refused = AwardRefusal(c.RoomId, account.Id, pid, arr.Count);
                    if (refused != null) { results.Add(new { AccountId = pid, RoomConsumableId = cid, Success = false, Error = refused }); continue; }
                    if (!CanEdit(c.RoomId, account.Id)) count = Math.Clamp(count, -MaxConsumablesPerAward, MaxConsumablesPerAward);
                    var nc = AddConsumable(pid, c, count);
                    results.Add(new { AccountId = pid, RoomConsumableId = cid, Success = nc != null, Error = (string?)null, Count = nc ?? 0 });
                }
            }
            catch (Exception ex) { Console.WriteLine($"[roomconsumable] award parse failed: {ex.Message}"); }
            return J(results);
        }

        // Buy with tokens or with the room currency the consumable is priced in: {Count, RequestedPrice}.
        // The client PUTs the two purchase/ routes (5 x 405 in two days with POST only); both verbs work now.
        [HttpPut("/api/roomconsumables/v1/roomconsumable/{id:guid}/purchase/tokens")]
        [HttpPut("/api/roomconsumables/v1/roomconsumable/{id:guid}/purchase/currency")]
        [HttpPost("/api/roomconsumables/v1/roomconsumable/{id:guid}/purchase/tokens")]
        [HttpPost("/api/roomconsumables/v1/roomconsumable/{id:guid}/purchase/currency")]
        [HttpPost("/api/roomconsumables/v1/roomConsumable/{id:guid}/purchase")]
        public ContentResult BuyConsumable(Guid id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = F();
            Console.WriteLine($"[roomconsumable] {account.Id} buy {Request.Path}: {RoomModeration.Dump(f)}");
            var c = GetConsumable(id);
            if (c == null) return Refuse("No such consumable");
            int count = (int)Math.Max(1, L(f, "Count", "count", "Amount", "amount", "Quantity"));
            if (c.MaximumCountPerPurchase > 0 && count > c.MaximumCountPerPurchase) count = c.MaximumCountPerPurchase;
            long unit = Math.Max(0, c.Price);
            long total = unit * count;
            long requested = L(f, "RequestedPrice", "requestedPrice", "Price");
            if (requested > 0 && requested != unit && requested != total) Console.WriteLine($"[roomconsumable] client rendered {requested}, charging {total} ({unit} x {count})");

            bool withCurrency = Request.Path.Value!.EndsWith("/currency", StringComparison.OrdinalIgnoreCase) || (c.PurchaseCurrencyId.HasValue && !Request.Path.Value.EndsWith("/tokens", StringComparison.OrdinalIgnoreCase));
            if (withCurrency && c.PurchaseCurrencyId.HasValue)
            {
                var cur = GetCurrency(c.PurchaseCurrencyId.Value);
                if (cur == null) return Refuse("This item's currency no longer exists");
                var left = AddCurrency(account.Id, cur, -total);
                if (left == null) return Refuse($"Not enough {cur.Name}");
                int have = AddConsumable(account.Id, c, count) ?? 0;
                Console.WriteLine($"[roomconsumable] @{account.Username} bought {count}x '{c.Name}' for {total} {cur.Name} -> {left}, has {have}");
                var d = new { RoomConsumableId = id, Count = have, Quantity = have, RoomConsumable = ConsumableDto(c) };
                return J(new { Value = new { Balance = left.Value, CurrencyId = cur.CurrencyId, CurrencyType = 300, BalanceUpdates = new[] { new { UpdateResponse = 0, Data = d } } }, Success = true, Error = (string?)null, error_id = (string?)null,
                               Balance = left.Value, CurrencyType = 300, CurrencyId = cur.CurrencyId, Platform = Classes.GameCurrency.AllPlatformsBucket, BalanceUpdates = new[] { new { UpdateResponse = 0, Data = d } } });
            }

            long balance = PlayerDB.GetBalance(account.Id, Tokens);
            if (total > 0 && balance < total) return Refuse("Not enough tokens");
            if (total > 0) balance = PlayerDB.AddBalance(account.Id, Tokens, -total);
            int now = AddConsumable(account.Id, c, count) ?? 0;
            EarningsDB.Record(c.CreatorId != 0 ? c.CreatorId : RoomCreator(c.RoomId), account.Id, "roomconsumable", c.RoomConsumableId.ToString(), c.Name, Tokens, total);
            GiftBoxes.PushBalance(account.Id, Tokens, balance, "room consumable");
            Console.WriteLine($"[roomconsumable] @{account.Username} bought {count}x '{c.Name}' for {total} tokens -> {balance}, has {now}");
            return J(BalanceAnswer(balance, new { RoomConsumableId = id, Count = now, Quantity = now, RoomConsumable = ConsumableDto(c) }));
        }
    }
}
