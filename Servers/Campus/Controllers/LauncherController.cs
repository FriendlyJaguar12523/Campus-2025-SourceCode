using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// The Campus launcher (CampusPatch2025\Launcher).
    ///
    /// UPDATES - public, so a signed-out launcher still updates: GET /api/launcher/v1/app answers
    /// latest.json ({version, build, sha256, size}) and /app/{sha256} the exe. Data/Launcher/app is
    /// staged by Launcher\tools\release_app.py and uploaded by UploadUpdate.bat (exe first).
    /// /campus.exe (the website download) serves the same latest exe.
    ///
    /// ACCOUNT - sign-in with a Campus username + password mints a token with surface "launcher"
    /// (30 days). DecodeToken refuses that surface everywhere else, so a launcher token can only
    /// read the launcher's own account summary; it can't play, post or reach the admin panel.
    /// </summary>
    public class LauncherController : ControllerBase
    {
        private static readonly TimeSpan TokenTtl = TimeSpan.FromDays(30);
        private static readonly Regex Sha = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
        private static string AppDir => Path.Combine(Environment.CurrentDirectory, "Data", "Launcher", "app");

        // ---- updates ------------------------------------------------------------------------------

        /// <summary>The newest released launcher exe on this server, or null.</summary>
        internal static (string path, string version, int build, string sha, long size)? Latest()
        {
            try
            {
                var j = JObject.Parse(System.IO.File.ReadAllText(Path.Combine(AppDir, "latest.json")));
                string sha = (string?)j["sha256"] ?? "";
                string path = Path.Combine(AppDir, sha + ".exe");
                if (!Sha.IsMatch(sha) || !System.IO.File.Exists(path)) return null;
                return (path, (string?)j["version"] ?? "", (int?)j["build"] ?? 0, sha, (long?)j["size"] ?? 0);
            }
            catch { return null; }
        }

        [HttpGet("/api/launcher/v1/app")]
        public IActionResult AppLatest()
        {
            var l = Latest();
            if (l == null) return Ok(new { ok = true, build = 0 });          // nothing released (or the exe isn't up yet)
            return Ok(new { ok = true, version = l.Value.version, build = l.Value.build, sha256 = l.Value.sha, size = l.Value.size });
        }

        [HttpGet("/api/launcher/v1/app/{id}")]
        public IActionResult AppDownload(string id)
        {
            if (!Sha.IsMatch(id ?? "")) return BadRequest(new { ok = false, error = "Bad version id." });
            string path = Path.Combine(AppDir, id + ".exe");
            if (!System.IO.File.Exists(path)) return NotFound(new { ok = false, error = "The server doesn't have this version of the launcher." });
            return PhysicalFile(path, "application/octet-stream", enableRangeProcessing: true);
        }

        // ---- the Campus patch ----------------------------------------------------------------------

        // The launcher no longer ships CampusPatch.dll: at every Play it downloads it (signed in only),
        // checks the SHA-256, loads it into the game and deletes it once the game closes - so the
        // patch only reaches people with a Campus account, and only through the launcher.
        // Data/Launcher/patch holds latest.json ({sha256, size}) and <sha256>.dll, staged by
        // Launcher\tools\release_patch.py and uploaded by UploadUpdate.bat.
        private static string PatchDir => Path.Combine(Environment.CurrentDirectory, "Data", "Launcher", "patch");

        // Release channels. "" = public (Data/Launcher/patch, everyone). "beta", "dev" and "2026" (Data/Launcher/patch/beta, /dev, /2026) are
        // for developer accounts only: the launcher shows the drop-down to them, and the patch routes below refuse anyone else.
        // "2026" is the patch for the Rec Room 2026 client (a different game; same Campus servers, same proof / launch-token gates).
        // Pushed by PushChannel.bat. Each channel has its own latest.json, <sha256>.dll and <sha256>.key.
        internal static readonly string[] Channels = { "", "beta", "dev", "2026" };
        internal static string NormChannel(string? c) { c = (c ?? "").Trim().ToLowerInvariant(); return c == "beta" || c == "dev" || c == "2026" ? c : ""; }
        internal static string PatchDirFor(string channel) => channel.Length == 0 ? PatchDir : Path.Combine(PatchDir, channel);

        /// <summary>Owner / dev accounts: the ones who may use the beta and dev channels.</summary>
        internal static bool IsDevAccount(ulong id) =>
            ((PlayerDB.GetFullAccount(id)?.dev_Flag ?? DevFlag.none) & (DevFlag.owner | DevFlag.dev)) != 0;


        /// <summary>The SHA-256 of the patch the launcher serves now (latest.json), or "" when none is uploaded.</summary>
        internal static string CurrentPatchSha(string channel = "")
        {
            try
            {
                var j = JObject.Parse(System.IO.File.ReadAllText(Path.Combine(PatchDirFor(channel), "latest.json")));
                string sha = ((string?)j["sha256"] ?? "").ToLowerInvariant();
                return Sha.IsMatch(sha) ? sha : "";
            }
            catch { return ""; }
        }

        [HttpGet("/api/launcher/v1/patch")]
        public IActionResult PatchLatest([FromQuery] string? channel = null)
        {
            var (u, err) = Caller();
            if (err != null) return err;
            string ch = NormChannel(channel);
            if (ch.Length > 0 && !IsDevAccount(u!.Id))
            {
                Console.WriteLine($"[launcher] @{u.Username} ({u.Id}) asked for the {ch} patch but is not a developer - refused");
                return StatusCode(403, new { ok = false, error = "The beta, dev and 2026 patches are for developer accounts." });
            }
            try
            {
                string dir = PatchDirFor(ch);
                var j = JObject.Parse(System.IO.File.ReadAllText(Path.Combine(dir, "latest.json")));
                string sha = (string?)j["sha256"] ?? "";
                if (Sha.IsMatch(sha) && System.IO.File.Exists(Path.Combine(dir, sha + ".dll")))
                    return Ok(new { ok = true, sha256 = sha, size = (long?)j["size"] ?? 0, channel = ch.Length == 0 ? "public" : ch });
            }
            catch { }
            Console.WriteLine($"[launcher] {u!.Id} asked for the {(ch.Length == 0 ? "public" : ch)} patch, but none is uploaded (Data/Launcher/patch{(ch.Length == 0 ? "" : "/" + ch)})");
            return StatusCode(503, new { ok = false, error = ch.Length == 0 ? "The Campus patch isn't on the server right now - try again later."
                                                                          : $"No {ch} patch has been pushed yet (PushChannel.bat)." });
        }

        [HttpGet("/api/launcher/v1/patch/{id}")]
        public IActionResult PatchDownload(string id, [FromQuery] string? channel = null)
        {
            var (u, err) = Caller();
            if (err != null) return err;
            if (!Sha.IsMatch(id ?? "")) return BadRequest(new { ok = false, error = "Bad patch id." });
            string ch = NormChannel(channel);
            if (ch.Length > 0 && !IsDevAccount(u!.Id)) return StatusCode(403, new { ok = false, error = "The beta, dev and 2026 patches are for developer accounts." });
            string path = Path.Combine(PatchDirFor(ch), id + ".dll");
            if (!System.IO.File.Exists(path)) return NotFound(new { ok = false, error = "The server doesn't have this version of the patch." });
            Console.WriteLine($"[launcher] @{u!.Username} ({u.Id}) downloaded the patch {id[..12]}");
            return PhysicalFile(path, "application/octet-stream", enableRangeProcessing: true);
        }

        // ---- the game itself --------------------------------------------------------------------------
        // Data/Launcher/game holds latest.json ({sha256, size, unpacked}) and <sha256>.zip - the whole
        // game as one zip (Campus.zip), staged by Launcher\tools\release_app.py game and uploaded by
        // UploadUpdate.bat. The launcher's INSTALL / UPDATE downloads it (signed in only), resuming
        // with Range requests, checks the SHA-256 and unpacks it.
        private static string GameDir => Path.Combine(Environment.CurrentDirectory, "Data", "Launcher", "game");

        [HttpGet("/api/launcher/v1/game")]
        public IActionResult GameLatest()
        {
            var (u, err) = Caller();
            if (err != null) return err;
            try
            {
                var j = JObject.Parse(System.IO.File.ReadAllText(Path.Combine(GameDir, "latest.json")));
                string sha = ((string?)j["sha256"] ?? "").ToLowerInvariant();
                if (Sha.IsMatch(sha) && System.IO.File.Exists(Path.Combine(GameDir, sha + ".zip")))
                    return Ok(new { ok = true, sha256 = sha, size = (long?)j["size"] ?? 0, unpacked = (long?)j["unpacked"] ?? 0 });
            }
            catch { }
            return StatusCode(503, new { ok = false, error = "The game download isn't on the server right now." });
        }

        [HttpGet("/api/launcher/v1/game/{id}")]
        public IActionResult GameDownload(string id)
        {
            var (u, err) = Caller();
            if (err != null) return err;
            if (!Sha.IsMatch(id ?? "")) return BadRequest(new { ok = false, error = "Bad game id." });
            string path = Path.Combine(GameDir, id + ".zip");
            if (!System.IO.File.Exists(path)) return NotFound(new { ok = false, error = "The server doesn't have this version of the game." });
            string range = Request.Headers["Range"].ToString();
            Console.WriteLine($"[launcher] @{u!.Username} ({u.Id}) downloading the game {id[..12]}{(range.Length > 0 ? " (resuming " + range + ")" : "")}");
            return PhysicalFile(path, "application/zip", enableRangeProcessing: true);
        }

        // ---- account ------------------------------------------------------------------------------

        public class LoginBody { public string? username { get; set; } public string? password { get; set; } }

        private (player_info? user, IActionResult? error) Caller()
        {
            string auth = Request.Headers["Authorization"].ToString();
            string token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth.Substring(7).Trim() : "";
            if (token.Length == 0) return (null, StatusCode(401, new { ok = false, error = "Sign in first." }));
            string data = ClientSecurity.DecodeToken(token, requireSurface: "launcher");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_"))
                return (null, StatusCode(401, new { ok = false, error = data == "error_token_expired" ? "Your sign-in has expired - sign in again." : "Sign in again." }));
            player_info? u;
            try { u = JsonConvert.DeserializeObject<player_info>(data); } catch { u = null; }
            if (u == null || PlayerDB.GetFullAccount(u.Id) == null) return (null, StatusCode(401, new { ok = false, error = "Sign in again." }));
            return (u, null);
        }

        /// <summary>POST /api/launcher/v1/login {username, password} -> a launcher token (30 days).</summary>
        [HttpPost("/api/launcher/v1/login")]
        public IActionResult Login([FromBody] LoginBody? body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.username) || string.IsNullOrWhiteSpace(body.password))
                return BadRequest(new { ok = false, error = "Enter your username and password." });
            var result = PlayerDB.GetAccountPassword(body.username.Trim().TrimStart('@'), body.password, isAdminPanel: false);
            if (!result.success)
                return StatusCode(401, new { ok = false, error = result.error ?? "Incorrect username or password." });
            string token = ClientSecurity.RefreshToken_Web(result.accid, TokenTtl, "launcher");
            var full = PlayerDB.GetFullAccount(result.accid);
            Console.WriteLine($"[launcher] @{full?.player?.Username} ({result.accid}) signed in to the launcher");
            return Ok(new { ok = true, token, id = result.accid, username = full?.player?.Username,
                            expires = DateTimeOffset.UtcNow.Add(TokenTtl).ToUnixTimeSeconds() });
        }

        /// <summary>What the launcher's account menu shows: who you are, level, tokens and gifts waiting in game.</summary>
        [HttpGet("/api/launcher/v1/me")]
        public IActionResult Me()
        {
            var (u, err) = Caller();
            if (err != null) return err;
            ulong id = u!.Id;
            var full = PlayerDB.GetFullAccount(id)!;

            var lvl = Progress.GetPlayerLevel(id);
            int need = lvl.Level >= 0 && lvl.Level < Progress.LEVEL_REQUIRED_XP.Length ? Progress.LEVEL_REQUIRED_XP[lvl.Level] : 0;

            bool banned = PlayerDB.HasActiveModerationBlock(id);
            // Same shape the website's /me uses so the launcher can show the same banner and
            // block PLAY instead of just saying "you can only go to your dorm". Null when the
            // account isn't banned; remaining_seconds null = permanent.
            var siteBan = PlayerDB.GetSiteBan(id);
            object? ban = siteBan == null ? null : new
            {
                permanent = siteBan.RemainingSeconds == null,
                remaining_seconds = siteBan.RemainingSeconds,
                until = siteBan.Until,
                reason = siteBan.Reason
            };
            return Ok(new
            {
                ok = true,
                id,
                username = full.player?.Username ?? "",
                display_name = full.player?.DisplayName ?? "",
                image = string.IsNullOrWhiteSpace(full.player?.profileImage) ? "" : "/img/" + full.player!.profileImage,
                level = lvl.Level,
                xp = lvl.XP,
                xp_needed = need,
                tokens = PlayerDB.GetBalance(id, (int)CurrencyType.RecCenterTokens),
                gifts = PlayerDB.GetPendingGiftBoxes(id).Count,
                banned,
                ban,
                // strings: a SteamID64 is past what JavaScript-style number parsers keep exactly
                steam_ids = PlayerDB.GetPlatformIds(id, Platforms.Steam).Where(s => s != 0).Select(s => s.ToString()),
                // the Steam ids this account whitelisted through the launcher (LauncherPlayers)
                launcher_steam = LauncherPlayers.ForAccount(id),
                // developer / owner accounts get the release-channel drop-down (public / beta / dev) in the launcher
                dev = IsDevAccount(id),
            });
        }

        public class SteamBody { public string? ticket { get; set; } }

        /// <summary>
        /// POST /api/launcher/v1/steam {ticket: hex} - link the signed-in account to the Steam account
        /// the launcher is running under. The launcher gets the ticket from Steam itself (the game's
        /// steam_api64, app 471710 - the same ticket the game logs in with), and SteamTicket checks
        /// Steam's signature OFFLINE, so the id linked is one Steam vouched for - a posted SteamID
        /// alone is public and proves nothing. Linking goes through the same one-account-per-Steam
        /// lock as a game login (AccountLock.TryEnforceAndBind, with no device: the launcher isn't a
        /// game device, so it doesn't take the account's PC slot).
        /// </summary>
        [HttpPost("/api/launcher/v1/steam")]
        public IActionResult LinkSteam([FromBody] SteamBody? body)
        {
            var (u, err) = Caller();
            if (err != null) return err;
            string hex = (body?.ticket ?? "").Trim();
            if (hex.Length == 0 || hex.Length > 8192) return BadRequest(new { ok = false, error = "No Steam ticket." });
            var proof = SteamTicket.Verify(new JObject { ["Ticket"] = hex }.ToString(Formatting.None));
            if (!proof.Ok)
            {
                Console.WriteLine($"[launcher] Steam link for {u!.Id} refused: {proof.Reason}");
                return BadRequest(new { ok = false, error = "Steam couldn't confirm your account (" + proof.Reason + ")." });
            }
            // Steam vouched for this id and it came through our launcher: it may play on PC
            // (LauncherPlayers - the normal-player whitelist), whatever the account link says below.
            LauncherPlayers.Add(proof.SteamId, u!.Id, u.Username ?? "");
            if (PlayerDB.GetPlatformIds(u!.Id, Platforms.Steam).Contains(proof.SteamId))
                return Ok(new { ok = true, steam_id = proof.SteamId.ToString(), already = true });
            if (!AccountLock.TryEnforceAndBind(u.Id, Platforms.Steam, proof.SteamId, null, out string lockErr))
                return StatusCode(409, new { ok = false, error = lockErr });
            Console.WriteLine($"[launcher] @{u.Username} ({u.Id}) linked Steam {proof.SteamId}");
            return Ok(new { ok = true, steam_id = proof.SteamId.ToString() });
        }
    }
}
