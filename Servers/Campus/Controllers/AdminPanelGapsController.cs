using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace CampusLiveSource.Controllers
{
    /// <summary>
    /// Admin panel routes the page (Web/Admin/Home/HomeV4.html) called but the server never had -
    /// each one was a 404 behind a button: Dashboard "View who is online", "Total Inventions",
    /// the Rooms tab's View / Edit links, Give Tokens, Log out, and the page's own script.
    /// Same auth as the rest of the panel (dev token + admin gate cookie via the middleware).
    /// </summary>
    public class AdminPanelGapsController : ControllerBase
    {
        private static ContentResult Json(object o) => new()
        {
            Content = JsonConvert.SerializeObject(o),
            ContentType = "application/json",
            StatusCode = 200
        };

        private static string? FindAdminFile(params string[] relative)
        {
            foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                var p = Path.Combine(new[] { root }.Concat(relative).ToArray());
                if (System.IO.File.Exists(p)) return p;
            }
            return null;
        }

        // ---- the page's script: theme (the website's light / dark / autumn + accent), the side
        // navigation and the room editor. Web/Admin/Home/home_page.js, shipped with the page.
        [HttpGet("/admin/home_page.js")]
        public IActionResult HomePageScript()
        {
            // Dev OR mod: this is the page's own shared JS (theme, nav, etc), not a privileged data
            // endpoint - a mod's page would fail to render at all without it.
            var (_, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            var path = FindAdminFile("Web", "Admin", "Home", "home_page.js");
            if (path == null) return Content("/* home_page.js not deployed */", "application/javascript");
            Response.Headers["Cache-Control"] = "no-cache";
            return PhysicalFile(path, "application/javascript");
        }

        // ---- log out: drop the dev token and the admin session, back to the login page ----------
        [HttpGet("/admin/logout")]
        public IActionResult Logout()
        {
            Response.Cookies.Delete("Authorization", new CookieOptions { Path = "/" });
            Response.Cookies.Delete(AdminGate.CookieName, new CookieOptions { Path = "/" });
            return Redirect("/admin/login");
        }

        // ---- Dashboard "View who is online": ids of players holding a live game connection -----
        [HttpGet("/admin/api/who_is_online")]
        public ContentResult WhoIsOnline()
        {
            var (_, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            var ids = NotificationsController.WebSockets.Keys.Distinct().OrderBy(x => x).ToList();
            return Json(ids);
        }

        // ---- where a player is, as text (the online list prints "is currently in <this>") -------
        [HttpGet("/admin/api/heartbeat_data/{playerId}/name")]
        public ContentResult HeartbeatRoomName(ulong playerId)
        {
            var (_, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            var hb = HeartbeatDB.GetPlayerHeartbeat(playerId);
            var inst = hb?.roomInstance;
            string text = inst == null
                ? "no room (menus / loading)"
                : (inst.isPrivate ? "[PRIVATE] " : "") + (string.IsNullOrEmpty(inst.name) ? $"room {inst.roomId}" : inst.name)
                  + $" (room {inst.roomId}, instance {inst.roomInstanceId})";
            return new ContentResult { Content = text, ContentType = "text/plain", StatusCode = 200 };
        }

        // ---- Give Tokens (Rec Center Tokens, credited now and pushed live) ----------------------
        [HttpPost("/admin/api/give_tokens")]
        public ContentResult GiveTokens()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!ulong.TryParse(Request.Form["playerId"].ToString().Trim(), out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            if (!long.TryParse(Request.Form["tokenAmount"].ToString().Trim(), out long amount) || amount == 0)
                return Json(new { success = false, message = "Amount must be a non-zero whole number." });
            if (PlayerDB.GetFullAccount(playerId)?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            int tokens = (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens;
            long total = PlayerDB.AddBalance(playerId, tokens, amount);
            Notifications.FireAndForget(
                Notifications.SendToPlayer(playerId, JsonConvert.SerializeObject(WebsocketEvents.createResponse_balanceupdate(tokens, total))),
                "admin give_tokens push");
            Console.WriteLine($"[admin] {account!.Username} ({account.Id}) gave {amount} tokens to {playerId} -> {total}");
            return Json(new { success = true, message = $"Gave {amount} tokens. New balance {total}.", balance = total });
        }

        // ---- inventions: the Dashboard "Total Inventions" list and one invention's details -------
        [HttpGet("/admin/api/inventions")]
        public ContentResult Inventions()
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var list = InventionDB.Collection().FindAll()
                .OrderByDescending(i => i.ModifiedAt)
                .Take(500)
                .Select(i => new
                {
                    i.InventionId, i.Name, i.Description, i.ImageName, i.CreatorPlayerId,
                    i.CreatedAt, LastModifiedAt = i.ModifiedAt, i.IsPublished, i.NumDownloads, i.CheerCount
                })
                .ToList();
            return Json(list);
        }

        [HttpGet("/admin/api/inventions_bulk/{inventionId:int}")]
        public ContentResult InventionById(int inventionId)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var i = InventionDB.GetById(inventionId);
            if (i == null) return Json(new { });
            return Json(new
            {
                i.InventionId, i.Name, i.Description, i.ImageName, i.CreatorPlayerId, i.CreatedAt,
                LastModifiedAt = i.ModifiedAt, i.IsPublished, i.NumDownloads, i.CheerCount, i.Price, i.Tags
            });
        }

        // ---- rooms: "View Room Details" / "View Dorm" / "Edit" open the panel's room editor -------
        // Both old links (a page that never existed) now land on the panel with ?room=<id>, and
        // home_page.js opens the editor for it.
        [HttpGet("/admin/home/view_room/{roomId}")]
        [HttpGet("/admin/edit-room/{roomId}")]
        public IActionResult OpenRoomEditor(string roomId)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return Redirect("/admin/v4/login");
            return Redirect($"/admin/home/v4?room={Uri.EscapeDataString(roomId)}");
        }

        [HttpGet("/admin/api/room/{roomId}")]
        public ContentResult RoomDetails(ulong roomId)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var r = RoomDB.GetRoom(roomId);
            if (r == null) return Json(new { success = false, message = $"No room {roomId}." });
            return Json(new
            {
                success = true,
                room = new
                {
                    r.RoomId, r.Name, r.Description, r.ImageName, r.CreatorAccountId,
                    CreatorName = r.CreatorAccountId == 0 ? "" : PlayerDB.GetPlayerNameById(r.CreatorAccountId),
                    Accessibility = (int)(r.SubRooms?.FirstOrDefault()?.Accessibility ?? RoomDBClasses.RoomAccessibility.Public),
                    r.CloningAllowed, r.IsDorm, r.IsRRO, DevRoom = r.IsDevRoom || r.IsDeveloperOwned, r.MaxPlayers, r.SupportsJuniors, r.CreatedAt,
                    PlayersNow = HeartbeatDB.CountInRoom(roomId),
                    SubRooms = (r.SubRooms ?? new()).Select(s => new { s.SubRoomId, s.Name, s.MaxPlayers, s.UnitySceneId, Accessibility = (int)s.Accessibility })
                }
            });
        }

        [HttpPost("/admin/api/edit_room")]
        public ContentResult EditRoom()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!ulong.TryParse(Request.Form["roomId"].ToString(), out ulong roomId))
                return Json(new { success = false, message = "Invalid room id." });
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return Json(new { success = false, message = $"No room {roomId}." });

            var changed = new List<string>();
            string name = Request.Form["name"].ToString().Trim().TrimStart('^');
            if (name.Length > 0 && name != room.Name)
            {
                if (RoomDB.SetRoomName(roomId, name) == null)
                    return Json(new { success = false, message = $"Another room is already called ^{name}." });
                changed.Add("name");
            }
            if (Request.Form.ContainsKey("description"))
            {
                string d = Request.Form["description"].ToString();
                if (d != (room.Description ?? "")) { RoomDB.SetRoomDescription(roomId, d); changed.Add("description"); }
            }
            int curAcc = (int)(room.SubRooms?.FirstOrDefault()?.Accessibility ?? RoomDBClasses.RoomAccessibility.Public);
            if (int.TryParse(Request.Form["accessibility"].ToString(), out int acc)
                && Enum.IsDefined(typeof(RoomDBClasses.RoomAccessibility), acc) && acc != curAcc)
            {
                RoomDB.SetRoomAccessibility(roomId, (RoomDBClasses.RoomAccessibility)acc); changed.Add("accessibility");
            }
            if (bool.TryParse(Request.Form["cloningAllowed"].ToString(), out bool clone) && clone != room.CloningAllowed)
            {
                RoomDB.SetRoomCloning(roomId, clone); changed.Add("cloning");
            }
            if (bool.TryParse(Request.Form["devRoom"].ToString(), out bool dev) && dev != (room.IsDevRoom || room.IsDeveloperOwned))
            {
                RoomDB.SetDevRoom(roomId, dev); changed.Add(dev ? "dev room on" : "dev room off");
            }
            if (int.TryParse(Request.Form["maxPlayers"].ToString(), out int max) && max >= 1 && max <= 100 && max != room.MaxPlayers)
            {
                RoomDB.SetMaxPlayers(roomId, max); changed.Add("max players");
            }

            Console.WriteLine($"[admin] {account!.Username} ({account.Id}) edited room {roomId}: {(changed.Count == 0 ? "nothing" : string.Join(", ", changed))}");
            return Json(new { success = true, message = changed.Count == 0 ? "Nothing changed." : "Saved " + string.Join(", ", changed) + "." });
        }

        // ---- clubs: the Dashboard "View All Clubs" list ------------------------------------------
        [HttpGet("/admin/api/clubs")]
        public ContentResult ClubsList()
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var list = ClubDB.All().Select(c => new
            {
                c.ClubId, c.Name, c.Description, c.MainImageName, c.CreatorAccountId,
                CreatorName = c.CreatorAccountId == 0 ? "" : PlayerDB.GetPlayerNameById(c.CreatorAccountId),
                c.CreatedAt, Members = ClubDB.MemberCount(c.ClubId), c.Visibility
            }).ToList();
            return Json(list);
        }

        // ---- rooms: the Rooms tab's Delete (a button that had no handler and no route) -----------
        [HttpPost("/admin/api/delete_room")]
        public ContentResult DeleteRoom()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!ulong.TryParse(Request.Form["roomId"].ToString(), out ulong roomId))
                return Json(new { success = false, message = "Invalid room id." });
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return Json(new { success = false, message = $"No room {roomId}." });
            // A dorm belongs to a player account - deleting it leaves them with nowhere to spawn.
            if (room.IsDorm) return Json(new { success = false, message = $"^{room.Name} is a player's dorm and can't be deleted here." });
            RoomDB.DeleteRoom(roomId);
            Console.WriteLine($"[admin] {account!.Username} ({account.Id}) deleted room {roomId} ^{room.Name}");
            return Json(new { success = true, message = $"Deleted ^{room.Name}." });
        }

        // ---- skins: gift any of the 340 skins in Data/APIS/items/Equipments.json -----------------
        // The item gift path can't: a skin's store row has an empty AvatarItemDesc, which it rejects,
        // and 153 of the skins aren't sold at all. A skin box carries EquipmentPrefabName +
        // EquipmentModificationGuid; the store's GiftDropId is added when the skin is sold, so the
        // box draws exactly like a bought one. Queuing the box grants ownership
        // (PlayerDB.AddPendingGiftBoxFull), so the locker has it either way.
        public sealed class SkinRow
        {
            public string PrefabName = "", Guid = "", Name = "", Tool = "";
            public int Rarity, GiftDropId, PurchasableItemId;
        }
        private static List<SkinRow>? _skins;
        private static DateTime _skinsAt;

        private static List<SkinRow> Skins()
        {
            if (_skins != null && (DateTime.UtcNow - _skinsAt).TotalMinutes < 10) return _skins;
            var root = Environment.CurrentDirectory;
            var list = new List<SkinRow>();
            try
            {
                var arr = Newtonsoft.Json.Linq.JArray.Parse(System.IO.File.ReadAllText(Path.Combine(root, "Data", "APIS", "items", "Equipments.json")));
                foreach (var e in arr)
                {
                    string prefab = (string?)e["PrefabName"] ?? "";
                    list.Add(new SkinRow
                    {
                        PrefabName = prefab,
                        Guid = Campus_live_rewrite.api.server.Classes.ItemGuids.NormalizeGuid((string?)e["ModificationGuid"]) ?? ((string?)e["ModificationGuid"] ?? ""),
                        Name = (string?)e["FriendlyName"] ?? "",
                        Rarity = (int?)e["Rarity"] ?? 0,
                        Tool = prefab.Trim('[', ']')
                    });
                }
            }
            catch (Exception ex) { Console.WriteLine($"[admin] Equipments.json unreadable: {ex.Message}"); }

            // Which of them are sold, and under which GiftDropId (the box art).
            var sold = new Dictionary<string, (int drop, int pid)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var file in Directory.GetFiles(Path.Combine(root, "Data", "APIS", "GiftDropStores"), "*.json"))
                {
                    var doc = Newtonsoft.Json.Linq.JToken.Parse(System.IO.File.ReadAllText(file));
                    foreach (var item in doc.SelectTokens("$..StoreItems[*]"))
                    {
                        var gd = item["GiftDrop"];
                        string? pf = (string?)gd?["EquipmentPrefabName"];
                        if (string.IsNullOrEmpty(pf)) continue;
                        string key = PlayerDB.EquipmentSkinKey(pf, (string?)gd?["EquipmentModificationGuid"]);
                        if (!sold.ContainsKey(key)) sold[key] = ((int?)gd?["GiftDropId"] ?? 0, (int?)item["PurchasableItemId"] ?? 0);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[admin] store scan for skins failed: {ex.Message}"); }
            foreach (var r in list)
                if (sold.TryGetValue(PlayerDB.EquipmentSkinKey(r.PrefabName, r.Guid), out var s)) { r.GiftDropId = s.drop; r.PurchasableItemId = s.pid; }

            _skins = list; _skinsAt = DateTime.UtcNow;
            return list;
        }

        private static bool SkinMatches(SkinRow s, string term) =>
            term.Length == 0 || s.Name.ToLowerInvariant().Contains(term) || s.Tool.ToLowerInvariant().Contains(term)
            || s.Guid.ToLowerInvariant().StartsWith(term);

        [HttpGet("/admin/api/gift/skins")]
        public ContentResult SkinSearch([FromQuery] string? q, [FromQuery] string? playerId)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string term = (q ?? "").Trim().ToLowerInvariant();
            var all = Skins();
            var matching = all.Where(s => SkinMatches(s, term)).ToList();
            HashSet<string>? owned = ulong.TryParse(playerId, out var pid) ? PlayerDB.OwnedEquipmentSkins(pid) : null;
            return Json(new
            {
                success = true,
                total = all.Count,
                matches = matching.Count,
                items = matching.OrderBy(s => s.Tool).ThenBy(s => s.Name).Take(150).Select(s => new
                {
                    prefab = s.PrefabName, guid = s.Guid, name = s.Name, tool = s.Tool, rarity = s.Rarity,
                    sold = s.GiftDropId > 0,
                    owned = owned?.Contains(PlayerDB.EquipmentSkinKey(s.PrefabName, s.Guid))
                })
            });
        }

        [HttpPost("/admin/api/gift/skin")]
        public ContentResult SkinGive()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var form = Request.Form;
            string rawPid = form["playerId"].ToString().Trim();
            bool toAll = rawPid == "-1" || string.Equals(rawPid, "all", StringComparison.OrdinalIgnoreCase);
            ulong one = 0;
            if (!toAll && !ulong.TryParse(rawPid, out one)) return Json(new { success = false, message = "Invalid player id." });

            string key = PlayerDB.EquipmentSkinKey(form["prefab"].ToString(), form["guid"].ToString());
            var skin = Skins().FirstOrDefault(s => PlayerDB.EquipmentSkinKey(s.PrefabName, s.Guid) == key);
            if (skin == null) return Json(new { success = false, message = "That skin is not in Equipments.json." });
            bool locker = string.Equals(form["mode"].ToString(), "locker", StringComparison.OrdinalIgnoreCase);
            string msg = string.IsNullOrWhiteSpace(form["message"].ToString()) ? "A gift for you <3" : form["message"].ToString().Trim();
            if (msg.Length > 120) msg = msg.Substring(0, 120);

            bool GiveOne(ulong pid)
            {
                if (PlayerDB.OwnedEquipmentSkins(pid).Contains(key)) return false;
                if (locker)
                {
                    PlayerDB.GrantEquipmentSkin(pid, skin.PrefabName, skin.Guid);
                    Notifications.FireAndForget(Notifications.RefreshAccount(pid), "admin skin refresh");
                    return true;
                }
                var box = new PendingGiftBox
                {
                    NumericId = Campus_live_rewrite.api.server.StoreCatalog.NextPurchaseGiftId(),
                    GiftContext = 0,
                    Message = msg,
                    FromAccountId = 0,
                    Anonymous = false,
                    AvatarItemDesc = "",
                    ConsumableItemDesc = "",
                    GiftDropId = skin.GiftDropId,
                    FriendlyName = skin.Name,
                    Rarity = skin.Rarity,
                    EquipmentPrefabName = skin.PrefabName,
                    EquipmentModificationGuid = skin.Guid
                };
                PlayerDB.AddPendingGiftBoxFull(pid, box, grantSkin: true);
                Campus_live_rewrite.api.server.GiftBoxes.Push(pid, box, Campus_live_rewrite.api.server.GiftBoxes.FrameReceivedImmediate, "admin skin gift");
                Notifications.FireAndForget(Notifications.RefreshAccount(pid), "admin skin refresh");
                return true;
            }

            int given = 0, skipped = 0;
            if (toAll)
            {
                foreach (var p in PlayerDB.PlayerDBFile.GetCollection<player_data>("players").FindAll().ToList())
                {
                    if (p?.player == null) continue;
                    if (GiveOne(p.playerid)) given++; else skipped++;
                }
            }
            else
            {
                if (PlayerDB.GetFullAccount(one)?.player == null) return Json(new { success = false, message = $"No player with id {one}." });
                if (GiveOne(one)) given = 1;
                else return Json(new { success = false, message = $"Player {one} already owns {skin.Name}." });
            }
            Console.WriteLine($"[admin] {account!.Username} ({account.Id}) gave skin '{skin.Name}' ({skin.PrefabName} {skin.Guid}) {(locker ? "to the locker" : "as a gift box")} to {(toAll ? $"{given} player(s), {skipped} already owned it" : rawPid)}");
            return Json(new
            {
                success = true,
                message = $"Gave {skin.Name} {(locker ? "straight to the locker" : "as a gift box")} to {(toAll ? $"{given} player(s) ({skipped} already had it)" : "the player")}."
            });
        }

        // ---- MIC BANS ---------------------------------------------------------------------------
        // Rec Room's mic ban (PlayerDB "MIC BAN"): the token's rn.privilege BanVChat switches the
        // mic off and the Moderation Notice lists it with a countdown. The player keeps playing.
        // minutes 0 = permanent (its countdown loops). Logged in Ban Logs as mic-ban / mic-unban.
        [HttpGet("/admin/api/mic_ban/{id}")]
        public ContentResult MicBanStatus(ulong id)
        {
            var (_, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            var b = PlayerDB.GetActiveMicBan(id);
            return Json(new
            {
                success = true,
                active = b != null,
                permanent = b?.Duration == int.MaxValue,
                remainingSeconds = b?.Duration ?? 0,
                message = b?.Message ?? ""
            });
        }

        [HttpPost("/admin/api/mic_ban")]
        public ContentResult MicBan()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            var form = Request.Form;
            if (!ulong.TryParse(form["playerId"].ToString(), out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            if (playerId == account!.Id)
                return Json(new { success = false, message = "You can't mic ban yourself." });
            if (PlayerDB.CheckDevFlag(playerId))
                return Json(new { success = false, message = "Can't mic ban this player." });
            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });
            long minutes = long.TryParse(form["minutes"].ToString(), out var m) ? m : 0;
            if (minutes < 0) return Json(new { success = false, message = "Duration can't be negative." });
            int seconds = minutes == 0 || minutes * 60 >= int.MaxValue ? int.MaxValue : (int)(minutes * 60);
            string reason = form["reason"].ToString().Trim();
            if (reason.Length > 200) reason = reason.Substring(0, 200);
            if (PlayerDB.SetMicBan(playerId, seconds, reason, Moderation_Detail.ReportCategory.Moderator, account.Id) == null)
                return Json(new { success = false, message = "Could not save the mic ban." });
            bool online = CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(playerId);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) mic banned {playerId} for {(seconds == int.MaxValue ? "permanent" : minutes + " min")}");
            string span = seconds == int.MaxValue ? "permanently" : minutes >= 1440 && minutes % 1440 == 0 ? $"for {minutes / 1440} day(s)" : minutes >= 60 && minutes % 60 == 0 ? $"for {minutes / 60} hour(s)" : $"for {minutes} minute(s)";
            return Json(new
            {
                success = true,
                message = $"@{acc.player.Username} is mic banned {span}." +
                          (online ? " Their game was told to refresh, so it applies now." : " It applies when they next log in.")
            });
        }

        [HttpPost("/admin/api/mic_unban")]
        public ContentResult MicUnban()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            if (!ulong.TryParse(Request.Form["playerId"].ToString(), out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });
            bool had = PlayerDB.ClearMicBan(playerId, account!.Id);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) lifted the mic ban on {playerId} (was active: {had})");
            return Json(new
            {
                success = true,
                message = had ? $"Lifted the mic ban on @{acc.player.Username}." : $"@{acc.player.Username} had no mic ban."
            });
        }
    }
}
