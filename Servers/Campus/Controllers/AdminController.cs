using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.IO.Compression;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;
using CampusLiveSource.Controllers;
using static Campus_live_rewrite.api.server.Moderation_Detail;

namespace Campus_live_rewrite.Controllers
{
    public class AdminController : Controller
    {
        private const string AuthCookieName = "Authorization";

        /// <summary>
        /// Finds an admin HTML file whether the process runs from the deploy dir or not.
        ///
        /// The real routes are /admin/v4/login and /admin/home/v4; the file is served with
        /// Environment.CurrentDirectory, which is the systemd WorkingDirectory (/opt/campus).
        /// After a reinstall that directory can differ, so also try the assembly's own base
        /// directory - otherwise every admin page 404s even though the files shipped.
        /// </summary>
        private static string? FindAdminFile(params string[] relative)
        {
            foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                var p = Path.Combine(new[] { root }.Concat(relative).ToArray());
                if (System.IO.File.Exists(p)) return p;
            }
            return null;
        }

        // Bare /admin and its common variants. There was no route for any of these, so the
        // most natural URL a person types - camprec.net/admin - 404'd, which reads as
        // "the admin panel is down". Send them to the panel: to the home page when they are
        // already an authenticated dev, otherwise to the login page.
        // "/admin" already matches "/admin/" (the matcher ignores the trailing slash), so a
        // separate "/admin/" template made both endpoints match "/admin/" -> AmbiguousMatchException
        // -> 500 on the bare entry point. One template covers both.
        [HttpGet("/admin")]
        [HttpGet("/admin/home")]
        [HttpGet("/admin/login")]
        public IActionResult AdminRoot()
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            return Redirect(error == null ? "/admin/home/v4" : "/admin/v4/login");
        }

        /// <summary>
        /// The Discord sign-in callback (gameauth.ddns.net) cannot set a cookie for this host, so it hands the token over under a one-time
        /// ticket (AdminHandoff); this takes it and signs the browser in on admin.camprec.net.
        /// </summary>
        [HttpGet("/admin/discord-finish")]
        public IActionResult DiscordFinish([FromQuery] string? t)
        {
            string? jwt = Campus_live_rewrite.Middleware.AdminHandoff.Take(t);
            if (jwt == null) return Redirect("/admin/v4/login");
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

        // NOTE: there is no separate visible gate page. The web token is just an extra field on the
        // admin login form (LoginV*.html) and is checked inside AdminLoginAPI below, so a person
        // only ever sees the normal login - with a token box on it. The login PAGE is reachable
        // without the gate cookie (that's where you enter the token); everything else on the admin
        // surface requires the gate cookie that a correct-token login stamps.
        [HttpGet("/admin/v{version}/login")]
        public IActionResult Login(int version)
        {
            var filePath = FindAdminFile("Web", "Admin", $"LoginV{version}.html");
            if (filePath == null)
                return NotFound();

            return PhysicalFile(filePath, "text/html");
        }

        // Issues the two admin cookies - the ONE place either login path (plain password, or password + 2FA code) ends up,
        // so there is exactly one way an admin session is ever actually minted. No server-side Response.Redirect here: the
        // login page calls this through fetch(), which (like a browser) follows a 302 itself before JS ever sees the body -
        // the JSON response is what the page reads to decide where to navigate, so IT does the navigating once it sees success.
        private ContentResult IssueAdminSession(ulong accountId, object body)
        {
            string jwt = ClientSecurity.RefreshToken_Web(accountId, TimeSpan.FromHours(1), "admin");
            Response.Cookies.Append(AuthCookieName, jwt, new Microsoft.AspNetCore.Http.CookieOptions
            {
                Expires = DateTime.UtcNow.AddHours(1),
                HttpOnly = true,
                Secure = true,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict
            });
            Response.Cookies.Append(AdminGate.CookieName, AdminGate.Issue(), new Microsoft.AspNetCore.Http.CookieOptions
            {
                Expires = DateTime.UtcNow.Add(AdminGate.Ttl),
                HttpOnly = true,
                Secure = true,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict
            });
            return new ContentResult { Content = JsonConvert.SerializeObject(body), ContentType = "text/html" };
        }

        [HttpPost("/api/web/admin/login")]
        public ContentResult AdminLoginAPI([FromForm] string username, [FromForm] string password)
        {
            try
            {
                var loginResult = PlayerDB.GetAccountPassword(username, password, isAdminPanel: true);

                if (!loginResult.success)
                {
                    return new ContentResult
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            success = loginResult.success,
                            error = loginResult.error
                        }),
                        ContentType = "text/html"
                    };
                }

                // Correct password. If this account turned 2FA on, that's only HALF the login - hold off on the admin
                // cookies and send back a ticket naming the account instead of a session. The browser then prompts for a
                // code and posts it to /api/web/admin/login/2fa, which is the only place the cookies actually get issued
                // for a 2FA account (see IssueAdminSession above).
                if (TwoFactorDB.IsEnabled(loginResult.accid))
                {
                    Console.WriteLine($"[admin-2fa] {username} ({loginResult.accid}): password ok, code required");
                    return new ContentResult
                    {
                        Content = JsonConvert.SerializeObject(new { success = false, need2fa = true, pending = PendingTwoFactorLogin.Issue(loginResult.accid, "admin") }),
                        ContentType = "text/html"
                    };
                }

                // Admin panel is 2FA-only: a correct password with NO 2FA enrolled does not get a session either - it
                // gets funneled into forced enrollment instead (AdminSetup2FABegin/Confirm below), off the SAME kind of
                // short-lived ticket, just on a different `surface` ("admin-setup") so it can't be replayed against the
                // real /login/2fa endpoint. No session cookie exists until ConfirmEnroll succeeds, so there's no window
                // where a password-only login reaches the dashboard.
                Console.WriteLine($"[admin-2fa] {username} ({loginResult.accid}): password ok, 2FA not set up - forcing enrollment");
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { success = false, needSetup = true, pending = PendingTwoFactorLogin.Issue(loginResult.accid, "admin-setup") }),
                    ContentType = "text/html"
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AdminLoginAPI] Exception: {ex}");
                return new ContentResult
                {
                    Content = "Invalid username or password. Please try again.",
                    ContentType = "text/html"
                };
            }
        }

        /// <summary>Step 2 of a 2FA account's login: the ticket from AdminLoginAPI, plus a code from their authenticator
        /// app (or one of their recovery codes). Wrong-code attempts count toward the same IP lockout a wrong password
        /// does, so this can't be brute-forced any more easily than the password itself.</summary>
        [HttpPost("/api/web/admin/login/2fa")]
        public ContentResult AdminLogin2FA([FromForm] string pending, [FromForm] string code)
        {
            string clientIp = LoginGuard.ClientIp(HttpContext);
            if (LoginGuard.IsLockedOut(clientIp, out int retry))
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, error = $"Too many attempts - try again in {retry}s." }), ContentType = "text/html" };

            ulong accountId = PendingTwoFactorLogin.Validate(pending, "admin");
            if (accountId == 0)
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, error = "That login attempt expired - log in again." }), ContentType = "text/html" };

            if (!TwoFactorDB.VerifyLogin(accountId, code))
            {
                LoginGuard.RecordFailure(clientIp);
                Console.WriteLine($"[admin-2fa] account {accountId}: wrong code from {clientIp}");
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, need2fa = true, pending, error = "Wrong code." }), ContentType = "text/html" };
            }
            LoginGuard.RecordSuccess(clientIp);
            Console.WriteLine($"[admin-2fa] account {accountId}: code accepted");
            return IssueAdminSession(accountId, new { success = true, error = (string?)null, accid = accountId });
        }

        /// <summary>Step 2 for an admin account that does NOT have 2FA set up yet (see the `needSetup` branch of
        /// AdminLoginAPI): the "admin-setup" ticket already proves the password was correct, so this starts enrollment
        /// off the ticket alone - no session exists yet, matching the "no 2FA, no admin panel" rule. Mirrors
        /// TwoFactorSetup() below but takes the account id from the ticket instead of a logged-in session.</summary>
        [HttpPost("/api/web/admin/login/setup2fa/begin")]
        public ContentResult AdminSetup2FABegin([FromForm] string pending)
        {
            ulong accountId = PendingTwoFactorLogin.Validate(pending, "admin-setup");
            if (accountId == 0)
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, error = "That login attempt expired - log in again." }), ContentType = "text/html" };

            string username = PlayerDB.GetFullAccount(accountId)?.player?.Username ?? ("admin" + accountId);
            string secret = TwoFactorDB.BeginEnroll(accountId);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { success = true, secret, otpauth = Totp.OtpAuthUri(secret, username) }),
                ContentType = "application/json"
            };
        }

        /// <summary>Confirms the code from AdminSetup2FABegin and, on success, mints the real admin session - the
        /// first and only cookie issuance for an account that just enrolled through forced setup.</summary>
        [HttpPost("/api/web/admin/login/setup2fa/confirm")]
        public ContentResult AdminSetup2FAConfirm([FromForm] string pending, [FromForm] string code)
        {
            ulong accountId = PendingTwoFactorLogin.Validate(pending, "admin-setup");
            if (accountId == 0)
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, error = "That login attempt expired - log in again." }), ContentType = "text/html" };

            var codes = TwoFactorDB.ConfirmEnroll(accountId, code ?? "");
            if (codes == null)
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, error = "Wrong code - try the current 6 digits from your app." }), ContentType = "text/html" };

            Console.WriteLine($"[admin-2fa] account {accountId}: 2FA set up during forced enrollment, session issued");
            return IssueAdminSession(accountId, new { success = true, error = (string?)null, accid = accountId, recoveryCodes = codes });
        }

        // ---- 2FA enrollment (the signed-in admin managing THEIR OWN account, from the Settings tab) --------------------

        [HttpGet("/admin/api/2fa/status")]
        public ContentResult TwoFactorStatus()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            return new ContentResult { Content = JsonConvert.SerializeObject(new { success = true, enabled = TwoFactorDB.IsEnabled(account!.Id) }), ContentType = "application/json" };
        }

        /// <summary>Starts enrollment: a fresh secret, not yet active. The admin panel shows it as text (and an
        /// otpauth:// URI) for the admin to add to their authenticator app, then calls /confirm with a real code.</summary>
        [HttpPost("/admin/api/2fa/setup")]
        public ContentResult TwoFactorSetup()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string secret = TwoFactorDB.BeginEnroll(account!.Id);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { success = true, secret, otpauth = Totp.OtpAuthUri(secret, account.Username) }),
                ContentType = "application/json"
            };
        }

        [HttpPost("/admin/api/2fa/confirm")]
        public ContentResult TwoFactorConfirm([FromBody] TwoFactorCodeBody body)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var codes = TwoFactorDB.ConfirmEnroll(account!.Id, body?.Code ?? "");
            if (codes == null)
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, message = "Wrong code - try the current 6 digits from your app." }), ContentType = "application/json" };
            Console.WriteLine($"[admin-2fa] {account.Username} ({account.Id}) turned on 2FA");
            return new ContentResult { Content = JsonConvert.SerializeObject(new { success = true, recoveryCodes = codes }), ContentType = "application/json" };
        }

        [HttpPost("/admin/api/2fa/disable")]
        public ContentResult TwoFactorDisable()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            TwoFactorDB.Disable(account!.Id);
            Console.WriteLine($"[admin-2fa] {account.Username} ({account.Id}) turned 2FA off");
            return new ContentResult { Content = JsonConvert.SerializeObject(new { success = true }), ContentType = "application/json" };
        }

        public class TwoFactorCodeBody { public string? Code { get; set; } }

        [HttpGet("/admin/home/v{version}")]
        public IActionResult AdminHomeVVersion(int version)
        {
            // Dev OR mod: the page shell itself has to load for either role - which tabs/data a mod
            // actually gets is enforced per-endpoint below and hidden client-side (HomeV4.html whoami).
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null)
            {
                return Redirect("/admin/v4/login");
            }

            var filePath = FindAdminFile("Web", "Admin", "Home", $"HomeV{version}.html");
            if (filePath == null)
                return NotFound();

            return PhysicalFile(filePath, "text/html");
        }

        /// <summary>The page's own self-role check - HomeV4.html calls this on load to decide which sidebar
        /// tabs to show. Mods get a fixed, server-decided slice (Dashboard, Players, Reports, Ban Logs, Chat);
        /// this endpoint just tells the client which one it's looking at, it doesn't grant anything by itself -
        /// every actual data/action endpoint enforces its own role independently.</summary>
        [HttpGet("/admin/api/whoami")]
        public ContentResult AdminWhoAmI()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            bool isDev = PlayerDB.CheckDevFlag(account!.Id);
            return Json(new { success = true, username = account.Username, isDev, isMod = !isDev });
        }

        [Route("/admin/api/dashboard_data")]
        public ContentResult DashboardData()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null)
                return error;

            // Each count is guarded on its own. A single DB hiccup (a checkpoint mid-read, a
            // locked collection) used to throw here and 500 the WHOLE dashboard, leaving every
            // card stuck - one bad count should cost one number, not the page. -1 marks a count
            // that could not be read.
            int Safe(Func<int> f) { try { return f(); } catch (Exception ex) { Console.WriteLine($"[dashboard] count failed: {ex.Message}"); return -1; } }

            int OnlinePlayers = Safe(() => NotificationsController.WebSockets.Count);
            string AdminMOTD = "Go wild like Campus!";
            long bytesUsed = GC.GetTotalMemory(false);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    total_players = Safe(() => (int)PlayerDB.GetPlayerCount()),
                    online_players = OnlinePlayers,
                    banned_players = 0,
                    total_rooms = Safe(() => (int)RoomDB.GetAllRoomsCount()),
                    total_inventions = Safe(() => InventionDB.Collection().Count()),
                    mem_usage = FormatBytes(bytesUsed),
                    total_clubs = Safe(() => ClubDB.CountAll()),
                    MOTD = AdminMOTD,
                    diskspace = 0,
                    quick_link_sidebar = new List<Object>
                    {
                        new
                        {
                            Title = "Reports",
                            Button_Title = "View Reports",
                            Button_url = "/admin/reports/all",
                            Button_icon = "",
                        }
                    },
                    // Quick links open panel TABS ("#Reports" -> the Reports tab, see the page's
                    // GoToPage). The old ones pointed at pages that don't exist (/admin/reports/all,
                    // /campus_net/home) or at placeholder text.
                    // Gifts is dev-only (mods never get that tab), so its tile is left out entirely for a
                    // mod account rather than shown as a dead link - filtered server-side here so the
                    // client doesn't need its own copy of this role rule.
                    quick_link = (PlayerDB.CheckDevFlag(account!.Id) ? new List<Object>
                    {
                        new { Title = "Reports", disc = "Open player reports and bug reports.", Button_Title = "Open Reports", Button_url = "#Reports" },
                        new { Title = "Ban Logs", disc = "Every ban, unban, kick and warning.", Button_Title = "Open Ban Logs", Button_url = "#BanLogs" },
                        new { Title = "Gifts", disc = "Give items or skins to a player.", Button_Title = "Open Gifts", Button_url = "#Gifts" },
                        new { Title = "Website", disc = "The Campus site players see.", Button_Title = "Open site", Button_url = "/" }
                    } : new List<Object>
                    {
                        new { Title = "Reports", disc = "Open player reports and bug reports.", Button_Title = "Open Reports", Button_url = "#Reports" },
                        new { Title = "Ban Logs", disc = "Every ban, unban, kick and warning.", Button_Title = "Open Ban Logs", Button_url = "#BanLogs" },
                        new { Title = "Website", disc = "The Campus site players see.", Button_Title = "Open site", Button_url = "/" }
                    }),
                }),
                ContentType = "application/json"
            };
        }

        [HttpGet("/admin/api/players")]
        public ContentResult AdminGetPlayersAPI()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null)
                return error;
            var query = HttpContext.Request.Query;

            string? id = query["id"];
            string? displayName = query["displayName"];
            string? minLevel = query["minLevel"];
            string? jrState = query["jrState"];
            string? platformId = query["platformId"];
            string? deviceId = query["deviceId"].FirstOrDefault();
            bool onlyBannedPlayers = bool.TryParse(query["banned"], out var b) && b;

            var players = PlayerDB.Find_all_player_filtered(id, displayName, minLevel, jrState, platformId, deviceId, onlyBannedPlayers);

            // Enrich each row with live presence so the panel can show WHERE a player is - the room
            // name, its instance id and the room id - and whether they're online. Read-only peek so
            // enumerating everyone doesn't mutate heartbeats. A player who is nowhere gets nulls.
            var arr = Newtonsoft.Json.Linq.JArray.FromObject(players);
            foreach (var row in arr.OfType<Newtonsoft.Json.Linq.JObject>())
            {
                ulong pid = (ulong?)row["AccountId"] ?? 0;
                var (online, room) = HeartbeatDB.PeekPresence(pid);
                row["IsOnline"] = online;
                row["RoomName"] = room?.name;
                row["RoomInstanceId"] = room != null ? room.roomInstanceId : (long?)null;
                row["RoomId"] = room != null ? room.roomId : (long?)null;
                row["SubRoomId"] = room != null ? room.subRoomId : (long?)null;
                row["RoomIsPrivate"] = room?.isPrivate ?? false;
            }

            return new ContentResult
            {
                Content = arr.ToString(Newtonsoft.Json.Formatting.None),
                ContentType = "application/json"
            };
        }

        // Force join: drop the JOINER into the TARGET's current room instance. Same mechanism as the
        // !goto teleport - the joiner's heartbeat is set to the target's exact instance, so on its
        // next heartbeat the joiner's client walks into that room (same instance = same Photon room =
        // they end up together).
        //   joinerId = the person being moved (the one who "joins")
        //   targetId = the person whose room they land in (the one "being joined")
        [HttpPost("/admin/api/force_join")]
        public ContentResult AdminForceJoin([FromForm] ulong joinerId, [FromForm] ulong targetId)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null)
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, error = "unauthorized" }), ContentType = "application/json", StatusCode = 401 };

            ContentResult Fail(string msg) => new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { success = false, error = msg }),
                ContentType = "application/json",
                StatusCode = 200
            };

            if (joinerId == 0 || targetId == 0) return Fail("Pick both players.");
            if (joinerId == targetId) return Fail("Joiner and target are the same player.");

            var (targetOnline, room) = HeartbeatDB.PeekPresence(targetId);
            if (room == null) return Fail("Target isn't in a room right now - nowhere to send the joiner.");

            // Land the joiner in the target's EXACT instance only when it's a joinable PUBLIC one -
            // then they end up together on the same Photon room. A PRIVATE instance (a Dorm) is
            // single-occupant: the client would try, fail to load, and bounce home (the "takes ages
            // then fails" symptom). Say so up front instead. For a public room we also accept a
            // sibling joinable instance if the exact one somehow isn't usable.
            // Admin override: the joiner is dropped into the target's EXACT instance, public or
            // private (a dorm, a private-instance game, an invite-only room). Two players meet when
            // their presence carries the same Photon room name, and a private instance has one just
            // like a public one - the old refusal for private rooms had no technical reason behind
            // it. Room bans and private-room roles are not consulted: this is the admin's call.
            HeartbeatDB.RoomInstance dest;
            if (!string.IsNullOrEmpty(room.photonRoomIdYeee))
            {
                dest = room;
            }
            else
            {
                // An instance with no Photon room name can't be shared (nothing to meet in) - fall
                // back to any joinable public instance of the same room.
                var joinable = HeartbeatDB.FindJoinableInstance((ulong)room.roomId, joinerId);
                if (joinable == null)
                    return Fail("Target's instance has no shareable session yet - try again in a few seconds.");
                dest = joinable;
            }
            if (dest.maxCapacity > 0)
            {
                int occupants = HeartbeatDB.CountInInstance(dest.roomInstanceId);
                if (occupants >= dest.maxCapacity)
                    Console.WriteLine($"[force_join] instance {dest.roomInstanceId} is at capacity ({occupants}/{dest.maxCapacity}) - admin override, joining anyway");
            }

            HeartbeatDB.UpdatePlayerHeartbeat(joinerId, dest, online: true);
            Console.WriteLine($"[force_join] admin moving {joinerId} -> {targetId}'s instance {dest.roomInstanceId} ({dest.name})");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    joinerId,
                    targetId,
                    roomName = dest.name,
                    roomInstanceId = dest.roomInstanceId,
                    roomId = dest.roomId,
                    note = "The joiner moves on their next heartbeat (up to ~20s) - that delay is the client's poll cadence."
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        /// <summary>
        /// Imports rooms from a JSON file placed in Data/Imports.
        ///
        /// The file is the shape Rec Room's own room API returns - { "Results": [ ...rooms... ] }
        /// - which is what RoomDB.ImportRooms already expects. Capture a room (with its
        /// subrooms) from the real service, save the response, drop it in Data/Imports, and
        /// import it here. This used to be possible only by uncommenting a line in Program.cs
        /// and rebuilding.
        ///
        /// Importing brings the room and its subrooms in, INCLUDING each save's DataBlob
        /// filename - but not the blob itself. The response therefore lists every .room file
        /// the imported rooms reference and whether it is present in Data/CDN/RoomBlobs, so
        /// you know exactly which ones still need downloading. A save whose blob is missing is
        /// hidden by FilterUnservableSaves until the file appears; drop it in and the room
        /// starts loading its build with no code change.
        ///
        /// Only reads from Data/Imports - a path from the request is never trusted.
        /// </summary>
        [HttpPost("/admin/api/import_rooms")]
        public async Task<ContentResult> AdminImportRooms()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string file = (Request.Form["file"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(file))
                return Json(new { success = false, message = "No file given." });

            // Filename only - no directory traversal.
            file = Path.GetFileName(file);
            string dir = Path.Combine(Environment.CurrentDirectory, "Data", "Imports");
            string path = Path.Combine(dir, file);

            if (!System.IO.File.Exists(path))
                return Json(new { success = false, message = $"Data/Imports/{file} not found." });

            ulong before = RoomDB.GetAllRoomsCount();

            try
            {
                await RoomDB.ImportRooms(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[import] {file} failed: {ex.Message}");
                return Json(new { success = false, message = $"Import failed: {ex.Message}" });
            }

            ulong after = RoomDB.GetAllRoomsCount();

            // Which .room blobs do the imported rooms now reference, and do we have them?
            string blobDir = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs");
            var missing = new List<string>();
            var present = new List<string>();

            foreach (var room in RoomDB.GetAllRooms())
            {
                foreach (var sub in room.SubRooms ?? new List<RoomDBClasses.SubRoom>())
                {
                    foreach (var save in sub.AllSaves ?? new List<RoomDBClasses.CurrentSave>())
                    {
                        string? blob = save.DataBlob;
                        if (string.IsNullOrWhiteSpace(blob)) continue;

                        var fi = new FileInfo(Path.Combine(blobDir, blob));
                        if (fi.Exists && fi.Length > 0) { if (!present.Contains(blob)) present.Add(blob); }
                        else if (!missing.Contains(blob)) missing.Add(blob);
                    }
                }
            }

            Console.WriteLine($"[import] {file}: rooms {before} -> {after}; " +
                              $"{present.Count} blob(s) present, {missing.Count} missing");

            return Json(new
            {
                success = true,
                message = $"Imported {after - before} room(s) from {file}. " +
                          $"{missing.Count} .room blob(s) still needed.",
                roomsBefore = before,
                roomsAfter = after,
                blobsPresent = present.Count,
                blobsMissing = missing
            });
        }

        // Drag-and-drop upload (admin "Rooms" tab). Accepts any mix of dropped files:
        //   • a .json room export  -> imported as rooms OWNED BY @Importer (ids reassigned)
        //   • a .room / .META blob  -> dropped into Data/CDN/RoomBlobs (makes saved builds load)
        // Does the whole thing in one request and reports what was imported + what blobs are still
        // missing. This is the one-shot version of drop-into-Imports + POST import_rooms.
        [HttpPost("/admin/api/import_upload")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
        public async Task<ContentResult> AdminImportUpload()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            if (!Request.HasFormContentType || Request.Form.Files.Count == 0)
                return Json(new { success = false, message = "No files uploaded." });

            ulong importerId = PlayerDB.GetAccountIdByUsernameCI("Importer");
            if (importerId == 0)
                return Json(new { success = false, message = "The @Importer account does not exist yet (restart the server to seed it)." });

            string importsDir = Path.Combine(Environment.CurrentDirectory, "Data", "Imports");
            string blobDir = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs");
            Directory.CreateDirectory(importsDir);
            Directory.CreateDirectory(blobDir);

            ulong before = RoomDB.GetAllRoomsCount();
            var importedFiles = new List<string>();
            var blobFiles = new List<string>();
            var errors = new List<string>();
            int avatarItemsImported = 0;
            int exportRooms = 0, exportBlobs = 0;
            var roomNames = new List<string>();

            string imagesDir = Path.Combine(Environment.CurrentDirectory, "Data", "Images");
            Directory.CreateDirectory(imagesDir);

            // Copy one extracted file to the right place by kind. Images -> Data/Images,
            // logs/readme/descriptor -> skipped, everything else (.room/.META/.binpb/subroom data)
            // -> RoomBlobs so whatever a save's DataBlob names is present.
            void RouteFile(string file)
            {
                string bn = Path.GetFileName(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png")
                { System.IO.File.Copy(file, Path.Combine(imagesDir, bn), true); return; }
                if (ext is ".log" || bn.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) return;
                if (ext == ".json") return;   // handled explicitly (RoomDetails.json)
                System.IO.File.Copy(file, Path.Combine(blobDir, bn), true);
                if (!blobFiles.Contains(bn)) blobFiles.Add(bn);
            }

            // Host a single loose .room blob and, if nothing already references it and it's old enough
            // for the client, wrap it in a joinable @Importer room. Shared by the single-file drop and
            // the "zip full of .room files" path. `onDiskPath` is where the file already sits on disk.
            void WrapLooseRoomFile(string onDiskPath)
            {
                string bn = Path.GetFileName(onDiskPath);
                if (!blobFiles.Contains(bn)) blobFiles.Add(bn);
                var (status, id, ver, roomName, err) = RoomDB.WrapLooseRoom(onDiskPath, importerId);
                switch (status)
                {
                    case RoomDB.LooseWrapStatus.Created:
                        exportRooms++; roomNames.Add(roomName);
                        importedFiles.Add($"{bn} → new room '{roomName}' (id {id}, save v{ver})"); break;
                    case RoomDB.LooseWrapStatus.TooNew:
                        errors.Add($"{bn}: save version {ver} is newer than this client can load (max {Campus_live_rewrite.api.server.ConfigCampus.ClientMaxRoomPersistenceVersion}) — blob hosted, no room created (it would freeze/kick to dorm)."); break;
                    case RoomDB.LooseWrapStatus.NameExists:
                        errors.Add($"{bn}: hosted the blob but a room named '{roomName}' already exists (rename the file to make another)."); break;
                    case RoomDB.LooseWrapStatus.Error:
                        errors.Add($"{bn}: {err}"); break;
                    // AlreadyUsed -> silently fine (blob already linked to a room)
                }
            }

            foreach (var f in Request.Form.Files)
            {
                string name = Path.GetFileName(f.FileName ?? "");
                if (string.IsNullOrWhiteSpace(name)) continue;
                string ext = Path.GetExtension(name).ToLowerInvariant();
                try
                {
                    if (ext == ".zip")
                    {
                        // Full room-export zip: extract, place blobs+image, import RoomDetails.json.
                        string zipPath = Path.Combine(importsDir, name);
                        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write)) await f.CopyToAsync(fs);
                        string exDir = Path.Combine(importsDir, Path.GetFileNameWithoutExtension(name) + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                        Directory.CreateDirectory(exDir);
                        ZipFile.ExtractToDirectory(zipPath, exDir, overwriteFiles: true);

                        // A UGC avatar-item pack (folders of avatarItem.json + .assetbundle + .png)?
                        // Route it to the avatar importer: host bundles -> AvatarBlobs, thumbs/textures
                        // -> Images, and add each item to the UGC catalog / dev locker.
                        if (Campus_live_rewrite.api.server.AvatarItemImporter.LooksLikeAvatarPack(exDir))
                        {
                            var ar = Campus_live_rewrite.api.server.AvatarItemImporter.ImportDir(exDir);
                            importedFiles.Add($"{name} → {ar.Items} avatar item(s), {ar.BundlesHosted} bundle(s), {ar.ThumbsHosted} image(s)");
                            foreach (var m in ar.MissingAssets.Distinct().Take(50)) errors.Add($"{name}: missing asset {m}");
                            foreach (var e in ar.Errors.Take(50)) errors.Add($"{name}: {e}");
                            avatarItemsImported += ar.Items;
                        }
                        else if (RoomDB.LooksLikeRoomExport(exDir))
                        {
                            // Rec Room's OFFICIAL room export (RoomDetails.json single object +
                            // super_room_data.binpb + SubRoom_*/persisted_room_data.binpb). Place the
                            // .binpb blobs under their DataBlob names (hash-matched) so the room loads
                            // its actual build, not a bare scene.
                            var rr = await RoomDB.ImportRoomExport(exDir, ownerOverride: importerId);
                            exportRooms += rr.Rooms;
                            exportBlobs += rr.Blobs;
                            roomNames.AddRange(rr.Names);
                            importedFiles.Add($"{name} → {rr.Rooms} room(s): {string.Join(", ", rr.Names.Take(10))}; {rr.Blobs} blob(s), {rr.Images} image(s)");
                            foreach (var e in rr.Errors.Take(50)) errors.Add($"{name}: {e}");
                        }
                        else if (Directory.EnumerateFiles(exDir, "*.room", SearchOption.AllDirectories).Any())
                        {
                            // A zip that's just a bunch of loose .room blobs (no RoomDetails.json). Wrap
                            // every loadable one into its own joinable room; too-new ones are hosted +
                            // reported. Any non-.room files (.META room-level blobs, images) are hosted.
                            var roomFiles = Directory.EnumerateFiles(exDir, "*.room", SearchOption.AllDirectories).ToList();
                            foreach (var file in Directory.EnumerateFiles(exDir, "*", SearchOption.AllDirectories))
                                if (!file.EndsWith(".room", StringComparison.OrdinalIgnoreCase)) RouteFile(file);
                            foreach (var rf in roomFiles) WrapLooseRoomFile(rf);
                            importedFiles.Add($"{name} → scanned {roomFiles.Count} loose .room file(s)");
                        }
                        else
                        {
                            // Legacy path: the server's own {Results:[...]} dump whose .room/.meta blobs
                            // are already present as loose, correctly-named files.
                            foreach (var file in Directory.EnumerateFiles(exDir, "*", SearchOption.AllDirectories))
                                RouteFile(file);

                            var manifest = Directory.EnumerateFiles(exDir, "RoomDetails.json", SearchOption.AllDirectories).FirstOrDefault()
                                        ?? Directory.EnumerateFiles(exDir, "*.json", SearchOption.AllDirectories).FirstOrDefault();
                            if (manifest != null)
                            {
                                await RoomDB.ImportRooms(manifest, ownerOverride: importerId);
                                importedFiles.Add(name + " → " + Path.GetFileName(manifest));
                            }
                            else errors.Add($"{name}: no RoomDetails.json, avatarItem.json, or .room files found in the zip");
                        }
                    }
                    else if (ext == ".json")
                    {
                        string dest = Path.Combine(importsDir, name);
                        using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write)) await f.CopyToAsync(fs);
                        await RoomDB.ImportRooms(dest, ownerOverride: importerId);
                        importedFiles.Add(name);
                    }
                    else
                    {
                        // Host the loose blob on the CDN.
                        string dest = Path.Combine(blobDir, name);
                        using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write)) await f.CopyToAsync(fs);
                        if (!blobFiles.Contains(name)) blobFiles.Add(name);

                        // A loose .room blob is a single subroom's build with no room record. Wrap it
                        // in a joinable @Importer room (if loadable). .META / other blobs are just hosted
                        // (they're room-level / referenced assets, not standalone rooms).
                        if (ext == ".room") WrapLooseRoomFile(dest);
                    }
                }
                catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
            }

            ulong after = RoomDB.GetAllRoomsCount();

            // Which .room blobs are still missing across ALL @Importer rooms?
            var missing = new List<string>();
            foreach (var room in RoomDB.GetAllRooms())
                foreach (var sub in room.SubRooms ?? new List<RoomDBClasses.SubRoom>())
                    foreach (var save in sub.AllSaves ?? new List<RoomDBClasses.CurrentSave>())
                    {
                        var blob = save.DataBlob;
                        if (string.IsNullOrWhiteSpace(blob)) continue;
                        var fi = new FileInfo(Path.Combine(blobDir, blob));
                        if ((!fi.Exists || fi.Length == 0) && !missing.Contains(blob)) missing.Add(blob);
                    }

            Console.WriteLine($"[import-upload] {account.Username}: {importedFiles.Count} file(s), export {exportRooms} room(s)/{exportBlobs} blob(s), {blobFiles.Count} loose blob(s), {avatarItemsImported} avatar item(s); rooms {before}->{after}; {missing.Count} blob(s) still missing");

            // Build a message from what ACTUALLY happened. The old line only counted legacy loose
            // blobs, so a fully-successful room-export import (which places blobs via the CDN path,
            // not blobFiles) reported "Saved 0 blob(s)" and looked broken.
            var parts = new List<string>();
            if (exportRooms > 0)
                parts.Add($"Imported {exportRooms} room(s){(roomNames.Count > 0 ? " (" + string.Join(", ", roomNames.Take(5)) + ")" : "")} under @Importer with {exportBlobs} build blob(s) placed on the CDN");
            else if (exportBlobs > 0)
                parts.Add($"Re-placed {exportBlobs} build blob(s) — that room was already imported (rooms are keyed by name, so re-dropping the same export doesn't duplicate it)");
            if (avatarItemsImported > 0)
                parts.Add($"Imported {avatarItemsImported} avatar item(s) into the dev locker ({CustomItemsCache.ImportedCount} total)");
            if (blobFiles.Count > 0)
                parts.Add($"Saved {blobFiles.Count} loose blob(s)");
            if (parts.Count == 0)
                parts.Add($"Processed {importedFiles.Count} file(s) but imported nothing — is this a room export (RoomDetails.json + *room_data*.binpb), a UGC pack (avatarItem.json), or a legacy JSON dump?");
            string message = string.Join(". ", parts) + ".";
            if (missing.Count > 0)
                message += $" Note: {missing.Count} .room blob(s) referenced by imported rooms aren't on the CDN yet (from other/older imports).";

            return Json(new
            {
                success = true,
                message,
                roomsImported = exportRooms,
                roomBlobsPlaced = exportBlobs,
                roomNames,
                avatarItemsImported,
                avatarItemsTotal = CustomItemsCache.ImportedCount,
                jsonFiles = importedFiles,
                blobFiles,
                blobsMissing = missing,
                errors
            });
        }

        /// <summary>
        /// Bulk-import UGC avatar packs already staged ON the server, WITHOUT a browser upload.
        /// This is the path for a large library (e.g. the 178GB archive): rsync/scp the extracted
        /// packs into Data/AvatarImportStaging/ on the box, then POST here. Scans that fixed folder
        /// (no caller-supplied path - no traversal surface), hosts bundles+images, and registers
        /// every avatarItem.json into the UGC catalog / dev locker.
        /// </summary>
        [HttpPost("/admin/api/import_avatar_staging")]
        public IActionResult AdminImportAvatarStaging()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string staging = Path.Combine(Environment.CurrentDirectory, "Data", "AvatarImportStaging");
            Directory.CreateDirectory(staging);

            if (!Campus_live_rewrite.api.server.AvatarItemImporter.LooksLikeAvatarPack(staging))
                return Json(new { success = false, message = $"No avatarItem.json found under Data/AvatarImportStaging. rsync your extracted UGC packs there first ({staging})." });

            // Assets are MOVED out of staging into their served folders (see ImportDir), so the
            // pack doesn't need to exist twice on the box; the staging tree can be deleted after.
            var r = Campus_live_rewrite.api.server.AvatarItemImporter.ImportDir(staging, moveAssets: true);
            Console.WriteLine($"[import-avatar-staging] {account.Username}: {r.Items} items, {r.BundlesHosted} bundles, {r.ThumbsHosted} images, {r.MissingAssets.Count} missing");
            // Bundles from a NEWER Unity than the clients (PC 2021.3 / Quest 2020.3) can never load,
            // however correct the metadata is - say so up front rather than let the wardrobe find out.
            int unloadable = r.BundleUnityVersions.Where(kv => Campus_live_rewrite.api.server.AvatarItemImporter.IsUnloadableUnityVersion(kv.Key)).Sum(kv => kv.Value);
            string versions = string.Join(", ", r.BundleUnityVersions.Select(kv => $"{kv.Key} x{kv.Value}"));
            string warn = unloadable > 0
                ? $" WARNING: {unloadable} bundle(s) were built with a newer Unity than the game clients can load (clients: PC 2021.3.41f1, Quest 2020.3.33f1) - those items will show in the locker but fail to render. Bundle versions: {versions}."
                : (r.BundlesHosted > 0 ? $" Bundle versions: {versions}." : "");
            if (unloadable > 0) Console.WriteLine($"[import-avatar-staging] {unloadable} bundle(s) unloadable by the clients: {versions}");
            return Json(new
            {
                success = true,
                message = $"Imported {r.Items} avatar item(s) into the dev locker ({CustomItemsCache.ImportedCount} total). Hosted {r.BundlesHosted} bundle(s), {r.ThumbsHosted} image(s).{warn}",
                bundleUnityVersions = r.BundleUnityVersions,
                unloadableBundles = unloadable,
                avatarItemsImported = r.Items,
                avatarItemsTotal = CustomItemsCache.ImportedCount,
                bundlesHosted = r.BundlesHosted,
                imagesHosted = r.ThumbsHosted,
                missingAssets = r.MissingAssets.Distinct().Take(100),
                errors = r.Errors.Take(100)
            });
        }

        /// <summary>
        /// Every .room / .META blob in Data/CDN/RoomBlobs, and which subroom (if any) points
        /// at it.
        ///
        /// Rec Room is gone, so no blob can ever be downloaded again - the files already on
        /// disk are the only room content that will ever exist. Several of them are ORPHANS:
        /// real saved builds that no subroom references, because the save record was lost
        /// while the file survived. A room whose save is missing loads its bare Unity scene,
        /// so an orphan is a room's contents sitting there doing nothing.
        ///
        /// Zero-byte files are listed separately: they are failed downloads from the old CDN
        /// caching bug, not content, and are treated as missing everywhere.
        /// </summary>
        [HttpGet("/admin/api/room_blobs")]
        public ContentResult AdminListRoomBlobs()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string dir = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs");
            if (!Directory.Exists(dir))
                return Json(new { success = false, message = "Data/CDN/RoomBlobs does not exist." });

            // blob filename -> "room 84 / subroom 748"
            var referencedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var room in RoomDB.GetAllRooms())
            {
                foreach (var sub in room.SubRooms ?? new List<RoomDBClasses.SubRoom>())
                {
                    foreach (var save in sub.AllSaves ?? new List<RoomDBClasses.CurrentSave>())
                        if (!string.IsNullOrWhiteSpace(save.DataBlob))
                            referencedBy[save.DataBlob!] = $"room {room.RoomId} ({room.Name}) / subroom {sub.SubRoomId}";

                    if (sub.CurrentSave != null && !string.IsNullOrWhiteSpace(sub.CurrentSave.DataBlob))
                        referencedBy[sub.CurrentSave.DataBlob!] = $"room {room.RoomId} ({room.Name}) / subroom {sub.SubRoomId}";
                }
                if (!string.IsNullOrWhiteSpace(room.DataBlob))
                    referencedBy[room.DataBlob!] = $"room {room.RoomId} ({room.Name}) [room-level]";
            }

            var blobs = new DirectoryInfo(dir).GetFiles()
                .OrderByDescending(f => f.Length)
                .Select(f => new
                {
                    name = f.Name,
                    size = f.Length,
                    empty = f.Length == 0,
                    modified = f.LastWriteTimeUtc,
                    usedBy = referencedBy.TryGetValue(f.Name, out var who) ? who : null
                })
                .ToList();

            return Json(new
            {
                success = true,
                total = blobs.Count,
                empty = blobs.Count(b => b.empty),
                orphans = blobs.Count(b => !b.empty && b.usedBy == null),
                blobs
            });
        }

        /// <summary>
        /// Points a room's subroom at an existing blob, i.e. records it as that subroom's
        /// save. This is how an orphaned build gets reconnected to a room.
        ///
        /// The blob must already exist and be non-empty. Nothing is downloaded and no file is
        /// written - only the save record changes, so getting it wrong costs nothing: attach a
        /// different blob, or attach the right one to a different room.
        /// </summary>
        [HttpPost("/admin/api/attach_blob")]
        public ContentResult AdminAttachBlob()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string blob = Path.GetFileName((Request.Form["blob"].ToString() ?? "").Trim());
            if (string.IsNullOrWhiteSpace(blob))
                return Json(new { success = false, message = "No blob given." });

            if (!ulong.TryParse(Request.Form["roomId"].ToString(), out ulong roomId))
                return Json(new { success = false, message = "Invalid room id." });

            var room = RoomDB.GetRoom(roomId);
            if (room == null)
                return Json(new { success = false, message = $"No room {roomId}." });

            // Default to the room's only/first subroom - these rooms have exactly one.
            ulong subRoomId;
            string rawSub = Request.Form["subRoomId"].ToString() ?? "";
            if (!ulong.TryParse(rawSub, out subRoomId) || subRoomId == 0)
            {
                var first = (room.SubRooms ?? new List<RoomDBClasses.SubRoom>()).FirstOrDefault();
                if (first == null)
                    return Json(new { success = false, message = $"Room {roomId} has no subrooms." });
                subRoomId = first.SubRoomId;
            }

            var fi = new FileInfo(Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs", blob));
            if (!fi.Exists || fi.Length == 0)
                return Json(new { success = false, message = $"{blob} is missing or empty in Data/CDN/RoomBlobs." });

            RoomDB.SetRoomSubroomRoomFile(
                roomId, subRoomId, blob, room.Description ?? "", account.Id,
                Platforms.All, persistenceVersion: 227, omVersion: 0, ugcSubVersion: 227);

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) attached {blob} " +
                              $"({fi.Length} bytes) to room {roomId} subroom {subRoomId}");

            return Json(new
            {
                success = true,
                message = $"Attached {blob} to room {roomId} ('{room.Name}') subroom {subRoomId}. " +
                          $"Load the room to see which build it is."
            });
        }

        /// <summary>
        /// Every subroom and the scene the client loads for it, grouped so placeholders stand
        /// out. A scene shared by many unrelated rooms is a placeholder that came in with the
        /// import, not that room's real map.
        /// </summary>
        [HttpGet("/admin/api/room_scenes")]
        public ContentResult AdminListRoomScenes()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var rows = new List<object>();
            var usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var room in RoomDB.GetAllRooms())
                foreach (var sub in room.SubRooms ?? new List<RoomDBClasses.SubRoom>())
                {
                    string scene = sub.UnitySceneId ?? "";
                    if (scene.Length > 0) usage[scene] = usage.TryGetValue(scene, out var n) ? n + 1 : 1;
                }

            foreach (var room in RoomDB.GetAllRooms())
                foreach (var sub in room.SubRooms ?? new List<RoomDBClasses.SubRoom>())
                {
                    string scene = sub.UnitySceneId ?? "";
                    rows.Add(new
                    {
                        roomId = room.RoomId,
                        roomName = room.Name,
                        isRRO = room.IsRRO,
                        subRoomId = sub.SubRoomId,
                        subRoomName = sub.Name,
                        scene,
                        sharedWith = scene.Length > 0 && usage.TryGetValue(scene, out var n) ? n : 0
                    });
                }

            return Json(new { success = true, total = rows.Count, rooms = rows });
        }

        /// <summary>
        /// Points a subroom at a different Unity scene. See RoomDB.SetSubRoomScene for what
        /// that means and why an imported room can load the wrong map.
        /// </summary>
        // Social sign-in apps (Settings > Services on the website). GET = ids + whether a secret
        // is stored + the redirect URI to register; POST = save one provider's credentials into
        // Data/social_apps.json (a blank secret keeps the stored one). Dev-only.
        [HttpGet("/admin/api/social_apps")]
        public ContentResult AdminSocialApps()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            return Json(new { success = true, providers = Campus_live_rewrite.api.server.SocialLinks.AdminView() });
        }

        [HttpPost("/admin/api/social_apps")]
        public ContentResult AdminSaveSocialApp()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string provider = (Request.Form["provider"].ToString() ?? "").Trim().ToLowerInvariant();
            string? clientId = Request.Form.ContainsKey("clientId") ? Request.Form["clientId"].ToString() : null;
            string? clientSecret = Request.Form.ContainsKey("clientSecret") ? Request.Form["clientSecret"].ToString() : null;
            string? apiKey = Request.Form.ContainsKey("apiKey") ? Request.Form["apiKey"].ToString() : null;
            bool ok = Campus_live_rewrite.api.server.SocialLinks.AdminSave(provider, clientId, clientSecret, apiKey, out var message);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) social app '{provider}': {(ok ? "saved" : "FAILED " + message)}");
            return Json(new { success = ok, message });
        }

        [HttpPost("/admin/api/set_room_scene")]
        public ContentResult AdminSetRoomScene()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            if (!ulong.TryParse(Request.Form["roomId"].ToString(), out ulong roomId))
                return Json(new { success = false, message = "Invalid room id." });

            string scene = (Request.Form["scene"].ToString() ?? "").Trim();
            if (!Guid.TryParse(scene, out _))
                return Json(new { success = false, message = "Scene must be a GUID." });

            var room = RoomDB.GetRoom(roomId);
            if (room == null)
                return Json(new { success = false, message = $"No room {roomId}." });

            ulong subRoomId;
            if (!ulong.TryParse(Request.Form["subRoomId"].ToString(), out subRoomId) || subRoomId == 0)
            {
                var first = (room.SubRooms ?? new List<RoomDBClasses.SubRoom>()).FirstOrDefault();
                if (first == null) return Json(new { success = false, message = "Room has no subrooms." });
                subRoomId = first.SubRoomId;
            }

            bool ok = RoomDB.SetSubRoomScene(roomId, subRoomId, scene);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) set room {roomId} subroom {subRoomId} scene -> {scene}");

            return Json(new
            {
                success = ok,
                message = ok
                    ? $"Room {roomId} ('{room.Name}') subroom {subRoomId} now loads scene {scene}. Rejoin to see it."
                    : "Subroom not found."
            });
        }

        /// <summary>
        /// Every photo in the game, newest first, each attributed to the player who took it.
        /// Backs the admin gallery. The image url is the same /img path the client fetches, so
        /// the panel can show a thumbnail; PlayerId names the photographer.
        /// </summary>
        // Shows the OUTFIT state stored in the database for one player, plus the running build
        // stamp - so we can tell instantly whether a save is landing. Open /admin/api/outfitdebug/6
        // after saving an outfit in-game: if savedOutfits is populated the save IS in the DB (and
        // any "empty wardrobe" is a client display issue); if it's empty the save never reached the
        // server, and /admin/api/apilog will show which endpoint the client hit and its status.
        [HttpGet("/admin/api/outfitdebug/{playerid}")]
        public IActionResult AdminOutfitDebug(ulong playerid)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string build = "";
            try
            {
                var bp = Path.Combine(Environment.CurrentDirectory, "Data", "BUILD.txt");
                if (System.IO.File.Exists(bp)) build = System.IO.File.ReadAllText(bp).Trim();
            }
            catch { }

            var p = PlayerDB.GetFullAccount(playerid);
            var savedRaw = PlayerDB.GetAvatarSaved(playerid);   // JSON array string of SavedOutfits
            int savedCount = p?.player_Extra?.SavedOutfits?.Count ?? 0;

            return Content(JsonConvert.SerializeObject(new
            {
                build,
                playerid,
                playerRowExists = p != null,
                playerExtraExists = p?.player_Extra != null,
                hasWornAvatar = !string.IsNullOrEmpty(p?.player_Extra?.avatar),
                savedOutfitCount = savedCount,
                savedOutfits = savedRaw
            }, Formatting.Indented), "application/json");
        }

        // The tail of Data/APILog.txt (every request the middleware logs as
        // "<time> <METHOD> <PATH> -> <status>"). Dev-gated. Open in a browser after reproducing a
        // bug to see exactly what the client called and which routes 404'd/500'd - e.g.
        //   /admin/api/apilog?lines=400
        //   /admin/api/apilog?lines=400&find=/api/    (only lines containing "/api/")
        // Returns plain text so it can be read straight in the browser and copied.
        [HttpGet("/admin/api/apilog")]
        public IActionResult AdminApiLog([FromQuery] int lines = 300, [FromQuery] string? find = null)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            if (lines < 1) lines = 1;
            if (lines > 5000) lines = 5000;

            // Primary source: the in-memory ring buffer the Log middleware fills on every
            // request (see Log.cs). It is instant and never blocked by disk state, and it is
            // populated regardless of the VerboseRequestLogging flag - which is why the tab
            // works now where it used to sit empty.
            if (Log.HasBufferedLines)
            {
                var buf = Log.Recent(lines, find);
                return Content(string.Join("\n", buf), "text/plain");
            }

            // Fallback: the persisted file (history from before this process started, or if the
            // buffer somehow hasn't filled yet).
            var path = Path.Combine(Environment.CurrentDirectory, "Data", "APILog.txt");
            if (!System.IO.File.Exists(path))
                return Content("(no requests logged yet - hit the server and refresh)", "text/plain");

            string[] all;
            try
            {
                // Open shared so we can read while the server is still appending to it.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                all = sr.ReadToEnd().Split('\n');
            }
            catch (Exception ex)
            {
                return Content($"(failed to read log: {ex.Message})", "text/plain");
            }

            IEnumerable<string> rows = all;
            if (!string.IsNullOrEmpty(find))
                rows = rows.Where(l => l.Contains(find, StringComparison.OrdinalIgnoreCase));

            var tail = rows.Where(l => l.Length > 0).TakeLast(lines);
            return Content(string.Join("\n", tail), "text/plain");
        }

        [HttpGet("/admin/api/photos")]
        public ContentResult AdminListPhotos()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var all = ImageMetadataDB.GetAllImages();

            // One bulk name lookup for every photographer + tagged player, so the gallery can
            // show @names instead of raw ids without a query per row.
            var ids = all.Select(i => i.PlayerId)
                         .Concat(all.SelectMany(i => i.TaggedPlayerIds ?? new List<ulong>()))
                         .Where(x => x != 0).Distinct().ToList();
            var names = new Dictionary<ulong, string>();
            foreach (var pid in ids)
            {
                var acc = PlayerDB.GetFullAccount(pid);
                names[pid] = acc?.player?.Username ?? pid.ToString();
            }
            string NameOf(ulong pid) => pid == 0 ? "" : (names.TryGetValue(pid, out var n) ? n : pid.ToString());

            var photos = all.Select(i => new
            {
                id = i.Id,
                imageName = i.ImageName,
                imageUrl = string.IsNullOrWhiteSpace(i.ImageName) ? null : $"/img/{i.ImageName}?sig=p1&thumb=1&width=320",   // small JPEG preview
                fullUrl = string.IsNullOrWhiteSpace(i.ImageName) ? null : $"/img/{i.ImageName}?sig=p1",
                takenBy = i.PlayerId,
                takenByName = NameOf(i.PlayerId),
                roomId = i.RoomId,
                createdAt = i.CreatedAt,
                description = i.Description ?? "",
                cheers = i.CheerCount,
                accessibility = i.Accessibility.ToString(),
                taggedPlayers = (i.TaggedPlayerIds ?? new List<ulong>()).Select(NameOf).ToList()
            });

            return Json(new { success = true, count = all.Count, photos });
        }

        /// <summary>
        /// Deletes one photo - its metadata, its cheers, and the file on disk. There was no way
        /// to remove a photo before, so an abusive one could only be handled in the database.
        /// </summary>
        [HttpPost("/admin/api/delete_photo")]
        public ContentResult AdminDeletePhoto()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            if (!int.TryParse(Request.Form["id"].ToString(), out int id))
                return Json(new { success = false, message = "Invalid photo id." });

            var deleted = ImageMetadataDB.DeleteImage(id);
            if (deleted == null)
                return Json(new { success = false, message = $"No photo with id {id}." });

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) deleted photo {id} " +
                              $"'{deleted.ImageName}' taken by {deleted.PlayerId}");

            return Json(new { success = true, message = $"Deleted photo {id}." });
        }

        /// <summary>
        /// Every room comment across all rooms, newest first - for moderation. A comment is a note
        /// a player pins in a room's scene (RoomCommentsController); there was no way to see or
        /// remove an abusive one except by editing the database by hand. Resolves the author id to
        /// an @name in one bulk pass, same as the photo gallery.
        /// </summary>
        [HttpGet("/admin/api/room_comments")]
        public ContentResult AdminListRoomComments()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var all = RoomCommentsDB.GetAll();

            var ids = all.Select(c => c.AccountId).Where(x => x != 0).Distinct().ToList();
            var names = new Dictionary<ulong, string>();
            foreach (var pid in ids)
            {
                var acc = PlayerDB.GetFullAccount(pid);
                names[pid] = acc?.player?.Username ?? pid.ToString();
            }
            string NameOf(ulong pid) => pid == 0 ? "" : (names.TryGetValue(pid, out var n) ? n : pid.ToString());

            var comments = all.Select(c => new
            {
                id = c.CommentId,
                roomId = c.RoomId,
                subRoomId = c.SubRoomId,
                authorId = c.AccountId,
                authorName = NameOf(c.AccountId),
                message = c.Message,
                createdAt = c.CreatedAt
            });

            return Json(new { success = true, count = all.Count, comments });
        }

        /// <summary>Deletes one pinned room comment. No prior way to remove one from the panel.</summary>
        [HttpPost("/admin/api/delete_room_comment")]
        public ContentResult AdminDeleteRoomComment()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            if (!int.TryParse(Request.Form["id"].ToString(), out int id))
                return Json(new { success = false, message = "Invalid comment id." });

            bool ok = RoomCommentsDB.Delete(id);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) deleted room comment {id}");

            return Json(new
            {
                success = ok,
                message = ok ? $"Deleted comment {id}." : $"No comment with id {id}."
            });
        }

        /// <summary>Every player-created event, newest first - for moderation.</summary>
        [HttpGet("/admin/api/events")]
        public ContentResult AdminListEvents()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var events = Campus_live_rewrite.api.dbs.EventDB.SearchEventByQuery("")
                .OrderByDescending(e => e.StartTime)
                .Select(e => new
                {
                    e.PlayerEventId,
                    e.CreatorPlayerId,
                    e.Name,
                    e.Description,
                    e.RoomId,
                    e.StartTime,
                    e.EndTime,
                    e.AttendeeCount
                });

            return Json(new { success = true, events });
        }

        /// <summary>
        /// Deletes a player event. There was no way to remove one at all - not in-game and not
        /// from the panel - so an event with an abusive name could only be dealt with by editing
        /// the database by hand.
        /// </summary>
        [HttpPost("/admin/api/delete_event")]
        public ContentResult AdminDeleteEvent()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string raw = Request.Form["eventId"].ToString();
            if (!ulong.TryParse(raw, out ulong eventId))
                return Json(new { success = false, message = "Invalid event id." });

            var ev = Campus_live_rewrite.api.dbs.EventDB.GetFullEventById(eventId);
            if (ev == null)
                return Json(new { success = false, message = $"No event with id {eventId}." });

            bool ok = Campus_live_rewrite.api.dbs.EventDB.DeleteEvent(eventId);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) deleted event {eventId} " +
                              $"'{ev.Name}' by player {ev.CreatorPlayerId}");

            return Json(new
            {
                success = ok,
                message = ok ? $"Deleted event {eventId} ('{ev.Name}')." : "Delete failed."
            });
        }

        [HttpGet("/admin/api/rooms")]
        public IActionResult AdminGetRoomsAPI(int skip = 0, int take = 200, string? q = null)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null)
                return error;

            // Was: return every room, full objects WITH subrooms. At thousands of rooms (after a bulk
            // import) that's a multi-MB payload the panel can't render -> blank table. Now: newest-first,
            // optional search, paged, and projected to just the columns the table shows (no subrooms).
            if (take <= 0 || take > 1000) take = 200;
            if (skip < 0) skip = 0;

            IEnumerable<RoomDBClasses.RoomRoot> rooms = RoomDB.GetAllRoomsLite();
            if (!string.IsNullOrWhiteSpace(q))
            {
                string ql = q.Trim().ToLowerInvariant();
                rooms = rooms.Where(r => (r.Name ?? "").ToLowerInvariant().Contains(ql)
                                      || (r.Description ?? "").ToLowerInvariant().Contains(ql)
                                      || r.RoomId.ToString().Contains(ql)
                                      || r.CreatorAccountId.ToString() == ql);
            }
            var ordered = rooms.OrderBy(r => r.RoomId).ToList();   // lowest -> highest room id
            var page = ordered.Skip(skip).Take(take).Select(r => new
            {
                r.RoomId,
                ImageName = r.ImageName ?? "",
                Name = r.Name ?? "",
                Description = r.Description ?? "",
                r.CreatorAccountId,
                r.MaxPlayers
            }).ToList();

            return Ok(new { total = ordered.Count, skip, take, rooms = page });
        }

        // -----------------------------------------------------------------------------------------
        // Role management: enable/disable dev / mod / screenshare bit flags on a target account.
        // Bit values match Campus_live_rewrite.api.server.Classes.db_class.DevFlag:
        //   screenshare = 1, dev = 2, mod = 4.
        // Caller must already be dev.
        // -----------------------------------------------------------------------------------------
        [HttpPost("/admin/api/set_role")]
        public ContentResult AdminSetRole()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId    = HttpContext.Request.Form["playerId"].ToString();
            string roleName = (HttpContext.Request.Form["role"].ToString() ?? "").ToLowerInvariant();
            string enabledS = HttpContext.Request.Form["enabled"].ToString();

            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            bool enable = enabledS == "1" || string.Equals(enabledS, "true", StringComparison.OrdinalIgnoreCase);

            // Influencer is NOT a DevFlag - it's the partner-program flag on
            // player_Extra.Influencer.isInfluencer (drives the influencer APIs / creator support).
            // Handle it before the DevFlag switch.
            if (roleName == "influencer")
            {
                PlayerDB.SetIsInfluencer(playerId, enable);
                PushEvent(playerId, Campus_live_rewrite.api.WebsocketEvents.CreateResponseReputationUpdate(playerId), "role/influencer", toAll: true);
                Console.WriteLine($"[admin] {account.Username} ({account.Id}) set influencer={enable} on player {playerId}");
                return Json(new { success = true, role = roleName, enabled = enable });
            }

            DevFlag flag;
            switch (roleName)
            {
                case "dev":         flag = DevFlag.dev; break;
                case "mod":         flag = DevFlag.mod; break;
                case "screenshare": flag = DevFlag.screenshare; break;
                case "support":     flag = DevFlag.support; break;
                case "studio":      flag = DevFlag.studio; break;   // CampusStudio publishing
                default: return Json(new { success = false, message = "Unknown role. Use dev / mod / screenshare / support / studio / influencer." });
            }

            // Don't let an admin strip their OWN dev flag — that would lock them out.
            if (playerId == account.Id && flag == DevFlag.dev && !enable)
                return Json(new { success = false, message = "Refusing to remove dev from your own account." });

            bool ok = PlayerDB.SetRoleFlag(playerId, flag, enable);

            // Live push: the dev flag is the RecRoomDeveloper badge carried in the reputation
            // payload, so broadcast a reputation update (proven-safe SendToAll event) - everyone
            // sees the badge appear/disappear instantly.
            if (ok)
                PushEvent(playerId, Campus_live_rewrite.api.WebsocketEvents.CreateResponseReputationUpdate(playerId), "role/badge", toAll: true);

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) set role {roleName}={enable} on player {playerId} — ok={ok}");
            return Json(new { success = ok, role = roleName, enabled = enable });
        }

        // ---- Database backups + server control (admin "Database"/"Server" tabs) ----------

        // Exit the process; systemd (Restart=always, RestartSec=3) brings it back in a few seconds.
        private void ScheduleRestart(int delayMs = 600)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                Console.WriteLine("[admin] restart requested — exiting for systemd to relaunch");
                Environment.Exit(0);
            });
        }

        [HttpGet("/admin/api/backups")]
        public ContentResult AdminListBackups()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            return Json(new { success = true, backups = BackupService.List() });
        }

        [HttpPost("/admin/api/backup_now")]
        public ContentResult AdminBackupNow()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string name = BackupService.CreateBackup();
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) ran a manual backup -> {name}");
            return Json(new { success = true, name, backups = BackupService.List() });
        }

        // Stage the chosen backup and restart; the restore is applied on startup before any DB opens.
        [HttpPost("/admin/api/restore_backup")]
        public ContentResult AdminRestoreBackup()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string name = HttpContext.Request.Form["name"].ToString();
            if (!BackupService.StageRestore(name))
                return Json(new { success = false, message = "No such backup." });

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) requested RESTORE of '{name}' — restarting");
            ScheduleRestart(800);
            return Json(new { success = true, message = $"Restoring '{name}'. The server is restarting; it will be back in a few seconds." });
        }

        [HttpPost("/admin/api/restart")]
        public ContentResult AdminRestartServer()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) requested a server RESTART");
            ScheduleRestart(600);
            return Json(new { success = true, message = "Server restarting… it will be back in a few seconds." });
        }

        // ---- Featured rooms ---------------------------------------------------
        // The ONE curated featured-rooms list, shared by the game (Room_server/featuredrooms/current)
        // and the website (/api/site/rooms/featured). Room ids are returned as STRINGS - a room id
        // is a 19-digit ulong that overflows JavaScript's 53-bit integer precision.

        [HttpGet("/admin/api/featured_rooms")]
        public ContentResult AdminGetFeaturedRooms()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var rooms = Campus_live_rewrite.api.server.FeaturedRoomsStore.GetIds()
                .Select(id =>
                {
                    var r = RoomDB.GetRoom(id);
                    return new { id = id.ToString(), name = r?.Name ?? "(missing room)", exists = r != null };
                })
                .ToList();

            return Json(new { success = true, rooms });
        }

        // The Featured tab: featured PLAYERS (first = the Featured Creator on the community board
        // and the Play tab) and the board announcement. Rooms are the routes below.
        [HttpGet("/admin/api/featured")]
        public ContentResult AdminGetFeatured()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var fs = Campus_live_rewrite.api.server.FeaturedStore.Get();
            var players = fs.PlayerIds.Select(id =>
            {
                var a = PlayerDB.GetAccountById(id);
                return new { id = id.ToString(), username = a?.Username ?? "(missing player)", displayName = a?.DisplayName ?? "", exists = a != null };
            }).ToList();
            ulong effective = Campus_live_rewrite.api.server.DiscoveryLists.FeaturedCreator();
            var eff = effective != 0 ? PlayerDB.GetAccountById(effective) : null;
            return Json(new
            {
                success = true,
                players,
                announcement = fs.Announcement ?? "",
                announcementUrl = fs.AnnouncementUrl ?? "",
                effectiveCreator = new { id = effective.ToString(), username = eff?.Username ?? "" }
            });
        }

        [HttpPost("/admin/api/set_featured_players")]
        public ContentResult AdminSetFeaturedPlayers()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string raw = Request.Form["playerIds"].ToString() ?? "";
            var ids = new List<ulong>();
            foreach (var part in raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim().TrimStart('@');
                if (ulong.TryParse(p, out var pid) && pid != 0) { if (!ids.Contains(pid)) ids.Add(pid); continue; }
                // A username works too.
                ulong byName = PlayerDB.GetAccountIdByUsernameCI(p);
                if (byName != 0 && !ids.Contains(byName)) ids.Add(byName);
            }
            var fs = Campus_live_rewrite.api.server.FeaturedStore.Get();
            fs.PlayerIds = ids;
            if (Request.Form.ContainsKey("announcement")) fs.Announcement = Request.Form["announcement"].ToString().Trim();
            if (Request.Form.ContainsKey("announcementUrl")) fs.AnnouncementUrl = Request.Form["announcementUrl"].ToString().Trim();
            Campus_live_rewrite.api.server.FeaturedStore.Set(fs);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) set featured players: {string.Join(", ", ids)}");

            var missing = ids.Where(id => PlayerDB.GetAccountById(id) == null).Select(id => id.ToString()).ToList();
            string msg = $"Featured players updated ({ids.Count}).";
            if (missing.Count > 0) msg += $" Warning - no player found for: {string.Join(", ", missing)}.";
            return Json(new { success = true, message = msg, count = ids.Count });
        }

        // ---- Bug reports (the in-game "Report a bug") ----
        [HttpGet("/admin/api/bugreports")]
        public ContentResult AdminBugReports()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            bool all = Request.Query["all"].ToString() == "1";
            var list = BugReportDB.Recent(all).Select(r => new
            {
                id = r.Id, accountId = r.AccountId.ToString(), username = r.Username, title = r.Title, description = r.Description,
                category = r.Category, room = r.Room, platform = r.Platform, raw = r.Raw, files = r.Files,
                at = r.At.ToString("o"), resolved = r.Resolved, resolvedBy = r.ResolvedBy, note = r.Note
            }).ToList();
            return Json(new { success = true, reports = list, openCount = BugReportDB.OpenCount() });
        }

        [HttpPost("/admin/api/bugreports/resolve")]
        public ContentResult AdminResolveBug()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            int id = int.TryParse(Request.Form["id"], out var i) ? i : 0;
            bool reopen = Request.Form["reopen"].ToString() == "1";
            bool ok = BugReportDB.Resolve(id, account.Username ?? account.Id.ToString(), Request.Form["note"].ToString(), !reopen);
            return Json(new { success = ok, message = ok ? (reopen ? "Reopened." : "Resolved.") : "No such report." });
        }

        [HttpGet("/admin/api/bugreports/file/{name}")]
        public IActionResult AdminBugFile(string name)
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            string safe = Path.GetFileName(name);
            string path = Path.Combine(Environment.CurrentDirectory, "Data", "BugReports", safe);
            if (!System.IO.File.Exists(path)) return NotFound();
            string ext = Path.GetExtension(safe).ToLowerInvariant();
            string ct = ext is ".png" ? "image/png" : ext is ".jpg" or ".jpeg" ? "image/jpeg" : ext is ".txt" or ".log" ? "text/plain" : "application/octet-stream";
            return PhysicalFile(path, ct);
        }

        // ---- Creator earnings ledger ----
        [HttpGet("/admin/api/earnings")]
        public ContentResult AdminEarnings()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var rows = EarningsDB.Recent(300).Select(e => new
            {
                id = e.Id, seller = e.SellerId.ToString(), sellerName = PlayerDB.GetAccountById(e.SellerId)?.Username ?? "",
                buyer = e.BuyerId.ToString(), buyerName = PlayerDB.GetAccountById(e.BuyerId)?.Username ?? "",
                kind = e.Kind, item = e.ItemName, spent = e.Spent, share = e.Share, currency = e.CurrencyType,
                boughtAt = e.BoughtAt.ToString("o"), payoutAt = e.PayoutAt.ToString("o"), paidAt = e.PaidAt?.ToString("o")
            }).ToList();
            return Json(new { success = true, earnings = rows, pending = rows.Where(r => r.paidAt == null).Sum(r => r.share) });
        }

        [HttpPost("/admin/api/earnings/paynow")]
        public ContentResult AdminPayEarningsNow()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            int n = EarningsDB.PayDue();
            return Json(new { success = true, message = $"Paid {n} due earning(s)." });
        }

        [HttpPost("/admin/api/set_featured_rooms")]
        public ContentResult AdminSetFeaturedRooms()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string raw = Request.Form["roomIds"].ToString() ?? "";
            var ids = new List<ulong>();
            foreach (var part in raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (ulong.TryParse(part.Trim(), out var rid) && rid != 0 && !ids.Contains(rid)) ids.Add(rid);

            Campus_live_rewrite.api.server.FeaturedRoomsStore.SetIds(ids);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) set featured rooms: {string.Join(", ", ids)}");

            // Report which ids don't resolve to a room, so the admin catches a typo.
            var missing = ids.Where(id => RoomDB.GetRoom(id) == null).Select(id => id.ToString()).ToList();
            string msg = $"Featured rooms updated ({ids.Count}).";
            if (missing.Count > 0) msg += $" Warning - no room found for: {string.Join(", ", missing)}.";
            return Json(new { success = true, message = msg, count = ids.Count });
        }

        // ---- Economy / progression admin -------------------------------------
        // All follow the existing pattern: dev-token gated, form-urlencoded in,
        // {success, message} JSON out, every action logged to the console.

        /// <summary>Read a player's XP, Level and all currency balances.</summary>
        [HttpGet("/admin/api/get_economy")]
        public ContentResult AdminGetEconomy()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId = HttpContext.Request.Query["playerId"].ToString();
            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            var balances = PlayerDB.GetAllBalances(playerId)
                .Select(kv => new
                {
                    CurrencyType = kv.Key,
                    Name = System.Enum.IsDefined(typeof(Campus_live_rewrite.api.server.Classes.CurrencyType), kv.Key)
                           ? ((Campus_live_rewrite.api.server.Classes.CurrencyType)kv.Key).ToString()
                           : $"Unknown({kv.Key})",
                    Amount = kv.Value
                })
                .OrderBy(b => b.CurrencyType)
                .ToList();

            return Json(new
            {
                success = true,
                playerId = playerId,
                username = acc.player.Username,
                xp = acc.player.XP,
                level = acc.player.Level,
                balances = balances
            });
        }

        /// <summary>Set XP and/or Level. Blank fields are left unchanged.</summary>
        [HttpPost("/admin/api/set_progression")]
        public ContentResult AdminSetProgression()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId = HttpContext.Request.Form["playerId"].ToString();
            string rawXp = HttpContext.Request.Form["xp"].ToString();
            string rawLv = HttpContext.Request.Form["level"].ToString();

            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });

            int? xp = null, level = null;
            if (!string.IsNullOrWhiteSpace(rawXp))
            {
                if (!int.TryParse(rawXp, out int vx))
                    return Json(new { success = false, message = "XP must be a whole number." });
                xp = vx;
            }
            if (!string.IsNullOrWhiteSpace(rawLv))
            {
                if (!int.TryParse(rawLv, out int vl))
                    return Json(new { success = false, message = "Level must be a whole number." });
                level = vl;
            }
            if (xp == null && level == null)
                return Json(new { success = false, message = "Enter an XP and/or Level value." });

            var result = PlayerDB.SetProgression(playerId, xp, level);
            if (result == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            // Live push: broadcast the level to everyone (same event + SendToAll the game uses in
            // PlayerDB.SetPlayerLevel), so the player and anyone looking at them updates at once.
            PushEvent(playerId, Campus_live_rewrite.api.WebsocketEvents.createResponse_levelupdate(
                playerId, result.Value.Level,
                Campus_live_rewrite.api.server.Classes.Progress.CumulativeXpForLevel(result.Value.Level, result.Value.XP)),
                "level", toAll: true);

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) set progression on {playerId} -> XP={result.Value.XP} Level={result.Value.Level}");
            return Json(new { success = true, message = $"Saved. XP {result.Value.XP}, Level {result.Value.Level}.", xp = result.Value.XP, level = result.Value.Level });
        }

        /// <summary>
        /// Toggle the in-game account creation TEST switch (ConfigCampus.AdminTestNewAccountFlow):
        /// on = allowlisted admin IPs get an empty cached-login picker so their client creates a new
        /// account in-game. GET reports the state; POST with on=1|0 sets it.
        /// </summary>
        [HttpGet("/admin/api/test_newaccount")]
        [HttpPost("/admin/api/test_newaccount")]
        public ContentResult AdminTestNewAccount()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string path = Path.Combine(Environment.CurrentDirectory, "Data", "admin_test_newaccount.txt");
            if (HttpContext.Request.Method == "POST")
            {
                string on = HttpContext.Request.Form["on"].ToString();
                bool enable = on == "1" || string.Equals(on, "true", StringComparison.OrdinalIgnoreCase);
                try
                {
                    if (enable) System.IO.File.WriteAllText(path, $"on by {account.Username} at {DateTime.UtcNow:u}\n");
                    else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
                catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
                Console.WriteLine($"[admin] {account.Username} ({account.Id}) set new-account test switch {(enable ? "ON" : "OFF")}");
            }
            bool state = ConfigCampus.AdminTestNewAccountFlow;
            return Json(new { success = true, enabled = state, message = state
                ? "ON: your next game launch from an allowlisted IP creates a NEW account in-game (empty picker)."
                : "OFF: normal login." });
        }

        /// <summary>A player's stored settings (the client's "recroom prefs"), for debugging onboarding.</summary>
        [HttpGet("/admin/api/player_settings")]
        public ContentResult AdminPlayerSettings([FromQuery] ulong playerId)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var settings = SettingsDB.LoadSettings(playerId);
            return Json(new { success = true, playerId, settings = settings.Select(s => new { s.Key, s.Value }) });
        }

        /// <summary>Grant currency, or remove it with a negative amount.</summary>
        /// <summary>Every account id in the player store. The "send to all" fan-out list.</summary>
        private static List<ulong> AllPlayerIds()
        {
            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            return col.FindAll().Where(p => p?.player != null).Select(p => p.playerid).ToList();
        }

        /// <summary>
        /// Give the same balance / XP / level to EVERY account, by re-entering AdminGrantBalance
        /// once per player with that player's id substituted in. Replaying the real handler is
        /// deliberate: the currency, XP and level branches each have their own delivery rules, and
        /// a second copy of them here would be the thing that silently rots.
        /// </summary>
        private ContentResult GrantBalanceToEveryone(player_info account, string rawCur, string rawAmt)
        {
            if (!long.TryParse(rawAmt, out long amount) || amount == 0)
                return Json(new { success = false, message = "Amount must be a non-zero whole number." });

            var ids = AllPlayerIds();
            int done = 0, failed = 0;

            // The form is read-only, so swap in a mutable copy per player and put the original back.
            var original = HttpContext.Request.Form;
            try
            {
                foreach (var pid in ids)
                {
                    try
                    {
                        var fields = original.ToDictionary(kv => kv.Key, kv => kv.Value);
                        fields["playerId"] = pid.ToString();
                        HttpContext.Request.Form = new Microsoft.AspNetCore.Http.FormCollection(fields);

                        var res = AdminGrantBalance();
                        if (res.Content != null && res.Content.Contains("\"success\":true")) done++; else failed++;
                    }
                    catch (Exception ex) { failed++; Console.WriteLine($"[admin] grant_balance to {pid} failed: {ex.Message}"); }
                }
            }
            finally { HttpContext.Request.Form = original; }

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) granted {amount} {rawCur} to EVERYONE - {done} ok, {failed} failed");
            return Json(new { success = true, message = $"Applied to {done} player(s)" + (failed > 0 ? $" ({failed} failed)." : ".") });
        }

        [HttpPost("/admin/api/grant_balance")]
        public ContentResult AdminGrantBalance()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId = HttpContext.Request.Form["playerId"].ToString();
            string rawCur = HttpContext.Request.Form["currencyType"].ToString();
            string rawAmt = HttpContext.Request.Form["amount"].ToString();

            // playerId -1 (or "all") = EVERY account, the same convention /admin/api/gift/give
            // already uses. Handled by replaying this endpoint once per player rather than by
            // duplicating the currency / XP / level logic, so "to everyone" can never drift from
            // what the single-player button does.
            bool toAll = rawId == "-1" || string.Equals(rawId, "all", StringComparison.OrdinalIgnoreCase);
            if (toAll) return GrantBalanceToEveryone(account, rawCur, rawAmt);

            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            if (!long.TryParse(rawAmt, out long amount) || amount == 0)
                return Json(new { success = false, message = "Amount must be a non-zero whole number." });

            if (PlayerDB.GetFullAccount(playerId)?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            // ---- "level": set the player TO that level (XP = that level's XP), applied now ----
            if (string.Equals(rawCur, "level", StringComparison.OrdinalIgnoreCase))
            {
                int lvl = (int)Math.Clamp(amount, 1, Campus_live_rewrite.api.server.Classes.Progress.MAX_LEVEL);
                var res = PlayerDB.SetProgression(playerId, 0, lvl);
                if (res == null) return Json(new { success = false, message = $"No player with id {playerId}." });
                PushEvent(playerId, Campus_live_rewrite.api.WebsocketEvents.createResponse_levelupdate(
                    playerId, res.Value.Level,
                    Campus_live_rewrite.api.server.Classes.Progress.CumulativeXpForLevel(res.Value.Level, res.Value.XP)),
                    "level", toAll: true);
                Console.WriteLine($"[admin] {account.Username} ({account.Id}) set {playerId} to level {res.Value.Level}");
                return Json(new { success = true, message = $"Set to Level {res.Value.Level} ({Campus_live_rewrite.api.server.Classes.Progress.CumulativeXpForLevel(res.Value.Level, 0)} XP)." });
            }

            // ---- "xp": added straight to the bar. The 2025 client has no drawable XP-only box: every
            // shape was tried live (reference toGameRewardDrop shape; null AvatarItemDesc; contexts
            // 50, 1, 2, 3, 5) and each rendered "Can't load item", and a currency-carried label
            // reads the currency's own name. So XP is banked now: the bar moves live and any level
            // crossed pays its level-up box.
            if (string.Equals(rawCur, "xp", StringComparison.OrdinalIgnoreCase))
            {
                if (amount < 0) return Json(new { success = false, message = "XP can only be given, not taken. Use Level to set a lower level." });
                var r = PlayerDB.AddXp(playerId, (int)Math.Min(amount, int.MaxValue));
                if (r == null) return Json(new { success = false, message = $"No player with id {playerId}." });
                Console.WriteLine($"[admin] {account.Username} ({account.Id}) gave {amount} XP to {playerId} -> level {r.Value.Level} (+{r.Value.LevelsGained})");
                return Json(new { success = true, message = $"Gave {amount} XP. Now Level {r.Value.Level}" + (r.Value.LevelsGained > 0 ? $" (+{r.Value.LevelsGained}, level-up box sent)." : ".") });
            }

            if (!int.TryParse(rawCur, out int currencyType))
                return Json(new { success = false, message = "Pick a currency." });

            string name = System.Enum.IsDefined(typeof(Campus_live_rewrite.api.server.Classes.CurrencyType), currencyType)
                          ? ((Campus_live_rewrite.api.server.Classes.CurrencyType)currencyType).ToString()
                          : $"Currency {currencyType}";
            long total = PlayerDB.GetBalance(playerId, currencyType);

            // A REMOVAL is balance-only, applied now. A GRANT arrives as a gift box (announced over
            // the hub right now so it drops in front of the player) and the amount is credited when
            // they OPEN it - the counter moves with the reveal. The box's look comes from the
            // panel's box type (GiftContext: 51 tokens, 50 XP box, 4003 Golden, ...) and its text
            // from the message field.
            if (amount < 0)
            {
                total = PlayerDB.AddBalance(playerId, currencyType, amount);
                PushEvent(playerId, Campus_live_rewrite.api.WebsocketEvents.createResponse_balanceupdate(currencyType, total), "balance");
            }
            else
            {
                // The text on the box comes from the panel's "Box message" field; blank falls back
                // to the default. Capped so a novel can't be pasted onto a box.
                string boxMsg = HttpContext.Request.Form["message"].ToString().Trim();
                if (boxMsg.Length > 120) boxMsg = boxMsg.Substring(0, 120);
                if (string.IsNullOrWhiteSpace(boxMsg)) boxMsg = "A gift for you <3";
                int boxType = int.TryParse(HttpContext.Request.Form["giftContext"].ToString(), out var bt) ? bt
                              : Campus_live_rewrite.api.server.QuestRewards.GiftContextGameRewardTokens;
                var box = new Campus_live_rewrite.api.server.Classes.db_class.PendingGiftBox
                {
                    GiftContext        = boxType,
                    Message            = boxMsg,
                    FromAccountId      = 0,
                    FriendlyName       = $"{amount} {(currencyType == (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens ? "Tokens" : name)}",
                    ThumbnailImageName = Campus_live_rewrite.api.server.QuestRewards.TokenThumbnail,
                    CurrencyType       = currencyType,
                    Currency           = amount,
                    Rarity             = Campus_live_rewrite.api.server.QuestRewards.TokenRarity(amount),
                    PayOnOpen          = true
                };
                if (!PlayerDB.AddPendingGiftBoxFull(playerId, box, grantSkin: true))
                    return Json(new { success = false, message = "Could not store the gift box." });
                Campus_live_rewrite.api.server.GiftBoxes.Push(playerId, box, Campus_live_rewrite.api.server.GiftBoxes.FrameReceivedImmediate, "admin token box");
            }

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) {(amount > 0 ? "boxed" : "removed")} {System.Math.Abs(amount)} {name} for {playerId}{(amount > 0 ? " (paid on open)" : $" - new total {total}")}");
            return Json(new
            {
                success = true,
                message = amount > 0
                    ? $"Gift box with {amount} {name} sent - it pays out when they open it."
                    : $"Removed {System.Math.Abs(amount)} {name}. New total: {total}.",
                total = total
            });
        }

        /// <summary>
        /// Send a player a GAME REWARD: exactly what POST /api/gamerewards/v1/request does after a
        /// game - a three-card reward picker on their watch, in the chosen activity's box, that pays
        /// when they pick and open. No cooldown here: the panel decides.
        /// </summary>
        [HttpPost("/admin/api/send_gamereward")]
        public ContentResult AdminSendGameReward()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId = HttpContext.Request.Form["playerId"].ToString();
            string rawCtx = HttpContext.Request.Form["giftContext"].ToString();
            string message = HttpContext.Request.Form["message"].ToString().Trim();
            int giftContext = int.TryParse(rawCtx, out var gc) ? gc : 0;
            if (message.Length > 120) message = message.Substring(0, 120);

            // playerId -1 / "all" = everyone (same convention as gift/give and grant_balance).
            if (rawId == "-1" || string.Equals(rawId, "all", StringComparison.OrdinalIgnoreCase))
            {
                int sent = 0, failed = 0;
                foreach (var pid in AllPlayerIds())
                {
                    try
                    {
                        var one = Campus_live_rewrite.api.server.RewardSelections.Offer(
                            pid, giftContext, null, string.IsNullOrWhiteSpace(message) ? "Game Complete" : message);
                        if (one != null) sent++; else failed++;
                    }
                    catch (Exception ex) { failed++; Console.WriteLine($"[admin] game reward to {pid} failed: {ex.Message}"); }
                }
                Console.WriteLine($"[admin] {account.Username} ({account.Id}) sent game reward (box {giftContext}) to EVERYONE - {sent} sent, {failed} failed");
                return Json(new { success = true, message = $"Reward picker sent to {sent} player(s)" + (failed > 0 ? $" ({failed} failed)." : ".") });
            }

            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            var sel = Campus_live_rewrite.api.server.RewardSelections.Offer(playerId, giftContext, null, string.IsNullOrWhiteSpace(message) ? "Game Complete" : message);
            if (sel == null)
                return Json(new { success = false, message = "Could not build a reward selection." });
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) sent game reward (box {giftContext}) to {playerId} - selection {sel.RewardSelectionId}");
            return Json(new { success = true, message = $"Reward picker sent to @{acc.player.Username} (box {giftContext}): {string.Join(" / ", sel.Drops.Select(d => d.Currency + " tokens"))}." });
        }

        /// <summary>Gift a gift box to a player.</summary>
        [HttpPost("/admin/api/gift_giftbox")]
        public ContentResult AdminGiftGiftBox()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId = HttpContext.Request.Form["playerId"].ToString();
            string rawCtx = HttpContext.Request.Form["giftContext"].ToString();
            string rawAmt = HttpContext.Request.Form["amount"].ToString();
            string message = HttpContext.Request.Form["message"].ToString() ?? "";

            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            // Accept EITHER a numeric gift context OR a mode name (e.g. "Discgolf_Propulsion"), so the
            // owner can grant a real gift box straight from that mode's reward pool.
            int giftContext = int.TryParse(rawCtx, out var gcParsed) ? gcParsed : 0;
            string? giftMode = int.TryParse(rawCtx, out _) ? null : (string.IsNullOrWhiteSpace(rawCtx) ? null : rawCtx.Trim());
            if (giftContext == 0 && string.IsNullOrWhiteSpace(giftMode))
                return Json(new { success = false, message = "Pick a gift box (context id or mode name)." });

            // Amount is optional; blank means "just queue the box, pay nothing".
            long amount = 0;
            if (!string.IsNullOrWhiteSpace(rawAmt) && !long.TryParse(rawAmt, out amount))
                return Json(new { success = false, message = "Amount must be a whole number." });
            if (amount < 0) amount = 0;

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            // Deliver a REAL gift box: roll an item from the reward pool for this context/mode
            // (Data/APIS/quest-rewards.json) and grant it fully (it enters the wardrobe too). Only
            // if the pool has nothing for it do we fall back to the old contentless box.
            var giftPick = Campus_live_rewrite.api.server.QuestRewards.Pick(giftContext, giftMode);
            if (giftPick != null
                && (!string.IsNullOrWhiteSpace(giftPick.AvatarItemDesc)
                    || !string.IsNullOrWhiteSpace(giftPick.ConsumableItemDesc)
                    || !string.IsNullOrWhiteSpace(giftPick.EquipmentPrefabName)))
            {
                var rolledBox = new Campus_live_rewrite.api.server.Classes.db_class.PendingGiftBox
                {
                    GiftContext               = giftContext,
                    Message                   = message,
                    FromAccountId             = account.Id,
                    AvatarItemDesc            = giftPick.AvatarItemDesc,
                    ConsumableItemDesc        = giftPick.ConsumableItemDesc,
                    EquipmentPrefabName       = giftPick.EquipmentPrefabName,
                    EquipmentModificationGuid = giftPick.EquipmentModificationGuid,
                    Rarity                    = giftPick.GiftRarity
                };
                if (PlayerDB.AddPendingGiftBoxFull(playerId, rolledBox, grantSkin: true))
                    Campus_live_rewrite.api.server.GiftBoxes.Push(playerId, rolledBox, Campus_live_rewrite.api.server.GiftBoxes.FrameReceivedImmediate, "admin gift box");
                Console.WriteLine($"[admin] gift box for {playerId}: mode='{giftMode ?? giftContext.ToString()}' -> item '{giftPick.AvatarItemDesc ?? giftPick.EquipmentPrefabName ?? giftPick.ConsumableItemDesc}'");
            }
            else
            {
                PlayerDB.AddPendingGiftBox(playerId, giftContext, message, account.Id);
            }

            // Pay out regardless. Currency IS confirmed working end-to-end, so the gift
            // is worth something to the recipient even while the box visual is unresolved.
            string payout = "no currency";
            if (amount > 0)
            {
                var currency = Campus_live_rewrite.api.server.Classes.GiftContextIds.CurrencyForGiftBox(giftContext);
                long total = PlayerDB.AddBalance(playerId, (int)currency, amount);
                payout = $"{amount} {currency} (new total {total})";
                // Live push: new balance to the owner.
                PushEvent(playerId, Campus_live_rewrite.api.WebsocketEvents.createResponse_balanceupdate((int)currency, total), "giftbox balance");
            }

            // Live push: nudge the client to re-read the account/gifts (same as gift/give does).
            PushAccountRefresh(playerId, "giftbox");

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) gifted box {giftContext} to {playerId} - {payout}");
            _ = Task.Run(() => Webhook.Send(
                playerId, "prod", "Gift box",
                $"@{acc.player.Username} ({playerId}) was gifted gift box {giftContext} by @{account.Username} - {payout}",
                5898927, null));

            return Json(new
            {
                success = true,
                message = amount > 0
                    ? $"Sent to @{acc.player.Username}: {payout}."
                    : $"Gift box queued for @{acc.player.Username} (no currency - set an amount to pay out)."
            });
        }

        // ---------------------------------------------------------------------------------------
        // Give a real ITEM as a proper gift box (with content) or straight to the wardrobe.
        // Ported from the 2023 server's admin GiveItem. The old 2025 admin gift queued a CONTENTLESS
        // box (no item), which the client can't render and can never open - "random gifts" that
        // won't clear. This resolves the item from the storefront catalog (its real GiftDropId,
        // Rarity and RAW descriptor) so the box opens correctly and the item lands in the wardrobe.
        // ---------------------------------------------------------------------------------------
        [HttpGet("/admin/api/gift/search")]
        public IActionResult AdminGiftSearch([FromQuery] string? q)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            // TWO sources, storefront first.
            //
            // This used to search only the storefront catalogs, which list what is FOR SALE - so
            // most of avatar_items.json could not be found at all ("thief hat" returned the three
            // on the Watch store and nothing else). The store entries still come first because
            // they carry a real GiftDropId, which is what lets the gift BOX render the item; a
            // catalog-only item is identified by its descriptor instead.
            const int cap = 60;
            var store = string.IsNullOrWhiteSpace(q)
                ? new List<Campus_live_rewrite.api.server.StoreCatalog.GiftDropInfo>()
                : Campus_live_rewrite.api.server.StoreCatalog.SearchItems(q, cap);

            var rows = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Custom (UGC) items first - modelled first-party and player items, found by name.
            // They are given by their CustomAvatarItemId (see gift/give).
            if (!string.IsNullOrWhiteSpace(q))
                foreach (var r in Campus_live_rewrite.api.server.CustomItemsCache.SearchRecs(q, 20))
                    rows.Add(new
                    {
                        purchasableItemId = 0,
                        storefrontType = -1,
                        avatarItemDesc = "",
                        customAvatarItemId = r.Id,
                        name = string.IsNullOrWhiteSpace(r.Name) ? "(unnamed)" : r.Name,
                        rarity = 0,
                        giftDropId = 0,
                        source = "ugc"
                    });

            foreach (var m in store)
            {
                if (!string.IsNullOrWhiteSpace(m.Reward))
                    seen.Add(PlayerDB.NormalizeAvatarItemDesc(m.Reward));
                rows.Add(new
                {
                    purchasableItemId = m.PurchasableItemId,
                    storefrontType = m.StorefrontType,
                    avatarItemDesc = m.Reward ?? "",
                    name = string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name,
                    rarity = m.Rarity,
                    giftDropId = m.GiftDropId,
                    source = "store"
                });
            }

            int catalogTotal = 0;
            if (!string.IsNullOrWhiteSpace(q))
            {
                catalogTotal = Campus_live_rewrite.api.server.AvatarCatalog.CountMatches(q);
                foreach (var e in Campus_live_rewrite.api.server.AvatarCatalog.Search(q, cap))
                {
                    if (rows.Count >= cap) break;
                    // Skip anything the storefront half already offered - one item, one row.
                    if (!seen.Add(PlayerDB.NormalizeAvatarItemDesc(e.Desc))) continue;
                    rows.Add(new
                    {
                        purchasableItemId = 0,
                        storefrontType = -1,
                        avatarItemDesc = e.Desc,
                        name = string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name,
                        rarity = e.Rarity,
                        giftDropId = 0,
                        source = "catalog"
                    });
                }
            }

            return Json(new
            {
                success = true,
                count = rows.Count,
                total = store.Count + catalogTotal,   // before the cap, so the UI can say "showing N of M"
                items = rows
            });
        }

        [HttpPost("/admin/api/gift/give")]
        public IActionResult AdminGiftGive()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var form = HttpContext.Request.Form;
            string rawPid = form["playerId"].ToString();
            bool toAll = rawPid == "-1" || string.Equals(rawPid, "all", StringComparison.OrdinalIgnoreCase);
            ulong onePlayer = 0;
            if (!toAll && !ulong.TryParse(rawPid, out onePlayer))
                return Json(new { success = false, message = "Invalid player id." });

            // A custom (UGC) item: straight into the wardrobe, plus a gift box unless the mode says
            // wardrobe only. Owners are skipped.
            string ugcId = (form["customAvatarItemId"].ToString() ?? "").Trim();
            if (ugcId.Length > 0)
            {
                var rec = Campus_live_rewrite.api.server.CustomItemsCache.GetRec(ugcId);
                if (rec == null) return Json(new { success = false, message = "No custom item with that id." });
                bool wardrobeOnly = string.Equals(form["mode"].ToString(), "wardrobe", StringComparison.OrdinalIgnoreCase);
                string? note = string.IsNullOrWhiteSpace(form["message"].ToString()) ? null : form["message"].ToString().Trim();
                var targets = toAll
                    ? PlayerDB.PlayerDBFile.GetCollection<player_data>("players").FindAll().Select(p => p.playerid).ToList()
                    : new List<ulong> { onePlayer };
                if (!toAll && PlayerDB.GetFullAccount(onePlayer)?.player == null)
                    return Json(new { success = false, message = $"No player with id {onePlayer}." });
                int ugcGiven = 0, ugcSkipped = 0;
                foreach (var pid in targets)
                {
                    if (!PlayerDB.GrantCustomItem(pid, rec.Id)) { ugcSkipped++; continue; }
                    ugcGiven++;
                    if (wardrobeOnly) continue;
                    var ubox = new PendingGiftBox
                    {
                        NumericId = Campus_live_rewrite.api.server.StoreCatalog.NextPurchaseGiftId(),
                        GiftContext = ConfigCampus.StoreGiftContext,
                        Message = note ?? "",
                        FromAccountId = 0,
                        AvatarItemDesc = "",
                        CustomAvatarItemId = rec.Id,
                        FriendlyName = rec.Name,
                        Rarity = ConfigCampus.DefaultGiftRarity
                    };
                    PlayerDB.AddPendingGiftBoxFull(pid, ubox);
                    Campus_live_rewrite.api.server.GiftBoxes.Push(pid, ubox, Campus_live_rewrite.api.server.GiftBoxes.FrameReceivedImmediate, "admin ugc gift");
                }
                Console.WriteLine($"[admin] {account!.Username} ({account.Id}) gave custom item '{rec.Name}' ({rec.Id}) to {(toAll ? "everyone" : onePlayer.ToString())}: {ugcGiven} given, {ugcSkipped} already had it");
                return Json(new { success = true, message = $"Gave {rec.Name} to {ugcGiven} player(s)" + (ugcSkipped > 0 ? $" ({ugcSkipped} already had it)." : ".") });
            }

            int.TryParse(form["purchasableItemId"].ToString(), out int itemId);
            int storefront = int.TryParse(form["storefrontType"].ToString(), out var sf) ? sf : -1;
            string rawDesc = (form["avatarItemDesc"].ToString() ?? "").Trim();
            bool wardrobe = string.Equals(form["mode"].ToString(), "wardrobe", StringComparison.OrdinalIgnoreCase);
            string? message = string.IsNullOrWhiteSpace(form["message"].ToString()) ? null : form["message"].ToString().Trim();
            int xp = int.TryParse(form["xp"].ToString(), out var x) && x > 0 ? x : 0;

            // An item now arrives one of two ways: a storefront drop (PurchasableItemId) or a bare
            // avatar_items.json entry (AvatarItemDesc). Both are resolved into the same
            // GiftDropInfo so everything below - box, wardrobe, give-to-all - stays one path.
            Campus_live_rewrite.api.server.StoreCatalog.GiftDropInfo? item = null;

            if (itemId > 0)
                item = Campus_live_rewrite.api.server.StoreCatalog.FindStoreItem(itemId, storefront);

            if ((item == null || string.IsNullOrWhiteSpace(item.Reward)) && !string.IsNullOrWhiteSpace(rawDesc))
            {
                // Prefer a storefront entry for the same descriptor when one exists: it carries the
                // real GiftDropId, and that is what the gift BOX needs to draw the item. Only fall
                // back to a synthesised record when nothing sells this item.
                item = Campus_live_rewrite.api.server.StoreCatalog
                           .SearchItems(rawDesc, 5)
                           .FirstOrDefault(s => string.Equals(
                               PlayerDB.NormalizeAvatarItemDesc(s.Reward),
                               PlayerDB.NormalizeAvatarItemDesc(rawDesc),
                               StringComparison.OrdinalIgnoreCase));

                if (item == null)
                {
                    var cat = Campus_live_rewrite.api.server.AvatarCatalog.ByDesc(rawDesc);
                    if (cat != null)
                    {
                        item = new Campus_live_rewrite.api.server.StoreCatalog.GiftDropInfo
                        {
                            Reward = cat.Desc,          // RAW - the client parses this
                            Name = cat.Name,
                            Rarity = cat.Rarity,
                            AvatarItemType = cat.AvatarItemType,
                            GiftDropId = 0,             // not a sold drop; see the wardrobe note below
                            ConsumableItemDesc = "",
                            EquipmentPrefabName = "",
                            EquipmentModificationGuid = "",
                            StorefrontType = -1
                        };
                    }
                }
            }

            if (item == null || string.IsNullOrWhiteSpace(item.Reward))
                return Json(new { success = false, message = "That item is not in the storefronts or the avatar catalog." });

            string friendly = string.IsNullOrWhiteSpace(item.Name) ? "Gift" : item.Name;
            string key = PlayerDB.NormalizeAvatarItemDesc(item.Reward);

            void GiveOne(ulong pid)
            {
                if (wardrobe)
                {
                    PlayerDB.GrantAvatarItem(pid, item.Reward);
                    // A skin row has no avatar item; it goes into the equipment locker instead.
                    PlayerDB.GrantEquipmentSkin(pid, item.EquipmentPrefabName, item.EquipmentModificationGuid);
                    if (xp > 0) PlayerDB.AddXp(pid, xp);
                }
                else
                {
                    var box = new PendingGiftBox
                    {
                        NumericId = Campus_live_rewrite.api.server.StoreCatalog.NextPurchaseGiftId(),
                        GiftContext = 0,
                        Message = message ?? "A gift for you <3",
                        FromAccountId = 0,               // 0 = from the server / admin
                        Anonymous = false,
                        AvatarItemDesc = item.Reward,    // RAW - the client parses this
                        ConsumableItemDesc = item.ConsumableItemDesc,
                        AvatarItemType = item.AvatarItemType,
                        GiftDropId = item.GiftDropId,    // real id so the box resolves, not the placeholder
                        FriendlyName = friendly,
                        Rarity = item.Rarity,
                        EquipmentPrefabName = item.EquipmentPrefabName,
                        EquipmentModificationGuid = item.EquipmentModificationGuid
                    };
                    PlayerDB.AddPendingGiftBoxFull(pid, box, grantSkin: true);   // grants ownership + queues the box
                    // Announce it now (GiftPackageReceivedImmediate) so the box drops in front of the
                    // player instead of waiting for their next gifts poll / relog.
                    Campus_live_rewrite.api.server.GiftBoxes.Push(pid, box, Campus_live_rewrite.api.server.GiftBoxes.FrameReceivedImmediate, "admin gift");
                    if (xp > 0) PlayerDB.AddXp(pid, xp);
                    Notifications.FireAndForget(Notifications.RefreshAccount(pid), "admin gift refresh");
                }
            }

            int given = 0;
            if (toAll)
            {
                var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
                foreach (var p in col.FindAll().ToList())
                {
                    if (p?.player == null) continue;
                    // Skip anyone who already owns it, so nobody gets a duplicate box.
                    if (PlayerDB.OwnedAvatarItemDescs(p.playerid).Contains(key)) continue;
                    GiveOne(p.playerid);
                    given++;
                }
            }
            else
            {
                var target = PlayerDB.GetFullAccount(onePlayer);
                if (target?.player == null)
                    return Json(new { success = false, message = $"No player with id {onePlayer}." });
                GiveOne(onePlayer);
                given = 1;
            }

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) gave '{friendly}' ({item.Reward}) " +
                              $"{(wardrobe ? "to wardrobe" : "as gift box")} to {(toAll ? $"{given} player(s)" : rawPid)}" +
                              $"{(xp > 0 ? $" +{xp} XP" : "")}");

            return Json(new
            {
                success = true,
                message = $"Gave '{friendly}' {(wardrobe ? "straight to the wardrobe" : "as a gift box")} to " +
                          $"{(toAll ? $"{given} player(s)" : "the player")}{(xp > 0 ? $" (+{xp} XP)" : "")}."
            });
        }

        [HttpPost("/admin/api/delete_player")]
        public ContentResult AdminDeletePlayerAPI()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string raw = HttpContext.Request.Form["playerId"].ToString();
            if (!ulong.TryParse(raw, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });

            if (playerId == account.Id)
                return Json(new { success = false, message = "You can't delete your own account from the panel." });

            var target = PlayerDB.GetFullAccount(playerId);
            if (target == null)
                return Json(new { success = false, message = "Player not found." });

            var ok = PlayerDB.DeletePlayerFull(playerId);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) deleted player {playerId} ({target.player?.Username}) — ok={ok}");
            return Json(new { success = ok, message = ok ? "Player deleted." : "Delete failed." });
        }

        private ContentResult Json(object o) => new ContentResult
        {
            Content = JsonConvert.SerializeObject(o),
            ContentType = "application/json",
            StatusCode = 200
        };

        // camelCase property names, for the panel code that reads r.id / r.reportedName. Json()
        // above keeps C# names (Id, ReportedName) and older panel code depends on that.
        private static readonly JsonSerializerSettings _camel = new()
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        };
        private ContentResult JsonCamel(object o) => new ContentResult
        {
            Content = JsonConvert.SerializeObject(o, _camel),
            ContentType = "application/json",
            StatusCode = 200
        };

        [HttpPost("/admin/api/create_player")]
        public ContentResult AdminCreatePlayerAPI()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null)
                return error;

            account_data NewAccount = null;
            // ADD oculus support later when we get that ingame
            string platformid = Request.Form["platform_id"];

            Platforms platform = Platforms.All;
            if (platformid.StartsWith("765611"))
            {
                platform = Platforms.Steam;
            }
            else
            {
                platform = Platforms.Oculus;
            }

            var acc = PlayerDB.CreateAccount(Platforms.Steam, ulong.Parse(platformid), Request.Form["device_id"], ulong.Parse(Request.Form["discord_id"]), out NewAccount);
            return new ContentResult
            {
                Content = NewAccount.accountId.ToString(),
                ContentType = "application/json"
            };
        }

        [HttpPost("/admin/api/ban_player")]
        public ContentResult AdminAPIBanPlayer()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null)
                return error;

            string playerIdStr = Request.Form["playerId"];
            string durationStr = Request.Form["duration"];
            string categoryStr = Request.Form["category"];
            string message = Request.Form["message"];

            if (!ulong.TryParse(playerIdStr, out ulong playerId))
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid Player Id"
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (playerId == 1)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot ban this player"
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (PlayerDB.CheckDevFlag(playerId))
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Cannot ban this player"
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (!int.TryParse(durationStr, out int duration) || duration < 0)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid duration"
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (!Enum.TryParse<Moderation_Detail.ReportCategory>(categoryStr, ignoreCase: true, out var category))
            {
                category = Moderation_Detail.ReportCategory.Moderator;
            }

            // Optional device / IP bans on top of the account ban.
            //
            // An account ban alone is trivially defeated - make a new account and you are
            // back in. These are checked at LOGIN (see AuthController), before an account
            // is even looked up, so the whole machine or address is refused.
            bool banDevice = Truthy(Request.Form["banDevice"]);
            bool banIp = Truthy(Request.Form["banIp"]);

            string? deviceId = null;
            var ips = new List<string>();
            int alsoBanned = 0;
            var extra = new List<string>();

            if (banDevice)
            {
                deviceId = PlayerDB.GetDeviceId(playerId);
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    extra.Add("no device id on record (they have never logged in), device NOT banned");
                }
                else
                {
                    BanDB.BanDevice(deviceId!, playerId, account.Id, message, duration);
                    alsoBanned++;

                    // Say what else this takes out, so it is never a surprise.
                    var alts = PlayerDB.AccountsOnDevice(deviceId).Where(x => x != playerId).ToList();
                    extra.Add(alts.Count > 0
                        ? $"device banned (also blocks {alts.Count} other account(s): {string.Join(", ", alts)})"
                        : "device banned");
                }
            }

            if (banIp)
            {
                ips = PlayerDB.GetDecryptedIpAddresses(playerId) ?? new List<string>();
                if (ips.Count == 0)
                {
                    extra.Add("no IP on record, IP NOT banned");
                }
                else
                {
                    foreach (var ip in ips)
                        BanDB.BanIp(ip, playerId, account.Id, message, duration);
                    alsoBanned++;
                    extra.Add($"{ips.Count} IP(s) banned");
                }
            }

            // The account ban itself, after the device/IP step so its Ban Logs entry says exactly
            // what else went down with it.
            PlayerDB.Set_moderation_block(
                playerId,
                category,
                duration,
                gameSessionId: -1,
                isHostKick: false,
                message: message,
                playerIdReporter: 0,
                isBan: true,
                bannedByPlayerId: account.Id,
                logKind: "ban",
                logNote: extra.Count > 0 ? string.Join("; ", extra) : "account only"
            );

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) banned {playerId} " +
                              $"for {duration}s{(extra.Count > 0 ? " - " + string.Join("; ", extra) : "")}");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    message = extra.Count > 0 ? string.Join("; ", extra) : "Account banned.",
                    deviceBanned = banDevice && !string.IsNullOrWhiteSpace(deviceId),
                    ipsBanned = banIp ? ips.Count : 0
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        /// <summary>Matches any character outside plain ASCII.</summary>
        private const string NonAsciiPattern = @"[^\x00-\x7f]";

        private static string SafeDecrypt(string v)
        {
            try { return Campus_live_rewrite.api.server.Utils.DecryptAes(v); }
            catch { return "(unreadable)"; }
        }

        /// <summary>Checkbox/switch values arrive as "1", "true" or "on" depending on the form.</summary>
        private static bool Truthy(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            v = v.Trim();
            return v == "1"
                || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Lift a ban: clears the account block and, by default, every device/IP ban that
        /// originated from this account.
        ///
        /// Clearing only the account block would leave their device blacklisted, so the
        /// player still could not log in and the unban would look broken.
        /// </summary>
        [HttpPost("/admin/api/unban_player")]
        public ContentResult AdminUnbanPlayer()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;

            string rawId = Request.Form["playerId"].ToString();
            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            bool clearedAccount = PlayerDB.ClearModerationBlock(playerId);

            // Default ON: an unban that leaves the device blocked is not an unban.
            bool liftDeviceIp = Request.Form["liftDeviceIp"].Count == 0 || Truthy(Request.Form["liftDeviceIp"]);
            int lifted = liftDeviceIp ? BanDB.UnbanAllForPlayer(playerId) : 0;

            ModerationLogDB.Record("unban", playerId, account.Id, 0, 0, 0, Moderation_Detail.ReportCategory.Moderator, "",
                $"account block {(clearedAccount ? "cleared" : "was not set")}; {lifted} device/IP ban(s) lifted");

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) unbanned {playerId} " +
                              $"(account block cleared: {clearedAccount}, device/IP entries lifted: {lifted})");

            string msg = clearedAccount ? $"@{acc.player.Username} unbanned." : $"@{acc.player.Username} had no account ban.";
            if (lifted > 0) msg += $" Lifted {lifted} device/IP ban(s).";

            return Json(new { success = true, message = msg, clearedAccount, liftedDeviceIpBans = lifted });
        }

        /// <summary>
        /// Reads a player's editable profile fields for the admin panel's Edit Player form.
        /// </summary>
        [HttpGet("/admin/api/get_profile")]
        public ContentResult AdminGetProfile()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;

            string rawId = Request.Query["playerId"].ToString();
            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            return Json(new
            {
                success = true,
                playerId,
                username = acc.player.Username,
                displayName = acc.player.DisplayName,
                profileImage = acc.player.profileImage,
                bannerImage = acc.player.bannerImage,
                // Strings, not numbers: a 17-digit SteamID64 exceeds JavaScript's 53-bit
                // integer precision and would be silently rounded on the way into the form.
                // An account may hold several, so this is a list; empty means not linked.
                steamIds = PlayerDB.GetPlatformIds(playerId, Platforms.Steam).Select(i => i.ToString()).ToArray(),
                discordId = (acc.player_Extra?.DiscordId ?? 0).ToString(),
                discordUsername = acc.player_Extra?.DiscordUsername ?? "",
                nameLocked = acc.player_Extra?.NameLocked == true,
                bioLocked = acc.player_Extra?.BioLocked == true,
                pictureLocked = acc.player_Extra?.PictureLocked == true,
                bannerLocked = acc.player_Extra?.BannerLocked == true,
                isJunior = acc.player.IsJunior,
                twoFactorEnabled = TwoFactorDB.IsEnabled(playerId)
            });
        }

        /// <summary>
        /// Disable a player's 2FA from the admin side - the "I lost my phone / authenticator" reset. Works on the
        /// website's 2FA and the admin panel's own (same TwoFactorDB, one account id space). Does not require the
        /// player's old code; that's the whole point of a support reset, so it's logged for accountability.
        /// </summary>
        [HttpPost("/admin/api/player/2fa/disable")]
        public ContentResult AdminDisablePlayer2FA()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!ulong.TryParse(Request.Form["playerId"].ToString(), out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null) return Json(new { success = false, message = $"No player with id {playerId}." });

            bool was = TwoFactorDB.IsEnabled(playerId);
            TwoFactorDB.Disable(playerId);
            Console.WriteLine($"[admin] {account!.Username} ({account.Id}) disabled 2FA for {playerId} (@{acc.player.Username})");
            return Json(new
            {
                success = true,
                message = was ? $"2FA turned off for @{acc.player.Username}. They can sign in with just their password again."
                              : $"@{acc.player.Username} didn't have 2FA on."
            });
        }

        /// <summary>
        /// Junior / un-junior a player (under-13 restrictions). Applied live: the player's client
        /// gets the account update and a RefreshLogin, so no relog is needed.
        /// </summary>
        [HttpPost("/admin/api/set_junior")]
        public ContentResult AdminSetJunior()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            if (!ulong.TryParse(Request.Form["playerId"].ToString(), out ulong playerId) || playerId == 0)
                return Json(new { success = false, message = "Invalid player id." });
            bool junior = Truthy(Request.Form["junior"].ToString());

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            if (!PlayerDB.SetJunior(playerId, junior, account.Id, "admin panel"))
                return Json(new { success = false, message = "Could not update the player." });

            return Json(new
            {
                success = true,
                isJunior = junior,
                message = junior ? $"@{acc.player.Username} is now a junior account, renamed @{PlayerDB.GetPlayerNameById(playerId)}."
                                 : $"@{acc.player.Username} is no longer a junior account."
            });
        }

        /// <summary>
        /// Name lock: while on, the player cannot change their own @name or display name anywhere
        /// (in-game, website, first-run onboarding). Admin edits (edit_player) are not gated, so a
        /// moderator can still set the name they want and then lock it.
        /// </summary>
        // Lock / unlock one part of a player's profile: what = bio | picture | banner.
        [HttpPost("/admin/api/set_profile_lock")]
        public ContentResult AdminSetProfileLock()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!ulong.TryParse(Request.Form["playerId"].ToString(), out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            PlayerDB.ProfileLock what;
            switch (Request.Form["what"].ToString().Trim().ToLowerInvariant())
            {
                case "bio": what = PlayerDB.ProfileLock.Bio; break;
                case "picture": what = PlayerDB.ProfileLock.Picture; break;
                case "banner": what = PlayerDB.ProfileLock.Banner; break;
                default: return Json(new { success = false, message = "Lock what? (bio, picture or banner)" });
            }
            bool locked = string.Equals(Request.Form["locked"].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                       || Request.Form["locked"].ToString() == "1";
            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null) return Json(new { success = false, message = $"No player with id {playerId}." });
            if (!PlayerDB.SetProfileLock(playerId, what, locked)) return Json(new { success = false, message = "Could not update the player." });
            string label = what == PlayerDB.ProfileLock.Picture ? "profile picture" : what.ToString().ToLowerInvariant();
            Console.WriteLine($"[admin] {account!.Username} ({account.Id}) set {label} lock {(locked ? "ON" : "off")} for {playerId} (@{acc.player.Username})");
            return Json(new
            {
                success = true,
                locked,
                message = locked ? $"@{acc.player.Username} can no longer change their {label}." : $"@{acc.player.Username} can change their {label} again."
            });
        }

        [HttpPost("/admin/api/set_name_lock")]
        public ContentResult AdminSetNameLock()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId = Request.Form["playerId"].ToString();
            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });

            bool locked = string.Equals(Request.Form["locked"].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                       || Request.Form["locked"].ToString() == "1";

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            if (!PlayerDB.SetNameLock(playerId, locked))
                return Json(new { success = false, message = "Could not update the player." });

            Console.WriteLine($"[admin] {account.Id} set name lock {(locked ? "ON" : "off")} for {playerId} (@{acc.player.Username})");
            return Json(new
            {
                success = true,
                nameLocked = locked,
                message = locked ? $"@{acc.player.Username} can no longer change their @ name or display name."
                                 : $"@{acc.player.Username} can change their names again."
            });
        }

        /// <summary>
        /// Edits another player's username / display name / profile picture / banner.
        ///
        /// Every field is optional - blank means "leave it alone" - so one form can change
        /// any combination without clobbering the rest.
        ///
        /// Validation mirrors the self-service endpoints in AccountsController (length,
        /// swears, spaces, non-ASCII, username uniqueness) so an admin cannot create a
        /// name the player themselves would be refused, and so a duplicate @name cannot
        /// be forced onto an account.
        ///
        /// The one rule NOT carried over is the "you are banned, you cannot rename" check:
        /// renaming a banned player is a normal moderation action.
        ///
        /// Deliberately does NOT push a notification. That is what caused
        /// "Dropping to dormroom due to error [385120]" - answering the request AND pushing
        /// an account update makes the client load the same image twice and complete one
        /// promise twice. See ChangeProfileImage in AccountsController.
        /// </summary>
        [HttpPost("/admin/api/edit_player")]
        public async Task<ContentResult> AdminEditPlayer()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId = Request.Form["playerId"].ToString();
            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            string username = (Request.Form["username"].ToString() ?? "").Trim();
            string displayName = (Request.Form["displayName"].ToString() ?? "").Trim();
            string profileImage = (Request.Form["profileImage"].ToString() ?? "").Trim();
            string bannerImage = (Request.Form["bannerImage"].ToString() ?? "").Trim();
            string steamId = (Request.Form["steamId"].ToString() ?? "").Trim();
            string discordId = (Request.Form["discordId"].ToString() ?? "").Trim();

            var changed = new List<string>();

            // Advisory lines appended to the success message. Nothing here blocks a save.
            var notes = new List<string>();

            // ---- VALIDATE EVERYTHING FIRST -------------------------------------
            // Nothing is written until every field passes. Validating as we went meant a
            // failure halfway through left earlier fields already applied - the @name
            // changed, then the display name was rejected, and the caller was told the
            // edit failed while half of it had actually happened.
            bool wantUsername = !string.IsNullOrWhiteSpace(username) && username != acc.player.Username;
            bool wantDisplay = !string.IsNullOrWhiteSpace(displayName) && displayName != acc.player.DisplayName;

            if (wantUsername)
            {
                // Admins may set single-character @names. The self-service endpoint keeps
                // its 3-character minimum - short names are a thing to hand out, not to
                // let players claim for themselves.
                if (username.Length < 1)
                    return Json(new { success = false, message = "@ Name cannot be empty." });
                if (username.Length > 50)
                    return Json(new { success = false, message = "@ Name must not exceed 50 characters." });
                if (username.Contains(" "))
                    return Json(new { success = false, message = "@ Name must not contain spaces." });
                if (System.Text.RegularExpressions.Regex.IsMatch(username, NonAsciiPattern))
                    return Json(new { success = false, message = "@ Name contains non-ASCII characters." });
            }

            if (wantDisplay)
            {
                // Cap is 50 here, not the 15 the self-service endpoint uses. Accounts the
                // SERVER generates already exceed 15 ("EndlessCheetah8397" is 18), so a
                // 15-char limit would stop an admin restoring a name the server itself
                // created. Admins are trusted; the 15-char rule stays for players.
                if (displayName.Length > 50)
                    return Json(new { success = false, message = "Display Name must not exceed 50 characters." });
                if (System.Text.RegularExpressions.Regex.IsMatch(displayName, NonAsciiPattern))
                    return Json(new { success = false, message = "Display Name contains non-ASCII characters." });
            }

            // ---- platform links -------------------------------------------------
            // Blank leaves the link alone; "0" (or "none"/"clear") removes it.
            //
            // Both are checked for uniqueness BEFORE anything is written. A Steam id is
            // how a sign-in resolves to an account (PlayerDB.GetLogins looks up by
            // platform + platformId), so the same id on two accounts makes the target of
            // a Steam login whichever row LiteDB happens to return first. Refusing the
            // duplicate is the only way to keep that lookup deterministic.
            var currentSteam = PlayerDB.GetPlatformIds(playerId, Platforms.Steam);
            ulong currentDiscord = acc.player_Extra?.DiscordId ?? 0;

            // Steam accepts a LIST - one account may hold several Steam ids, and the same
            // id may sit on several accounts. Separate with commas, spaces or newlines.
            //
            // Sharing an id across accounts is allowed on purpose, so there is no
            // uniqueness check here. It is still surfaced in the response, because
            // PlayerDB.GetLogins resolves a sign-in by (platform, platformId) and returns
            // every account holding it - worth seeing, not worth blocking.
            bool wantSteam = false;
            var newSteam = new List<ulong>();
            if (!string.IsNullOrWhiteSpace(steamId))
            {
                if (!IsClearKeyword(steamId))
                {
                    foreach (var part in steamId.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                                                       StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!ulong.TryParse(part.Trim(), out ulong parsed) || parsed == 0)
                            return Json(new { success = false, message = $"'{part.Trim()}' is not a valid Steam ID. Use 17-digit SteamID64 numbers separated by commas, or 0 to unlink all." });

                        if (!newSteam.Contains(parsed)) newSteam.Add(parsed);
                    }
                }

                wantSteam = !newSteam.SequenceEqual(currentSteam);
            }

            bool wantDiscord = false;
            ulong newDiscord = 0;
            if (!string.IsNullOrWhiteSpace(discordId))
            {
                if (IsClearKeyword(discordId)) newDiscord = 0;
                else if (!ulong.TryParse(discordId, out newDiscord))
                    return Json(new { success = false, message = "Discord ID must be a number (the 17-19 digit user id), or 0 to unlink." });

                wantDiscord = newDiscord != currentDiscord;

                if (wantDiscord && newDiscord != 0)
                {
                    ulong owner = PlayerDB.FindPlayerByDiscordId(newDiscord, playerId);
                    if (owner != 0)
                        return Json(new { success = false, message = $"Discord ID {newDiscord} is already linked to player {owner}. Unlink it there first." });
                }
            }

            // ---- APPLY ---------------------------------------------------------
            // Username first: it is the only field that can still fail (uniqueness is only
            // knowable at write time), so a clash aborts before anything else is touched.
            if (wantUsername)
            {
                bool ok = await Task.Run(() => PlayerDB.SetPlayerUsername(username, playerId));
                if (!ok)
                    return Json(new { success = false, message = $"The @name '{username}' is already in use." });
                changed.Add($"@name -> {username}");
            }

            if (wantDisplay)
            {
                await Task.Run(() => PlayerDB.SetDisplayName(displayName, playerId));
                changed.Add($"display name -> {displayName}");
            }

            // ---- profile picture / banner --------------------------------------
            if (!string.IsNullOrWhiteSpace(profileImage) && profileImage != acc.player.profileImage)
            {
                await Task.Run(() => PlayerDB.SetPlayerImageName(profileImage, playerId));
                changed.Add($"profile picture -> {profileImage}");
            }

            if (!string.IsNullOrWhiteSpace(bannerImage) && bannerImage != acc.player.bannerImage)
            {
                await Task.Run(() => PlayerDB.SetPlayerBanner(bannerImage, playerId));
                changed.Add($"banner -> {bannerImage}");
            }

            if (wantSteam)
            {
                await Task.Run(() => PlayerDB.SetPlatformIds(playerId, Platforms.Steam, newSteam));
                changed.Add(newSteam.Count == 0
                    ? "Steam IDs unlinked"
                    : $"Steam IDs -> {string.Join(", ", newSteam)}");

                // Not a refusal - just say which ids this account now shares, and with whom.
                foreach (var id in newSteam)
                {
                    var others = PlayerDB.FindPlayersByPlatformId(Platforms.Steam, id, playerId);
                    if (others.Count > 0)
                        notes.Add($"Steam ID {id} is also on player{(others.Count > 1 ? "s" : "")} {string.Join(", ", others)}.");
                }
            }

            if (wantDiscord)
            {
                await Task.Run(() => PlayerDB.SetDiscordId(playerId, newDiscord));
                changed.Add(newDiscord == 0 ? "Discord ID unlinked" : $"Discord ID -> {newDiscord}");
            }

            if (changed.Count == 0)
                return Json(new { success = false, message = "Nothing to change - every field was blank or unchanged." });

            // Live push: refresh the affected player's OWN client so a new @name / display name /
            // profile picture / banner / identity shows without a relog.
            //
            // The 385120 dorm-drop only happens on a SELF-service image change, where the client
            // both answers its own change request (load #1) AND gets this push (load #2) - the same
            // promise completed twice. An ADMIN edit is different: the target's client never made a
            // request, so this push is a SINGLE load and is safe even for a picture/banner change.
            //
            // SelfAccountUpdate alone (PushAccountRefresh) does NOT redraw the Portfolio - the banner
            // and picture only changed after a relog, and other players never saw it at all. The
            // AccountUpdate push (owner + everyone) is what the Portfolio / player cards redraw from.
            Notifications.FireAndForget(Notifications.PushAccountUpdate(playerId), "admin edit account-update");

            // The player themselves updates instantly via the refresh above. This client has NO
            // handler to change ANOTHER player's name in real time (the old "AccountUpdate"
            // broadcast applied the payload to the RECEIVER's own account and mass-kicked everyone
            // - see Notifications.RefreshAccount). As a SAFE best-effort, broadcast a reputation
            // update for the renamed player: it's a proven SendToAll event and nudges other
            // clients to re-read that player, so a re-rendered nameplate / opened profile can pick
            // up the new name. Existing in-room nametags still refresh on the next room join.
            bool nameChanged = changed.Any(c => c.StartsWith("@name") || c.StartsWith("display name"));
            if (nameChanged)
                PushEvent(playerId, Campus_live_rewrite.api.WebsocketEvents.CreateResponseReputationUpdate(playerId), "name broadcast", toAll: true);

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) edited player {playerId}: {string.Join("; ", changed)}");

            var after = PlayerDB.GetFullAccount(playerId);
            string message = "Updated " + string.Join("; ", changed) + ".";
            if (notes.Count > 0) message += " " + string.Join(" ", notes);

            return Json(new
            {
                success = true,
                message,
                username = after?.player?.Username,
                displayName = after?.player?.DisplayName,
                profileImage = after?.player?.profileImage,
                steamIds = PlayerDB.GetPlatformIds(playerId, Platforms.Steam).Select(i => i.ToString()).ToArray(),
                discordId = (after?.player_Extra?.DiscordId ?? 0).ToString()
            });
        }

        /// <summary>
        /// Resets another player's login password from the admin panel.
        ///
        /// Kept as its own endpoint (not folded into edit_player) so a blank password box on
        /// the profile Save can never touch the password, and so the action is deliberate and
        /// separately audited. Mirrors the self-service /Auth/account/me/changepassword rules
        /// (min 4, max 64) so an admin cannot set a password the player could not. The plaintext
        /// is hashed by PlayerDB.SetPlayerPassword - it is never stored or logged in the clear.
        /// </summary>
        [HttpPost("/admin/api/reset_password")]
        public async Task<ContentResult> AdminResetPassword()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId = Request.Form["playerId"].ToString();
            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            string newPassword = Request.Form["newPassword"].ToString() ?? "";
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
                return Json(new { success = false, message = "Password must be at least 4 characters." });
            if (newPassword.Length > 64)
                return Json(new { success = false, message = "Password must be fewer than 64 characters." });

            await Task.Run(() => PlayerDB.SetPlayerPassword(newPassword, playerId, log: false));

            // Live push: sign the player out so their old session ends and they must re-login with
            // the new password (existing JWTs stay valid until expiry otherwise). No-op if offline.
            PushEvent(playerId, Campus_live_rewrite.api.WebsocketEvents.CreateLogout(playerId), "password logout");

            // Log WHO did it and to WHOM - never the password itself.
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) reset the password for @{acc.player.Username} ({playerId}).");

            return Json(new { success = true, message = $"Password reset for @{acc.player.Username} (id {playerId}). They were signed out and must log in with the new password." });
        }

        // Expire EVERY in-game bearer token at once - the response to a leaked token/password.
        // Bumps the game-token revoke epoch (ClientSecurity), so any game token minted before now
        // is rejected on its next request and the player is bounced to login. Also broadcasts a
        // logout to everyone currently connected so it takes effect immediately rather than on
        // their next call. WEBSITE and ADMIN sessions are unaffected (the epoch is game-scoped).
        [HttpPost("/admin/api/revoke_game_tokens")]
        public ContentResult AdminRevokeGameTokens()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            long epoch = ClientSecurity.RevokeAllGameTokens();

            // Kick everyone connected right now (best-effort; a no-op for anyone offline, and the
            // epoch already covers them on their next request either way).
            int kicked = 0;
            try
            {
                foreach (var pid in CampusLiveSource.Controllers.NotificationsController.WebSockets.Keys.ToList())
                {
                    PushEvent(pid, Campus_live_rewrite.api.WebsocketEvents.CreateLogout(pid), "revoke-all logout");
                    kicked++;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[admin] revoke_game_tokens broadcast error: {ex.Message}"); }

            Console.WriteLine($"[admin] {account.Username} ({account.Id}) EXPIRED ALL in-game tokens (epoch {epoch}); kicked {kicked} connected player(s).");
            return new ContentResult
            {
                Content = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    epoch,
                    kicked,
                    message = $"All in-game sign-ins expired. {kicked} connected player(s) were signed out; everyone else is signed out on their next request. Website and admin logins are unaffected."
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        /// <summary>"0", "none", "clear" and "unlink" all mean "remove this link".</summary>
        private static bool IsClearKeyword(string s) =>
            s == "0" ||
            s.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("unlink", StringComparison.OrdinalIgnoreCase);

        // ---- live push helpers -----------------------------------------------------------
        // Push an admin change to the affected player's game client so it shows without a relog.
        // All fire-and-forget and a no-op if the player has no open socket, so they never block
        // or fail an admin action.

        // "Re-read your account" - updates username, display name, profile picture, banner and
        // identity (account_data_me). Safe EXCEPT when the profile image itself just changed:
        // answering the change AND pushing makes the client load the new image twice and drop to
        // the dorm ([385120]). Callers must not use this on a profile-image change.
        private static void PushAccountRefresh(ulong playerId, string what)
            => Notifications.FireAndForget(Notifications.RefreshAccount(playerId), $"admin {what} refresh");

        // Serialize + send any websocket event object. toAll broadcasts to every connected client
        // (only for events the client has a per-player handler for - Level, Reputation - never for
        // account_data_me, which other clients apply to THEMSELVES and get kicked to the dorm).
        private static void PushEvent(ulong playerId, object ev, string what, bool toAll = false)
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(ev);
            Notifications.FireAndForget(
                toAll ? Notifications.SendToAll(json) : Notifications.SendToPlayer(playerId, json),
                $"admin {what} push");
        }

        /// <summary>Everything currently banned for a player - shown by the admin panel.</summary>
        // ---- Steam allowlist ------------------------------------------------------
        //
        // Data/steam_allowlist.txt gates STEAM logins only (AuthController). Quest/Meta
        // and every other platform ignore it. An empty list means the check is OFF.

        [HttpGet("/admin/api/steam_allowlist")]
        public ContentResult AdminGetSteamAllowlist()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var ids = SteamAllowlist.All().OrderBy(i => i).ToList();

            // Show who each id belongs to where we can, so the panel is not a wall of
            // 17-digit numbers with no way to tell whose is whose.
            var rows = ids.Select(id =>
            {
                var owners = PlayerDB.FindPlayersByPlatformId(Platforms.Steam, id);
                string? name = null;
                if (owners.Count > 0)
                    name = PlayerDB.GetAccountById(owners[0])?.Username;
                return new
                {
                    steamId = id.ToString(),
                    accountId = owners.Count > 0 ? owners[0] : 0,
                    username = name,
                    linkedAccounts = owners.Count
                };
            }).ToList();

            return Json(new
            {
                success = true,
                active = SteamAllowlist.IsActive,
                count = ids.Count,
                entries = rows
            });
        }

        [HttpPost("/admin/api/steam_allowlist/add")]
        public ContentResult AdminAddSteamAllowlist([FromForm] string steamId)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            if (!ulong.TryParse((steamId ?? "").Trim(), out ulong id) || id == 0)
                return Json(new { success = false, message = "Enter a valid SteamID64 (17 digits)." });

            bool added = SteamAllowlist.Add(id);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) "
                            + (added ? "added" : "re-added") + $" SteamID {id} to the allowlist");

            return Json(new
            {
                success = true,
                message = added ? $"SteamID {id} added." : $"SteamID {id} was already on the list.",
                active = SteamAllowlist.IsActive,
                count = SteamAllowlist.All().Count
            });
        }

        // ---- Normal-player whitelist (Steam ids that came through the launcher) ---------------

        [HttpGet("/admin/api/launcher_players")]
        public ContentResult AdminGetLauncherPlayers()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var all = LauncherPlayers.All();
            return Json(new
            {
                success = true,
                required = LauncherPlayers.Required,
                count = all.Count,
                entries = all.Select(e => new { steamId = e.SteamId, accountId = e.AccountId, username = e.Username, addedAt = e.AddedAt, lastSeen = e.LastSeen }),
            });
        }

        [HttpPost("/admin/api/launcher_players/remove")]
        public ContentResult AdminRemoveLauncherPlayer([FromForm] string steamId)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!ulong.TryParse((steamId ?? "").Trim(), out ulong id) || id == 0)
                return Json(new { success = false, message = "Enter a valid SteamID64." });
            bool removed = LauncherPlayers.Remove(id);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) removed SteamID {id} from the launcher whitelist");
            return Json(new { success = removed, message = removed ? $"SteamID {id} removed - it can't play on PC until it goes through the launcher again." : $"SteamID {id} was not on the list." });
        }

        [HttpPost("/admin/api/launcher_players/required")]
        public ContentResult AdminSetLauncherRequired([FromForm] bool on)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            LauncherPlayers.SetRequired(on);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) turned the launcher requirement for Steam logins {(on ? "ON" : "OFF")}");
            return Json(new { success = true, required = on, message = on
                ? "On: PC (Steam) players must have been through the Campus launcher (or be on the allowlist above)."
                : "Off: only the allowlist above gates Steam logins (empty = everyone)." });
        }

        [HttpPost("/admin/api/steam_allowlist/remove")]
        public ContentResult AdminRemoveSteamAllowlist([FromForm] string steamId)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            if (!ulong.TryParse((steamId ?? "").Trim(), out ulong id) || id == 0)
                return Json(new { success = false, message = "Enter a valid SteamID64." });

            bool removed = SteamAllowlist.Remove(id);
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) removed SteamID {id} from the allowlist");

            // Removing the last entry turns the whole check off - that is a big enough
            // behaviour change that the panel should say so out loud.
            bool nowOff = !SteamAllowlist.IsActive;
            return Json(new
            {
                success = removed,
                message = removed
                    ? (nowOff
                        ? $"SteamID {id} removed. The list is now EMPTY - every Steam user can connect."
                        : $"SteamID {id} removed.")
                    : $"SteamID {id} was not on the list.",
                active = SteamAllowlist.IsActive,
                count = SteamAllowlist.All().Count
            });
        }

        /// <summary>
        /// Ban Logs tab: the moderation log (ModerationLogDB) - every ban from every path (admin
        /// panel, report actions, staff in-game reports, chat commands, the upload guard), unbans,
        /// kicks, warnings, vote kicks and room bans - newest first, names and rooms resolved.
        /// ?kinds=ban,report-ban&amp;player=&lt;id or @username&gt;&amp;skip=0&amp;take=100
        /// </summary>
        [HttpGet("/admin/api/ban_logs")]
        public ContentResult AdminBanLogs()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;

            var kinds = Request.Query["kinds"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
            ulong? player = null;
            string p = Request.Query["player"].ToString().Trim().TrimStart('@');
            if (p.Length > 0)
            {
                if (ulong.TryParse(p, out var pid)) player = pid;
                else
                {
                    ulong byName = PlayerDB.GetPlayerIdByName(p);
                    if (byName == 0) return Json(new { success = false, message = $"No player named @{p}." });
                    player = byName;
                }
            }
            int.TryParse(Request.Query["skip"].ToString(), out int skip);
            if (!int.TryParse(Request.Query["take"].ToString(), out int take)) take = 100;

            var (rows, total) = ModerationLogDB.Query(kinds, player, skip, take);

            var names = new Dictionary<ulong, string>();
            string Name(ulong id)
            {
                if (id == 0) return "System";
                if (!names.TryGetValue(id, out var n)) names[id] = n = PlayerDB.GetPlayerNameById(id);
                return n;
            }
            var roomNames = new Dictionary<ulong, string>();
            string? RoomName(ulong id)
            {
                if (id == 0) return null;
                if (!roomNames.TryGetValue(id, out var n)) roomNames[id] = n = RoomDB.GetRoom(id)?.Name ?? $"room {id}";
                return n;
            }

            return Json(new
            {
                success = true,
                total,
                skip,
                rows = rows.Select(e => new
                {
                    id = e.Id,
                    kind = e.Kind,
                    at = DateTime.SpecifyKind(e.At, DateTimeKind.Utc),
                    targetId = e.TargetId,
                    target = Name(e.TargetId),
                    byAccountId = e.ByAccountId,
                    by = Name(e.ByAccountId),
                    roomId = e.RoomId,
                    room = RoomName(e.RoomId),
                    durationSeconds = e.DurationSeconds,
                    permanent = e.Kind.Contains("ban") && e.Kind != "roomunban" && e.Kind != "unban"
                                && (e.DurationSeconds <= 0 || e.DurationSeconds == int.MaxValue),
                    category = Enum.IsDefined(typeof(ReportCategory), e.Category) ? ((ReportCategory)e.Category).ToString() : e.Category.ToString(),
                    message = e.Message,
                    note = e.Note
                })
            });
        }

        [HttpGet("/admin/api/get_bans")]
        public ContentResult AdminGetBans()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string rawId = Request.Query["playerId"].ToString();
            if (!ulong.TryParse(rawId, out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            var block = acc.player_Extra?.ModerationBlockDetails;
            bool accountBanned = PlayerDB.HasActiveModerationBlock(playerId);
            string? deviceId = PlayerDB.GetDeviceId(playerId);

            // Decrypt IP entries for display. An admin managing IP bans has to be able to
            // see WHICH address is blocked - otherwise an over-broad ban (shared house,
            // CGNAT, mobile carrier) is invisible and impossible to reason about.
            var entries = BanDB.ForPlayer(playerId).Select(b => new
            {
                b.Type,
                Value = b.Type == BanDB.Ip ? SafeDecrypt(b.Value) : b.Value,
                b.Reason,
                b.CreatedAt,
                b.ExpiresAt,
                Permanent = b.ExpiresAt == 0
            }).ToList();

            return Json(new
            {
                success = true,
                playerId,
                username = acc.player.Username,
                accountBanned,
                banMessage = block?.Message,
                deviceId,
                deviceBanned = BanDB.IsDeviceBanned(deviceId),
                knownIps = (PlayerDB.GetDecryptedIpAddresses(playerId) ?? new List<string>()).Count,
                entries
            });
        }

        [HttpPost("/admin/api/kick_player")]
        public ContentResult KickPlayer()
        {
                var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
                if (error != null)
                    return error;

                string playerid = HttpContext.Request.Form["playerId"].ToString();

                if (!ulong.TryParse(playerid, out ulong playerId))
                {
                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            success = false,
                            message = "Invalid player ID"
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                if (playerId == account.Id)
                {
                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            success = false,
                            message = "You can't kick yourself!"
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }
                // Re-enabled: this was dead (commented out) before kick_player was ever reachable by anyone
                // but a dev, which made it low-stakes. Now that a mod can reach this too, the same
                // "can't act on another dev" rule the ban endpoints already enforce belongs here as well.
                if (PlayerDB.CheckDevFlag(playerId))
                {
                    return new ContentResult()
                    {
                        Content = "{\"success\":false,\"message\":\"You can't kick developers.\"}",
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                PlayerDB.Set_moderation_block(
                    playerId,
                    ReportCategory.Moderator,
                    duration: 1,
                    gameSessionId: -1,
                    isHostKick: true,
                    message: "You have been kicked.",
                    playerIdReporter: null,
                    isBan: false,
                    bannedByPlayerId: account.Id,
                    logKind: "kick"
                );
                var acc = PlayerDB.GetFullAccount(playerId);
                // Was hardcoded "[DEV]" - now that a mod can reach this endpoint too (ValidateTokenDevOrMod
                // above), that tag would misreport who actually did it.
                string actorTag = PlayerDB.CheckDevFlag(account.Id) ? "[DEV]" : "[MOD]";
            Webhook.Send(
                    account.Id,
                    "modacc",
                    "Reporting info - Player",
                    $"({account.Id}) @{account.Username} {actorTag} kicked player ({playerId}) @{acc.player.Username} {acc.player.DisplayName}",
                    5898927
                );

                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "Kicked player!"
                    }),
                    ContentType = "application/json"
                };

        }

        /// <summary>A mod-only quick action: a fixed 10-minute block, no dialog, no duration picker - the
        /// "Timeout" button on the Players table row (see HomeV4.html's applyRoleUI/MOD_TIMEOUT_BTN). Devs
        /// have the full Ban dialog for anything longer; this exists because a mod does NOT get that dialog
        /// (Edit Player / the role-toggle buttons stay dev-only), just this one bounded action. Logged with
        /// logKind "report-timeout" - already a recognized value in the Ban Logs filter ("Bans & timeouts")
        /// that nothing was actually writing yet.</summary>
        [HttpPost("/admin/api/timeout_player")]
        public ContentResult AdminTimeoutPlayer()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;

            if (!ulong.TryParse(Request.Form["playerId"].ToString(), out ulong playerId))
                return Json(new { success = false, message = "Invalid player id." });
            if (playerId == account!.Id)
                return Json(new { success = false, message = "You can't time out yourself." });
            if (PlayerDB.CheckDevFlag(playerId))
                return Json(new { success = false, message = "Can't time out this player." });

            var acc = PlayerDB.GetFullAccount(playerId);
            if (acc?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            const int TimeoutSeconds = 600;   // 10 minutes
            PlayerDB.Set_moderation_block(
                playerId,
                ReportCategory.Moderator,
                duration: TimeoutSeconds,
                gameSessionId: -1,
                isHostKick: false,
                message: "You have been timed out for 10 minutes.",
                playerIdReporter: null,
                isBan: true,
                bannedByPlayerId: account.Id,
                logKind: "report-timeout"
            );
            string actorTag2 = PlayerDB.CheckDevFlag(account.Id) ? "[DEV]" : "[MOD]";
            Webhook.Send(
                account.Id,
                "modacc",
                "Reporting info - Player",
                $"({account.Id}) @{account.Username} {actorTag2} timed out player ({playerId}) @{acc.player.Username} for 10 minutes",
                5898927
            );
            Console.WriteLine($"[admin] {account.Username} ({account.Id}) timed out {playerId} (@{acc.player.Username}) for 10 minutes");

            return Json(new { success = true, message = $"@{acc.player.Username} is timed out for 10 minutes." });
        }

        [HttpGet("/admin/api/account/bulk")]
        public ContentResult AccountBulk([FromQuery] List<ulong> id)
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = PlayerDB.GetAccountsBulkAdmin(id),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        /// <summary>
        /// Force-complete Orientation for one player. For the "stuck in Orientation on Rec Center"
        /// case: the client walks every Recroom.AccountCreation.* flag plus TUTORIAL_COMPLETE_MASK
        /// and re-runs Orientation on the first "false" it sees, so leaving any of them unset gets
        /// a player ping-ponged back to Orientation every time they enter ^RecCenter. This sets all
        /// of them at once (same as /acc/account/me/skip-orientation but for a chosen player id).
        /// Form: id = the player's account id OR username (with or without the @).
        /// </summary>
        [HttpPost("/admin/api/account/complete-orientation")]
        public ContentResult AdminCompleteOrientation([FromForm] string id)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            string s = (id ?? "").Trim().TrimStart('@');
            if (string.IsNullOrEmpty(s))
                return Json(new { success = false, message = "Enter a player id or @username." });

            ulong playerId = 0;
            if (!ulong.TryParse(s, out playerId))
            {
                var byName = PlayerDB.GetFullAccountByName(s);
                if (byName?.player != null) playerId = (ulong)byName.player.Id;
            }
            if (playerId == 0)
                return Json(new { success = false, message = $"No player called @{s}." });

            bool changed = SettingsDB.MarkOrientationComplete(playerId);
            Console.WriteLine($"[orientation] {admin!.Username} ({admin.Id}) marked orientation complete for {playerId} (changed={changed})");
            return Json(new
            {
                success = true,
                playerId = playerId,
                changed = changed,
                message = changed
                    ? $"Orientation marked complete for {playerId}. Have them restart Campus."
                    : $"Orientation was already complete for {playerId}."
            });
        }

        /// <summary>
        /// Read / toggle ConfigCampus.ForceCdnFallback (the "error code summer" kill switch).
        /// GET returns the current state; POST with form "on" (true / false) flips it. See the
        /// comment on ConfigCampus.ForceCdnFallback - this is the admin's one-button response to
        /// a wave of "error code summer" reports on connect.
        /// </summary>
        [HttpGet("/admin/api/cdn/force-fallback")]
        public ContentResult AdminGetForceCdnFallback()
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            return Json(new
            {
                success = true,
                forceFallback = ConfigCampus.ForceCdnFallback,
                currentCdn = ConfigCampus.ForceCdnFallback ? ConfigCampus.CdnFallbackUrl : Campus_live_rewrite.api.server.CdnHost.Current,
                cdnBaseUrl = ConfigCampus.CdnBaseUrl,
                cdnFallbackUrl = ConfigCampus.CdnFallbackUrl
            });
        }

        [HttpPost("/admin/api/cdn/force-fallback")]
        public ContentResult AdminSetForceCdnFallback([FromForm] bool on)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            ConfigCampus.ForceCdnFallback = on;
            Console.WriteLine($"[cdn-host] {admin!.Username} ({admin.Id}) turned ForceCdnFallback {(on ? "ON" : "OFF")} - advertising {(on ? ConfigCampus.CdnFallbackUrl : Campus_live_rewrite.api.server.CdnHost.Current)}");
            return Json(new
            {
                success = true,
                forceFallback = on,
                message = on
                    ? $"On: /ns now advertises the fallback CDN ({ConfigCampus.CdnFallbackUrl}). Stops 'error code summer' at the cost of clothing-bundle caching. Players rejoin to pick it up."
                    : $"Off: /ns advertises the probed CDN ({Campus_live_rewrite.api.server.CdnHost.Current}) again."
            });
        }

        [HttpPost("/admin/api/send_annoucement")]
        public ContentResult AdminAPISendAnnoucement([FromForm] string message, [FromForm] ulong playerId)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null)
                return error;

            var response = WebsocketEvents.createResponse_msg(message, playerId);
            Notifications.SendToAll(JsonConvert.SerializeObject(response));

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "sent!"
                }),
                ContentType = "application/json"
            };
        }

        [HttpPost("/admin/api/{roomId}/{subroomId}/set_room_datablob")]
        public ContentResult set_room_datablob([FromForm] string dataBlob, ulong roomId, ulong subroomId)
        {
            /*var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;*/

            try 
            {
                var e = RoomDB.SetRoomSubroomRoomFile(roomId, subroomId, dataBlob, "", 1, currentplatform: Platforms.All);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        message = "unknown error!"
                    }),
                    ContentType = "application/json"
                };
            }

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "success!"
                }),
                ContentType = "application/json"
            };
        }


        // =====================================================================================
        //  REPORTS  and  STAFF ROLES
        //
        //  Neither existed. Player reports had nowhere to go (the client's
        //  /api/PlayerReporting/v3/create route was not implemented, see APIController) and there
        //  was no way at all to make someone a moderator - DevFlag.mod was in the enum, and
        //  PlayerDB.CheckModFlag read it, but nothing could ever SET it. That is why assigning a
        //  mod from the admin panel did nothing: there was no endpoint behind the button.
        // =====================================================================================

        /// <summary>
        /// The report queue. Open reports by default, newest first, each enriched with the two
        /// players' names and the reported player's prior-report count so a repeat offender is
        /// obvious without opening every row.
        /// </summary>
        [HttpGet("/admin/api/reports")]
        public ContentResult AdminReports()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;

            bool openOnly = !Truthy(Request.Query["all"]);
            int skip = int.TryParse(Request.Query["skip"], out var sk) ? sk : 0;
            int take = int.TryParse(Request.Query["take"], out var tk) ? tk : 100;
            ulong playerId = ulong.TryParse(Request.Query["playerId"], out var pid) ? pid : 0;

            string NameOf(ulong id)
            {
                if (id == 0) return "";
                var a = PlayerDB.GetFullAccount(id);
                if (a?.player == null) return $"#{id}";
                return string.IsNullOrWhiteSpace(a.player.DisplayName) ? a.player.Username ?? $"#{id}" : a.player.DisplayName;
            }

            var rows = Campus_live_rewrite.api.dbs.ReportsDB.List(openOnly, skip, take, playerId)
                .Select(r => new
                {
                    r.Id,
                    r.ReporterPlayerId,
                    ReporterName = NameOf(r.ReporterPlayerId),
                    r.ReportedPlayerId,
                    ReportedName = NameOf(r.ReportedPlayerId),
                    r.ReportCategory,
                    CategoryName = !string.IsNullOrEmpty(r.Reason) ? r.Reason
                        : Enum.IsDefined(typeof(Moderation_Detail.ReportCategory), r.ReportCategory)
                        ? ((Moderation_Detail.ReportCategory)r.ReportCategory).ToString()
                        : $"Unknown({r.ReportCategory})",
                    r.Details,
                    // website reports: where it came from and a link to the reported thing
                    Source = r.Source ?? "game",
                    r.Subject,
                    r.SubjectId,
                    r.SubjectLink,
                    r.RoomId,
                    RoomName = r.RoomId != null ? (RoomDB.GetRoom(r.RoomId.Value)?.Name ?? "") : "",
                    r.RoomInstanceType,
                    r.HeightReporter,
                    r.HeightReported,
                    r.CreatedAt,
                    r.Resolved,
                    r.Action,
                    r.ResolvedByPlayerId,
                    ResolvedByName = NameOf(r.ResolvedByPlayerId),
                    r.ResolvedAt,
                    r.ModeratorNote,
                    // How many times this player has been reported, ever - the number that tells
                    // you whether one complaint is a pattern.
                    PriorReports = Campus_live_rewrite.api.dbs.ReportsDB.CountAgainst(r.ReportedPlayerId),
                    // Get_moderation_block returns a JSON string (never null), so "!= null" flagged
                    // every reported player as already banned.
                    ReportedIsBanned = PlayerDB.HasActiveModerationBlock(r.ReportedPlayerId)
                })
                .ToList();

            // camelCase: the Reports tab reads r.id / r.reportedName / r.createdAt. Sent with C#
            // names every card rendered "undefined" and its Ban/Warn/Dismiss posted reportId=undefined.
            return JsonCamel(new { success = true, openCount = Campus_live_rewrite.api.dbs.ReportsDB.CountOpen(), reports = rows });
        }

        /// <summary>
        /// Act on one report. `action` is dismiss | warn | ban.
        ///
        /// A ban goes through PlayerDB.Set_moderation_block - the SAME path /admin/api/ban_player
        /// uses - rather than a second ban mechanism living behind the report queue. Acting on a
        /// player also resolves their other open reports, so a pile-on does not leave nine rows
        /// describing the thing you just dealt with.
        /// </summary>
        [HttpPost("/admin/api/report_action")]
        public ContentResult AdminReportAction()
        {
            var (account, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;

            if (!long.TryParse(Request.Form["reportId"].ToString(), out long reportId))
                return Json(new { success = false, message = "Invalid report id." });

            var report = Campus_live_rewrite.api.dbs.ReportsDB.Get(reportId);
            if (report == null)
                return Json(new { success = false, message = $"No report #{reportId}." });

            string action = (Request.Form["action"].ToString() ?? "").Trim().ToLowerInvariant();
            string note = (Request.Form["note"].ToString() ?? "").Trim();
            ulong target = report.ReportedPlayerId;

            switch (action)
            {
                case "dismiss":
                    Campus_live_rewrite.api.dbs.ReportsDB.Resolve(reportId, account.Id, "dismissed", note);
                    return Json(new { success = true, message = $"Report #{reportId} dismissed." });

                case "warn":
                {
                    // A warning is delivered as a zero-duration moderation block: the client shows
                    // the block screen with the message and then lets them straight back in. That
                    // is how the real service words a warning, and it reuses the one delivery path.
                    string wmsg = string.IsNullOrWhiteSpace(note) ? "You have received a warning from a moderator." : note;
                    PlayerDB.Set_moderation_block(target, (Moderation_Detail.ReportCategory)report.ReportCategory,
                        0, gameSessionId: -1, isHostKick: false, message: wmsg,
                        playerIdReporter: report.ReporterPlayerId, isBan: false, bannedByPlayerId: account.Id,
                        logKind: "warning", logNote: $"from report #{reportId}");
                    int n = Campus_live_rewrite.api.dbs.ReportsDB.ResolveAllAgainst(target, account.Id, "warned", note);
                    return Json(new { success = true, message = $"Warned {target}. {n} report(s) closed." });
                }

                case "ban":
                {
                    if (PlayerDB.CheckDevFlag(target))
                        return Json(new { success = false, message = "Cannot ban this player." });

                    // The Reports tab sends MINUTES (60 / 1440 / 10080 / 43200) and 0 for permanent;
                    // the block is in SECONDS with int.MaxValue as permanent. Passed through raw, a
                    // "1 day" ban lasted 24 minutes and a "permanent" one (0 s) was no ban at all.
                    if (!int.TryParse(Request.Form["duration"].ToString(), out int minutes) || minutes < 0)
                        minutes = 0;
                    int duration = minutes == 0 ? int.MaxValue : (int)Math.Min(int.MaxValue - 1L, minutes * 60L);

                    string bmsg = string.IsNullOrWhiteSpace(note) ? "Banned following a player report." : note;
                    var category = (Moderation_Detail.ReportCategory)report.ReportCategory;

                    PlayerDB.Set_moderation_block(target, category, duration,
                        gameSessionId: -1, isHostKick: false, message: bmsg,
                        playerIdReporter: report.ReporterPlayerId, isBan: true, bannedByPlayerId: account.Id,
                        logKind: "report-ban", logNote: $"from report #{reportId}");

                    int n = Campus_live_rewrite.api.dbs.ReportsDB.ResolveAllAgainst(target, account.Id, "banned", note);
                    Console.WriteLine($"[report] {account.Id} banned {target} from report #{reportId} ({(minutes == 0 ? "permanent" : minutes + " min")})");
                    return Json(new { success = true, message = $"Banned {target}. {n} report(s) closed." });
                }

                default:
                    return Json(new { success = false, message = $"Unknown action '{action}'. Use dismiss, warn or ban." });
            }
        }

        /// <summary>
        /// Staff: everyone holding a moderator / developer / screenshare flag. This is the "vmod
        /// list" - there was no way to see or edit it.
        /// </summary>
        [HttpGet("/admin/api/staff")]
        public ContentResult AdminStaff()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            var staff = col.FindAll()
                .Where(p => p.dev_Flag != DevFlag.none && p.player != null)
                .OrderBy(p => p.player!.Id)
                .Select(p => new
                {
                    // camelCase: the panel reads p.accountId / p.username / p.isModerator
                    accountId = p.player!.Id,
                    username = p.player.Username,
                    displayName = p.player.DisplayName,
                    isModerator = (p.dev_Flag & DevFlag.mod) == DevFlag.mod,
                    isDeveloper = (p.dev_Flag & DevFlag.dev) == DevFlag.dev,
                    isScreenshare = (p.dev_Flag & DevFlag.screenshare) == DevFlag.screenshare,
                    isSupport = (p.dev_Flag & DevFlag.support) == DevFlag.support,
                    flags = (int)p.dev_Flag
                })
                .ToList();

            return Json(new { success = true, staff });
        }

        /// <summary>
        /// Grant or revoke staff flags. Send only the ones you mean to change - an absent field is
        /// left alone, so ticking "moderator" cannot silently strip someone's dev flag.
        ///
        /// Developer is deliberately harder to hand out than moderator: it is the flag
        /// AuthHelper.ValidateTokenDev gates this whole admin panel on, so granting it hands over
        /// the panel itself. Revoking your OWN developer flag is refused - locking yourself out of
        /// the admin panel with a mis-click is not a recoverable mistake from inside the panel.
        /// </summary>
        [HttpPost("/admin/api/set_roles")]
        public ContentResult AdminSetRoles()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            if (!ulong.TryParse(Request.Form["playerId"].ToString(), out ulong playerId) || playerId == 0)
                return Json(new { success = false, message = "Invalid player id." });

            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            var rec = col.FindOne(x => x.id == playerId);
            if (rec?.player == null)
                return Json(new { success = false, message = $"No player with id {playerId}." });

            var before = rec.dev_Flag;
            var flags = rec.dev_Flag;
            var changed = new List<string>();

            // An absent form field means "leave it"; only a present one flips a bit.
            void Apply(string field, DevFlag bit, string label)
            {
                string raw = Request.Form[field].ToString();
                if (string.IsNullOrEmpty(raw)) return;

                bool want = Truthy(raw);
                bool has = (flags & bit) == bit;
                if (want == has) return;

                if (bit == DevFlag.dev && !want && playerId == account.Id)
                {
                    changed.Add("developer NOT removed (you cannot revoke your own admin access)");
                    return;
                }

                flags = want ? (flags | bit) : (flags & ~bit);
                changed.Add($"{label} {(want ? "granted" : "removed")}");
            }

            Apply("moderator", DevFlag.mod, "moderator");
            Apply("developer", DevFlag.dev, "developer");
            Apply("screenshare", DevFlag.screenshare, "screenshare");
            Apply("support", DevFlag.support, "support");

            if (flags == before)
                return Json(new { success = true, message = changed.Count > 0 ? string.Join("; ", changed) : "Nothing to change.", flags = (int)flags });

            rec.dev_Flag = flags;
            col.Update(rec);

            // The roles are baked into the JWT at mint time (ClientSecurity), so a player holding
            // a live token keeps the old set until it is re-issued. Say so rather than letting a
            // moderator wonder why their new powers have not appeared.
            Console.WriteLine($"[roles] {account.Id} changed {playerId}: {before} -> {flags} ({string.Join("; ", changed)})");

            // Take effect NOW: the player's client is told to refresh its login (frame 5,
            // RefreshLogin) so it re-mints a token carrying the new roles - the moderator menu
            // (warn / kick / room ban) and their vmod tag appear without a relog. Everyone else's
            // view of their tag reads /Auth/role/moderator, which is the DB - already current.
            try
            {
                Notifications.FireAndForget(Notifications.SendToPlayer(playerId, JsonConvert.SerializeObject(new WebsocketEvents.Reponse
                {
                    Id = ((int)WebsocketEvents.ResponseResults.RefreshLogin).ToString(),
                    Msg = new { Reason = "roles changed" }
                })), "roles refresh-login");
                Notifications.FireAndForget(Notifications.RefreshAccount(playerId), "roles account refresh");
            }
            catch { }
            // ...and everyone ELSE's copy of their nametag badge: other clients only read
            // /Auth/role/* when the player first appears, so a staff badge granted mid-session
            // stayed hidden until a rejoin. Same broadcast the influencer / badge toggles use.
            PushEvent(playerId, Campus_live_rewrite.api.WebsocketEvents.CreateResponseReputationUpdate(playerId), "roles/badge", toAll: true);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(playerId), "roles account broadcast");
            return Json(new
            {
                success = true,
                message = string.Join("; ", changed) + ". Applied; their client has been told to refresh its login (a relog also does it).",
                flags = (int)flags
            });
        }

        string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return $"{len:0.0} {sizes[order]}";
        }
    }
}
