using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;
using CampusLiveSource.Controllers;

namespace Campus_live_rewrite.Controllers
{
    // ---------------------------------------------------------------------------------
    // The website's quick-admin popup (the "Admin" button on a profile or room page).
    //
    // Who: a WEBSITE session (campus_site cookie) on a dev-flagged account, from an IP that could
    // reach the admin panel itself (AdminProtectionMiddleware.AdminReachableFrom - the panel's
    // allowlist). /api/site/me says `admin: true` only when all of that holds, which is what shows
    // the button.
    //
    // What: a FIXED list of actions, each run by the admin panel's own handler (AdminController /
    // AdminPanelGapsController) in-process with a two-minute admin token for the signed-in
    // account - so a ban, kick or mic ban does exactly what the panel's does, including its
    // checks (no banning devs, no kicking yourself), its Ban Logs entry and its webhook, and is
    // attributed to the admin who clicked. Nothing else of the panel is reachable through here.
    // ---------------------------------------------------------------------------------
    [ApiController]
    public class SiteAdminController : ControllerBase
    {
        private const string SiteCookie = "campus_site";

        // Signed-in dev on an admin-reachable IP, or null.
        private player_info? Admin()
        {
            string token = Request.Cookies[SiteCookie] ?? "";
            if (string.IsNullOrEmpty(token)) return null;
            string data = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_")) return null;
            player_info? me;
            try { me = JsonConvert.DeserializeObject<player_info>(data); } catch { return null; }
            if (me == null || !PlayerDB.CheckDevFlag(me.Id)) return null;
            if (!AdminProtectionMiddleware.AdminReachableFrom(PatchGateMiddleware.ClientIp(HttpContext))) return null;
            return me;
        }

        /// <summary>For /api/site/me: should this session see the Admin button.</summary>
        public static bool ShowsAdmin(HttpContext ctx, ulong accountId) =>
            PlayerDB.CheckDevFlag(accountId) && AdminProtectionMiddleware.AdminReachableFrom(PatchGateMiddleware.ClientIp(ctx));

        // 40 actions a minute per admin - plenty for clicking, a brake on a runaway script.
        private static readonly ConcurrentDictionary<ulong, (int n, long start)> _rate = new();
        private static bool Throttled(ulong id)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var e = _rate.AddOrUpdate(id, _ => (1, now), (_, c) => now - c.start >= 60 ? (1, now) : (c.n + 1, c.start));
            return e.n > 40;
        }

        private IActionResult Denied() => StatusCode(403, new { ok = false, error = "Admin only." });

        private static ulong ResolvePlayer(string key)
        {
            key = (key ?? "").Trim().TrimStart('@');
            if (ulong.TryParse(key, out var id) && PlayerDB.GetFullAccount(id)?.player != null) return id;
            return PlayerDB.GetAccountIdByUsernameCI(key);
        }

        private static RoomDBClasses.RoomRoot? ResolveRoom(string key)
        {
            key = (key ?? "").Trim().TrimStart('^');
            return RoomDB.GetRoomByName(key) ?? (ulong.TryParse(key, out var rid) ? RoomDB.GetRoom(rid) : null);
        }

        // ---- reads --------------------------------------------------------------------------

        [HttpGet("/api/site/admin/player/{key}")]
        public IActionResult PlayerInfo(string key)
        {
            var me = Admin(); if (me == null) return Denied();
            ulong id = ResolvePlayer(key);
            var full = id == 0 ? null : PlayerDB.GetFullAccount(id);
            if (full?.player == null) return NotFound(new { ok = false, error = "No such player." });
            var (online, room) = HeartbeatDB.PeekPresence(id);
            var ban = PlayerDB.GetSiteBan(id);
            var mic = PlayerDB.GetActiveMicBan(id);
            return Ok(new
            {
                ok = true,
                id,
                username = full.player.Username,
                display_name = full.player.DisplayName,
                level = full.player.Level,
                tokens = PlayerDB.GetBalance(id, 2),
                dev = PlayerDB.CheckDevFlag(id),
                mod = PlayerDB.CheckModFlag(id),
                junior = full.player.IsJunior,
                online,
                room = room == null ? null : new { name = room.name, instance = room.roomInstanceId, isPrivate = room.isPrivate },
                ban = ban == null ? null : new { permanent = ban.RemainingSeconds == null, remaining_seconds = ban.RemainingSeconds, reason = ban.Reason },
                mic_ban = mic == null ? null : new { permanent = mic.Duration == int.MaxValue, remaining_seconds = mic.Duration == int.MaxValue ? (int?)null : mic.Duration },
                is_self = id == me.Id
            });
        }

        [HttpGet("/api/site/admin/room/{key}")]
        public IActionResult RoomInfo(string key)
        {
            var me = Admin(); if (me == null) return Denied();
            var r = ResolveRoom(key);
            if (r == null) return NotFound(new { ok = false, error = "No such room." });
            ulong rid = (ulong)r.RoomId;
            return Ok(new
            {
                ok = true,
                id = rid,
                name = r.Name,
                description = r.Description ?? "",
                creator_id = r.CreatorAccountId,
                creator = r.CreatorAccountId == 0 ? "" : PlayerDB.GetPlayerNameById(r.CreatorAccountId),
                accessibility = (int)(r.SubRooms?.FirstOrDefault()?.Accessibility ?? RoomDBClasses.RoomAccessibility.Public),
                max_players = r.MaxPlayers,
                cloning = r.CloningAllowed,
                dorm = r.IsDorm,
                featured = FeaturedRoomsStore.GetIds().Contains(rid),
                players_now = HeartbeatDB.CountInRoom(rid)
            });
        }

        // ---- actions ------------------------------------------------------------------------

        [HttpPost("/api/site/admin/player/{key}/{op}")]
        public async Task<IActionResult> PlayerAction(string key, string op)
        {
            var me = Admin(); if (me == null) return Denied();
            if (Throttled(me.Id)) return StatusCode(429, new { ok = false, error = "Slow down a little." });
            ulong id = ResolvePlayer(key);
            if (id == 0) return NotFound(new { ok = false, error = "No such player." });
            var b = await Body();
            string pid = id.ToString();
            var admin = new AdminController();
            var gaps = new AdminPanelGapsController();

            ContentResult? r = op switch
            {
                "ban" => Run(admin, me, new()
                {
                    ["playerId"] = pid,
                    ["duration"] = Str(b, "duration") == "permanent" ? int.MaxValue.ToString() : Str(b, "duration"),
                    ["category"] = "Moderator",
                    ["message"] = Str(b, "reason"),
                    ["banDevice"] = Bool(b, "device") ? "1" : "0",
                    ["banIp"] = Bool(b, "ip") ? "1" : "0",
                }, c => ((AdminController)c).AdminAPIBanPlayer()),
                "unban" => Run(admin, me, new() { ["playerId"] = pid, ["liftDeviceIp"] = "1" }, c => ((AdminController)c).AdminUnbanPlayer()),
                "kick" => Run(admin, me, new() { ["playerId"] = pid }, c => ((AdminController)c).KickPlayer()),
                "micban" => Run(gaps, me, new() { ["playerId"] = pid, ["minutes"] = Str(b, "minutes"), ["reason"] = Str(b, "reason") }, c => ((AdminPanelGapsController)c).MicBan()),
                "micunban" => Run(gaps, me, new() { ["playerId"] = pid }, c => ((AdminPanelGapsController)c).MicUnban()),
                "tokens" => Run(gaps, me, new() { ["playerId"] = pid, ["tokenAmount"] = Str(b, "amount") }, c => ((AdminPanelGapsController)c).GiveTokens()),
                // Go to them (you join their instance) / bring them to you (they join yours).
                "join" => Run(admin, me, new(), c => ((AdminController)c).AdminForceJoin(me.Id, id)),
                "bring" => Run(admin, me, new(), c => ((AdminController)c).AdminForceJoin(id, me.Id)),
                _ => null
            };
            if (r == null) return BadRequest(new { ok = false, error = "Unknown action." });
            Console.WriteLine($"[site-admin] {me.Username} ({me.Id}) {op} -> player {id}");
            return Relay(r);
        }

        [HttpPost("/api/site/admin/room/{key}/{op}")]
        public async Task<IActionResult> RoomAction(string key, string op)
        {
            var me = Admin(); if (me == null) return Denied();
            if (Throttled(me.Id)) return StatusCode(429, new { ok = false, error = "Slow down a little." });
            var room = ResolveRoom(key);
            if (room == null) return NotFound(new { ok = false, error = "No such room." });
            ulong rid = (ulong)room.RoomId;
            var b = await Body();
            var admin = new AdminController();
            var gaps = new AdminPanelGapsController();

            ContentResult? r;
            switch (op)
            {
                case "edit":
                {
                    var form = new Dictionary<string, string> { ["roomId"] = rid.ToString() };
                    foreach (var k in new[] { "name", "description", "accessibility", "maxPlayers", "cloningAllowed" })
                        if (b[k] != null) form[k] = Str(b, k);
                    r = Run(gaps, me, form, c => ((AdminPanelGapsController)c).EditRoom());
                    break;
                }
                case "feature":
                case "unfeature":
                {
                    var ids = FeaturedRoomsStore.GetIds().Where(x => x != rid).ToList();
                    if (op == "feature") ids.Insert(0, rid);
                    r = Run(admin, me, new() { ["roomIds"] = string.Join(",", ids) }, c => ((AdminController)c).AdminSetFeaturedRooms());
                    break;
                }
                case "delete":
                    r = Run(gaps, me, new() { ["roomId"] = rid.ToString() }, c => ((AdminPanelGapsController)c).DeleteRoom());
                    break;
                default:
                    return BadRequest(new { ok = false, error = "Unknown action." });
            }
            Console.WriteLine($"[site-admin] {me.Username} ({me.Id}) {op} -> room {rid} ^{room.Name}");
            return Relay(r);
        }

        // ---- plumbing -----------------------------------------------------------------------

        private async Task<JObject> Body()
        {
            try
            {
                using var reader = new StreamReader(Request.Body);
                string s = await reader.ReadToEndAsync();
                return string.IsNullOrWhiteSpace(s) ? new JObject() : JObject.Parse(s);
            }
            catch { return new JObject(); }
        }
        private static string Str(JObject b, string k) => b[k]?.Type == JTokenType.Null ? "" : (b[k]?.ToString() ?? "").Trim();
        private static bool Bool(JObject b, string k) => b[k]?.Type == JTokenType.Boolean ? (bool)b[k]! : Str(b, k) is "1" or "true" or "on";

        // Run a panel handler as this admin: a fresh request carrying a short-lived admin token in
        // the Authorization cookie (what AuthHelper.ValidateTokenDev reads) and the form fields.
        private ContentResult Run(ControllerBase ctl, player_info me, Dictionary<string, string> form, Func<ControllerBase, ContentResult> call)
        {
            var ctx = new DefaultHttpContext { RequestServices = HttpContext.RequestServices };
            ctx.Connection.RemoteIpAddress = HttpContext.Connection.RemoteIpAddress;
            ctx.Request.Method = "POST";
            ctx.Request.Headers["X-Forwarded-For"] = Request.Headers["X-Forwarded-For"];
            ctx.Request.Headers["Cookie"] = "Authorization=" + ClientSecurity.RefreshToken_Web(me.Id, TimeSpan.FromMinutes(2), "admin");
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(form.ToDictionary(kv => kv.Key, kv => new StringValues(kv.Value)));
            ctl.ControllerContext = new ControllerContext { HttpContext = ctx };
            return call(ctl);
        }

        // The panel's handlers answer {success, message|error}; the website speaks {ok, message}.
        private IActionResult Relay(ContentResult r)
        {
            JObject o;
            try { o = JObject.Parse(r.Content ?? "{}"); } catch { o = new JObject(); }
            bool ok = (r.StatusCode ?? 200) < 400 && (bool?)o["success"] != false;
            string msg = (string?)o["message"] ?? (string?)o["error"] ?? (ok ? "Done." : "That didn't work.");
            if (string.IsNullOrWhiteSpace(msg)) msg = ok ? "Done." : "That didn't work.";
            return Ok(new { ok, message = msg });
        }
    }
}
