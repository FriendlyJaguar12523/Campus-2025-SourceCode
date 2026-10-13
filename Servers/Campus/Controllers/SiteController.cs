using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    // Public website backend.
    //
    // Replaces Website/server.py (Flask). That process held a token.json with a bearer token
    // + HMAC signing key and made HTTP round trips BACK to camprec.net to answer the
    // site's own search calls. Running in-process removes the extra hop, the second listener,
    // and the on-disk credential file entirely: searches read PlayerDB/RoomDB directly and
    // images are already served by ImagesController at /img/.
    //
    // Paths and response shapes are kept byte-compatible with what app.js already fetches
    // ({ ok, results } / { ok, error }), so the front-end change is limited to enabling the
    // calls rather than rewriting them.
    //
    // AUTH MODEL - deliberately separate from the game and admin surfaces:
    //   - cookie name is `campus_site`, NOT `Authorization`. AuthHelper.ValidateToken falls
    //     back to an `Authorization` cookie for the admin panel; reusing that name would hand
    //     every website visitor the panel's cookie fallback on game endpoints.
    //   - the cookie is HttpOnly, so page script cannot read the token.
    //   - 12h lifetime, matching the game client's access-token window.
    [ApiController]
    public class SiteController : ControllerBase
    {
        private const string SiteCookie = "campus_site";
        private static readonly TimeSpan SiteSessionTtl = TimeSpan.FromHours(12);

        // ---- session helpers --------------------------------------------------------------

        private void IssueSiteCookie(ulong accountId)
        {
            string jwt = ClientSecurity.RefreshToken_Web(accountId, SiteSessionTtl, "website");
            Response.Cookies.Append(SiteCookie, jwt, new CookieOptions
            {
                // Expire the cookie with the token rather than outliving it, so the browser
                // stops sending a credential the server will only reject.
                Expires = DateTimeOffset.UtcNow.Add(SiteSessionTtl),
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/"
            });
        }

        private void ClearSiteCookie()
        {
            Response.Cookies.Append(SiteCookie, "", new CookieOptions
            {
                Expires = DateTimeOffset.UnixEpoch,
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/"
            });
        }

        // Returns the signed-in account, or null. DecodeToken re-reads the account from the DB
        // on every call, so a ban or a rename applies immediately instead of at token refresh.
        private player_info? CurrentUser()
        {
            string token = Request.Cookies[SiteCookie] ?? "";
            if (string.IsNullOrEmpty(token)) return null;

            // ONLY website-minted tokens are valid website sessions. A game or admin token (or a
            // legacy no-surface one) is refused here, so in-game / admin auth can't act as a
            // website session - the auth surfaces are kept separate.
            string playerData = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(playerData) || playerData.StartsWith("error_")) return null;

            try { return JsonConvert.DeserializeObject<player_info>(playerData); }
            catch { return null; }
        }

        private object PublicUser(player_data full) => new
        {
            id = full.playerid,
            username = full.player?.Username,
            display_name = full.player?.DisplayName,
            profile_image = full.player?.profileImage,
            banner_image = full.player?.bannerImage,
            bio = full.player_Extra?.Bio,
            level = full.player?.Level ?? 0,
            xp = full.player?.XP ?? 0,
            // RecCenterTokens (currency type 2) - the same "Tokens" the game shows. Only on the
            // authed /me response, never the public profile, so a balance isn't exposed publicly.
            tokens = PlayerDB.GetBalance(full.playerid, 2),
            banned = PlayerDB.HasActiveModerationBlock(full.playerid),
            // The site-wide ban banner reads this: null when not banned, else the time left
            // (remaining_seconds null = permanent) and the reason. SiteBanGateMiddleware refuses
            // every change a banned session tries to make.
            ban = SiteBanDto(full.playerid),
        };

        private static object? SiteBanDto(ulong playerid)
        {
            var b = PlayerDB.GetSiteBan(playerid);
            if (b == null) return null;
            return new
            {
                permanent = b.RemainingSeconds == null,
                remaining_seconds = b.RemainingSeconds,
                until = b.Until,
                reason = b.Reason
            };
        }

        // ---- accounts ---------------------------------------------------------------------

        // POST /api/site/login {username, password}
        [HttpPost("/api/site/login")]
        public IActionResult Login([FromBody] SiteCreds? body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.username) || string.IsNullOrWhiteSpace(body.password))
                return BadRequest(new { ok = false, error = "Enter a username and password." });

            // Same helper the launcher and admin panel use, so password upgrades (legacy SHA256
            // rows -> PBKDF2) and lockout behaviour stay in ONE place.
            var result = PlayerDB.GetAccountPassword(body.username.Trim(), body.password, isAdminPanel: false);
            if (!result.success)
                return Unauthorized(new { ok = false, error = result.error ?? "Incorrect username or password." });

            // Correct password. If this account turned 2FA on (Settings -> Security), that's only half the login - hold
            // off on the site cookie and send back a ticket naming the account instead. The page then asks for a code and
            // posts it to /api/site/login/2fa, which is the only place the cookie actually gets issued for a 2FA account.
            if (TwoFactorDB.IsEnabled(result.accid))
                return Ok(new { ok = false, need2fa = true, pending = PendingTwoFactorLogin.Issue(result.accid, "website") });

            // A banned account CAN sign in - every page then shows the ban banner (time left +
            // reason) - but it can't change anything: SiteBanGateMiddleware refuses its writes.
            IssueSiteCookie(result.accid);

            var full = PlayerDB.GetFullAccount(result.accid);
            return Ok(new { ok = true, user = full == null ? null : PublicUser(full) });
        }

        /// <summary>Step 2 of a 2FA account's website login: the ticket from Login, plus a code from their authenticator
        /// app (or a recovery code). Covered by the same /api/site/login flood ceiling as the password step
        /// (TrafficGuardMiddleware.IsAuthPath matches by prefix).</summary>
        public class SiteLogin2FA { public string? pending { get; set; } public string? code { get; set; } }

        [HttpPost("/api/site/login/2fa")]
        public IActionResult Login2FA([FromBody] SiteLogin2FA? body)
        {
            ulong accountId = PendingTwoFactorLogin.Validate(body?.pending, "website");
            if (accountId == 0)
                return Unauthorized(new { ok = false, error = "That login attempt expired - log in again." });

            if (!TwoFactorDB.VerifyLogin(accountId, body?.code))
                return Unauthorized(new { ok = false, need2fa = true, pending = body!.pending, error = "Wrong code." });

            IssueSiteCookie(accountId);
            var full = PlayerDB.GetFullAccount(accountId);
            return Ok(new { ok = true, user = full == null ? null : PublicUser(full) });
        }

        // ---- 2FA enrollment (the signed-in player managing THEIR OWN account) -------------------------------------

        [HttpGet("/api/site/2fa/status")]
        public IActionResult TwoFactorStatusSite()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            return Ok(new { ok = true, enabled = TwoFactorDB.IsEnabled(account.Id) });
        }

        [HttpPost("/api/site/2fa/setup")]
        public IActionResult TwoFactorSetupSite()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            string secret = TwoFactorDB.BeginEnroll(account.Id);
            return Ok(new { ok = true, secret, otpauth = Totp.OtpAuthUri(secret, account.Username ?? ("player" + account.Id), "Campus") });
        }

        public class SiteTwoFactorCode { public string? code { get; set; } }

        [HttpPost("/api/site/2fa/confirm")]
        public IActionResult TwoFactorConfirmSite([FromBody] SiteTwoFactorCode? body)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var codes = TwoFactorDB.ConfirmEnroll(account.Id, body?.code ?? "");
            if (codes == null) return BadRequest(new { ok = false, error = "Wrong code - try the current 6 digits from your app." });
            return Ok(new { ok = true, recoveryCodes = codes });
        }

        [HttpPost("/api/site/2fa/disable")]
        public IActionResult TwoFactorDisableSite()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            TwoFactorDB.Disable(account.Id);
            return Ok(new { ok = true });
        }

        // POST /api/site/register
        //
        // Campus accounts are now MADE here, on the website (like a normal web account), and the
        // game only logs in to them. The account carries no Steam binding at creation; the Steam
        // id is stamped on the first in-game login (AccountLock), which is where the one-account-
        // per-Steam / per-device rule is enforced. Protections against mass creation:
        //   - layered signup caps: per IP per day/week, per /24 per day, and a GLOBAL per-hour cap
        //     (ConfigCampus.SignupsPer*; see PlayerDB.CheckSignupLimit),
        //   - username must be unique (case-insensitive) and match a strict charset,
        //   - reserved names blocked, a hidden honeypot field traps naive bots,
        //   - password confirmation checked server-side.
        [HttpPost("/api/site/register")]
        public IActionResult Register([FromBody] SiteRegister? body)
        {
            if (body == null)
                return BadRequest(new { ok = false, error = "Missing sign-up details." });

            // Honeypot: a real form leaves `website` empty (it is hidden). A bot that fills every
            // field trips it. Generic message so it learns nothing.
            if (!string.IsNullOrWhiteSpace(body.website))
            {
                Console.WriteLine("[web-signup] honeypot tripped - refused");
                return BadRequest(new { ok = false, error = "Could not create account." });
            }

            string username = (body.username ?? "").Trim();
            string pass      = body.password ?? "";
            string confirm   = body.confirm_password ?? "";
            string email     = (body.email ?? "").Trim();

            // ---- date of birth (required) ----
            // One username field only now, so the @name IS the display name. But first: age gates
            // everything. Under 13 -> we IGNORE the chosen name, hand out a random one (privacy), and
            // flag the account junior (chat/social restricted client-side).
            if (string.IsNullOrWhiteSpace(body.birthday) ||
                !DateTime.TryParse(body.birthday, System.Globalization.CultureInfo.InvariantCulture,
                                   System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                   out var dob))
                return BadRequest(new { ok = false, error = "Enter your date of birth." });

            var today = DateTime.UtcNow.Date;
            if (dob.Date > today)
                return BadRequest(new { ok = false, error = "Enter a valid date of birth." });
            int age = today.Year - dob.Year;
            if (dob.Date > today.AddYears(-age)) age--;   // not had this year's birthday yet
            if (age > 120)
                return BadRequest(new { ok = false, error = "Enter a valid date of birth." });
            bool isJunior = age < 13;
            string birthdayIso = dob.ToString("yyyy-MM-ddT00:00:00.000Z");

            // ---- username ----
            // Juniors never choose an identifying name: we generate one (e.g. FunkyHippo1293) and
            // skip the user's input entirely. Everyone else's single username is validated and used
            // as BOTH the @name and the display name.
            string display;
            if (isJunior)
            {
                username = PlayerDB.GenerateJuniorUsername();
                display  = username;
            }
            else
            {
                if (username.Length < 3 || username.Length > 20)
                    return BadRequest(new { ok = false, error = "Username must be 3-20 characters." });
                if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[A-Za-z0-9_]+$"))
                    return BadRequest(new { ok = false, error = "Username can only use letters, numbers and underscores." });
                if (IsReservedName(username))
                    return BadRequest(new { ok = false, error = "That username isn't available." });
                display = username;   // one field: the @name is the display name
            }

            // ---- password ----
            if (pass.Length < 6 || pass.Length > 64)
                return BadRequest(new { ok = false, error = "Password must be 6-64 characters." });
            if (pass != confirm)
                return BadRequest(new { ok = false, error = "The passwords don't match." });

            // ---- email (optional) ----
            if (email.Length > 0)
            {
                if (email.Length > 254 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                    return BadRequest(new { ok = false, error = "Enter a valid email, or leave it blank." });
            }

            // ---- IP rate limit ----
            // The owner's own accounts (Coach/Kyle), when signed in on the site, may make test
            // accounts without waiting out the week.
            string? ip = ClientIp();
            var signedIn = CurrentUser();
            bool ownerBypass = signedIn != null &&
                               Campus_live_rewrite.api.server.AccountLock.IsExempt(signedIn.Id);
            if (!ownerBypass)
            {
                var limit = PlayerDB.CheckSignupLimit(ip, out DateTime nextAllowed);
                if (limit != PlayerDB.SignupLimit.None)
                {
                    var wait = nextAllowed - DateTime.UtcNow;
                    string when = wait.TotalHours < 1 ? "in a few minutes"
                                : wait.TotalHours < 36 ? "in about " + Math.Max(1, (int)Math.Ceiling(wait.TotalHours)) + " hours"
                                : "in about " + (int)Math.Ceiling(wait.TotalDays) + " days";
                    Console.WriteLine("[web-signup] refused (" + limit + ") IP " + ip + " - next allowed " + nextAllowed.ToString("u"));
                    string msg = limit == PlayerDB.SignupLimit.Global
                        ? "Lots of accounts are being created right now. Please try again " + when + "."
                        : "Too many accounts have been created from your network recently. Please try again " + when + ".";
                    return StatusCode(429, new { ok = false, error = msg });
                }
            }

            // ---- uniqueness (re-checked inside CreateWebAccount too, this gives a clean 409) ----
            if (PlayerDB.IsUsernameTaken(username))
                return Conflict(new { ok = false, error = "That username is already taken." });

            try
            {
                if (!PlayerDB.CreateWebAccount(username, display, pass,
                        email.Length == 0 ? null : email, out var acc, out string createErr,
                        isJunior: isJunior, birthday: birthdayIso))
                {
                    return BadRequest(new { ok = false, error = string.IsNullOrEmpty(createErr) ? "Could not create account." : createErr });
                }

                PlayerDB.RecordSignup(ip, acc.accountId);
                Console.WriteLine($"[web-signup] account {acc.accountId} created from IP {ip}");
                IssueSiteCookie(acc.accountId);

                var full = PlayerDB.GetFullAccount(acc.accountId);
                return Ok(new { ok = true, user = full == null ? null : PublicUser(full) });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[web-signup] failed: {ex}");
                return StatusCode(500, new { ok = false, error = "Could not create account. Please try again." });
            }
        }

        // Names nobody may register (impersonation / reserved). The owner's own accounts already
        // exist, so IsUsernameTaken blocks re-registering them without needing them listed here.
        private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "admin", "administrator", "moderator", "mod", "campus", "system", "root",
            "staff", "support", "null", "undefined", "everyone", "here", "recroom", "official"
        };
        private static bool IsReservedName(string name) => _reserved.Contains(name.Trim());

        // The real browser IP, for the signup rate limit. The app sits behind Caddy
        // (`reverse_proxy localhost:8080`), so HttpContext.Connection.RemoteIpAddress is always
        // 127.0.0.1 (Caddy) - which the rate limit treats as the exempt localhost, so every
        // signup slipped through. Caddy APPENDS the true connecting IP as the LAST
        // X-Forwarded-For entry; that entry is authoritative and cannot be spoofed by a client
        // sending its own X-Forwarded-For / CF-Connecting-IP (Caddy passes those through but
        // always adds the real peer after them), so we take the LAST entry. CF-Connecting-IP is
        // deliberately NOT trusted here: there is no Cloudflare in front, so it would be a free
        // spoof to rotate past the limit.
        private string? ClientIp()
        {
            string xff = Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(xff))
            {
                var parts = xff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0) return parts[parts.Length - 1];
            }
            return HttpContext.Connection.RemoteIpAddress?.ToString();
        }

        // POST /api/site/logout
        // Clears this browser's session. The JWT itself stays valid until it expires - there is
        // no server-side session store yet, so this cannot revoke a token already copied out.
        [HttpPost("/api/site/logout")]
        public IActionResult Logout()
        {
            ClearSiteCookie();
            return Ok(new { ok = true });
        }

        // ---- Support tickets --------------------------------------------------------------
        // A signed-in player opens a ticket (the Support page's form); STAFF (dev / mod / owner /
        // support flag) see every ticket and can reply, write internal notes, claim, close and
        // reopen. Topics, read markers, screenshots, limits and auto-close live in SupportDB.

        private static bool IsStaffFlag(DevFlag f) => (f & (DevFlag.dev | DevFlag.mod | DevFlag.owner | DevFlag.support)) != 0;
        private bool IsStaff(ulong id) => IsStaffFlag(PlayerDB.GetFullAccount(id)?.dev_Flag ?? DevFlag.none);

        public class SupportCreateReq
        {
            public string? subject { get; set; }
            public string? message { get; set; }
            public string? category { get; set; }
            public List<string>? attachments { get; set; }
        }
        public class SupportReplyReq
        {
            public string? message { get; set; }
            public bool @internal { get; set; }
            public List<string>? attachments { get; set; }
        }
        public class SupportAssignReq { public bool assign { get; set; } = true; }

        private static readonly Dictionary<string, string> SupportCategories = new()
        {
            ["account"] = "Account / login issue",
            ["join"] = "Joining & invites",
            ["econ"] = "Currency, rewards & gift boxes",
            ["report_player"] = "Report a player",
            ["report_room"] = "Report a room",
            ["bug"] = "Bug report",
            ["appeal"] = "Ban appeal",
            ["other"] = "Something else",
        };

        // A ticket's topic key: stored since this change; older tickets carry it as the
        // "Issue type: <label>" line of their opening message.
        private static string CategoryOf(SupportDB.Ticket t)
        {
            if (!string.IsNullOrEmpty(t.Category)) return t.Category!;
            string first = t.Messages?.FirstOrDefault()?.Body ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(first, @"^Issue type:\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (m.Success)
            {
                string label = m.Groups[1].Value.Trim();
                foreach (var kv in SupportCategories) if (string.Equals(kv.Value, label, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }
            return "other";
        }

        private static string AttachmentUrl(string name) => "/api/site/support/attachment/" + name;

        private object TicketSummary(SupportDB.Ticket t, bool forStaff)
        {
            var last = (t.Messages ?? new()).LastOrDefault(m => forStaff || !m.Internal);
            return new
            {
                id = t.Id,
                subject = t.Subject,
                status = t.Status,
                category = CategoryOf(t),
                categoryLabel = SupportCategories.TryGetValue(CategoryOf(t), out var cl) ? cl : "Something else",
                username = t.Username,
                accountId = t.AccountId,
                createdAt = t.CreatedAt,
                updatedAt = t.UpdatedAt,
                messageCount = (t.Messages ?? new()).Count(m => !m.System && (forStaff || !m.Internal)),
                lastMessage = last == null ? "" : (last.Internal ? "[note] " : "") + (last.Body.Length > 0 ? last.Body : "(screenshot)"),
                awaiting = SupportDB.AwaitingOf(t),
                unread = forStaff ? SupportDB.UnreadForStaff(t) : SupportDB.UnreadForPlayer(t),
                assignedTo = t.AssignedTo,
                assignedName = t.AssignedName,
            };
        }

        // Resolve an account's profile picture to a website URL (/img/<name>), or null.
        // Pass through values that are already a URL or an absolute path (legacy accounts).
        private static string? AvatarUrl(string? imageName) =>
            string.IsNullOrEmpty(imageName) ? null
            : (imageName.StartsWith("http", StringComparison.OrdinalIgnoreCase) || imageName.StartsWith("/")) ? imageName
            : "/img/" + imageName;

        private object TicketDetail(SupportDB.Ticket t, ulong viewerId, bool forStaff)
        {
            // Cache lookups so a person who sent several messages is only fetched once.
            var cache = new Dictionary<ulong, string?>();
            string? avatarFor(ulong id)
            {
                if (id == 0) return null;
                if (!cache.TryGetValue(id, out var url))
                {
                    url = AvatarUrl(PlayerDB.GetFullAccount(id)?.player?.profileImage);
                    cache[id] = url;
                }
                return url;
            }
            return new
            {
                id = t.Id,
                subject = t.Subject,
                status = t.Status,
                category = CategoryOf(t),
                categoryLabel = SupportCategories.TryGetValue(CategoryOf(t), out var cl) ? cl : "Something else",
                username = t.Username,
                accountId = t.AccountId,
                avatar = avatarFor(t.AccountId),
                createdAt = t.CreatedAt,
                updatedAt = t.UpdatedAt,
                awaiting = SupportDB.AwaitingOf(t),
                assignedTo = t.AssignedTo,
                assignedName = t.AssignedName,
                messages = (t.Messages ?? new()).Where(m => forStaff || !m.Internal).Select(m => new
                {
                    fromName = m.FromName,
                    staff = m.Staff,
                    body = m.Body,
                    at = m.At,
                    mine = m.FromAccountId == viewerId && !m.System,
                    @internal = m.Internal,
                    system = m.System,
                    avatar = m.System ? null : avatarFor(m.FromAccountId),
                    attachments = (m.Attachments ?? new()).Select(AttachmentUrl).ToList()
                }),
                // Staff see who they're helping: account age, bans, their other tickets.
                player = forStaff ? SupportPlayerContext(t) : null
            };
        }

        private static object? SupportPlayerContext(SupportDB.Ticket t)
        {
            var full = PlayerDB.GetFullAccount(t.AccountId);
            if (full?.player == null) return null;
            var ban = PlayerDB.GetSiteBan(t.AccountId);
            var mic = PlayerDB.GetActiveMicBan(t.AccountId);
            var others = SupportDB.ForAccount(t.AccountId).Where(x => x.Id != t.Id).ToList();
            return new
            {
                id = t.AccountId,
                username = full.player.Username,
                displayName = full.player.DisplayName,
                createdAt = full.player.created_at,
                level = full.player.Level,
                junior = full.player.IsJunior,
                banned = ban != null,
                banPermanent = ban != null && ban.RemainingSeconds == null,
                banRemainingSeconds = ban?.RemainingSeconds,
                banReason = ban?.Reason,
                micBanned = mic != null,
                micBanPermanent = mic != null && mic.Duration == int.MaxValue,
                micBanRemainingSeconds = mic == null || mic.Duration == int.MaxValue ? (int?)null : mic.Duration,
                otherTickets = others.Count,
                otherOpen = others.Count(x => x.Status == "open"),
            };
        }

        // Does the caller get the staff panel? Also tells the page whether to show ticket UI at all.
        [HttpGet("/api/site/support/role")]
        public IActionResult SupportRole()
        {
            var account = CurrentUser();
            if (account == null) return Ok(new { signedIn = false, staff = false });
            bool staff = IsStaff(account.Id);
            var mine = SupportDB.ForAccount(account.Id);
            return Ok(new
            {
                signedIn = true,
                staff,
                username = account.Username,
                openCount = staff ? SupportDB.OpenCount() : 0,
                awaitingStaff = staff ? SupportDB.AwaitingStaffCount() : 0,
                unreadMine = mine.Count(SupportDB.UnreadForPlayer),
                openMine = mine.Count(x => x.Status == "open"),
                maxOpen = SupportDB.MaxOpenTicketsPerPlayer,
                // The assistant's "I'm banned" answers read this.
                ban = PlayerDB.GetSiteBan(account.Id) is { } b ? new { permanent = b.RemainingSeconds == null, remainingSeconds = b.RemainingSeconds, reason = b.Reason } : null,
                micBan = PlayerDB.GetActiveMicBan(account.Id) is { } mb ? new { permanent = mb.Duration == int.MaxValue, remainingSeconds = mb.Duration == int.MaxValue ? (int?)null : mb.Duration } : null,
            });
        }

        [HttpPost("/api/site/support/create")]
        public IActionResult SupportCreate([FromBody] SupportCreateReq? body)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "Log in to open a ticket." });

            string msg = (body?.message ?? "").Trim();
            if (msg.Length < 3) return BadRequest(new { ok = false, error = "Please describe your issue." });
            if (msg.Length > 4000) msg = msg.Substring(0, 4000);
            string subject = (body?.subject ?? "").Trim();
            if (subject.Length > 120) subject = subject.Substring(0, 120);
            string category = body?.category is { } c && SupportCategories.ContainsKey(c) ? c : "other";

            // Anti-spam: a few open tickets at a time, and a short gap between new ones.
            if (!IsStaff(account.Id))
            {
                if (SupportDB.OpenCountFor(account.Id) >= SupportDB.MaxOpenTicketsPerPlayer)
                    return StatusCode(429, new { ok = false, error = $"You already have {SupportDB.MaxOpenTicketsPerPlayer} open tickets. Add to one of them in My tickets, or close one you don't need." });
                var lastAt = SupportDB.LastCreatedBy(account.Id);
                if (lastAt != null && DateTime.UtcNow - lastAt.Value < SupportDB.NewTicketCooldown)
                    return StatusCode(429, new { ok = false, error = "You just opened a ticket - please wait a minute before opening another." });
            }

            var files = SupportDB.OwnUnclaimed(account.Id, body?.attachments);
            var t = SupportDB.Create(account.Id, account.Username ?? $"Player{account.Id}", subject, msg, category, files);

            try
            {
                Webhook.Send(account.Id, "mod", $"🎫 New support ticket #{t.Id} ({SupportCategories[category]})",
                    $"From @{t.Username} (id {account.Id})\nSubject: {t.Subject}\n\n{msg}{(files.Count > 0 ? $"\n\n[{files.Count} screenshot(s)]" : "")}", 15844367);
            }
            catch (Exception ex) { Console.WriteLine($"[support] webhook failed: {ex.Message}"); }

            Console.WriteLine($"[support] ticket #{t.Id} ({category}) opened by @{t.Username} ({account.Id})");
            NotifyStaff();
            return Ok(new { ok = true, id = t.Id });
        }

        [HttpGet("/api/site/support/mine")]
        public IActionResult SupportMine()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            return Ok(new { ok = true, tickets = SupportDB.ForAccount(account.Id).Select(t => TicketSummary(t, false)) });
        }

        // Staff list. status: open | closed | all ; category: a topic key ; q: #id, @username or
        // words in the subject ; mine: only tickets claimed by the caller ; unclaimed: only unclaimed ;
        // waiting: only tickets waiting on staff.
        [HttpGet("/api/site/support/all")]
        public IActionResult SupportAll([FromQuery] string? status = null, [FromQuery] string? category = null, [FromQuery] string? q = null,
            [FromQuery] bool mine = false, [FromQuery] bool unclaimed = false, [FromQuery] bool waiting = false)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (!IsStaff(account.Id)) return StatusCode(403, new { ok = false, error = "staff only" });
            IEnumerable<SupportDB.Ticket> rows = SupportDB.All(status);
            if (!string.IsNullOrWhiteSpace(category) && category != "all") rows = rows.Where(t => CategoryOf(t) == category);
            if (mine) rows = rows.Where(t => t.AssignedTo == account.Id);
            if (unclaimed) rows = rows.Where(t => t.AssignedTo == 0);
            if (waiting) rows = rows.Where(t => SupportDB.AwaitingOf(t) == "staff");
            string term = (q ?? "").Trim();
            if (term.Length > 0)
            {
                string lower = term.ToLowerInvariant().TrimStart('@');
                bool isNum = int.TryParse(term.TrimStart('#'), out int num);
                rows = rows.Where(t => (isNum && (t.Id == num || t.AccountId == (ulong)num))
                                    || t.Username.ToLowerInvariant().Contains(lower)
                                    || t.Subject.ToLowerInvariant().Contains(lower));
            }
            // Waiting on staff first, then newest activity.
            var list = rows.OrderBy(t => SupportDB.AwaitingOf(t) == "staff" ? 0 : 1).ThenByDescending(t => t.UpdatedAt).ToList();
            return Ok(new { ok = true, tickets = list.Select(t => TicketSummary(t, true)) });
        }

        [HttpGet("/api/site/support/ticket/{id:int}")]
        public IActionResult SupportTicket(int id)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var t = SupportDB.Get(id);
            if (t == null) return NotFound(new { ok = false, error = "no such ticket" });
            bool staff = IsStaff(account.Id);
            bool owner = t.AccountId == account.Id;
            if (!owner && !staff) return StatusCode(403, new { ok = false, error = "not your ticket" });
            // Opening it is reading it (the owner's view counts as the player's even for staff).
            bool asStaffView = staff && !owner;
            if (asStaffView ? SupportDB.UnreadForStaff(t) : SupportDB.UnreadForPlayer(t))
            {
                SupportDB.MarkRead(id, asStaffView);
                t = SupportDB.Get(id)!;
                if (asStaffView) NotifyStaff(); else SiteNotifications.Notify(account.Id);
            }
            return Ok(new { ok = true, ticket = TicketDetail(t, account.Id, staff), staff });
        }

        [HttpPost("/api/site/support/ticket/{id:int}/reply")]
        public IActionResult SupportReply(int id, [FromBody] SupportReplyReq? body)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var t = SupportDB.Get(id);
            if (t == null) return NotFound(new { ok = false, error = "no such ticket" });
            bool staff = IsStaff(account.Id);
            if (t.AccountId != account.Id && !staff) return StatusCode(403, new { ok = false, error = "not your ticket" });

            string msg = (body?.message ?? "").Trim();
            var files = SupportDB.OwnUnclaimed(account.Id, body?.attachments);
            if (msg.Length < 1 && files.Count == 0) return BadRequest(new { ok = false, error = "empty reply" });
            if (msg.Length > 4000) msg = msg.Substring(0, 4000);
            bool isInternal = staff && body?.@internal == true;
            if (!staff)
            {
                if (!RateLimit.TryConsumeQuota($"supreply:{account.Id}", maxInWindow: 15, windowSeconds: 600, out int retry))
                {
                    Response.Headers["Retry-After"] = retry.ToString();
                    return StatusCode(429, new { ok = false, error = $"You're sending replies too fast. Try again in {(retry + 59) / 60} minute(s)." });
                }
                if (files.Count > 0 && SupportDB.PlayerAttachmentCount(t, account.Id) + files.Count > SupportDB.MaxPlayerAttachmentsPerTicket)
                    return StatusCode(429, new { ok = false, error = $"This ticket already has the most screenshots allowed ({SupportDB.MaxPlayerAttachmentsPerTicket}). Send your reply without them." });
            }

            var updated = SupportDB.AddMessage(id, account.Id, account.Username ?? $"Player{account.Id}", staff, msg, isInternal, files)!;
            if (!isInternal)
            {
                if (staff && account.Id != t.AccountId)
                {
                    // The player's bell lights up: "Staff replied to your ticket #N".
                    SiteNotifications.Notify(t.AccountId);
                }
                else
                {
                    try
                    {
                        Webhook.Send(account.Id, "mod", $"💬 Reply on ticket #{id}",
                            $"@{t.Username}: {(msg.Length > 600 ? msg.Substring(0, 600) + "…" : msg)}{(files.Count > 0 ? $"\n[{files.Count} screenshot(s)]" : "")}", 3447003);
                    }
                    catch { }
                    NotifyStaff();
                }
            }
            return Ok(new { ok = true, ticket = TicketDetail(updated, account.Id, staff), staff });
        }

        [HttpPost("/api/site/support/ticket/{id:int}/close")]
        public IActionResult SupportClose(int id) => SupportSetStatus(id, "closed");

        [HttpPost("/api/site/support/ticket/{id:int}/reopen")]
        public IActionResult SupportReopen(int id) => SupportSetStatus(id, "open");

        private IActionResult SupportSetStatus(int id, string status)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var t = SupportDB.Get(id);
            if (t == null) return NotFound(new { ok = false, error = "no such ticket" });
            bool staff = IsStaff(account.Id);
            if (t.AccountId != account.Id && !staff) return StatusCode(403, new { ok = false, error = "not your ticket" });
            SupportDB.SetStatus(id, status, account.Id, account.Username ?? $"Player{account.Id}", staff);
            Console.WriteLine($"[support] ticket #{id} {(status == "closed" ? "closed" : "reopened")} by @{account.Username} ({account.Id})");
            if (staff && account.Id != t.AccountId) SiteNotifications.Notify(t.AccountId); else NotifyStaff();
            return Ok(new { ok = true });
        }

        // Claim (assign to yourself) or release a ticket. Staff only.
        [HttpPost("/api/site/support/ticket/{id:int}/assign")]
        public IActionResult SupportAssign(int id, [FromBody] SupportAssignReq? body)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (!IsStaff(account.Id)) return StatusCode(403, new { ok = false, error = "staff only" });
            string name = account.Username ?? $"Player{account.Id}";
            var t = SupportDB.Assign(id, body?.assign == false ? 0 : account.Id, name, account.Id, name);
            if (t == null) return NotFound(new { ok = false, error = "no such ticket" });
            return Ok(new { ok = true, ticket = TicketDetail(t, account.Id, true), staff = true });
        }

        // Screenshot upload (multipart field "file"): returns a name to send with the ticket or
        // reply. Images only (decoded and re-encoded server-side), up to SupportDB.MaxAttachmentBytes.
        //
        // Anti-spam, cheapest check first and all BEFORE the body is read or decoded:
        //   * burst quota per account (10 per 10 min) and per IP (20 per 10 min, so a pile of
        //     accounts on one connection can't multiply it) - in memory;
        //   * persisted caps in SupportDB.UploadRefusal: 8 unsent uploads, 30 uploads / 40 MB a
        //     day per player, and a hard cap on the whole folder;
        // and unsent uploads are deleted after 2 hours, so uploading without ever sending the
        // ticket can't pile files up either. Staff are exempt from the quotas (not the caps).
        [HttpPost("/api/site/support/upload")]
        [RequestSizeLimit(8 * 1024 * 1024)]
        public async Task<IActionResult> SupportUpload()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (!IsStaff(account.Id))
            {
                string? ip = ClientIp();
                int retry;
                if (!RateLimit.TryConsumeQuota($"supup:{account.Id}", maxInWindow: 10, windowSeconds: 600, out retry)
                    || (!string.IsNullOrEmpty(ip) && !RateLimit.TryConsumeQuota($"supupip:{ip}", maxInWindow: 20, windowSeconds: 600, out retry)))
                {
                    Response.Headers["Retry-After"] = retry.ToString();
                    return StatusCode(429, new { ok = false, error = $"You're adding screenshots too fast. Try again in {(retry + 59) / 60} minute(s)." });
                }
            }
            string? refusal = SupportDB.UploadRefusal(account.Id);
            if (refusal != null) return StatusCode(429, new { ok = false, error = refusal });
            if (!Request.HasFormContentType || Request.Form.Files.Count == 0) return BadRequest(new { ok = false, error = "No file." });
            var file = Request.Form.Files[0];
            if (file.Length > SupportDB.MaxAttachmentBytes)
                return BadRequest(new { ok = false, error = $"That image is too big (max {SupportDB.MaxAttachmentBytes / 1024 / 1024} MB)." });
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            string? name = SupportDB.SaveAttachment(account.Id, ms.ToArray());
            if (name == null) return BadRequest(new { ok = false, error = "That isn't a PNG, JPG, GIF or WebP image we can read (or it's too large)." });
            return Ok(new { ok = true, name, url = AttachmentUrl(name) });
        }

        // The × on an unsent screenshot: frees it (and its slot in the unsent-upload cap) right away.
        [HttpDelete("/api/site/support/upload/{name}")]
        public IActionResult SupportUploadDelete(string name)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            return Ok(new { ok = SupportDB.DeleteUnsent(account.Id, name ?? "") });
        }

        // A screenshot: its uploader, the ticket's owner, or staff.
        [HttpGet("/api/site/support/attachment/{name}")]
        public IActionResult SupportAttachment(string name)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[0-9a-f]{32}\.(png|jpg|gif|webp)$")) return NotFound();
            var a = SupportDB.GetAttachment(name);
            if (a == null) return NotFound();
            bool allowed = a.UploaderId == account.Id || IsStaff(account.Id)
                           || (a.TicketId != 0 && SupportDB.Get(a.TicketId)?.AccountId == account.Id);
            if (!allowed) return StatusCode(403);
            string path = Path.Combine(SupportDB.UploadDir, name);
            if (!System.IO.File.Exists(path)) return NotFound();
            Response.Headers["Cache-Control"] = "private, max-age=86400";
            return PhysicalFile(path, a.ContentType);
        }

        // Every staff member's open site tabs re-read their bell ("N tickets waiting for staff").
        private static List<ulong>? _staffIds;
        private static DateTime _staffIdsAt;
        private static void NotifyStaff()
        {
            try
            {
                if (_staffIds == null || (DateTime.UtcNow - _staffIdsAt).TotalMinutes > 5)
                {
                    _staffIds = PlayerDB.PlayerDBFile.GetCollection<player_data>("players").FindAll()
                        .Where(p => IsStaffFlag(p.dev_Flag)).Select(p => p.playerid).ToList();
                    _staffIdsAt = DateTime.UtcNow;
                }
                foreach (var id in _staffIds) SiteNotifications.Notify(id);
            }
            catch { }
        }

        public static bool IsSupportStaff(ulong id) => IsStaffFlag(PlayerDB.GetFullAccount(id)?.dev_Flag ?? DevFlag.none);

        // GET /api/site/me
        // 200 with the account when signed in, 401 when not. Every page calls this on load to
        // decide whether to render the profile chip or the "Log in" button.
        [HttpGet("/api/site/me")]
        public IActionResult Me()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });

            var full = PlayerDB.GetFullAccount(account.Id);
            if (full == null) return NotFound(new { ok = false, error = "account not found" });

            // The owner sees their own email (it is never in PublicUser, which other people get).
            // `admin`: a dev account on an IP that can reach the admin panel - shows the quick-admin
            // button on profiles and rooms (SiteAdminController).
            return Ok(new { ok = true, user = PublicUser(full), email = full.player?.Email ?? "",
                            admin = SiteAdminController.ShowsAdmin(HttpContext, account.Id) });
        }

        public class EmailBody { public string? email { get; set; } }

        // Set or clear the account's email from the website's Email settings. Stored on the same
        // field the signup form and the in-game acc/account/me/email write, so the game's own
        // account screen shows it too (SelfAccountUpdate carries it live). Blank clears it. One
        // address per account: a different account already using it is refused, so an email can
        // later serve as a recovery / login identifier without ambiguity.
        [HttpPost("/api/site/email")]
        public IActionResult SetEmail([FromBody] EmailBody? body)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            string email = (body?.email ?? "").Trim();
            if (email.Length > 0)
            {
                if (email.Length > 254 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                    return BadRequest(new { ok = false, error = "Enter a valid email address." });
                ulong other = PlayerDB.GetAccountIdByEmail(email);
                if (other != 0 && other != account.Id)
                    return Conflict(new { ok = false, error = "That email is already used by another account." });
            }
            if (!PlayerDB.SetPlayerEmail(email.Length == 0 ? null : email, account.Id))
                return StatusCode(500, new { ok = false, error = "Could not save your email." });
            Console.WriteLine($"[site] {account.Id} {(email.Length == 0 ? "cleared their email" : "set their email")}");
            Campus_live_rewrite.api.server.Notifications.FireAndForget(
                Campus_live_rewrite.api.server.Notifications.PushAccountUpdate(account.Id), $"email-update {account.Id}");
            return Ok(new { ok = true, email });
        }

        public class PasswordBody
        {
            public string? current_password { get; set; }
            public string? new_password { get; set; }
            public string? confirm_password { get; set; }
        }

        // POST /api/site/password {current_password, new_password, confirm_password} - Settings > Password.
        // This used to be a button that only said "saved". The current password is checked with the same helper
        // the login uses (so wrong guesses count towards the same lockout), then the new one is hashed into the
        // players db by PlayerDB.SetPlayerPassword - the SAME field the game logs in with, so it changes there
        // too. Same 6-64 length rule as signup.
        [HttpPost("/api/site/password")]
        public IActionResult ChangeMyPassword([FromBody] PasswordBody? body)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });

            string cur = body?.current_password ?? "";
            string pw = body?.new_password ?? "";
            string confirm = body?.confirm_password ?? "";
            if (cur.Length == 0) return BadRequest(new { ok = false, error = "Enter your current password." });
            if (pw.Length < 6 || pw.Length > 64) return BadRequest(new { ok = false, error = "Password must be 6-64 characters." });
            if (pw != confirm) return BadRequest(new { ok = false, error = "The passwords don't match." });
            if (pw == cur) return BadRequest(new { ok = false, error = "Your new password is the same as the current one." });

            string? username = PlayerDB.GetFullAccount(account.Id)?.player?.Username;
            if (string.IsNullOrEmpty(username)) return StatusCode(500, new { ok = false, error = "Could not find your account." });

            var check = PlayerDB.GetAccountPassword(username, cur, isAdminPanel: false);
            if (!check.success || check.accid != account.Id)
                return BadRequest(new { ok = false, error = "Your current password is wrong." });

            PlayerDB.SetPlayerPassword(pw, account.Id, log: true);
            return Ok(new { ok = true });
        }

        // ---- Discord account linking (Settings > Discord tab) ------------------------------
        // A dedicated, ISOLATED flow: this hands the signed-in player a short code; they run
        // `/link <code>` with the Campus Discord bot, which binds their Discord id via
        // PlayerDB.SetDiscordLink. It shares NOTHING with the admin session gate (AdminGate) - a
        // Discord link records only an association and can never grant admin/dev access. See
        // DiscordLinkCodes + BotCommands./link.

        // GET /api/site/discord/status -> current link state for the signed-in account.
        [HttpGet("/api/site/discord/status")]
        public IActionResult DiscordStatus()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });

            var full = PlayerDB.GetFullAccount(account.Id);
            ulong did = full?.player_Extra?.DiscordId ?? 0;
            return Ok(new
            {
                ok = true,
                linked = did != 0,
                discordId = did == 0 ? "" : did.ToString(),
                discordUsername = full?.player_Extra?.DiscordUsername ?? ""
            });
        }

        // POST /api/site/discord/link/start -> a fresh 10-minute code to type into the bot's /link.
        [HttpPost("/api/site/discord/link/start")]
        public IActionResult DiscordLinkStart()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });

            var (code, ttl) = Campus_live_rewrite.api.server.DiscordLinkCodes.Generate(account.Id);
            Console.WriteLine($"[discord-link] account {account.Id} started a link (code issued, {ttl}s)");
            return Ok(new { ok = true, code, ttlSeconds = ttl });
        }

        // GET /api/site/discord/oauth/start -> the "Link Discord" button. Sends the signed-in user
        // to Discord's authorize page; the callback (DiscordController, state="link:") links their
        // Discord to THIS account and returns them to /settings. Reuses the same Discord app +
        // registered redirect URI as the admin login, but the "link:" state routes it to the
        // link branch, so it never mints an admin session.
        [HttpGet("/api/site/discord/oauth/start")]
        public IActionResult DiscordOAuthStart()
        {
            var account = CurrentUser();
            if (account == null) return Redirect("/login");

            var (code, _) = Campus_live_rewrite.api.server.DiscordLinkCodes.Generate(account.Id);
            string state = "link:" + code;
            string url = "https://discord.com/api/oauth2/authorize"
                + "?client_id=" + DiscordController.ClientId
                + "&redirect_uri=" + Uri.EscapeDataString(DiscordController.RedirectUri)
                + "&response_type=code"
                + "&scope=" + Uri.EscapeDataString("identify")
                + "&state=" + Uri.EscapeDataString(state);
            return Redirect(url);
        }

        // POST /api/site/discord/unlink -> remove the Discord link from the signed-in account.
        [HttpPost("/api/site/discord/unlink")]
        public IActionResult DiscordUnlink()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });

            PlayerDB.SetDiscordLink(account.Id, 0, null);
            return Ok(new { ok = true });
        }

        // ---- Spotify linking (Settings > Services > Spotify) --------------------------------
        // OAuth against the Spotify app in Data/spotify_app.json (see SpotifyLink). The link
        // records a refresh token + the player's "show my Spotify to" choice; the profile page and
        // friends list show what they are listening to, to whoever that choice allows.

        // ---- linked socials (Settings > Services) ----------------------------------------
        // GET  /api/site/social/status                 every provider's state for the signed-in account
        // GET  /api/site/social/{provider}/start       sign in with the service (redirect)
        // GET  /api/site/social/{provider}/callback    the service sends the player back here
        // POST /api/site/social/{provider}/unlink
        // POST /api/site/social/{provider}/show {show:bool}   the "show on my profile" switch
        [HttpGet("/api/site/social/status")]
        public IActionResult SocialStatus()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var full = PlayerDB.GetFullAccount(account.Id);
            return Ok(new { ok = true, providers = SocialLinks.Status(account.Id, full?.player_Extra), preview = SocialLinks.ProfileRows(account.Id, full?.player_Extra) });
        }

        [HttpGet("/api/site/social/{provider}/start")]
        public IActionResult SocialStart(string provider)
        {
            var account = CurrentUser();
            if (account == null) return Redirect("/login");
            provider = (provider ?? "").ToLowerInvariant();
            var p = SocialLinks.Find(provider);
            if (p == null) return NotFound();
            if (provider == "discord") return Redirect("/api/site/discord/oauth/start");
            if (provider == "spotify") return Redirect("/api/site/spotify/oauth/start");
            string? url = SocialLinks.StartUrl(account.Id, provider);
            if (url == null) return Redirect("/settings?social=" + provider + ":unconfigured");
            return Redirect(url);
        }

        [HttpGet("/api/site/social/{provider}/callback")]
        public async Task<IActionResult> SocialCallback(string provider)
        {
            provider = (provider ?? "").ToLowerInvariant();
            if (SocialLinks.Find(provider) == null) return NotFound();
            string outcome = await SocialLinks.Complete(provider, Request.Query, CurrentUser()?.Id ?? 0);
            return Redirect(ConfigCampus.SiteUrl + "/settings?social=" + provider + ":" + outcome);
        }

        [HttpPost("/api/site/social/{provider}/unlink")]
        public IActionResult SocialUnlink(string provider)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            provider = (provider ?? "").ToLowerInvariant();
            if (SocialLinks.Find(provider) == null || provider == "discord" || provider == "spotify") return BadRequest(new { ok = false, error = "unknown provider" });
            SocialLinks.Unlink(account.Id, provider);
            SiteNotifications.ProfileChanged(account.Id);
            return Ok(new { ok = true });
        }

        public class SocialShowBody { public bool? show { get; set; } }

        [HttpPost("/api/site/social/{provider}/show")]
        public IActionResult SocialShow(string provider, [FromBody] SocialShowBody? body)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            provider = (provider ?? "").ToLowerInvariant();
            if (SocialLinks.Find(provider) == null) return BadRequest(new { ok = false, error = "unknown provider" });
            SocialLinks.SetShow(account.Id, provider, body?.show ?? true);
            SiteNotifications.ProfileChanged(account.Id);
            return Ok(new { ok = true, show = body?.show ?? true });
        }

        [HttpGet("/api/site/spotify/status")]
        public async Task<IActionResult> SpotifyStatus()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var link = SpotifyLink.Get(account.Id);
            object? now = link == null ? null : await SpotifyLink.PayloadFor(account.Id, account.Id);
            return Ok(new
            {
                ok = true,
                configured = SpotifyLink.Configured,
                linked = link != null,
                broken = link?.Broken ?? false,
                notAllowed = link?.NotAllowed ?? false,
                displayName = link?.DisplayName ?? "",
                profileUrl = link?.ProfileUrl ?? "",
                visibility = link?.Visibility ?? SpotifyLink.VisFriends,
                now_playing = now
            });
        }

        [HttpGet("/api/site/spotify/oauth/start")]
        public IActionResult SpotifyOAuthStart()
        {
            var account = CurrentUser();
            if (account == null) return Redirect("/login");
            string? url = SpotifyLink.StartUrl(account.Id);
            if (url == null) return Redirect("/settings?spotify=unconfigured");
            return Redirect(url);
        }

        [HttpGet("/api/site/spotify/callback")]
        public async Task<IActionResult> SpotifyCallback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
        {
            string outcome = await SpotifyLink.Complete(code, state, error);
            return Redirect(ConfigCampus.SiteUrl + "/settings?spotify=" + outcome);
        }

        [HttpPost("/api/site/spotify/unlink")]
        public IActionResult SpotifyUnlink()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            SpotifyLink.Unlink(account.Id);
            return Ok(new { ok = true });
        }

        // POST /api/site/spotify/visibility  {visibility: 0 everyone | 1 friends | 2 favourites | 3 no one}
        [HttpPost("/api/site/spotify/visibility")]
        public async Task<IActionResult> SpotifyVisibility()
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            int vis = -1;
            try
            {
                var f = RoomModeration.Fields(Request);   // form, query or JSON body
                string raw = RoomModeration.Str(f, "visibility", "value") ?? "";
                if (!int.TryParse(raw, out vis))
                    vis = raw.ToLowerInvariant() switch { "everyone" => 0, "friends" => 1, "favourites" or "favorites" => 2, "none" or "noone" or "nobody" => 3, _ => -1 };
            }
            catch { }
            await Task.CompletedTask;
            if (vis < 0 || vis > 3) return BadRequest(new { ok = false, error = "visibility must be 0-3" });
            SpotifyLink.SetVisibility(account.Id, vis);
            return Ok(new { ok = true, visibility = vis });
        }

        // GET /api/site/spotify/nowplaying/{handle} - what that player is listening to, if their
        // setting lets the signed-in viewer see it. {ok, now_playing: null|{...}}. Polled by the
        // profile page; Spotify itself is asked at most every 10 s per player.
        [HttpGet("/api/site/spotify/nowplaying/{handle}")]
        public async Task<IActionResult> SpotifyNowPlaying(string handle)
        {
            var viewer = CurrentUser();
            if (viewer == null) return Ok(new { ok = true, now_playing = (object?)null });
            ulong owner = ulong.TryParse(handle, out var n) ? n : PlayerDB.GetAccountIdByUsernameCI(handle.TrimStart('@'));
            if (owner == 0) return NotFound(new { ok = false, error = "no such player" });
            return Ok(new { ok = true, now_playing = await SpotifyLink.PayloadFor(owner, viewer.Id) });
        }

        // ---- search (ported from server.py) -----------------------------------------------

        // GET /api/search/rooms?query=
        // Flask fetched https://camprec.net/rooms/search with a stored bearer token. We
        // are that server, so read RoomDB directly.
        //
        // Visibility goes through RoomDB.CanPlayerSeeRoom with the site visitor's account id
        // (0 when signed out) rather than a hand-rolled filter, so the website can never show
        // a room the game would hide. Dorms are dropped on top of that: they pass the check
        // for their owner, and a personal dorm has no business in a public site search.
        [HttpGet("/api/search/rooms")]
        public IActionResult SearchRooms(string? query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return Ok(new { ok = true, results = Array.Empty<object>() });

            try
            {
                ulong viewerId = CurrentUser()?.Id ?? 0UL;

                var rooms = RoomDB.Search(query) ?? new List<RoomDBClasses.RoomRoot>();
                var visible = rooms
                    .Where(r => r != null && !r.IsDorm && RoomDB.CanPlayerSeeRoom(r, viewerId))
                    .Take(30)
                    .Select(r => new
                    {
                        RoomId = r.RoomId,
                        Name = r.Name,
                        ImageName = r.ImageName,
                        Description = r.Description,
                        Stats = new { VisitCount = r.Stats?.VisitCount ?? 0 }
                    })
                    .ToList();

                return Ok(new { ok = true, results = visible });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] room search failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "search failed" });
            }
        }

        // GET /api/search/players?name=
        [HttpGet("/api/search/players")]
        public IActionResult SearchPlayers(string? name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return Ok(new { ok = true, results = Array.Empty<object>() });

            try
            {
                var matches = PlayerDB.Search(name) ?? new List<account_data>();
                var results = matches
                    .Take(30)
                    .Select(a => new
                    {
                        accountId = a.accountId,
                        username = a.username,
                        displayName = a.displayName,
                        profileImage = a.profileImage
                    })
                    .ToList();

                return Ok(new { ok = true, results });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] player search failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "search failed" });
            }
        }

        // ---- profile image ----------------------------------------------------------------

        // POST /api/profile-image   (multipart field "image")
        //
        // Flask re-encoded the upload and pushed it to an EXTERNAL image host, signing the
        // request with an HMAC key it kept in token.json on disk. That whole path is gone:
        // Campus already stores and serves player images itself out of Data/Images, so the
        // file is written locally and the account row is pointed at it.
        //
        // Requires a signed-in site session - without it anyone could rewrite any avatar.
        [HttpPost("/api/profile-image")]
        [RequestSizeLimit(8 * 1024 * 1024)]
        public async Task<IActionResult> UploadProfileImage(IFormFile? image)
        {
            var account = CurrentUser();
            if (account == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (PlayerDB.IsProfileLocked(account.Id, PlayerDB.ProfileLock.Picture))
                return BadRequest(new { ok = false, error = PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Picture) });

            if (image == null || image.Length == 0)
                return BadRequest(new { ok = false, error = "no image uploaded" });
            if (image.Length > 8 * 1024 * 1024)
                return BadRequest(new { ok = false, error = "image is larger than 8MB" });

            // Rate limit BEFORE decoding: a 8MB image decode is the expensive part, so a
            // rejected caller must never reach it. Two limits, because either alone is weak -
            // the cooldown stops bursts, the quota stops a patient script uploading every
            // 11 seconds forever. Keyed on account id, not IP: the endpoint already requires
            // a session, and an IP key would punish everyone behind one NAT.
            string rlKey = $"pfp:{account.Id}";
            if (!RateLimit.TryConsume(rlKey, cooldownSeconds: 10, out int retryAfter))
            {
                Response.Headers["Retry-After"] = retryAfter.ToString();
                return StatusCode(429, new { ok = false, error = $"Slow down - try again in {retryAfter}s." });
            }
            if (!RateLimit.TryConsumeQuota(rlKey, maxInWindow: 10, windowSeconds: 3600, out int quotaRetry))
            {
                Response.Headers["Retry-After"] = quotaRetry.ToString();
                return StatusCode(429, new
                {
                    ok = false,
                    error = $"Too many profile picture changes. Try again in {quotaRetry / 60 + 1} minutes."
                });
            }

            try
            {
                // Decode + re-encode rather than trusting the upload. This is the check that
                // matters: a file is only written if ImageSharp could actually parse it as an
                // image, so a renamed .exe or a polyglot never lands in the images folder. It
                // also strips EXIF (location data) as a side effect.
                using var src = await Image.LoadAsync<Rgba32>(image.OpenReadStream());

                // Cap the stored size so one upload can't fill the disk with a 10000x10000 PNG,
                // but keep it large enough to look crisp on the profile page (1024, up from 512 -
                // 512 was the visible "compression" on website-uploaded pictures). Re-encoding is
                // to PNG, which is lossless; this Max resize is the only quality-affecting step,
                // and it only ever DOWNscales (never upscales a small in-game capture).
                if (src.Width > 1024 || src.Height > 1024)
                {
                    src.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(1024, 1024)
                    }));
                }

                // Name is server-generated - never derived from the uploaded filename, which
                // is attacker-controlled and would otherwise be a path-traversal write.
                string imageName = $"pfp_{account.Id}_{Guid.NewGuid():N}.png";
                string dir = Path.Combine(Environment.CurrentDirectory, "Data", "Images", "player_images");
                Directory.CreateDirectory(dir);

                await src.SaveAsPngAsync(Path.Combine(dir, imageName));

                // Note what is NOT happening here: no row is written to ImageMetadataDB. A
                // profile picture is not a photo, so it must not appear in the photo feeds.
                // (The feeds also filter to ShareCamera, so this is belt and braces.)
                string? previous = PlayerDB.GetFullAccount(account.Id)?.player?.profileImage;
                PlayerDB.SetPlayerImageName(imageName, account.Id);

                // Live push: redraw the player's profile in-game (and for anyone viewing it) with
                // the new picture - no relog. AccountUpdate is the redraw notification; immediate is
                // safe here because the GAME client didn't make this request, so there's no in-flight
                // image load to double-complete (the self-service double-load dorm-drop [385120]).
                Campus_live_rewrite.api.server.Notifications.FireAndForget(
                    Campus_live_rewrite.api.server.Notifications.PushAccountUpdate(account.Id), "site pfp refresh");

                // Delete the picture this one replaces, so changing your avatar does not leave
                // a copy behind every time. Strictly scoped to files this endpoint generated
                // for THIS account (the "pfp_<id>_" prefix): a profile picture set in-game
                // points at a shared saved-image that other records may still reference, and
                // deleting one of those would blank it elsewhere.
                if (!string.IsNullOrWhiteSpace(previous)
                    && previous != imageName
                    && previous.StartsWith($"pfp_{account.Id}_", StringComparison.Ordinal)
                    && !previous.Contains('/') && !previous.Contains('\\'))
                {
                    try
                    {
                        string old = Path.Combine(dir, previous);
                        if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                    }
                    catch (Exception ex)
                    {
                        // Never fail the upload over cleanup - the new picture is already live.
                        Console.WriteLine($"[site] could not remove old pfp '{previous}': {ex.Message}");
                    }
                }

                // ImagesController serves Data/Images/player_images at /img/<name>.
                return Ok(new { ok = true, imageName, url = "/img/" + imageName });
            }
            catch (UnknownImageFormatException)
            {
                return BadRequest(new { ok = false, error = "that file is not a supported image" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] profile image upload failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not save the image" });
            }
        }

        // GET /api/health - kept so anything that probed the Flask health route still works.
        [HttpGet("/api/health")]
        public IActionResult Health() => Ok(new { ok = true });

        public class SiteCreds
        {
            public string? username { get; set; }
            public string? password { get; set; }
        }

        public class SiteRegister
        {
            public string? username { get; set; }
            public string? display_name { get; set; }
            public string? password { get; set; }
            public string? confirm_password { get; set; }
            public string? email { get; set; }
            // Date of birth (YYYY-MM-DD from the form's <input type=date>). Required: under-13 gets a
            // random @name + a junior account.
            public string? birthday { get; set; }
            // Hidden honeypot field - must stay empty. Bots that autofill everything trip it.
            public string? website { get; set; }
        }
    }
}
