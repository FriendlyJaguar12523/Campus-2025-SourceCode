using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Campus_live_rewrite.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class DiscordController : Controller
    {
        // ClientId + RedirectUri are shared with the website "Link Discord" flow (SiteController
        // builds the same authorize URL and reuses this SAME callback, branching on state="link:").
        // ClientSecret stays private - only the token exchange here needs it.
        internal const string ClientId = "1537203658725265488";
        private const string ClientSecret = "REDACTED";
        internal const string RedirectUri = "https://gameauth.ddns.net/api/auth/discord/callback";     // as registered with Discord - NOT the site host (camprec.net)

        [HttpGet("discord")]
        public IActionResult DiscordLogin()
        {
            var url =
                "https://discord.com/api/oauth2/authorize" +
                $"?client_id={ClientId}" +
                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                "&response_type=code" +
                "&scope=identify email";

            return Redirect(url);
        }

        [HttpGet("discord/callback")]
        public async Task<IActionResult> DiscordCallback([FromQuery] string code, [FromQuery] string? state = null)
        {
            if (string.IsNullOrEmpty(code))
                return BadRequest("Missing OAuth code.");

            using var http = new HttpClient();

            // Exchange code for access token
            var tokenResponse = await http.PostAsync(
                "https://discord.com/api/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri
                })
            );

            if (!tokenResponse.IsSuccessStatusCode)
                return Unauthorized("OAuth token exchange failed.");

            var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
            var token = JsonDocument.Parse(tokenJson)
                .RootElement.GetProperty("access_token").GetString();

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var userJson = await http.GetStringAsync("https://discord.com/api/users/@me");
            var user = JsonDocument.Parse(userJson).RootElement;

            string discordId = user.GetProperty("id").GetString();
            string username = user.GetProperty("username").GetString();

            // Email may be null
            string? email = null;
            if (user.TryGetProperty("email", out var emailProp))
                email = emailProp.GetString();

            Console.WriteLine($"Discord Login: {username} ({discordId}) | Email: {email ?? "N/A"}");

            // ---- Discord LINK flow (website Settings > Discord "Link Discord") --------------
            // state "link:<code>" means the user started this from Settings while signed in on the
            // website. This is COMPLETELY separate from the admin login below: resolve the code to
            // their Campus account, attach the Discord id, and send them back to /settings. It
            // NEVER issues an admin cookie, NEVER redirects to /admin, and NEVER auto-creates an
            // account - so linking Discord can't accidentally log anyone into the admin panel.
            if (!string.IsNullOrEmpty(state) && state.StartsWith("link:", StringComparison.Ordinal))
            {
                string linkCode = state.Substring("link:".Length);
                if (!DiscordLinkCodes.TryResolve(linkCode, out ulong linkAccId))
                    return Redirect(ConfigCampus.SiteUrl + "/settings?discord=expired");
                if (!ulong.TryParse(discordId, out ulong linkDid))
                    return Redirect(ConfigCampus.SiteUrl + "/settings?discord=error");

                // One Discord may own only one Campus account.
                ulong otherAcc = PlayerDB.FindPlayerByDiscordId(linkDid, linkAccId);
                if (otherAcc != 0)
                    return Redirect(ConfigCampus.SiteUrl + "/settings?discord=inuse");

                PlayerDB.SetDiscordLink(linkAccId, linkDid, username);
                DiscordLinkCodes.Consume(linkCode);
                Console.WriteLine($"[DiscordLink] account {linkAccId} linked to discord {linkDid} ({username}) via OAuth");
                return Redirect(ConfigCampus.SiteUrl + "/settings?discord=linked");
            }

            // 🔐 Lookup account (auto-create if this Discord id has never been seen)
            ulong discordIdUlong = ulong.Parse(discordId);
            var accountIds = PlayerDB.GetAccountIdsByDiscordId(discordIdUlong);

            ulong resolvedAccId;
            if (accountIds == null || !accountIds.Any())
            {
                Console.WriteLine($"[DiscordAuth] No account linked to discord_id={discordIdUlong} ({username}) — auto-creating.");
                if (!PlayerDB.CreateAccount(Campus_live_rewrite.api.server.Classes.db_class.Platforms.RecNet, 0UL, "", discordIdUlong, out var newAcc))
                {
                    Console.WriteLine($"[DiscordAuth] CreateAccount FAILED for discord_id={discordIdUlong}");
                    return StatusCode(500, "Failed to create Discord-linked account.");
                }
                Console.WriteLine($"[DiscordAuth] Auto-created account_id={newAcc.accountId} for discord_id={discordIdUlong}");
                resolvedAccId = newAcc.accountId;
            }
            else
            {
                resolvedAccId = accountIds.First();
                Console.WriteLine($"[DiscordAuth] Existing account_id={resolvedAccId} for discord_id={discordIdUlong}");
            }

            var account = PlayerDB.GetFullAccount(resolvedAccId);
            if (account is null)
            {
                Console.WriteLine($"[DiscordAuth] GetFullAccount returned null for accountId={resolvedAccId} (discord_id={discordIdUlong})");
                return Unauthorized("Account not found.");
            }

            // The admin panel is 2FA-only (see AdminController's AdminLoginAPI) - Discord sign-in used to skip that
            // entirely and hand out a session on the spot, which made it a full bypass of the password-login gate.
            // Route it through the exact same ticket + step the password flow uses instead of minting a cookie here:
            // LoginV4.html already knows how to pick a `pending` ticket up from the query string and continue into
            // either the code step or forced-enrollment step, so this is just a different way to get a ticket there.
            // The admin ("Log in with Discord" button on LoginV4.html) flow sends NO `state` at all - only the
            // launcher's OAuth uses "launcher:<port>", and "link:" already returned above - so "not launcher" is
            // the correct test here, not "state present". Getting this backwards is exactly what would silently
            // reopen the bypass this whole block exists to close.
            bool isLauncherFlow = !string.IsNullOrEmpty(state) && state.StartsWith("launcher:", StringComparison.Ordinal);
            // Only a dev-or-mod-flagged account is admin-panel-eligible at all (same check AdminLoginAPI's
            // isAdminPanel password path applies via PlayerDB.CheckDevOrModFlag, now that mods can log into a
            // narrow slice of the panel too) - skip the 2FA detour for anyone else so a random Discord-linked
            // player isn't walked through mandatory enrollment just to get bounced by ValidateTokenDev(OrMod)
            // on the next page anyway. Mods must go through this exactly like devs do - CheckDevFlag alone here
            // would silently let a mod's Discord login skip 2FA entirely, reopening the bypass this closes.
            if (!isLauncherFlow && PlayerDB.CheckDevOrModFlag(account.playerid))
            {
                string step = TwoFactorDB.IsEnabled(account.playerid) ? "2fa" : "setup";
                string surface = step == "2fa" ? "admin" : "admin-setup";
                string pending = PendingTwoFactorLogin.Issue(account.playerid, surface);
                string loginHost = ConfigCampus.HostRouting && !string.Equals(Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase) && Request.Host.Host != "127.0.0.1"
                    ? "https://" + Campus_live_rewrite.Middleware.HostRoutingMiddleware.AdminHost
                    : "";
                Console.WriteLine($"[DiscordAuth] account_id={account.playerid} needs admin 2FA ({step}) - routing to login page with a pending ticket instead of a session");
                return Redirect($"{loginHost}/admin/v4/login?step={step}&pending={Uri.EscapeDataString(pending)}");
            }

            // ✅ Issue JWT
            string jwtToken = ClientSecurity.RefreshToken_Web(account.playerid);

            // SameSite=Lax (not Strict): a cookie set on a cross-site redirect from Discord is
            // still allowed AND is sent on the follow-up top-level GET (the /admin/home/v4
            // redirect). With Strict, the browser refuses to send it on that navigation, so the
            // admin page bounces you right back to the login form — which was the symptom.
            Response.Cookies.Append("Authorization", jwtToken, new Microsoft.AspNetCore.Http.CookieOptions
            {
                Expires = DateTime.Now.AddHours(24),
                HttpOnly = true,
                Secure = true,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Path = "/"
            });

            // Discriminate based on `state`. Web-admin login (default / "admin") stays on the
            // original path — cookie is set on camprec.net and we redirect to the admin
            // dashboard. Launcher OAuth (state="launcher:<port>") skips the cookie and instead
            // redirects the browser to the launcher's local http listener with the JWT in the
            // query string; the launcher picks it up there and closes the browser tab.
            if (!string.IsNullOrEmpty(state) && state.StartsWith("launcher:", StringComparison.Ordinal))
            {
                string portStr = state.Substring("launcher:".Length);
                if (int.TryParse(portStr, out int port) && port >= 1024 && port <= 65535)
                {
                    string dest = $"http://127.0.0.1:{port}/campus-auth?token={Uri.EscapeDataString(jwtToken)}&account_id={account.playerid}";
                    Console.WriteLine($"[DiscordAuth] launcher flow — redirecting to {dest}");
                    return Redirect(dest);
                }
                Console.WriteLine($"[DiscordAuth] launcher flow — malformed port in state='{state}', falling through to admin redirect");
            }

            // The panel lives on admin.camprec.net, but this callback lands on the address registered with Discord (gameauth.ddns.net) and a cookie
            // never crosses hosts: leave the token under a one-time ticket and let the panel's own host pick it up (/admin/discord-finish).
            if (ConfigCampus.HostRouting && !string.Equals(Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase) && Request.Host.Host != "127.0.0.1")
                return Redirect("https://" + Campus_live_rewrite.Middleware.HostRoutingMiddleware.AdminHost + "/admin/discord-finish?t=" +
                                Campus_live_rewrite.Middleware.AdminHandoff.Create(jwtToken));
            Console.WriteLine($"[DiscordAuth] Issued admin JWT for account_id={account.playerid}, redirecting to /admin/home/v4");
            return Redirect("/admin/home/v4");
        }
    }
}
