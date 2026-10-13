using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Campus_live_rewrite.Controllers
{
    // Auth endpoints for the CampusPatch2025 Launcher.
    //
    // These live alongside the existing DiscordController (web-admin OAuth flow). The launcher
    // uses a different callback strategy than the web admin: it spins up a short-lived HTTP
    // listener on 127.0.0.1:<random port>, opens the browser at
    //   /api/auth/discord/login?state=launcher:<port>
    // and expects the browser to be redirected to
    //   http://127.0.0.1:<port>/campus-auth?token=<jwt>
    // after Discord OAuth completes. That JWT is the launcher's session credential; the launcher
    // saves it locally and uses it as a Bearer for /api/auth/me + /api/auth/link/steam etc.
    //
    // Non-OAuth flows: launcher can also register with username+password (creates account with
    // no Steam yet) or log in with existing credentials. Same JWT is returned in all three cases.
    [ApiController]
    [Route("api/auth")]
    public class AuthLauncherController : ControllerBase
    {
        // Discord OAuth app is shared with DiscordController — same Client Id/Secret/Redirect. The
        // callback discriminates admin-panel vs launcher by inspecting the `state` param format:
        //   "admin"           => (default, existing) redirect to /admin/home/v4
        //   "launcher:<port>" => (new) redirect browser to http://127.0.0.1:<port>/campus-auth?token=<jwt>
        private const string ClientId     = "1537203658725265488";
        private const string ClientSecret = "REDACTED";
        private const string RedirectUri  = "https://gameauth.ddns.net/api/auth/discord/login";     // as registered with Discord - NOT the site host (camprec.net)

        // ---- Discord OAuth — combined start + callback ------------------------------------------
        // GET /api/auth/discord/login
        //   Discord's registered redirect URI is now this same endpoint (see RedirectUri
        //   constant above), so the browser makes TWO trips through here per login:
        //     1) Initial hit from the launcher's "Log in with Discord" button. `code` is
        //        absent → we 302 the browser to Discord's OAuth authorize page.
        //     2) Discord redirects the user back with ?code=<X>&state=<Y>. `code` is present
        //        → we exchange the code for an access token, fetch the Discord user, find or
        //        auto-create the linked Campus account, mint a JWT, and either:
        //          - state == "launcher:<port>" → redirect to http://127.0.0.1:<port>/campus-auth?token=<jwt>
        //          - state == "admin" (default) → set cookie + redirect to /admin/home/v4
        //   Doing both phases in one route matches whatever redirect URI is on the Discord
        //   dashboard without needing a separate /callback endpoint.
        [HttpGet("discord/login")]
        public async Task<IActionResult> DiscordLoginLauncher([FromQuery] string? code = null,
                                                              [FromQuery] string? state = null)
        {
            // Phase 1: no code yet → kick off OAuth by 302-ing to Discord.
            if (string.IsNullOrEmpty(code))
            {
                string stateOut = string.IsNullOrEmpty(state) ? "admin" : state;
                var url =
                    "https://discord.com/api/oauth2/authorize"
                    + $"?client_id={ClientId}"
                    + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                    + "&response_type=code"
                    + "&scope=identify email"
                    + $"&state={Uri.EscapeDataString(stateOut)}";
                return Redirect(url);
            }

            // Phase 2: Discord redirected the user back here with a code. Exchange it.
            using var http = new HttpClient();
            var tokenResponse = await http.PostAsync(
                "https://discord.com/api/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"]     = ClientId,
                    ["client_secret"] = ClientSecret,
                    ["grant_type"]    = "authorization_code",
                    ["code"]          = code,
                    ["redirect_uri"]  = RedirectUri
                })
            );
            if (!tokenResponse.IsSuccessStatusCode)
                return Unauthorized("OAuth token exchange failed.");

            var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
            var accessTok = System.Text.Json.JsonDocument.Parse(tokenBody)
                                                        .RootElement.GetProperty("access_token").GetString();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessTok);

            var userBody = await http.GetStringAsync("https://discord.com/api/users/@me");
            var user     = System.Text.Json.JsonDocument.Parse(userBody).RootElement;
            string discordIdStr = user.GetProperty("id").GetString();
            string discordName  = user.GetProperty("username").GetString();
            ulong discordId     = ulong.Parse(discordIdStr);

            // Find existing Campus account for this Discord id, or auto-create one.
            var accountIds = PlayerDB.GetAccountIdsByDiscordId(discordId);
            ulong accId;
            if (accountIds == null || !accountIds.Any())
            {
                if (!PlayerDB.CreateAccount(Platforms.RecNet, 0UL, "", discordId, out var newAcc))
                    return StatusCode(500, "Failed to create Discord-linked account.");
                accId = newAcc.accountId;
                Console.WriteLine($"[DiscordAuth/launcher] Auto-created account_id={accId} for discord_id={discordId} ({discordName})");
            }
            else
            {
                accId = accountIds.First();
                Console.WriteLine($"[DiscordAuth/launcher] Existing account_id={accId} for discord_id={discordId} ({discordName})");
            }

            string jwt = ClientSecurity.RefreshToken_Web(accId);

            // Launcher flow: bounce to the launcher's local http listener with the JWT.
            if (!string.IsNullOrEmpty(state) && state.StartsWith("launcher:", StringComparison.Ordinal))
            {
                string portStr = state.Substring("launcher:".Length);
                if (int.TryParse(portStr, out int port) && port >= 1024 && port <= 65535)
                {
                    string dest = $"http://127.0.0.1:{port}/campus-auth?token={Uri.EscapeDataString(jwt)}&account_id={accId}";
                    Console.WriteLine($"[DiscordAuth/launcher] redirecting to {dest}");
                    return Redirect(dest);
                }
            }

            // The panel lives on admin.camprec.net, but this callback lands on the address registered with Discord (gameauth.ddns.net) and a cookie
            // never crosses hosts: leave the token under a one-time ticket and let the panel's own host pick it up (/admin/discord-finish).
            if (ConfigCampus.HostRouting && !string.Equals(Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase) && Request.Host.Host != "127.0.0.1")
                return Redirect("https://" + Campus_live_rewrite.Middleware.HostRoutingMiddleware.AdminHost + "/admin/discord-finish?t=" +
                                Campus_live_rewrite.Middleware.AdminHandoff.Create(jwt));
            // Admin panel flow (default): cookie + redirect to dashboard.
            Response.Cookies.Append("Authorization", jwt, new Microsoft.AspNetCore.Http.CookieOptions
            {
                Expires  = DateTime.Now.AddHours(24),
                HttpOnly = true,
                Secure   = true,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Path     = "/"
            });
            return Redirect("/admin/home/v4");
        }

        // ---- Register: username + password ------------------------------------------------------
        // POST /api/auth/register  {username, password}
        //   Creates a PlayerDB account with no Steam / Discord link yet. Both are added later:
        //   Steam via /api/auth/link/steam when the launcher extracts the local Steam ID, Discord
        //   via /api/auth/link/discord if the user then chooses to Discord-link. Returns a JWT.
        [HttpPost("register")]
        public IActionResult Register([FromBody] AuthCreds body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.username) || string.IsNullOrWhiteSpace(body.password))
                return BadRequest(new { success = false, error = "username and password required" });

            // Reject if username already taken. The DB reserves username via PlayerDB.SetPlayerUsername
            // but there's no atomic create-with-username helper, so we check-then-create with a note
            // that racing two identical registers is possible (rare; second one gets rejected on next
            // rename check).
            var existing = PlayerDB.GetFullAccountByName(body.username);
            if (existing != null)
                return Conflict(new { success = false, error = "username already taken" });

            // Create account with Platforms.RecNet (no Steam yet) and blank device id. Discord id 0
            // means "not Discord-linked". Link steps happen separately.
            if (!PlayerDB.CreateAccount(Platforms.RecNet, 0UL, "", 0UL, out var newAcc))
                return StatusCode(500, new { success = false, error = "account creation failed" });

            PlayerDB.SetPlayerUsername(body.username, newAcc.accountId);
            PlayerDB.SetPlayerPassword(body.password, newAcc.accountId, log: true);

            string jwt = ClientSecurity.RefreshToken_Web(newAcc.accountId);
            return Ok(new { success = true, token = jwt, account_id = newAcc.accountId });
        }

        // ---- Login: username + password ---------------------------------------------------------
        // POST /api/auth/login  {username, password}
        //   Delegates to PlayerDB.GetAccountPassword (same helper the web admin login uses).
        //   Returns a JWT and a minimal account snapshot. Launcher usually calls /api/auth/me
        //   right after for the fuller view including ban state, dev/mod flags, pfp, etc.
        [HttpPost("login")]
        public IActionResult Login([FromBody] AuthCreds body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.username) || string.IsNullOrWhiteSpace(body.password))
                return BadRequest(new { success = false, error = "username and password required" });

            var result = PlayerDB.GetAccountPassword(body.username, body.password, isAdminPanel: false);
            if (!result.success)
                return Unauthorized(new { success = false, error = result.error });

            string jwt = ClientSecurity.RefreshToken_Web(result.accid);
            return Ok(new { success = true, token = jwt, account_id = result.accid });
        }

        // ---- Logout -----------------------------------------------------------------------------
        // POST /api/auth/logout
        //   JWTs are stateless (HMACSHA256, no server-side session store), so logout is a no-op
        //   server-side today. The launcher wipes its local JWT on 401 anyway. This endpoint just
        //   returns 200 so the launcher has a well-known URL to POST to for symmetry.
        [HttpPost("logout")]
        public IActionResult Logout() => Ok(new { success = true });

        // ---- Me ---------------------------------------------------------------------------------
        // GET /api/auth/me
        //   Returns the full account snapshot the launcher UI needs: display name, pfp, discord +
        //   steam ids, ban status + expiry, dev/mod flags. Launcher polls this at startup and on
        //   every panel refresh. 401 tells the launcher to wipe its local JWT and show
        //   "Login time expired, please log back in".
        [HttpGet("me")]
        public IActionResult Me()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return Unauthorized(new { success = false, error = "invalid or expired token" });

            var full = PlayerDB.GetFullAccount(account.Id);
            if (full == null) return NotFound(new { success = false, error = "account not found" });

            // Ban info: HasActiveModerationBlock is the source of truth. Get_moderation_block
            // returns a JSON string of the current block; we parse it lightly to extract end time
            // + reason. If no active ban, everything stays null.
            bool banned = PlayerDB.HasActiveModerationBlock(account.Id);
            long? banEndsAt = null;
            string? banReason = null;
            if (banned)
            {
                try
                {
                    var blockJson = PlayerDB.Get_moderation_block(account.Id);
                    if (!string.IsNullOrEmpty(blockJson) && blockJson != "null")
                    {
                        var doc = System.Text.Json.JsonDocument.Parse(blockJson);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("EndTime", out var e) && e.TryGetInt64(out var eSec)) banEndsAt = eSec;
                        else if (root.TryGetProperty("ends_at", out var e2) && e2.TryGetInt64(out var e2Sec)) banEndsAt = e2Sec;
                        if (root.TryGetProperty("Reason", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String) banReason = r.GetString();
                        else if (root.TryGetProperty("reason", out var r2) && r2.ValueKind == System.Text.Json.JsonValueKind.String) banReason = r2.GetString();
                    }
                }
                catch { /* leave ban fields null on parse error */ }
            }

            // Platform ids: pull first Steam / Oculus ids off PlatformIds list.
            string? steamId = null, metaId = null;
            if (full.player?.PlatformIds != null)
            {
                foreach (var p in full.player.PlatformIds)
                {
                    if (p == null) continue;
                    if (p.Platform == Platforms.Steam  && steamId == null) steamId = p.PlatformId.ToString();
                    if (p.Platform == Platforms.Oculus && metaId  == null) metaId  = p.PlatformId.ToString();
                }
            }

            return Ok(new
            {
                success          = true,
                id               = full.playerid,
                username         = full.player?.Username,
                display_name     = full.player?.DisplayName,
                profile_image    = full.player?.profileImage,
                bio              = full.player_Extra?.Bio,
                discord_id       = full.player_Extra?.DiscordId == 0 ? (ulong?)null : full.player_Extra?.DiscordId,
                // Discord username isn't stored today. If it were captured during Discord OAuth
                // callback (and persisted onto player_Extra), it would surface here. For now null.
                discord_username = (string?)null,
                steam_id         = steamId,
                meta_id          = metaId,
                banned           = banned,
                ban_ends_at      = banEndsAt,
                ban_reason       = banReason,
                is_dev           = (full.dev_Flag & DevFlag.dev) == DevFlag.dev,
                is_mod           = (full.dev_Flag & DevFlag.mod) == DevFlag.mod,
            });
        }

        // ---- Link Steam --------------------------------------------------------------------------
        // POST /api/auth/link/steam  {steam_id}
        //   Called by the launcher after Discord/register login once it extracts the local Steam
        //   ID (via steam_api). Behaviour matrix:
        //     - account has NO Steam yet AND no other account owns this steam_id → add it, 200 ok
        //     - account has THIS steam_id already → 200 no-op
        //     - account has a DIFFERENT steam_id (user changed Steam accounts) → swap to new one
        //     - a DIFFERENT account owns this steam_id → 409 conflict, caller decides (probably
        //       "This Steam account is linked to a different Campus account, log in there instead")
        [HttpPost("link/steam")]
        public IActionResult LinkSteam([FromBody] SteamLinkBody body)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return Unauthorized(new { success = false, error = "invalid or expired token" });
            if (body == null || body.steam_id == 0) return BadRequest(new { success = false, error = "steam_id required" });

            // Check if the steam_id is already claimed by another account.
            PlayerDB.GetLogins(Platforms.Steam, body.steam_id, out var owners);
            if (owners != null)
            {
                foreach (var o in owners)
                {
                    if (o.accountId != account.Id)
                        return Conflict(new { success = false, error = "steam_id already linked to a different Campus account", other_account_id = o.accountId });
                }
            }

            // Safe to add or swap. SetPlayerSteamId replaces the account's Steam entry (or adds if
            // missing) — see PlayerDB.SetPlayerSteamId.
            bool ok = PlayerDB.SetPlayerSteamId(body.steam_id, account.Id);
            return Ok(new { success = ok, steam_id = body.steam_id });
        }

        // Body shapes.
        public class AuthCreds       { public string? username { get; set; } public string? password { get; set; } }
        public class SteamLinkBody   { public ulong  steam_id { get; set; } }
    }
}
