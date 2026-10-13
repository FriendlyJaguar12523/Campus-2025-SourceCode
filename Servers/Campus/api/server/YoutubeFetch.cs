using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Admin "Videos" tab: fetch a YouTube video that its uploader has released under Creative Commons (CC BY).
    ///
    /// The rule is DEFAULT-DENY. Before anything is downloaded the video is looked up with YouTube's own Data API and only
    /// <c>status.license == "creativeCommon"</c> - the uploader's explicit licence setting, not a guess - lets it through. Every
    /// other video (the standard "all rights reserved" licence, which is nearly all of YouTube) is refused, as are private / live /
    /// too-long videos. The download itself is done by yt-dlp on the exact 11-character id that was verified (never on text the
    /// admin typed), and the file is deleted again after <see cref="ConfigCampus.YoutubeFetchTtlDays"/> days.
    ///
    /// CC BY requires crediting the author: title and channel are stored next to the file and shown in the Videos tab.
    ///
    /// Needs on the server: a YouTube Data API key (env YOUTUBE_API_KEY or Data/youtube_api_key.txt), yt-dlp and ffmpeg on the PATH.
    /// </summary>
    public static class YoutubeFetch
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly object RegLock = new();
        private static readonly SemaphoreSlim OneAtATime = new(1, 1);
        private static readonly ConcurrentDictionary<ulong, List<DateTime>> Recent = new();
        private static Timer? _sweeper;

        private static string VideosDir => Path.Combine(Environment.CurrentDirectory, "Data", "Videos");
        private static string RegistryPath => Path.Combine(Environment.CurrentDirectory, "Data", "youtube-fetched.json");

        public sealed class Entry
        {
            public string File { get; set; } = "";
            public string VideoId { get; set; } = "";
            public string Title { get; set; } = "";
            public string Channel { get; set; } = "";
            public DateTime FetchedAtUtc { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public ulong By { get; set; }
        }

        // ---- the YouTube link ---------------------------------------------------------------------
        private static readonly Regex IdRe = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

        /// <summary>The 11-character video id of a youtube.com / youtu.be / shorts / embed / live link, or null if it is not one.</summary>
        public static string? ExtractId(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (!text.Contains("://")) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return null;
            string host = u.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host[4..];
            if (host.StartsWith("m.")) host = host[2..];
            string? id = null;
            if (host == "youtu.be") id = u.AbsolutePath.Trim('/').Split('/')[0];
            else if (host == "youtube.com" || host == "music.youtube.com" || host == "youtube-nocookie.com")
            {
                var seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length >= 1 && seg[0] == "watch")
                {
                    foreach (var kv in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = kv.Split('=', 2);
                        if (p.Length == 2 && p[0] == "v") { id = Uri.UnescapeDataString(p[1]); break; }
                    }
                }
                else if (seg.Length >= 2 && (seg[0] == "shorts" || seg[0] == "embed" || seg[0] == "live" || seg[0] == "v")) id = seg[1];
            }
            return id != null && IdRe.IsMatch(id) ? id : null;
        }

        // ---- registry of fetched files (what may be auto-deleted) -----------------------------------------
        private static List<Entry> Load()
        {
            try { if (System.IO.File.Exists(RegistryPath)) return JsonConvert.DeserializeObject<List<Entry>>(System.IO.File.ReadAllText(RegistryPath)) ?? new(); }
            catch (Exception ex) { Console.WriteLine($"[youtube] could not read the registry: {ex.Message}"); }
            return new();
        }
        private static void Save(List<Entry> l)
        {
            try { System.IO.File.WriteAllText(RegistryPath, JsonConvert.SerializeObject(l, Newtonsoft.Json.Formatting.Indented)); }
            catch (Exception ex) { Console.WriteLine($"[youtube] could not write the registry: {ex.Message}"); }
        }

        public static Entry? Find(string file) { lock (RegLock) return Load().FirstOrDefault(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase)); }

        /// <summary>Forget a file's registry entry (when an admin deletes the video by hand).</summary>
        public static void Forget(string file) { lock (RegLock) { var l = Load(); if (l.RemoveAll(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase)) > 0) Save(l); } }

        /// <summary>Deletes every fetched video whose time is up (and entries whose file is already gone). Only registered files are ever touched.</summary>
        public static int Sweep()
        {
            int removed = 0;
            lock (RegLock)
            {
                var l = Load(); var keep = new List<Entry>(); var now = DateTime.UtcNow;
                foreach (var e in l)
                {
                    string path = Path.Combine(VideosDir, e.File);
                    if (!System.IO.File.Exists(path)) { continue; }
                    if (e.ExpiresAtUtc <= now)
                    {
                        try { System.IO.File.Delete(path); removed++; Console.WriteLine($"[youtube] expired {e.File} ({e.VideoId}) - deleted"); }
                        catch (Exception ex) { Console.WriteLine($"[youtube] could not delete {e.File}: {ex.Message}"); keep.Add(e); }
                    }
                    else keep.Add(e);
                }
                if (keep.Count != l.Count) Save(keep);
            }
            return removed;
        }

        /// <summary>Called once at startup: sweeps now, then every hour.</summary>
        public static void StartSweeper()
        {
            _sweeper ??= new Timer(_ => { try { Sweep(); } catch (Exception ex) { Console.WriteLine($"[youtube] sweep failed: {ex.Message}"); } }, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
        }

        // ---- rate limit (per admin account, rolling hour) ------------------------------------------------
        private static bool RateLimited(ulong account, out int retryMinutes)
        {
            var now = DateTime.UtcNow;
            var list = Recent.GetOrAdd(account, _ => new List<DateTime>());
            lock (list)
            {
                list.RemoveAll(t => now - t > TimeSpan.FromHours(1));
                if (list.Count >= ConfigCampus.YoutubeFetchMaxPerHour)
                {
                    retryMinutes = Math.Max(1, (int)Math.Ceiling((list[0] + TimeSpan.FromHours(1) - now).TotalMinutes));
                    return true;
                }
                retryMinutes = 0;
                return false;
            }
        }
        private static void CountUse(ulong account) { var l = Recent.GetOrAdd(account, _ => new List<DateTime>()); lock (l) l.Add(DateTime.UtcNow); }

        // ---- the fetch ---------------------------------------------------------------------------------------
        public sealed class Result { public bool Ok; public string Message = ""; public int Status = 200; public Entry? Entry; public bool AlreadyHad; }
        private static Result Fail(string m, int status = 400) => new() { Ok = false, Message = m, Status = status };

        public static async Task<Result> FetchAsync(string link, ulong account)
        {
            string? id = ExtractId(link);
            if (id == null) return Fail("That is not a YouTube link (youtube.com/watch?v=..., youtu.be/..., /shorts/...).");
            string key = ConfigCampus.YoutubeApiKey;
            if (key.Length == 0) return Fail("No YouTube API key is set on the server (env YOUTUBE_API_KEY or Data/youtube_api_key.txt) - the licence of a video cannot be checked, so nothing is fetched.", 503);

            // already fetched and still on disk: hand it back (no new download, no rate-limit use)
            lock (RegLock)
            {
                var have = Load().FirstOrDefault(e => e.VideoId == id && System.IO.File.Exists(Path.Combine(VideosDir, e.File)));
                if (have != null) return new Result { Ok = true, Entry = have, AlreadyHad = true, Message = $"Already fetched: {have.File}." };
            }

            if (RateLimited(account, out int wait)) return Fail($"Slow down - at most {ConfigCampus.YoutubeFetchMaxPerHour} YouTube fetches per hour. Try again in about {wait} min.", 429);

            // 1. the licence check, on YouTube's own API. Default-deny.
            JObject? item;
            try
            {
                string url = $"{ConfigCampus.YoutubeApiBase}/youtube/v3/videos?part=status,snippet,contentDetails&id={id}&key={Uri.EscapeDataString(key)}";
                using var resp = await Http.GetAsync(url);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    string reason = "";
                    try { reason = (string?)JObject.Parse(body)["error"]?["message"] ?? ""; } catch { }
                    Console.WriteLine($"[youtube] Data API answered {(int)resp.StatusCode} for {id}: {reason}");
                    return Fail($"YouTube's API refused the licence lookup (HTTP {(int)resp.StatusCode}). Check the API key and its quota.", 502);
                }
                item = (JObject?)JObject.Parse(body)["items"]?.FirstOrDefault();
            }
            catch (Exception ex) { Console.WriteLine($"[youtube] licence lookup failed: {ex.GetType().Name}"); return Fail("Could not reach YouTube to check the licence - nothing was fetched.", 502); }
            if (item == null) return Fail("YouTube has no public video with that id.");

            string license = (string?)item["status"]?["license"] ?? "";
            string privacy = (string?)item["status"]?["privacyStatus"] ?? "";
            string title = (string?)item["snippet"]?["title"] ?? id;
            string channel = (string?)item["snippet"]?["channelTitle"] ?? "";
            string live = (string?)item["snippet"]?["liveBroadcastContent"] ?? "none";
            if (license != "creativeCommon")
            {
                Console.WriteLine($"[youtube] refused {id} (licence '{license}')");
                return Fail($"Refused: \"{title}\" is not Creative Commons - its uploader kept all rights (licence: {(license == "youtube" ? "standard YouTube licence" : license)}). Only videos whose uploader chose Creative Commons Attribution can be fetched.");
            }
            if (privacy != "public") return Fail("That video is not public.");
            if (live != "none") return Fail("Live streams and premieres cannot be fetched.");
            TimeSpan dur;
            try { dur = XmlConvert.ToTimeSpan((string?)item["contentDetails"]?["duration"] ?? "PT0S"); } catch { dur = TimeSpan.Zero; }
            if (dur <= TimeSpan.Zero) return Fail("Could not read that video's length.");
            if (dur.TotalSeconds > ConfigCampus.YoutubeFetchMaxSeconds)
                return Fail($"That video is {(int)dur.TotalMinutes} min long - the limit is {ConfigCampus.YoutubeFetchMaxSeconds / 60} min.");

            // 2. one download at a time
            if (!await OneAtATime.WaitAsync(TimeSpan.FromSeconds(5))) return Fail("Another YouTube fetch is running - try again in a minute.", 429);
            string tmp = Path.Combine(VideosDir, ".yt_" + Guid.NewGuid().ToString("N"));
            try
            {
                CountUse(account);
                Directory.CreateDirectory(tmp);
                var psi = new ProcessStartInfo(ConfigCampus.YtDlpPath)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = tmp,
                };
                // Plain argument list - nothing typed by the admin reaches a shell, and the url is rebuilt from the verified id.
                foreach (var a in new[]
                {
                    "--no-config", "--no-playlist", "--no-warnings", "--no-progress", "--no-part",
                    "--max-filesize", $"{ConfigCampus.YoutubeFetchMaxBytes}",
                    // H.264 + AAC in an mp4: what the game's video player plays reliably
                    "-f", "bv*[vcodec^=avc1][height<=720]+ba[acodec^=mp4a]/b[vcodec^=avc1][height<=720][ext=mp4]/b[ext=mp4]",
                    "--merge-output-format", "mp4",
                    "-o", "video.%(ext)s",
                    $"https://www.youtube.com/watch?v={id}",
                }) psi.ArgumentList.Add(a);

                string output;
                try
                {
                    using var p = Process.Start(psi)!;
                    var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                    try { await p.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { try { p.Kill(true); } catch { } return Fail("The download took too long and was stopped."); }
                    output = (await so) + "\n" + (await se);
                    if (p.ExitCode != 0)
                    {
                        Console.WriteLine($"[youtube] yt-dlp exit {p.ExitCode} for {id}: {output.Trim().Split('\n').LastOrDefault()}");
                        return Fail("The download failed" + (output.Contains("max-filesize") || output.Contains("larger than") ? " (the file is over the size limit)." : "."), 502);
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return Fail("yt-dlp is not installed on the server (or not on the PATH). Install yt-dlp and ffmpeg there - see Deploy/install-ytdlp.sh.", 503);
                }

                string? mp4 = Directory.GetFiles(tmp, "video.mp4").FirstOrDefault();
                if (mp4 == null) return Fail("The download produced no mp4 file (is ffmpeg installed on the server?).", 502);
                var fi = new FileInfo(mp4);
                if (fi.Length == 0 || fi.Length > ConfigCampus.YoutubeFetchMaxBytes) return Fail("The downloaded file is over the size limit.");
                var head = new byte[12];
                using (var fs = System.IO.File.OpenRead(mp4)) { if (fs.Read(head, 0, 12) < 12 || Encoding.ASCII.GetString(head, 4, 4) != "ftyp") return Fail("The download is not a valid mp4."); }

                string name = $"yt_{id}.mp4";
                var now = DateTime.UtcNow;
                var entry = new Entry { File = name, VideoId = id, Title = title, Channel = channel, FetchedAtUtc = now, ExpiresAtUtc = now.AddDays(ConfigCampus.YoutubeFetchTtlDays), By = account };
                lock (RegLock)
                {
                    System.IO.File.Move(mp4, Path.Combine(VideosDir, name), overwrite: true);
                    var l = Load(); l.RemoveAll(e => e.VideoId == id); l.Add(entry); Save(l);
                }
                Console.WriteLine($"[youtube] fetched {id} \"{title}\" by {channel} ({fi.Length} bytes) for account {account}; expires {entry.ExpiresAtUtc:u}");
                return new Result { Ok = true, Entry = entry, Message = $"Fetched \"{title}\" by {channel}." };
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
                OneAtATime.Release();
            }
        }
    }
}
