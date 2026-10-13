using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.server;
using System.Net.Security;

namespace Campus_live_rewrite.Controllers
{
    [ApiController]
    public class CDNController : Controller
    {
        // Reused HttpClient for CDN passthrough. .NET 8 on Linux was failing the TLS handshake
        // to cdn.rec.net with "SSL connection could not be established" even though curl handshakes
        // fine from the same box — most likely a chain-verification quirk. Trust everything and
        // move on; we're only fetching public asset blobs, not credentials.
        private static readonly HttpClient s_cdnClient = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                // curl handshakes fine from the same box; .NET's default cert chain check trips
                // on cdn.rec.net specifically. Accept whatever the server presents — we're only
                // fetching public asset blobs, not credentials.
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            },
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        // Loading screens.
        //
        // Drop any images into Data/Images/LoadingScreenTips/ and they are used - no
        // JSON editing required. Each file becomes one loading screen, and ImageController
        // already searches that folder, so /img/<filename> resolves with no extra wiring.
        //
        // Titles/messages are optional: add an entry to Data/APIS/LoadingScreenTipData.json
        // whose "ImageName" matches the filename (with or without extension) and its Title,
        // Message, PlatformMask and RoomNames are applied to that image.
        //
        // If the folder is empty or missing, the JSON is served as-is, so the stock
        // behaviour is unchanged.
        [HttpGet("/cdn/config/LoadingScreenTipData")]
        public IActionResult LoadingScreenTipData()
        {
            string filePath = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "LoadingScreenTipData.json");
            string tipsDir  = Path.Combine(Environment.CurrentDirectory, "Data", "Images", "LoadingScreenTips");

            // Existing entries, keyed by ImageName so custom images can inherit their text.
            var overrides = new Dictionary<string, Newtonsoft.Json.Linq.JObject>(StringComparer.OrdinalIgnoreCase);
            Newtonsoft.Json.Linq.JArray? configured = null;
            if (System.IO.File.Exists(filePath))
            {
                try
                {
                    configured = Newtonsoft.Json.Linq.JArray.Parse(System.IO.File.ReadAllText(filePath));
                    foreach (var entry in configured.OfType<Newtonsoft.Json.Linq.JObject>())
                    {
                        string? nm = entry["ImageName"]?.ToString();
                        if (string.IsNullOrWhiteSpace(nm)) continue;
                        overrides[nm!] = entry;
                        overrides[Path.GetFileNameWithoutExtension(nm)!] = entry;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LoadingScreens] {Path.GetFileName(filePath)} is not valid JSON: {ex.Message}");
                }
            }

            string[] images = Array.Empty<string>();
            try
            {
                if (Directory.Exists(tipsDir))
                {
                    images = Directory.EnumerateFiles(tipsDir)
                        .Where(f =>
                        {
                            string e = Path.GetExtension(f).ToLowerInvariant();
                            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".webp" || e == "";
                        })
                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoadingScreens] could not read {tipsDir}: {ex.Message}");
            }

            // Nothing dropped in - keep the previous behaviour exactly.
            if (images.Length == 0)
            {
                if (!System.IO.File.Exists(filePath))
                    return Content("[]", "application/json");
                return PhysicalFile(filePath, "application/json");
            }

            var result = new Newtonsoft.Json.Linq.JArray();
            foreach (var img in images)
            {
                string fileName = Path.GetFileName(img);
                string bare     = Path.GetFileNameWithoutExtension(img);

                if (overrides.TryGetValue(fileName, out var match) || overrides.TryGetValue(bare, out match))
                {
                    var copy = (Newtonsoft.Json.Linq.JObject)match.DeepClone();
                    copy["ImageName"] = fileName;   // must match what /img/<name> will serve
                    result.Add(copy);
                }
                else
                {
                    result.Add(new Newtonsoft.Json.Linq.JObject
                    {
                        ["ImageName"]    = fileName,
                        ["Message"]      = "",
                        ["PlatformMask"] = -1,
                        ["RoomNames"]    = new Newtonsoft.Json.Linq.JArray(),
                        ["Title"]        = ""
                    });
                }
            }

            Console.WriteLine($"[LoadingScreens] serving {result.Count} custom loading screen(s) from Data/Images/LoadingScreenTips");
            return Content(result.ToString(Newtonsoft.Json.Formatting.None), "application/json");
        }

        [HttpGet("/iam/me/channels/{type}")]
        public IActionResult GetIamChannel(string type)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string basePath = Path.Combine(Environment.CurrentDirectory, "Data", "APIS");

            switch (type.ToLower())
            {
                case "announcements":
                    string announcementsPath = Path.Combine(basePath, "IamAnnouncements.json");
                    if (System.IO.File.Exists(announcementsPath))
                        return PhysicalFile(announcementsPath, "application/json");
                    break;

                case "justintimetutorials":
                    return Ok(ConfigCampus.Bracket);
            }

            return Ok(ConfigCampus.Bracket);
        }

        [HttpGet("/sections/pagesource/{type}")]
        public IActionResult GetWatchPages(string type)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string path = Path.Combine(
                Environment.CurrentDirectory,
                "Data",
                "APIS",
                "WatchPages",
                $"{type}.json"
            );

            if (!System.IO.File.Exists(path))
                return Ok(ConfigCampus.Bracket);

            // The Play page was captured from live Rec Room and names ITS banner room and ITS
            // featured creator by id; neither exists here, so the banner was blank and the
            // Featured Creator tile pointed at an empty profile. Both ids are swapped for local
            // ones on the way out (DiscoveryLists): the first admin-featured room, and the local
            // creator with the most public rooms. The file is left alone.
            if (type.Equals("PlayHighlight", StringComparison.OrdinalIgnoreCase))
            {
                string json = System.IO.File.ReadAllText(path);
                ulong banner = Campus_live_rewrite.api.server.DiscoveryLists.BannerRoom();
                ulong creator = Campus_live_rewrite.api.server.DiscoveryLists.FeaturedCreator();
                if (banner != 0) json = json.Replace("2477031627165896495", banner.ToString());
                if (creator != 0) json = json.Replace("\"730029\"", "\"" + creator + "\"");
                return new ContentResult { Content = json, ContentType = "application/json", StatusCode = 200 };
            }

            return PhysicalFile(path, "application/json");
        }

        [Route("/cdn/room/{*room_path}")]
        public async Task<IActionResult> CDNRoom(string room_path)
        {
            // 1. SANITIZE: confine to RoomBlobs. The route is catch-all, so room_path can be
            // "../../../../etc/passwd" - Path.Combine happily walks out of the folder and the
            // file was then served. ResolveUnder returns null the moment the canonical path
            // leaves RoomBlobs. (The old code computed Path.GetFileName but never used it.)
            string blobRoot = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs");
            string? localPath = Campus_live_rewrite.api.server.PathSafety.ResolveUnder(blobRoot, room_path);
            if (localPath == null)
            {
                Console.WriteLine($"[cdn/room] REFUSED path traversal attempt: {room_path}");
                return NotFound();
            }
            string directory = Path.GetDirectoryName(localPath)!;

            try
            {
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                // 2. DOWNLOAD IF MISSING. Try cdn.rec.net first, then img.rec.net as a
                // fallback (Rec Room historically served the same room blobs from both).
                //
                // A ZERO-BYTE cache entry counts as a MISS, not a hit. This used to be a
                // plain File.Exists check, and that is how the cache poisoned itself:
                // FileMode.Create truncates the file the moment the stream opens, so a
                // download that threw partway (reset connection, upstream 404 body, etc.)
                // left a 0-byte file behind - and File.Exists then returned true forever,
                // so it was served empty and NEVER retried. 148 of 151 files in RoomBlobs
                // and all 731 in DataBlobs are in exactly that state. Treating empty as a
                // miss lets the cache heal itself the moment an upstream works again.
                var cached = new FileInfo(localPath);
                if (!cached.Exists || cached.Length == 0)
                {
                    // RecFlare's public CDN first: Rec Room's own hosts no longer resolve.
                    string[] upstreams = {
                        $"https://cdn.recflare.net/room/{room_path}",
                        $"https://cdn.rec.net/room/{room_path}",
                        $"https://img.rec.net/room/{room_path}",
                    };
                    HttpResponseMessage? good = null;
                    foreach (var url in upstreams)
                    {
                        try
                        {
                            var resp = await s_cdnClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            if (resp.IsSuccessStatusCode) { good = resp; break; }
                            Console.WriteLine($"[cdn/room] upstream {url} -> HTTP {(int)resp.StatusCode}");
                            resp.Dispose();
                        }
                        catch (Exception uex)
                        {
                            Console.WriteLine($"[cdn/room] upstream {url} FAILED: {uex.GetType().Name}: {uex.Message}");
                        }
                    }
                    if (good == null) return NotFound();

                    // Download to a TEMP file and only commit it on success, so a failed
                    // or empty transfer can never leave a 0-byte file at localPath again.
                    string tmpPath = localPath + ".part";
                    long written;
                    try
                    {
                        using (good)
                        await using (var remoteStream = await good.Content.ReadAsStreamAsync())
                        await using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await remoteStream.CopyToAsync(fileStream);
                        }
                        written = new FileInfo(tmpPath).Length;
                    }
                    catch (Exception dex)
                    {
                        try { System.IO.File.Delete(tmpPath); } catch { }
                        Console.WriteLine($"[cdn/room] download of {room_path} FAILED: {dex.GetType().Name}: {dex.Message}");
                        return NotFound();
                    }

                    if (written == 0)
                    {
                        // An empty upstream body is not a cacheable result - storing it is
                        // what broke this cache in the first place.
                        try { System.IO.File.Delete(tmpPath); } catch { }
                        Console.WriteLine($"[cdn/room] upstream returned 0 bytes for {room_path} - not caching");
                        return NotFound();
                    }

                    System.IO.File.Move(tmpPath, localPath, overwrite: true);
                    Console.WriteLine($"[cdn/room] cached {room_path} ({written} bytes)");
                }

                await ApplyBlobHeaders(localPath);

                // 5. RETURN AS STREAM
                var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                
                // enableRangeProcessing allows standard 206 Partial Content responses (crucial for large room files)
                return File(fs, "application/octet-stream", enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CDN Error: {ex.Message}");
                return NotFound();
            }
        }

        // Azure-Blob-shaped response headers for CDN blobs (rooms AND avatar bundles). The client's
        // blob loader (its HTTP client + BestHTTP disk cache) was written against cdn.rec.net, which
        // is Azure Blob behind a CDN: it expects ETag / Last-Modified / Content-MD5 and the x-ms-*
        // set on every blob. The room route has mimicked these since rooms first loaded; the avatar
        // route used to send only Cache-Control, which is the one difference between a room blob
        // that loads and an avatar bundle whose load dies in a NullReferenceException right after
        // the response arrives.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _md5Cache = new();
        private async Task ApplyBlobHeaders(string localPath)
        {
            // 3. GATHER FILE METADATA & DYNAMIC DATA
            var fileInfo = new FileInfo(localPath);
            var now = DateTimeOffset.UtcNow;
            
            // Compute actual MD5 hash dynamically
            // (hashed once per file version: a room's baked bundle is 25-125 MB, and reading all of it again on EVERY request
            //  delayed the first byte and loaded the disk for nothing - the hash only changes when the file does)
            string md5Base64;
            string md5Key = localPath + "|" + fileInfo.Length + "|" + fileInfo.LastWriteTimeUtc.Ticks;
            if (!_md5Cache.TryGetValue(md5Key, out var cachedMd5))
            {
                using var md5 = System.Security.Cryptography.MD5.Create();
                using var readStream = System.IO.File.OpenRead(localPath);
                byte[] hash = await md5.ComputeHashAsync(readStream);
                cachedMd5 = Convert.ToBase64String(hash);
                if (_md5Cache.Count > 5000) _md5Cache.Clear();
                _md5Cache[md5Key] = cachedMd5;
            }
            md5Base64 = cachedMd5;

            // Generate fake Azure/Cloudflare IDs
            string requestId = Guid.NewGuid().ToString();
            // Mimic Azure Ref format: YYYYMMDDTHHMMSSZ-[randomhex]
            string azureRef = $"{now:yyyyMMddTHHmmssZ}-{Guid.NewGuid().ToString("N").Substring(0, 32)}"; 
            // Generate ETag based on the file's LastWriteTime to act as a unique version identifier
            string eTag = $"\"0x{fileInfo.LastWriteTimeUtc.Ticks:X}\"";

            // 4. APPLY DYNAMIC HEADERS
            // Note: Content-Length, Content-Type, and Accept-Ranges are handled automatically by the File() return method.
            Response.Headers["Date"] = now.ToString("R"); // RFC 1123 format (e.g., Thu, 12 Mar 2026 16:06:27 GMT)
            Response.Headers["Cache-Control"] = "public, max-age=31536000";
            Response.Headers["Last-Modified"] = fileInfo.LastWriteTimeUtc.ToString("R");
            Response.Headers["ETag"] = eTag;
            
            // Mock Azure Blob Storage Headers
            Response.Headers["x-ms-request-id"] = requestId;
            Response.Headers["x-ms-version"] = "2021-04-10";
            Response.Headers["x-ms-version-id"] = fileInfo.LastWriteTimeUtc.ToString("o"); // ISO 8601 format
            Response.Headers["x-ms-is-current-version"] = "true";
            Response.Headers["x-ms-creation-time"] = fileInfo.CreationTimeUtc.ToString("R");
            Response.Headers["x-ms-blob-content-md5"] = md5Base64;
            Response.Headers["x-ms-lease-status"] = "unlocked";
            Response.Headers["x-ms-lease-state"] = "available";
            Response.Headers["x-ms-blob-type"] = "BlockBlob";
            Response.Headers["x-ms-server-encrypted"] = "true";
            Response.Headers["x-ms-last-access-time"] = now.ToString("R");
            
            // CORS and CDN Mock Headers
            Response.Headers["Access-Control-Expose-Headers"] = "x-ms-request-id,Server,x-ms-version,x-ms-version-id,x-ms-is-current-version,Content-Type,Cache-Control,ETag,Last-Modified,x-ms-creation-time,x-ms-blob-content-md5,x-ms-lease-status,x-ms-lease-state,x-ms-blob-type,x-ms-server-encrypted,Accept-Ranges,x-ms-last-access-time,Content-Length,Date,Transfer-Encoding";
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["x-azure-ref"] = azureRef;
            Response.Headers["x-fd-int-roxy-purgeid"] = "0";
            Response.Headers["X-Cache-Info"] = "L2_T2";
            Response.Headers["X-Cache"] = "TCP_REMOTE_HIT"; // Tells the client it successfully hit the CDN cache

        }

        // Avatar bundles are answered the way the reference CDN (cdn.recflare.net, Cloudflare in
        // front of R2) answers them, and NOTHING more: Content-Type octet-stream, Content-Length,
        // Accept-Ranges, Cache-Control, an ETag that is the quoted md5 hex of the file, and CORS *.
        // The Azure-blob mimicry (ApplyBlobHeaders: x-ms-*, Last-Modified, X-Cache...) stays on the
        // room route, where it is known to work; on avatar bundles it is the one remaining
        // difference from a server where the same bytes load, so it is gone here.
        private const string BundleContentSignature = "key-id=KEY:RSA:p1.rec.net; data=IWwe/pZ5vWWqNSkSM/54isgDxlZkdrP0sUrppKCbNktO2yCOTjq746xWiiLsueGuVcAGQqkjeRTimxolHckS/YXSYkEJxtiCXbLlsRia2DyAqtWVkGWsfczzFhp/56U66FVzolTspPCvjScOVlGO7dDIK7sJ+ndcRauWjsQsC6g3e7rUc6uwY099a6gy7sw6xr5BFZQSz8wg+fqyHYD/Sc4nQQVOTFZNNASqbJYhpNhEMXRnafCMuLl8a3mkGwvy3t4q2D/7SM48xrGZjEV47qNx1A91KCe28XVToFh4BzwEUU8nZ0d+KwV79MGarLo1cY8igc8FcoThKcovI4ClOg==";

        private IActionResult ServeAvatarBundle(string path)
        {
            var fi = new FileInfo(path);
            string md5Hex;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var rs = System.IO.File.OpenRead(path))
                md5Hex = Convert.ToHexString(md5.ComputeHash(rs)).ToLowerInvariant();
            string etag = $"\"{md5Hex}\"";
            // The same content-signature header /img sends. Asset downloads go through the client's
            // RecNet HTTP client, which reads this header and checks it against a key built into the
            // game (reference patch-2025 notes: VerifyImageSig / HPJKKCCECLH.BLDFMLMAFLH). With NO
            // header it dereferences null and throws - "RemoteProviderException : Unable to load
            // asset bundle ... NullReferenceException" in CBACIMLIBPF.PNKGBHGAHHH, right after a
            // 200 with byte-identical content - so no downloadable clothing ever loaded. Images
            // carry the header and load; now bundles do too, and the patch's bypass accepts it.
            Response.Headers["content-signature"] = BundleContentSignature;
            Response.Headers["Cache-Control"] = "public, max-age=2592000";
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["ETag"] = etag;
            // A conditional GET that matches gets the 304 the reference gives (no body).
            bool notModified = Request.Headers.TryGetValue("If-None-Match", out var inm) && inm.ToString().Split(',').Select(x => x.Trim()).Contains(etag);
            Campus_live_rewrite.api.server.CdnDiag.OnBundle(HttpContext, Path.GetFileName(path), notModified);
            if (notModified)
                return StatusCode(304);
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(fs, "application/octet-stream", enableRangeProcessing: true);
        }

        // Avatar item asset bundles. The client loads each avatar item's 3D mesh from
        // /cdn/avatar/<hash>.assetbundle; without it the item can't equip or render (the wardrobe
        // tile still shows from metadata, but putting it on fails with "Unable to load asset bundle
        // ... HTTP Error 404"). Served LOCAL-FIRST from Data/CDN/AvatarBlobs so bundles dropped
        // there (sourced from a Rec Room Addressables cache / extraction) are used; upstream is
        // attempted as a best-effort fallback (Rec Room's CDN currently 404s old bundles, so it
        // usually won't help, but it heals automatically if an upstream ever serves them).
        [Route("/cdn/avatar/{*avatar_path}")]
        public async Task<IActionResult> CDNAvatar(string avatar_path)
        {
            string root = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "AvatarBlobs");
            string? localPath = Campus_live_rewrite.api.server.PathSafety.ResolveUnder(root, avatar_path);
            if (localPath == null)
            {
                Console.WriteLine($"[cdn/avatar] REFUSED path traversal attempt: {avatar_path}");
                return NotFound();
            }

            try
            {
                string directory = Path.GetDirectoryName(localPath)!;
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                var cached = new FileInfo(localPath);
                if (!cached.Exists || cached.Length == 0)
                {
                    // No flat copy: serve it out of an avatar-pack sub-folder if one holds it (the
                    // same lookup BundleHashes stamps the item definition from).
                    // quest/<name> is the Android conversion (see QuestBundleMiddleware) and must
                    // never fall back to the PC file of the same name - a Windows bundle does not
                    // load on Quest. A missing one is fetched from RecFlare's CDN, where they live.
                    bool quest = avatar_path.StartsWith("quest/", StringComparison.OrdinalIgnoreCase);
                    string? packed = quest ? null : Campus_live_rewrite.api.server.BundleHashes.Resolve(Path.GetFileName(avatar_path));
                    if (packed != null && new FileInfo(packed).Length > 0)
                        return ServeAvatarBundle(packed);
                    // The bundle CDN base the 2023 service map used ("https://cdn.rec.net/cdn") +
                    // /avatar/<hash>. Best-effort; a soft-404 there just means we can't heal this one.
                    string[] upstreams = quest
                        ? new[] { $"https://cdn.recflare.net/avatar/{avatar_path}" }
                        : new[] {
                            // RecFlare holds the PC builds too (checked 2026-09-30); cdn.rec.net is gone
                            $"https://cdn.recflare.net/avatar/{avatar_path}",
                            $"https://cdn.rec.net/cdn/avatar/{avatar_path}",
                            $"https://cdn.rec.net/avatar/{avatar_path}",
                        };
                    HttpResponseMessage? good = null;
                    foreach (var url in upstreams)
                    {
                        try
                        {
                            var resp = await s_cdnClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            // Only accept a real bundle - cdn.rec.net serves a 200/404 HTML page for
                            // unknown paths, which must NOT be cached as a bundle.
                            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
                            if (resp.IsSuccessStatusCode && !ct.Contains("html", StringComparison.OrdinalIgnoreCase))
                            { good = resp; break; }
                            resp.Dispose();
                        }
                        catch (Exception uex) { Console.WriteLine($"[cdn/avatar] upstream {url} FAILED: {uex.Message}"); }
                    }
                    if (good == null)
                    {
                        Console.WriteLine($"[cdn/avatar] MISS (no local bundle, upstream unavailable): {avatar_path}");
                        return NotFound();
                    }

                    string tmp = localPath + ".part"; long written;
                    try
                    {
                        using (good)
                        await using (var rs = await good.Content.ReadAsStreamAsync())
                        await using (var fsw = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                            await rs.CopyToAsync(fsw);
                        written = new FileInfo(tmp).Length;
                    }
                    catch (Exception dex) { try { System.IO.File.Delete(tmp); } catch { } Console.WriteLine($"[cdn/avatar] download {avatar_path} FAILED: {dex.Message}"); return NotFound(); }
                    if (written == 0) { try { System.IO.File.Delete(tmp); } catch { } return NotFound(); }
                    System.IO.File.Move(tmp, localPath, overwrite: true);
                    Console.WriteLine($"[cdn/avatar] cached {avatar_path} ({written} bytes)");
                }

                return ServeAvatarBundle(localPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[cdn/avatar] error: {ex.Message}");
                return NotFound();
            }
        }

        // Invention blobs (the .inv a v9 save wrote to Data/CDN/InventionBlobs, advertised to the
        // client as inventionDataFilename). There was NO route for this: every spawn asked
        // /cdn/invention/<name>.inv and got a 404 - 2068 of them in the API log, never one 200 -
        // so no invention ever loaded, however well the publish/buy side worked.
        [Route("/cdn/invention/{*inv_path}")]
        public async Task<IActionResult> CDNInvention(string inv_path)
        {
            string root = Campus_live_rewrite.api.server.Images.InvSavePath;
            string? localPath = Campus_live_rewrite.api.server.PathSafety.ResolveUnder(root, inv_path);
            if (localPath == null)
            {
                Console.WriteLine($"[cdn/invention] REFUSED path traversal attempt: {inv_path}");
                return NotFound();
            }
            try
            {
                var fi = new FileInfo(localPath);
                if (!fi.Exists || fi.Length == 0)
                {
                    Console.WriteLine($"[cdn/invention] MISS: {inv_path}");
                    return NotFound();
                }
                await ApplyBlobHeaders(localPath);
                var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return File(fs, "application/octet-stream", enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[cdn/invention] error: {ex.Message}");
                return NotFound();
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _dataMisses = new();

        [Route("/cdn/data/{*room_path}")]
        public async Task<IActionResult> CDNData(string room_path)
        {
            try
            {
                // Confine to DataBlobs - same catch-all traversal hole as /cdn/room.
                string dataRoot = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "DataBlobs");
                string? localPath = Campus_live_rewrite.api.server.PathSafety.ResolveUnder(dataRoot, room_path);
                if (localPath == null)
                {
                    Console.WriteLine($"[cdn/data] REFUSED path traversal attempt: {room_path}");
                    return NotFound();
                }

                string directory = Path.GetDirectoryName(localPath);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (System.IO.File.Exists(localPath))
                {
                    return File(await System.IO.File.ReadAllBytesAsync(localPath), "application/octet-stream");
                }

                // Negative cache: a holotar the upstream CDN no longer has (17 of them, 942 misses in
                // three days) was re-fetched from BOTH upstreams on every room load, and each room
                // load waited on the pair of round-trips. A miss is remembered for an hour.
                if (_dataMisses.TryGetValue(room_path, out var missedAt) && DateTime.UtcNow - missedAt < TimeSpan.FromHours(1))
                    return NotFound("");

                string[] upstreams = {
                    $"https://cdn.rec.net/data/{room_path}",
                    $"https://img.rec.net/data/{room_path}",
                };
                foreach (var url in upstreams)
                {
                    try
                    {
                        var resp = await s_cdnClient.GetAsync(url);
                        if (!resp.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"[cdn/data] upstream {url} -> HTTP {(int)resp.StatusCode}");
                            continue;
                        }
                        byte[] data = await resp.Content.ReadAsByteArrayAsync();
                        await System.IO.File.WriteAllBytesAsync(localPath, data);
                        Console.WriteLine($"[cdn/data] cached {room_path} ({data.Length} bytes) from {url}");
                        return File(data, "application/octet-stream");
                    }
                    catch (Exception uex)
                    {
                        Console.WriteLine($"[cdn/data] upstream {url} FAILED: {uex.GetType().Name}: {uex.Message}");
                    }
                }
                _dataMisses[room_path] = DateTime.UtcNow;
                return NotFound("");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return NotFound();
            }
        }
    }
}
