using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    // Patch handshake. CampusPatch (the DLL) POSTs here once at startup (main.cpp) with the
    // shared secret in the X-Campus-Patch header. The SERVER holds the real secret and compares:
    // a match answers 200, anything else answers 403. A DLL with the wrong/absent header is
    // denied here, and main.cpp aborts on a non-200 so that client never connects.
    //
    // The secret lives ONLY on the server (ConfigCampus.PatchSignature) - the DLL just echoes
    // what it was built with; the server is what decides.
    [ApiController]
    public class PatchController : ControllerBase
    {
        public const string HeaderName = "X-Campus-Patch";

        [HttpPost("/api/patch/verify")]
        [HttpGet("/api/patch/verify")]
        public IActionResult Verify()
        {
            string sent = Request.Headers[HeaderName].FirstOrDefault() ?? "";
            string expected = ConfigCampus.PatchSignature ?? "";

            string ip = Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                        ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()
                        ?? HttpContext.Connection.RemoteIpAddress?.ToString()
                        ?? "?";

            bool ok = !string.IsNullOrEmpty(expected) && FixedTimeEquals(sent, expected);

            if (!ok)
            {
                Console.WriteLine($"[patchverify] DENIED {ip} - signature {(string.IsNullOrEmpty(sent) ? "absent" : "mismatch")}");
                return StatusCode(StatusCodes.Status403Forbidden, new { ok = false });
            }

            // Just a friendly startup check for the DLL - real enforcement is the per-request
            // X-Campus-Patch header validated in PatchGateMiddleware. No whitelist is kept.
            Console.WriteLine($"[patchverify] OK {ip}");
            return Ok(new { ok = true });
        }

        public class ProofBody {
            public long ts { get; set; }
            public string? sha { get; set; }
            public string? steam { get; set; }
            public string? mac { get; set; }
            // Optional launcher-launch token "accountId|steamId|ts|hmac". Minted by the launcher
            // and passed to the game via CAMPUS_LAUNCH_TOKEN. Verified by LauncherLaunchProofs
            // and remembered per ip / steam so the login gate can require it.
            public string? launchToken { get; set; }
        }

        /// <summary>
        /// POST /api/patch/proof {ts, sha, steam, mac, launchToken?} - the PC patch proving it is
        /// the current build (PatchProofs) AND the launcher proving IT started the game
        /// (LauncherLaunchProofs, via the launchToken field). Sent at startup and right before
        /// each game login; a good one lets PC logins from this IP / Steam id through
        /// /Auth/connect/token for a few minutes. An older patch is told so (409), and its logins
        /// are refused. A missing / bad launch token is recorded as "no launcher proof" (the login
        /// gate's own decision whether that refuses the login - see ConfigCampus.RequireLauncherLaunch).
        /// </summary>
        /// <summary>GET /api/patch/proof/info - ReleaseGame.bat checks this before it gives the patch a new
        /// key: only a server that reads Data/Launcher/patch/&lt;sha256&gt;.key can check a rotated key.</summary>
        [HttpGet("/api/patch/proof/info")]
        public IActionResult ProofInfo() => Ok(new { ok = true, perBuildKeys = true, launcherLaunchGate = true, channels = true });

        [HttpPost("/api/patch/proof")]
        public IActionResult Proof([FromBody] ProofBody? body)
        {
            string ip = PatchGateMiddleware.ClientIp(HttpContext);
            if (body == null) return BadRequest(new { ok = false });
            ulong.TryParse(body.steam, out ulong steam);
            var check = PatchProofs.Verify(body.ts, body.sha ?? "", steam, body.mac ?? "");
            if (!check.Ok)
            {
                Console.WriteLine($"[patchproof] refused {ip} (steam {steam}): {check.Why}");
                bool old = check.Why.StartsWith("old patch");
                return StatusCode(old ? 409 : 403, new { ok = false, error = old ? "This Campus patch is out of date - restart the game from the Campus launcher." : "Patch proof refused." });
            }
            // The beta / dev patches are for developer accounts: their proof must carry a launcher token that names one.
            if (check.Channel.Length > 0)
            {
                var who = string.IsNullOrEmpty(body.launchToken) ? default : LauncherLaunchProofs.Verify(body.launchToken!);
                if (!who.Ok || !Campus_live_rewrite.Controllers.LauncherController.IsDevAccount(who.AccountId))
                {
                    Console.WriteLine($"[patchproof] refused {ip} (steam {steam}): the {check.Channel} patch is for developer accounts");
                    return StatusCode(403, new { ok = false, error = "Patch proof refused." });
                }
            }
            PatchProofs.Record(ip, steam);

            // Launcher-launch proof is OPTIONAL on this endpoint - we always accept the patch
            // proof, and the login gate decides what to do when the launcher proof is missing.
            // That way an older build of the patch (no launchToken yet) stays compatible: it just
            // never satisfies the launcher-launch gate.
            bool launcherOk = false;
            string? launcherWhy = null;
            if (!string.IsNullOrEmpty(body.launchToken))
            {
                var lc = LauncherLaunchProofs.Verify(body.launchToken!);
                if (lc.Ok)
                {
                    // Cross-check: the token's steam id must be the same one the patch is signing
                    // for. Otherwise someone could lift a token from another account and attach it.
                    if (steam != 0 && lc.SteamId != 0 && lc.SteamId != steam)
                    {
                        launcherWhy = $"token steam {lc.SteamId} != proof steam {steam}";
                    }
                    else
                    {
                        LauncherLaunchProofs.Record(ip, steam);
                        launcherOk = true;
                    }
                }
                else launcherWhy = lc.Why;
            }
            else launcherWhy = "no launchToken";
            if (!launcherOk) Console.WriteLine($"[launchproof] no fresh launcher proof for {ip} (steam {steam}): {launcherWhy}");
            return Ok(new { ok = true, launcherOk });
        }

        // Constant-time compare so the secret can't be brute-forced a byte at a time by timing.
        private static bool FixedTimeEquals(string a, string b)
        {
            var ab = System.Text.Encoding.UTF8.GetBytes(a ?? "");
            var bb = System.Text.Encoding.UTF8.GetBytes(b ?? "");
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ab, bb);
        }
    }
}
