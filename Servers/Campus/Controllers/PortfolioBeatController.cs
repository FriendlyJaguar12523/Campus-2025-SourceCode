using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// The college portfolio's "finished beat" (the owner's hip-hop track, 98 BPM): anyone can play
    /// the latest upload, only the portfolio OWNER can replace it.
    ///
    /// Owner = the website session (campus_site cookie, a website-surface token - the same login as
    /// the Campus website) of ConfigCampus.PortfolioOwnerAccountId. The page lives on the same origin
    /// (/collegeportfolio), so the browser sends that cookie with the upload. Anyone else - signed out
    /// or signed in as any other account - gets 403 and nothing is written. Files go to
    /// Data/CollegePortfolio/beats (every upload is kept; the newest is the one played), which the
    /// DLL-only deploy never touches, so an upload survives updates.
    /// </summary>
    [ApiController]
    public class PortfolioBeatController : ControllerBase
    {
        private const string SiteCookie = "campus_site";
        private const long MaxBytes = 100L * 1024 * 1024;
        private static readonly string Dir = Path.Combine(Environment.CurrentDirectory, "Data", "CollegePortfolio", "beats");
        private static readonly string MetaPath = Path.Combine(Dir, "beat.json");
        private static readonly object _lock = new();

        private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            [".wav"] = "audio/wav", [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac",
            [".flac"] = "audio/flac", [".ogg"] = "audio/ogg", [".aif"] = "audio/aiff", [".aiff"] = "audio/aiff",
        };

        private class BeatMeta
        {
            public string File { get; set; } = "";
            public string OriginalName { get; set; } = "";
            public long Size { get; set; }
            public DateTime UploadedAt { get; set; }
        }

        private player_info? SiteUser()
        {
            string token = Request.Cookies[SiteCookie] ?? "";
            if (string.IsNullOrEmpty(token)) return null;
            string data = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_")) return null;
            try { return JsonConvert.DeserializeObject<player_info>(data); } catch { return null; }
        }

        private static BeatMeta? Latest()
        {
            try
            {
                if (!System.IO.File.Exists(MetaPath)) return null;
                var m = JsonConvert.DeserializeObject<BeatMeta>(System.IO.File.ReadAllText(MetaPath));
                return m != null && System.IO.File.Exists(Path.Combine(Dir, m.File)) ? m : null;
            }
            catch { return null; }
        }

        /// <summary>Who is looking: drives the page (upload box enabled only for the owner).</summary>
        [HttpGet("/collegeportfolio/api/me")]
        public IActionResult Me()
        {
            var u = SiteUser();
            bool owner = u != null && u.Id == ConfigCampus.PortfolioOwnerAccountId;
            Response.Headers["Cache-Control"] = "no-store";
            return Ok(new { signedIn = u != null, owner, username = u?.Username });
        }

        /// <summary>The current beat's details (null when nothing has been uploaded yet).</summary>
        [HttpGet("/collegeportfolio/api/beat")]
        public IActionResult Info()
        {
            var m = Latest();
            Response.Headers["Cache-Control"] = "no-store";
            if (m == null) return Ok(new { exists = false });
            return Ok(new { exists = true, name = m.OriginalName, size = m.Size, uploadedAt = m.UploadedAt,
                            url = "/collegeportfolio/api/beat/file?v=" + m.UploadedAt.Ticks });
        }

        /// <summary>The audio itself - range requests on, so the player can seek.</summary>
        [HttpGet("/collegeportfolio/api/beat/file")]
        public IActionResult File()
        {
            var m = Latest();
            if (m == null) return NotFound();
            string ext = Path.GetExtension(m.File);
            return PhysicalFile(Path.Combine(Dir, m.File), Types.TryGetValue(ext, out var ct) ? ct : "application/octet-stream", enableRangeProcessing: true);
        }

        /// <summary>Replace the beat. Owner's website session only.</summary>
        [HttpPost("/collegeportfolio/api/beat")]
        [RequestSizeLimit(MaxBytes + 1024 * 1024)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxBytes + 1024 * 1024)]
        public async Task<IActionResult> Upload()
        {
            var u = SiteUser();
            if (u == null)
                return StatusCode(401, new { ok = false, error = "Sign in to your Campus account on the website first." });
            if (u.Id != ConfigCampus.PortfolioOwnerAccountId)
            {
                Console.WriteLine($"[portfolio] upload refused: @{u.Username} ({u.Id}) is not the portfolio owner");
                return StatusCode(403, new { ok = false, error = "Only the portfolio owner can upload the beat." });
            }
            // Same-origin only: a page on another site can't post with the owner's cookie.
            string origin = Request.Headers["Origin"].ToString();
            if (origin.Length > 0)
            {
                bool sameSite = Uri.TryCreate(origin, UriKind.Absolute, out var o)
                                && string.Equals(o.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase);
                if (!sameSite) return StatusCode(403, new { ok = false, error = "Cross-site upload refused." });
            }
            if (!Request.HasFormContentType)
                return BadRequest(new { ok = false, error = "Send the file as multipart/form-data." });

            var form = await Request.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return BadRequest(new { ok = false, error = "No file received." });
            if (file.Length > MaxBytes) return BadRequest(new { ok = false, error = "That file is over 100 MB." });
            string ext = Path.GetExtension(file.FileName ?? "");
            if (!Types.ContainsKey(ext))
                return BadRequest(new { ok = false, error = "Audio files only: .wav, .mp3, .m4a, .aac, .flac, .ogg, .aif(f)." });

            // The bytes must really be audio of that kind, not a renamed file.
            byte[] head = new byte[12];
            using (var s = file.OpenReadStream()) { int n = 0, r; while (n < 12 && (r = await s.ReadAsync(head, n, 12 - n)) > 0) n += r; }
            if (!LooksLikeAudio(ext, head))
                return BadRequest(new { ok = false, error = "That file isn't a valid " + ext.TrimStart('.').ToUpperInvariant() + " audio file." });

            Directory.CreateDirectory(Dir);
            var now = DateTime.UtcNow;
            string stored = $"beat-{now:yyyyMMdd-HHmmss}{ext.ToLowerInvariant()}";
            string tmp = Path.Combine(Dir, stored + ".part");
            using (var outS = System.IO.File.Create(tmp)) await file.CopyToAsync(outS);
            string safeName = new string(Path.GetFileName(file.FileName ?? "beat").Where(c => c >= 32 && c < 127).ToArray());
            lock (_lock)
            {
                System.IO.File.Move(tmp, Path.Combine(Dir, stored), overwrite: true);
                var meta = new BeatMeta { File = stored, OriginalName = safeName.Length > 0 ? safeName : stored, Size = file.Length, UploadedAt = now };
                System.IO.File.WriteAllText(MetaPath, JsonConvert.SerializeObject(meta, Formatting.Indented));
            }
            Console.WriteLine($"[portfolio] @{u.Username} uploaded the beat: '{safeName}' ({file.Length / 1024} KB) -> {stored}");
            return Ok(new { ok = true, name = safeName, size = file.Length, uploadedAt = now,
                            url = "/collegeportfolio/api/beat/file?v=" + now.Ticks });
        }

        private static bool LooksLikeAudio(string ext, byte[] h)
        {
            string a(int i, int n) => System.Text.Encoding.ASCII.GetString(h, i, n);
            switch (ext.ToLowerInvariant())
            {
                case ".wav": return a(0, 4) == "RIFF" && a(8, 4) == "WAVE";
                case ".mp3": return a(0, 3) == "ID3" || (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0);
                case ".m4a": return a(4, 4) == "ftyp";
                case ".aac": return (h[0] == 0xFF && (h[1] & 0xF6) == 0xF0) || a(4, 4) == "ftyp";
                case ".flac": return a(0, 4) == "fLaC";
                case ".ogg": return a(0, 4) == "OggS";
                case ".aif": case ".aiff": return a(0, 4) == "FORM" && (a(8, 4) == "AIFF" || a(8, 4) == "AIFC");
                default: return false;
            }
        }
    }
}
