using System.Text;
using System.Text.RegularExpressions;
using Campus_live_rewrite.api.server;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Admin panel "Videos" tab: upload / list / delete the .mp4 files a Remote Video Player can play.
    /// They live in Data/Videos and the server hands them out at /cdn/video/&lt;name&gt; (Program.cs, video/mp4 only), so the
    /// address to type into the player's Video Url is &lt;CdnBaseUrl&gt;/video/&lt;name&gt;. Deploys never touch Data/Videos.
    /// </summary>
    public class AdminVideosController : ControllerBase
    {
        private const long MaxBytes = 500L * 1024 * 1024;
        private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,100}\.mp4$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static ContentResult J(object o, int status = 200) => new() { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = status };
        private static string Dir => Path.Combine(Environment.CurrentDirectory, "Data", "Videos");

        private static string UrlOf(string name) => $"{ConfigCampus.CdnBaseUrl.TrimEnd('/')}/video/{name}";

        /// <summary>Which video / audio codecs the file declares (looks at the first and last 4 MB, where the mp4 "moov" box sits).</summary>
        private static string Codecs(string path)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(path);
                var buf = new byte[(int)Math.Min(fs.Length, 4 * 1024 * 1024)];
                var found = new List<string>();
                void Scan()
                {
                    string s = Encoding.Latin1.GetString(buf);
                    foreach (var (tag, label) in new[] { ("avc1", "H.264"), ("hvc1", "H.265"), ("hev1", "H.265"), ("av01", "AV1"), ("vp09", "VP9"), ("mp4a", "AAC"), ("Opus", "Opus") })
                        if (s.Contains(tag) && !found.Contains(label)) found.Add(label);
                }
                int n = fs.Read(buf, 0, buf.Length); if (n < buf.Length) Array.Resize(ref buf, n); Scan();
                if (fs.Length > buf.Length)
                {
                    buf = new byte[(int)Math.Min(fs.Length - n, 4 * 1024 * 1024)];
                    fs.Seek(-buf.Length, SeekOrigin.End); fs.ReadExactly(buf, 0, buf.Length); Scan();
                }
                return string.Join(" + ", found);
            }
            catch { return ""; }
        }

        private static object Row(FileInfo f)
        {
            var yt = YoutubeFetch.Find(f.Name);
            return new
            {
                name = f.Name, size = f.Length, modified = f.LastWriteTimeUtc, url = UrlOf(f.Name), codecs = Codecs(f.FullName),
                youtube = yt == null ? null : new { id = yt.VideoId, title = yt.Title, channel = yt.Channel, expires = yt.ExpiresAtUtc },
            };
        }

        [HttpGet("/admin/api/videos")]
        public ContentResult List()
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            Directory.CreateDirectory(Dir);
            YoutubeFetch.Sweep();   // expired YouTube fetches go now, not just on the hourly timer
            var files = new DirectoryInfo(Dir).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc).Select(Row).ToList();
            return J(new { success = true, baseUrl = UrlOf(""), videos = files });
        }

        [HttpPost("/admin/api/videos/upload")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxBytes + 1024 * 1024)]
        public async Task<ContentResult> Upload()
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!Request.HasFormContentType) return J(new { success = false, message = "Send the video as a form upload." }, 400);
            var form = await Request.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return J(new { success = false, message = "Pick an .mp4 file." }, 400);
            if (file.Length > MaxBytes) return J(new { success = false, message = $"That file is over {MaxBytes / (1024 * 1024)} MB." }, 400);

            // name: what the admin typed, else the file's own name; spaces become '_', anything else odd is dropped
            string name = form["name"].ToString().Trim();
            if (name.Length == 0) name = Path.GetFileName(file.FileName ?? "");
            name = Regex.Replace(name.Replace(' ', '_'), @"[^A-Za-z0-9._-]", "");
            if (name.Length > 0 && !name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) name += ".mp4";
            if (!SafeName.IsMatch(name)) return J(new { success = false, message = "Give it a simple name (letters, numbers, . _ -) ending in .mp4." }, 400);
            name = name[..^4] + ".mp4";   // lower-case extension: the server only hands out *.mp4 as video/mp4

            Directory.CreateDirectory(Dir);
            string target = Path.Combine(Dir, name);
            bool replace = string.Equals(form["replace"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (System.IO.File.Exists(target) && !replace) return J(new { success = false, message = $"{name} already exists - tick 'replace' or pick another name." }, 409);

            string tmp = Path.Combine(Dir, ".upload_" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var fs = System.IO.File.Create(tmp)) await file.CopyToAsync(fs);
                // a real mp4/mov starts with a size, then the box type "ftyp"
                var head = new byte[12];
                int got;
                using (var fs = System.IO.File.OpenRead(tmp)) got = fs.Read(head, 0, 12);
                if (got < 12 || Encoding.ASCII.GetString(head, 4, 4) != "ftyp")
                {
                    System.IO.File.Delete(tmp);
                    return J(new { success = false, message = "That is not an MP4 file (no 'ftyp' header)." }, 400);
                }
                System.IO.File.Move(tmp, target, overwrite: true);
            }
            catch (Exception ex)
            {
                try { System.IO.File.Delete(tmp); } catch { }
                return J(new { success = false, message = "Upload failed: " + ex.Message }, 500);
            }
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) uploaded video {name} ({new FileInfo(target).Length} bytes)");
            var row = Row(new FileInfo(target));
            string codecs = Codecs(target);
            string note = codecs.Contains("H.264") || codecs.Length == 0 ? "" : " Note: this is not H.264 - the game's video player on Windows plays H.264/AAC reliably, other codecs may show a black screen.";
            return J(new { success = true, video = row, message = $"Uploaded {name}.{note}" });
        }

        public sealed class YoutubeBody { public string Url { get; set; } = ""; }

        /// <summary>Fetch a Creative Commons YouTube video (see YoutubeFetch): licence checked first, default-deny, rate limited, deleted after 3 days.</summary>
        [HttpPost("/admin/api/videos/youtube")]
        public async Task<ContentResult> Youtube([FromBody] YoutubeBody b)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            Directory.CreateDirectory(Dir);
            var r = await YoutubeFetch.FetchAsync(b?.Url ?? "", admin!.Id);
            if (!r.Ok) return J(new { success = false, message = r.Message }, r.Status);
            var e = r.Entry!;
            Console.WriteLine($"[admin] {admin.Username} ({admin.Id}) {(r.AlreadyHad ? "re-used" : "fetched")} YouTube video {e.VideoId} as {e.File}");
            return J(new { success = true, message = r.Message + $" Credit: \"{e.Title}\" by {e.Channel} (CC BY). Deleted automatically on {e.ExpiresAtUtc:yyyy-MM-dd}.", video = Row(new FileInfo(Path.Combine(Dir, e.File))) });
        }

        public sealed class DeleteBody { public string Name { get; set; } = ""; }

        [HttpPost("/admin/api/videos/delete")]
        public ContentResult Delete([FromBody] DeleteBody b)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string name = b?.Name ?? "";
            if (!SafeName.IsMatch(name)) return J(new { success = false, message = "Bad name." }, 400);
            string path = Path.Combine(Dir, name);
            if (!System.IO.File.Exists(path)) return J(new { success = false, message = "No such video." }, 404);
            System.IO.File.Delete(path);
            YoutubeFetch.Forget(name);
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) deleted video {name}");
            return J(new { success = true, message = $"Deleted {name}." });
        }
    }
}
