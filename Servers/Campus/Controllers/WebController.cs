using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.server.Classes.db_class;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.dbs;
using System.ComponentModel.DataAnnotations;

namespace Campus_live_rewrite.Controllers
{
    public class WebController : ControllerBase
    {
        // "/" is shared between two callers that want completely different things:
        //
        //   - the GAME CLIENT treats "/" as the NameServer endpoint and rejects anything that
        //     isn't a valid NS DTO with FAILED_TO_CONNECT_CODE_LOCKER (=4, "Failed to connect
        //     to RecNet, error code: locker"). This is load-bearing: get it wrong and NOBODY
        //     can connect.
        //   - a BROWSER hitting camprec.net should get the Campus website.
        //
        // They are told apart by the request itself: BestHTTP (the client's HTTP stack) does
        // not ask for text/html. The test is deliberately biased - anything that isn't clearly
        // a browser gets the NS JSON, so an unrecognised client fails safe rather than being
        // handed a web page it cannot parse. /home and /site still serve the old landing page.
        //
        // NOTE: this is also why the site is NOT mounted with UseDefaultFiles in Program.cs.
        // That would serve Website/index.html for "/" inside the static-file middleware,
        // before routing ever reaches this action, and break every client.
        [Route("/")]
        public IActionResult NsRoot()
        {
            if (WantsHtml(Request))
            {
                var index = Path.Combine(Environment.CurrentDirectory, "Website", "index.html");
                if (System.IO.File.Exists(index))
                    return PhysicalFile(index, "text/html");
            }

            return ns(Version: "20250731");
        }

        /// <summary>
        /// True only for a request that is unambiguously a web browser asking for a page.
        /// Anything else - the game client, a probe, a bare curl - is treated as the game.
        /// </summary>
        private static bool WantsHtml(HttpRequest req)
        {
            if (!HttpMethods.IsGet(req.Method)) return false;

            string ua = req.Headers.UserAgent.ToString();
            if (ua.Contains("BestHTTP", StringComparison.OrdinalIgnoreCase)) return false;

            return req.Headers.Accept.ToString()
                      .Contains("text/html", StringComparison.OrdinalIgnoreCase);
        }

        [Route("/home")]
        [Route("/site")]
        public IActionResult Index()
        {
            var filePath = Path.Combine(Environment.CurrentDirectory, "Web", "Index.html");

            if (!System.IO.File.Exists(filePath))
                return NotFound();

            return PhysicalFile(filePath, "text/html");
        }

        // ---- Clean website URLs (no .html) --------------------------------------------------
        // The public site is served as static files (profile.html, room.html, …), but the links
        // people see and share should be clean: /user/kyle, /room/{id}, /image/{id}, /news, etc.
        // Each clean path just serves the matching HTML file; the page's JS reads the id/handle
        // from location.pathname (see app.js routeParam()). The game client never requests these
        // paths (they carry no NS meaning), so there is no browser-vs-game split to make here.
        private IActionResult SitePage(string file)
        {
            var path = Path.Combine(Environment.CurrentDirectory, "Website", file);
            if (!System.IO.File.Exists(path)) return NotFound();

            // These pages are served at a CLEAN path (/user/kyle, /room/{id}, …). Their asset refs
            // (app.js, style.css, assets/…) are RELATIVE, so at /user/kyle the browser would look
            // for /user/app.js and 404 - the page couldn't even load its own JS. Inject
            // <base href="/"> right after <head> so every relative URL resolves from the site root,
            // regardless of the clean path's depth. (Static /profile.html serving is untouched.)
            var html = System.IO.File.ReadAllText(path);
            if (html.IndexOf("<base ", StringComparison.OrdinalIgnoreCase) < 0)
            {
                int i = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) html = html.Insert(i + "<head>".Length, "\n  <base href=\"/\">");
            }
            return Content(html, "text/html; charset=utf-8");
        }

        // True when the request carries a valid website session cookie (campus_site). Mirrors
        // SiteContentController.CurrentUser - a lightweight decode, no DB hit.
        private bool IsSignedIn()
        {
            string token = Request.Cookies["campus_site"] ?? "";
            if (string.IsNullOrEmpty(token)) return false;
            // Require the "website" surface (the site cookie is minted with it). Without this the
            // default DecodeToken now refuses website tokens, so /profile and /settings would
            // redirect to /login even when the visitor is signed in.
            string data = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_")) return false;
            try { var u = JsonConvert.DeserializeObject<player_info>(data); return u != null && u.Id != 0; }
            catch { return false; }
        }

        private const string SiteBase = "https://camprec.net";

        // Serve a site page with resource-specific OpenGraph/Twitter-card meta injected, so pasting
        // the link in Discord/social embeds the image/profile/room. Reads the page, drops any
        // existing og:/twitter: meta (so ours win - a duplicate og:image confuses crawlers), and
        // injects title/description/image + a <base href="/"> for the clean path. Humans still get
        // the full SPA; crawlers (which don't run JS) read the injected meta.
        private IActionResult SitePageOg(string file, string title, string? description, string? imageAbsUrl, string urlPath, bool largeImage)
        {
            var path = Path.Combine(Environment.CurrentDirectory, "Website", file);
            if (!System.IO.File.Exists(path)) return NotFound();
            var html = System.IO.File.ReadAllText(path);

            if (html.IndexOf("<base ", StringComparison.OrdinalIgnoreCase) < 0)
            {
                int b = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                if (b >= 0) html = html.Insert(b + "<head>".Length, "\n  <base href=\"/\">");
            }

            html = System.Text.RegularExpressions.Regex.Replace(
                html,
                "\\s*<meta[^>]*(property=\"og:[^\"]*\"|name=\"twitter:[^\"]*\")[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var sb = new System.Text.StringBuilder();
            sb.Append("\n  <meta property=\"og:site_name\" content=\"Campus\">");
            sb.Append($"\n  <meta property=\"og:title\" content=\"{Esc(title)}\">");
            sb.Append($"\n  <meta property=\"og:url\" content=\"{Esc(SiteBase + urlPath)}\">");
            sb.Append("\n  <meta property=\"og:type\" content=\"website\">");
            if (!string.IsNullOrWhiteSpace(description))
                sb.Append($"\n  <meta property=\"og:description\" content=\"{Esc(description)}\">");
            if (!string.IsNullOrWhiteSpace(imageAbsUrl))
            {
                sb.Append($"\n  <meta property=\"og:image\" content=\"{Esc(imageAbsUrl)}\">");
                sb.Append($"\n  <meta name=\"twitter:image\" content=\"{Esc(imageAbsUrl)}\">");
            }
            sb.Append($"\n  <meta name=\"twitter:card\" content=\"{(largeImage ? "summary_large_image" : "summary")}\">");
            sb.Append($"\n  <meta name=\"twitter:title\" content=\"{Esc(title)}\">");
            if (!string.IsNullOrWhiteSpace(description))
                sb.Append($"\n  <meta name=\"twitter:description\" content=\"{Esc(description)}\">");

            int i = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) html = html.Insert(i + "<head>".Length, sb.ToString());
            return Content(html, "text/html; charset=utf-8");
        }

        private static string? ImgAbs(string? name) =>
            string.IsNullOrWhiteSpace(name) || name == "DefaultPFP.png"
                ? null
                : SiteBase + "/img/" + name;

        // A small styled "X does not exist" page (404) for a slug that resolves to nothing.
        private IActionResult DoesNotExist(string entity)
        {
            string e = System.Net.WebUtility.HtmlEncode(entity);
            string html =
                "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                $"<title>{e} not found - Campus</title>" +
                $"<meta property=\"og:title\" content=\"{e} does not exist\">" +
                "<meta property=\"og:site_name\" content=\"Campus\">" +
                "<style>body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
                "background:#14121a;color:#eee;font-family:Inter,system-ui,sans-serif;text-align:center}" +
                ".c{padding:40px}h1{font-size:28px;margin:0 0 8px}p{opacity:.75;margin:0 0 20px}" +
                "a{color:#ff5a3c;text-decoration:none;font-weight:600}</style></head>" +
                $"<body><div class=\"c\"><h1>{e} does not exist</h1>" +
                $"<p>We couldn't find that {e.ToLower()} on Campus.</p>" +
                "<a href=\"/\">&larr; Back to home</a></div></body></html>";
            return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = 404 };
        }

        // Profile URLs are by USERNAME, never the account id:
        //   - a real username         -> the profile (with OG embed);
        //   - a bare account id        -> 302 REDIRECT to that account's /user/<username>, so the URL
        //                                 bar shows the name, not the id;
        //   - anything else            -> "Player does not exist".
        [HttpGet("/user/{handle}")]
        public IActionResult UserPage(string handle)
        {
            var full = PlayerDB.GetFullAccountByName(handle);
            if (full?.player != null)
            {
                string title = $"{full.player.DisplayName} (@{full.player.Username})";
                string? bio = full.player_Extra?.Bio;
                string desc = string.IsNullOrWhiteSpace(bio) ? "View this player's Campus profile." : bio!;
                string img = ImgAbs(full.player.profileImage) ?? (SiteBase + "/img/DefaultProfileImage.jpg");
                return SitePageOg("profile.html", title, desc, img, "/user/" + Uri.EscapeDataString(handle), largeImage: false);
            }

            // Not a username. If it's a real account id, send them to the canonical @name URL.
            if (ulong.TryParse(handle, out var byId))
            {
                var acc = PlayerDB.GetFullAccount(byId);
                if (!string.IsNullOrEmpty(acc?.player?.Username))
                    return Redirect("/user/" + Uri.EscapeDataString(acc.player.Username));
            }

            return DoesNotExist("Player");
        }

        // Your OWN profile requires a session - signed out it would only show placeholder
        // ("Username" / "@handle"), so send it to the login page instead.
        [HttpGet("/profile")]
        public IActionResult ProfilePage() => IsSignedIn() ? SitePage("profile.html") : Redirect("/login");

        // Room URLs are by NAME, never the room id:
        //   - a real room name  -> the room page (with OG embed);
        //   - a bare room id     -> 302 REDIRECT to that room's /room/<name>, so the URL shows the name;
        //   - anything else      -> "Room does not exist".
        [HttpGet("/room/{id}")]
        public IActionResult RoomPage(string id)
        {
            var room = RoomDB.GetRoomByName(id);
            if (room != null && room.IsDorm) return DoesNotExist("Room");   // dorms are never web pages
            if (room != null)
            {
                string title = (room.Name ?? "Room").TrimStart('^');
                return SitePageOg("room.html", title, room.Description, ImgAbs(room.ImageName),
                                  "/room/" + Uri.EscapeDataString(id), largeImage: true);
            }

            if (ulong.TryParse(id, out var rid))
            {
                var byId = RoomDB.GetRoom(rid);
                if (byId != null && byId.IsDorm) return DoesNotExist("Room");
                if (!string.IsNullOrEmpty(byId?.Name))
                    return Redirect("/room/" + Uri.EscapeDataString(byId.Name.TrimStart('^')));
            }

            return DoesNotExist("Room");
        }

        // An event's page: /event/{numeric id}. Public events (or your own) only; anything else is "does not exist".
        [HttpGet("/event/{id}")]
        public IActionResult EventPage(string id)
        {
            if (!ulong.TryParse(id, out var eid)) return DoesNotExist("Event");
            var ev = EventDB.GetFullEventById(eid);
            if (ev == null) return DoesNotExist("Event");
            bool mine = false;
            try
            {
                string token = Request.Cookies["campus_site"] ?? "";
                string data = string.IsNullOrEmpty(token) ? "" : ClientSecurity.DecodeToken(token, requireSurface: "website");
                if (!string.IsNullOrEmpty(data) && !data.StartsWith("error_"))
                {
                    var u = JsonConvert.DeserializeObject<player_info>(data);
                    mine = u != null && u.Id != 0 && ev.CreatorPlayerId == (ulong)u.Id;
                }
            }
            catch { }
            if (ev.Accessibility != Campus_live_rewrite.api.server.Classes.db_class.EventDBClasses.PlayerEventAccessibility.Public && !mine)
                return DoesNotExist("Event");
            string? imageName = ev.ImageName;
            if (string.IsNullOrWhiteSpace(imageName)) { try { imageName = RoomDB.GetRoom(ev.RoomId)?.ImageName; } catch { } }
            return SitePageOg("event.html", string.IsNullOrWhiteSpace(ev.Name) ? "Event" : ev.Name!, ev.Description, ImgAbs(imageName),
                              "/event/" + Uri.EscapeDataString(id), largeImage: true);
        }

        [HttpGet("/image/{id}")]
        public IActionResult ImagePage(string id)
        {
            string title = "Campus Photo";
            string? desc = null, img = null;
            // ids can be a bare number or an old "user__id" compound - take the numeric tail.
            var tail = id.Contains("__") ? id.Substring(id.LastIndexOf("__") + 2) : id;
            if (int.TryParse(tail, out int imageId))
            {
                var meta = ImageMetadataDB.GetImagesByIds(new List<int> { imageId })?.FirstOrDefault();
                if (meta != null)
                {
                    img = ImgAbs(meta.ImageName);
                    desc = meta.Description;
                    var author = PlayerDB.GetAccountById(meta.PlayerId);
                    if (author != null) title = $"Photo by @{author.Username}";
                }
            }
            return SitePageOg("image.html", title, desc, img, "/image/" + Uri.EscapeDataString(id), largeImage: true);
        }

        [HttpGet("/news")]
        public IActionResult NewsPage() => SitePage("news.html");

        [HttpGet("/rooms")]
        public IActionResult RoomsPage() => SitePage("rooms.html");

        [HttpGet("/patch-notes")]
        public IActionResult PatchNotesPage() => SitePage("patch-notes.html");

        // Support replaced the old Weekly page. /weekly redirects so any old link still lands here.
        // Deep links (/support/ticket/{id}, /support/logs) all serve the same page; support.html's
        // JS reads the path and opens the right ticket / the staff logs view.
        [HttpGet("/support")]
        [HttpGet("/support/ticket/{id:int}")]
        [HttpGet("/support/logs")]
        public IActionResult SupportPage() => SitePage("support.html");

        [HttpGet("/weekly")]
        public IActionResult WeeklyPage() => Redirect("/support");

        // Weekly challenges: this week's five, your progress and the reward (WeeklyChallengeController).
        [HttpGet("/challenges")]
        public IActionResult ChallengesPage() => SitePage("challenges.html");

        // Settings is entirely your-account controls - useless signed out, so gate it too.
        [HttpGet("/settings")]
        public IActionResult SettingsPage() => IsSignedIn() ? SitePage("settings.html") : Redirect("/login");

        // Stats is the signed-in creator's own numbers - signed out there is nothing to show.
        [HttpGet("/stats")]
        public IActionResult StatsPage() => IsSignedIn() ? SitePage("stats.html") : Redirect("/login");

        // DMs / group chats (SiteChatController) - the same threads the game client sees. Signed-out has no one to be messaged as.
        [HttpGet("/messages")]
        public IActionResult MessagesPage() => IsSignedIn() ? SitePage("messages.html") : Redirect("/login");

        [HttpGet("/login")]
        public IActionResult LoginPage() => SitePage("login.html");

        [HttpGet("/download")]
        public IActionResult DownloadPage() => SitePage("download.html");

        [HttpGet("/accessibility")]
        public IActionResult AccessibilityPage() => SitePage("accessibility.html");

        [HttpGet("/signup")]
        public IActionResult SignupPage() => IsSignedIn() ? Redirect("/profile") : SitePage("signup.html");

        // The old ".html" addresses stay reachable but bounce to the clean path, so a shared
        // /signup.html link lands on /signup and the bar never shows a file name.
        private static readonly Dictionary<string, string> CleanPaths = new(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"] = "/", ["signup.html"] = "/signup", ["login.html"] = "/login",
            ["download.html"] = "/download", ["news.html"] = "/news", ["patch-notes.html"] = "/patch-notes", ["rooms.html"] = "/rooms",
            ["accessibility.html"] = "/accessibility", ["support.html"] = "/support", ["weekly.html"] = "/weekly",
            ["challenges.html"] = "/challenges", ["settings.html"] = "/settings", ["profile.html"] = "/profile", ["stats.html"] = "/stats",
            ["messages.html"] = "/messages",
        };

        [HttpGet("/{page}.html")]
        public IActionResult HtmlRedirect(string page)
        {
            if (!CleanPaths.TryGetValue(page + ".html", out var clean)) return NotFound();
            // profile.html?u=kyle -> /user/kyle ; keep any other query string / fragment as-is
            if (clean == "/profile" && !string.IsNullOrEmpty(Request.Query["u"]))
                return RedirectPermanent("/user/" + Uri.EscapeDataString(Request.Query["u"].ToString()));
            return RedirectPermanent(clean + Request.QueryString.Value);
        }

        // The Windows launcher download: https://camprec.net/campus.exe
        // Drop the built launcher on the server at Data/Downloads/campus.exe (preferred) or
        // Website/campus.exe and it is served here as a file download. enableRangeProcessing lets
        // browsers resume/segment the download of a large exe.
        [HttpGet("/campus.exe")]
        public IActionResult CampusExe()
        {
            // The newest launcher released through UploadUpdate.bat (the same exe the launcher's
            // self-update downloads), else a file dropped by hand.
            var latest = LauncherController.Latest();
            if (latest != null) return PhysicalFile(latest.Value.path, "application/octet-stream", "Campus.exe", enableRangeProcessing: true);
            foreach (var p in new[]
            {
                Path.Combine(Environment.CurrentDirectory, "Data", "Downloads", "campus.exe"),
                Path.Combine(Environment.CurrentDirectory, "Website", "campus.exe"),
            })
            {
                if (System.IO.File.Exists(p))
                    return PhysicalFile(p, "application/octet-stream", "campus.exe", enableRangeProcessing: true);
            }
            return NotFound("Launcher not uploaded yet - place campus.exe in Data/Downloads/ on the server.");
        }

        [Route("/robots.txt")]
        public IActionResult Robots()
        {
            var content = string.Join(Environment.NewLine, new[]
            {
                "User-agent: *",
                "Disallow: /admin/v4/login",
                "Disallow: /admin/v4/home",
                "Disallow: /admin/v5/login",
                "Disallow: /admin/v5/home"
            });

            return Content(content, "text/plain");
        }

        [HttpGet("/favicon.ico")]
        public IActionResult Favicon()
        {
            // Serve the Campus AnimatedIcon as the browser-tab icon for camprec.net.
            // Falls back to Web/favicon.png if the admin-assets file isn't shipped for some reason.
            var animated = Path.Combine(Environment.CurrentDirectory, "AdminPanel", "Assets", "AnimatedIcon.png");
            if (System.IO.File.Exists(animated))
                return PhysicalFile(animated, "image/png");

            var legacy = Path.Combine(Environment.CurrentDirectory, "Web", "favicon.png");
            if (System.IO.File.Exists(legacy))
                return PhysicalFile(legacy, "image/png");

            return NotFound();
        }

        [HttpGet("/ns")]
        public IActionResult ns([FromQuery(Name = "v")] string? Version = null)
        {
            // Reference server only served for "20250731" (July 31 2025 build). We serve the same
            // body for any/no version so older + newer client builds don't 400 here.
            {
                // Asked on ns.camprec.net (the patch since 2026-10-07) -> every service lives on api.camprec.net.
                // Asked on any other host (older patches / the Quest build still use camprec.net) -> stay on that host,
                // so nothing depends on the api. certificate for them.
                string reqHost = Request.Host.Host;
                string api = reqHost.Equals("ns.camprec.net", StringComparison.OrdinalIgnoreCase) ? "https://api.camprec.net" : "https://camprec.net";
                // A developer's own server (the launcher's Dev channel points the game at localhost): everything stays on that address.
                if (reqHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) || reqHost == "127.0.0.1" || reqHost == "[::1]") api = $"{Request.Scheme}://{Request.Host}";
                // The CDN host. A build still on gameauth.ddns.net (older Quest APKs, older launchers) already resolves that name and
                // gameauthcdn.ddns.net next to it; cdn.camprec.net is a NEW name for them, and a name a player's DNS cannot resolve is
                // "error code: summer". So they keep the old CDN host (its Caddy block and DNS record stay); everyone else gets cdn.camprec.net.
                string cdn = ConfigCampus.ForceCdnFallback ? ConfigCampus.CdnFallbackUrl : CdnHost.Current;
                if (!ConfigCampus.ForceCdnFallback && reqHost.Equals("gameauth.ddns.net", StringComparison.OrdinalIgnoreCase)) cdn = "https://gameauthcdn.ddns.net/cdn";
                return Ok(new
                {
                    Accounts = $"{api}/Accounts",
                    API = api,
                    AI = $"{api}/Ai",
                    Auth = $"{api}/Auth",
                    BugReporting = api,
                    Cards = api,
                    // Was `https://cdn.rec.net` (a leftover in the E12354 base that pointed at
                    // real Rec Room's CDN to "save on our own bandwidth"). The July 19 2025
                    // client's `summer` NS-validation gate checks every URL's host and fails
                    // when they don't all resolve back to us - mate's working server puts CDN
                    // on his host, so we do the same. 2026-08-13.
                    // Normally the dedicated host (CdnBaseUrl) is advertised once CdnHost's probe
                    // has verified it resolves to this server and answers over https. Must NOT
                    // share a host with any API service - a shared host switches off the client's
                    // bundle cache and every avatar bundle load dies in a NullReferenceException.
                    // Kill switch: ConfigCampus.ForceCdnFallback flips every /ns response to the
                    // API-host fallback when players report "error code summer" on connect (their
                    // DNS can't resolve the dedicated CDN host even though the server's probe can).
                    CDN = cdn,
                    Chat = $"{api}/chat",
                    Clubs = $"{api}/clubs",
                    CMS = $"{api}/cms",
                    Commerce = $"{api}/Commerce",
                    Data = api,
                    DataCollection = api,
                    Discovery = api,
                    Econ = api,
                    GameLogs = api,
                    Geo = api,
                    Images = $"{api}/img/",
                    Leaderboard = $"{api}/server/Leaderboard",
                    Link = $"{api}/link",
                    Lists = $"{api}/lists",
                    Matchmaking = $"{api}/Matchmaking",
                    Moderation = api,
                    Notifications = $"{api}/Notifications",
                    PlatformNotifications = $"{api}/Notifications",
                    PlayerSettings = $"{api}/server/PlayerSettings",
                    RoomComments = $"{api}/RoomComments",
                    RoomieIntegrations = $"{api}/Roomie",
                    Rooms = $"{api}/Room_server",
                    Storage = $"{api}/server/Storage",
                    Strings = $"{api}/Strings",
                    StringsCDN = $"{api}/StringsCDN",
                    Studio = $"{api}/Studio",
                    Thorn = $"{api}/Thorn",
                    Videos = $"{api}/videodir",
                    WWW = "https://camprec.net"
                });
            }
        }
    }
}
