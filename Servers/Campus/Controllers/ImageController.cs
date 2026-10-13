using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Campus_live_rewrite.Controllers
{
    public class ImagesController : Controller
    {
        private static readonly HttpClient _recNetClient = new() { Timeout = TimeSpan.FromSeconds(3) };
        private static DateTime _recNetRetryAfter = DateTime.MinValue;

        // Pictures asked for at the site ROOT (/DefaultPFP.png, /pfp_6_....png, /<polaroid>.png -
        // 400+ such 404s a day): the same files the image store serves under /img. Anything the
        // static website doesn't have at that name is sent there, query string and all.
        [HttpGet(@"/{file:regex(^\w[[\w.-]]*\.(png|jpg|jpeg)$)}")]
        public IActionResult RootImage(string file) => Redirect("/img/" + file + Request.QueryString);

        [Route("/img/{*img_path}")]
        public async Task<IActionResult> ImgServer(string img_path, int width = 0, int height = 0, string sig = "p1")
        {
            img_path = Uri.UnescapeDataString(img_path ?? "").TrimStart('/');

            // Confine to Data/Images. This route is catch-all too, and every candidate path
            // below appends img_path to a subfolder of Data/Images - so "../../../etc/passwd"
            // walked straight out and File.Exists happily served it. One check on the shared
            // parent is enough: nothing legitimate lives outside Data/Images, and the deeper
            // candidate folders are all beneath it. Also blocks the img.rec.net fallback URL
            // being built from a traversal string.
            string imagesRoot = Path.Combine(Environment.CurrentDirectory, "Data", "Images");
            if (Campus_live_rewrite.api.server.PathSafety.ResolveUnder(imagesRoot, img_path) == null)
            {
                Console.WriteLine($"[img] REFUSED path traversal attempt: {img_path}");
                return NotFound();
            }

            HttpContext.Response.Headers.Append("content-signature", "key-id=KEY:RSA:p1.rec.net; data=IWwe/pZ5vWWqNSkSM/54isgDxlZkdrP0sUrppKCbNktO2yCOTjq746xWiiLsueGuVcAGQqkjeRTimxolHckS/YXSYkEJxtiCXbLlsRia2DyAqtWVkGWsfczzFhp/56U66FVzolTspPCvjScOVlGO7dDIK7sJ+ndcRauWjsQsC6g3e7rUc6uwY099a6gy7sw6xr5BFZQSz8wg+fqyHYD/Sc4nQQVOTFZNNASqbJYhpNhEMXRnafCMuLl8a3mkGwvy3t4q2D/7SM48xrGZjEV47qNx1A91KCe28XVToFh4BzwEUU8nZ0d+KwV79MGarLo1cY8igc8FcoThKcovI4ClOg==");
            HttpContext.Response.Headers.Append("Content-Disposition", $"inline; filename=\"{Path.GetFileName(img_path)}\"");
            HttpContext.Response.Headers.Append("Access-Control-Allow-Origin", "*");
            HttpContext.Response.Headers.Append("Access-Control-Allow-Headers", "*");
            Response.Headers["Cache-Control"] = "public, max-age=14400";

            HttpContext.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, Authorization, Cache-Control");
            HttpContext.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");

            // Stable across restarts (string.GetHashCode is randomised per process) and per variant.
            var etag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(img_path + Request.QueryString))).Substring(0, 16)}\"";
            HttpContext.Response.Headers.Append("ETag", etag);

            //return File(await System.IO.File.ReadAllBytesAsync(Path.Combine(Environment.CurrentDirectory, "Data", "Images",  "imageee.jpg")), "image/png");

            // ?thumb=1 (opt-in, so the game and the site get exactly what they got before): a SMALL JPEG preview - width
            // 320 unless width/height are given (never above 640), quality 70 - for galleries that show many photos at
            // once (the admin Photos tab). A 1024 px photo is ~400 KB as PNG and ~25 KB like this.
            bool thumb = Request.Query.TryGetValue("thumb", out var thumbVal) && (thumbVal == "1" || string.Equals(thumbVal, "true", StringComparison.OrdinalIgnoreCase));
            if (thumb)
            {
                if (width <= 0 && height <= 0) width = 320;
                width = Math.Min(width, 640); height = Math.Min(height, 640);
                Response.Headers["Cache-Control"] = "public, max-age=86400";
            }

            bool cropSquare = HttpContext.Request.Query.ContainsKey("cropsquare") &&
                (HttpContext.Request.Query["cropsquare"].ToString().ToLower() == "true" ||
                 HttpContext.Request.Query["cropsquare"].ToString() == "1");

            string[] paths = new[]
            {
                Path.Combine(Environment.CurrentDirectory, "Data", "Images", img_path),
                Path.Combine(Environment.CurrentDirectory, "Data", "Images", "rro_images", img_path),
                Path.Combine(Environment.CurrentDirectory, "Data", "Images", "rro_images", "base_rros", img_path),
                Path.Combine(Environment.CurrentDirectory, "Data", "Images", "player_images", img_path),
                Path.Combine(Environment.CurrentDirectory, "Data", "Images", "polaroid_images", img_path),
                Path.Combine(Environment.CurrentDirectory, "Data", "Images", "DefaultImgs", img_path),
                Path.Combine(Environment.CurrentDirectory, "Data", "Images", "rec_net", img_path),
                Path.Combine(Environment.CurrentDirectory, "Data", "Images", "custom_shirt_imgs", img_path),
                Path.Combine(Environment.CurrentDirectory, "Data", "Images", "LoadingScreenTips", img_path)
                //Path.Combine(Environment.CurrentDirectory, "Data", "cdn_rooms", img_path)
            };

            foreach (var path in paths)
            {
                if (System.IO.File.Exists(path))
                {
                    try
                    {
                        return File(await ProcessedCached(path, cropSquare, width, height, thumb), thumb ? "image/jpeg" : "image/png");
                    }
                    catch
                    {
                        var fallbackBytes = await System.IO.File.ReadAllBytesAsync(path);
                        return File(fallbackBytes, "image/png");
                    }
                }
            }

            string recNetLocalPath = Path.Combine(Environment.CurrentDirectory, "Data", "Images", "rec_net", img_path);
            // img.rec.net is gone. Every miss used to open a fresh HttpClient and sit through a
            // full TLS failure (seconds) before falling through - hundreds of times per boot, and
            // each one stalled the request that wanted the image. Now: one shared client with a
            // short timeout, and after any failure the upstream is skipped for 10 minutes.
            if (!System.IO.File.Exists(recNetLocalPath) && DateTime.UtcNow >= _recNetRetryAfter)
            {
                try
                {
                    byte[] data = await _recNetClient.GetByteArrayAsync($"https://img.rec.net/{img_path}");
                    Directory.CreateDirectory(Path.GetDirectoryName(recNetLocalPath)!);
                    await System.IO.File.WriteAllBytesAsync(recNetLocalPath, data);

                    try
                    {
                        var processed = await ProcessImageAsync(data, cropSquare, width, height);
                        return File(processed, GetMimeType(img_path));
                    }
                    catch
                    {
                        return File(data, GetMimeType(img_path));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    _recNetRetryAfter = DateTime.UtcNow.AddMinutes(10);
                    Console.WriteLine($"[rec_net fetch failed] {ex.Message} - skipping img.rec.net for 10 minutes");
                }
            }
            else if (System.IO.File.Exists(recNetLocalPath))
            {
                // Read inside the try: the file can be gone between Exists and the read (a
                // concurrent fetch writing it, a cleanup) and that was a 500 with a stack trace
                // in the journal instead of the 404 the client handles.
                try
                {
                    try
                    {
                        return File(await ProcessedCached(recNetLocalPath, cropSquare, width, height, thumb), thumb ? "image/jpeg" : GetMimeType(img_path));
                    }
                    catch (IOException) { throw; }
                    catch
                    {
                        return File(await System.IO.File.ReadAllBytesAsync(recNetLocalPath), GetMimeType(img_path));
                    }
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[img] rec_net/{img_path} unreadable: {ex.Message}");
                }
            }

            // Was: "rro_image" (singular). The directory on disk is "rro_images", and
            // DefaultRoomImage.jpg actually lives in "rec_net" - so the path was wrong
            // twice, File.Exists was ALWAYS false, and this fell through to NotFound().
            // That is where the client's "HTTP Error 404" entries come from. Try the
            // real location first, then the plural directory, so a renamed/moved asset
            // still resolves instead of 404ing.
            foreach (var dir in new[] { "rec_net", "rro_images", "DefaultImgs" })
            {
                var notFoundImagePath = Path.Combine(
                    Environment.CurrentDirectory, "Data", "Images", dir, "DefaultRoomImage.jpg");
                if (System.IO.File.Exists(notFoundImagePath))
                {
                    var notFoundImageBytes = await System.IO.File.ReadAllBytesAsync(notFoundImagePath);
                    return File(notFoundImageBytes, "image/jpeg");
                }
            }

            return NotFound();
        }

        private static string GetMimeType(string filePath)
        {
            var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(filePath, out var contentType))
            {
                contentType = "image/png";
            }
            return contentType;
        }

        // ---- processed-image cache --------------------------------------------------------------
        // Every /img request used to decode, resize and PNG-encode the file from scratch: 0.6-1.5 s
        // of server time per banner / profile picture (2026-09-26: a 1024-wide banner took 1.8 s to
        // its first byte against a 0.29 s baseline), across ~250k image requests a day - and the
        // game host is uncached on the client, so every profile view paid it again. Each
        // (file, version, crop, size) result is now kept in memory, and concurrent requests for the
        // same variant share one encode. Keyed on the file's mtime + length, so a replaced file is
        // re-processed. Bounded; the least recently used variants go first.
        private sealed class CachedImage { public byte[] Bytes = Array.Empty<byte>(); public long LastUsed; }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedImage> _imgCache = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<byte[]>>> _imgInFlight = new();
        private static long _imgCacheBytes;
        private static readonly long ImgCacheMaxBytes = 768L * 1024 * 1024;
        private static readonly object _imgEvictLock = new();

        // A gallery asks for dozens of previews at once; each first encode is 0.6-1.5 s of CPU. At most two run at a
        // time, so a page of thumbnails fills in steadily instead of starving the game server of cores.
        private static readonly SemaphoreSlim _thumbGate = new(2);

        private static async Task<byte[]> ProcessedCached(string path, bool cropSquare, int width, int height, bool thumb = false)
        {
            var fi = new FileInfo(path);
            string key = $"{fi.FullName}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{(cropSquare ? 1 : 0)}|{width}|{height}|{(thumb ? "t" : "p")}";
            if (_imgCache.TryGetValue(key, out var hit))
            {
                hit.LastUsed = Environment.TickCount64;
                return hit.Bytes;
            }
            var lazy = _imgInFlight.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(async () =>
            {
                var raw = await System.IO.File.ReadAllBytesAsync(path);
                if (!thumb) return await ProcessImageAsync(raw, cropSquare, width, height);
                await _thumbGate.WaitAsync();
                try { return await ProcessImageAsync(raw, cropSquare, width, height, true); }
                finally { _thumbGate.Release(); }
            }));
            try
            {
                var bytes = await lazy.Value;
                if (_imgCache.TryAdd(key, new CachedImage { Bytes = bytes, LastUsed = Environment.TickCount64 }))
                {
                    if (Interlocked.Add(ref _imgCacheBytes, bytes.Length) > ImgCacheMaxBytes) EvictImages();
                }
                return bytes;
            }
            finally
            {
                _imgInFlight.TryRemove(key, out _);
            }
        }

        private static void EvictImages()
        {
            lock (_imgEvictLock)
            {
                if (Interlocked.Read(ref _imgCacheBytes) <= ImgCacheMaxBytes) return;
                long target = ImgCacheMaxBytes * 3 / 4;
                foreach (var kv in _imgCache.OrderBy(k => k.Value.LastUsed).ToList())
                {
                    if (Interlocked.Read(ref _imgCacheBytes) <= target) break;
                    if (_imgCache.TryRemove(kv.Key, out var gone)) Interlocked.Add(ref _imgCacheBytes, -gone.Bytes.Length);
                }
            }
        }

        private static bool IsPng(byte[] b) =>
            b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

        private static async Task<byte[]> ProcessImageAsync(byte[] imageBytes, bool cropSquare, int width, int height, bool thumb = false)
        {
            // A PNG that needs no crop and no shrinking goes out as is (it used to be decoded and
            // re-encoded anyway). Never UPSCALE either: an 800 px picture asked for at width=1024
            // was blown up and re-encoded into a bigger, blurrier file.
            if (!thumb && !cropSquare && IsPng(imageBytes))
            {
                if (width <= 0 && height <= 0) return imageBytes;
                var info = Image.Identify(imageBytes);
                if (info != null && (width <= 0 || width >= info.Width) && (height <= 0 || height >= info.Height))
                    return imageBytes;
            }

            using var ms = new MemoryStream(imageBytes);
            using var image = await Image.LoadAsync<Rgba32>(ms);

            if (!cropSquare && (width <= 0 || width >= image.Width) && (height <= 0 || height >= image.Height))
            {
                width = 0;
                height = 0;
            }

            if (cropSquare)
            {
                int size = Math.Min(image.Width, image.Height);
                int x = (image.Width - size) / 2;
                int y = (image.Height - size) / 2;
                image.Mutate(ctx => ctx.Crop(new Rectangle(x, y, size, size)));

                int targetSize = width > 0 ? width : height;
                if (targetSize > 0)
                {
                    image.Mutate(ctx => ctx.Resize(targetSize, targetSize));
                }
            }
            else if (width > 0 || height > 0)
            {
                int resizeWidth = width;
                int resizeHeight = height;

                if (width > 0 && height == 0)
                {
                    resizeHeight = (int)((double)image.Height / image.Width * width);
                }
                else if (height > 0 && width == 0)
                {
                    resizeWidth = (int)((double)image.Width / image.Height * height);
                }

                image.Mutate(ctx => ctx.Resize(resizeWidth, resizeHeight));
            }

            using var output = new MemoryStream();
            if (thumb) await image.SaveAsync(output, new JpegEncoder { Quality = 70 });
            else await image.SaveAsync(output, new PngEncoder());
            return output.ToArray();
        }
    }
}
