using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Admin panel "RecFlare CDN" tab: what cdn.recflare.net (the RecFlare reference server's public
    /// CDN) holds, and pulling files from it into this server.
    ///
    /// The CDN has NO listing - it only serves a file you name (/room/, /avatar/, /invention/, /data/,
    /// /config/, /sigs/). So this tab works from names: look one up, or scan every room save this
    /// server references but doesn't have (the rooms that load empty - Run The Block, My Little
    /// Monsters...) and restore the ones RecFlare has. A lookup is a one-byte Range request, answered
    /// 206 with the whole size in Content-Range. Restored room saves are servable at once
    /// (RoomBlobFilter checks the file on every read).
    /// </summary>
    public class AdminRecflareCdnController : ControllerBase
    {
        private const string Base = "https://cdn.recflare.net";
        private static readonly string[] Kinds = { "room", "avatar", "invention", "data", "config", "sigs" };
        // A file name (avatar bundles may sit under quest/). No "..", no leading slash.
        private static readonly Regex SafeName = new(@"^(quest/|\d{4}-\d{2}-\d{2}/)?[A-Za-z0-9][A-Za-z0-9._-]{0,200}$", RegexOptions.Compiled);
        private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromSeconds(60),
            DefaultRequestHeaders = { { "User-Agent", "CampusServer (admin RecFlare CDN tab)" } }
        };

        private static ContentResult J(object o, int status = 200) => new()
        { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = status };

        private static string? LocalDir(string kind) => kind switch
        {
            "room" => RoomBlobFilter.BlobDirectory,
            "avatar" => Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "AvatarBlobs"),
            _ => null
        };

        private static bool HaveLocally(string kind, string name)
        {
            var dir = LocalDir(kind);
            if (dir == null) return false;
            try { var fi = new FileInfo(Path.Combine(dir, name)); return fi.Exists && fi.Length > 0; } catch { return false; }
        }

        public sealed record Probe(bool Found, long Size, string? ContentType, int Status, string? Error);

        /// <summary>Does RecFlare have it? One byte is fetched; the size comes from Content-Range.</summary>
        public static async Task<Probe> ProbeAsync(string kind, string name, CancellationToken ct = default)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/{kind}/{name}");
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                string? type = resp.Content.Headers.ContentType?.MediaType;
                // a miss is a small JSON {"error":...}; a real file is binary (or text for config)
                bool found = (resp.StatusCode == HttpStatusCode.PartialContent || resp.StatusCode == HttpStatusCode.OK)
                             && !(kind != "config" && type != null && type.Contains("json"));
                long size = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? -1;
                return new Probe(found, found ? size : 0, type, (int)resp.StatusCode, null);
            }
            catch (Exception ex) { return new Probe(false, 0, null, 0, ex.Message); }
        }

        private static bool Valid(string? kind, string? name, out string why)
        {
            why = "";
            if (kind == null || !Kinds.Contains(kind)) { why = "Kind must be one of: " + string.Join(", ", Kinds) + "."; return false; }
            if (string.IsNullOrWhiteSpace(name) || !SafeName.IsMatch(name.Trim())) { why = "That isn't a file name (letters, digits, . _ - only; avatar bundles may start with quest/, RecFlare room files with a date folder)."; return false; }
            return true;
        }

        // ---- look one up ------------------------------------------------------------------------
        [HttpGet("/admin/api/recflarecdn/lookup")]
        public async Task<ContentResult> Lookup(string kind, string name)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!Valid(kind, name, out var why)) return J(new { success = false, message = why });
            name = name.Trim();
            var p = await ProbeAsync(kind, name, HttpContext.RequestAborted);
            return J(new
            {
                success = true, kind, name, url = $"{Base}/{kind}/{name}",
                found = p.Found, size = p.Size, contentType = p.ContentType, status = p.Status, error = p.Error,
                local = HaveLocally(kind, name), canRestore = p.Found && LocalDir(kind) != null
            });
        }

        // ---- every room save we reference but don't have -----------------------------------------
        private static readonly SemaphoreSlim _scanGate = new(1, 1);
        private static object? _lastScan;
        private static DateTime _lastScanAt;

        [HttpGet("/admin/api/recflarecdn/scan")]
        public async Task<ContentResult> Scan(bool refresh = false)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!refresh && _lastScan != null && DateTime.UtcNow - _lastScanAt < TimeSpan.FromMinutes(10)) return J(_lastScan);
            if (!await _scanGate.WaitAsync(0)) return J(new { success = false, message = "A scan is already running - try again in a moment." });
            try
            {
                var rows = new List<(ulong roomId, string room, long subId, string sub, string what, string blob)>();
                foreach (var r in RoomDB.GetAllRoomsIncludingDorms())
                {
                    if (!string.IsNullOrWhiteSpace(r.DataBlob) && r.DataBlob != RoomBlobFilter.DefaultRoomMetaBlob && !RoomBlobFilter.BlobHasContent(r.DataBlob))
                        rows.Add((r.RoomId, r.Name ?? "", 0, "", "room data", r.DataBlob!));
                    foreach (var s in r.SubRooms ?? new())
                    {
                        string? b = s.CurrentSave?.DataBlob;
                        if (!string.IsNullOrWhiteSpace(b) && !RoomBlobFilter.BlobHasContent(b))
                            rows.Add((r.RoomId, r.Name ?? "", (long)s.SubRoomId, s.Name ?? "", "save", b!));
                    }
                }
                // probe them, 8 at a time
                var results = new ConcurrentDictionary<string, Probe>();
                using var gate = new SemaphoreSlim(8);
                await Task.WhenAll(rows.Select(x => x.blob).Distinct().Select(async b =>
                {
                    await gate.WaitAsync();
                    try { results[b] = SafeName.IsMatch(b) ? await ProbeAsync("room", b) : new Probe(false, 0, null, 0, "unusual name"); }
                    finally { gate.Release(); }
                }));
                var list = rows.Select(x => new
                {
                    roomId = x.roomId, room = x.room, subRoomId = x.subId, subRoom = x.sub, what = x.what, blob = x.blob,
                    found = results[x.blob].Found, size = results[x.blob].Size, error = results[x.blob].Error
                }).OrderByDescending(x => x.found).ThenBy(x => x.room).ThenBy(x => x.subRoomId).ToList();
                _lastScan = new
                {
                    success = true, scannedAt = DateTime.UtcNow, missing = list.Count, onRecflare = list.Count(x => x.found),
                    rooms = list.Select(x => x.roomId).Distinct().Count(), rows = list
                };
                _lastScanAt = DateTime.UtcNow;
                Console.WriteLine($"[recflare-cdn] scan: {list.Count} missing room file(s), {list.Count(x => x.found)} on RecFlare");
                return J(_lastScan);
            }
            finally { _scanGate.Release(); }
        }

        // ---- pull one (or several) into this server ------------------------------------------------
        public sealed class RestoreBody { public string? Kind { get; set; } public List<string>? Names { get; set; } }

        [HttpPost("/admin/api/recflarecdn/restore")]
        public async Task<ContentResult> Restore([FromBody] RestoreBody body)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string kind = body?.Kind ?? "room";
            var dir = LocalDir(kind);
            if (dir == null) return J(new { success = false, message = "Only room and avatar files can be restored into the server." });
            var done = new List<object>();
            foreach (var raw in (body?.Names ?? new()).Take(200))
            {
                string name = raw?.Trim() ?? "";
                if (!Valid(kind, name, out var why)) { done.Add(new { name, ok = false, message = why }); continue; }
                string dest = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
                if (HaveLocally(kind, name)) { done.Add(new { name, ok = true, message = "already here" }); continue; }
                try
                {
                    using var resp = await Http.GetAsync($"{Base}/{kind}/{name}", HttpCompletionOption.ResponseHeadersRead);
                    var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                    if (!resp.IsSuccessStatusCode || type.Contains("json") || type.Contains("html"))
                    { done.Add(new { name, ok = false, message = $"RecFlare doesn't have it ({(int)resp.StatusCode})" }); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    string tmp = dest + ".part";
                    await using (var fs = System.IO.File.Create(tmp))
                        await resp.Content.CopyToAsync(fs);
                    long len = new FileInfo(tmp).Length;
                    if (len == 0) { System.IO.File.Delete(tmp); done.Add(new { name, ok = false, message = "empty download" }); continue; }
                    System.IO.File.Move(tmp, dest, overwrite: true);
                    done.Add(new { name, ok = true, message = "restored", size = len });
                    Console.WriteLine($"[recflare-cdn] {admin?.Id} restored {kind}/{name} ({len} bytes) from RecFlare");
                }
                catch (Exception ex) { done.Add(new { name, ok = false, message = ex.Message }); }
            }
            _lastScan = null;   // the next scan reflects what's here now
            return J(new { success = true, results = done });
        }

        // ---- RecFlare's rooms: the file list the CDN doesn't have ----------------------------------
        // The CDN can't list itself, but RecFlare's ROOMS service can: every public room record names
        // its save files (SubRooms[].CurrentSave.DataBlob), and those are exactly the files on the CDN.
        // rooms.recflare.net/rooms/bulk takes up to 100 ids; excludePrivateRooms=True skips dorms and
        // private rooms. The id space is walked once (~200 requests) and cached for 30 minutes.
        private const string RoomsApi = "https://rooms.recflare.net";
        private static readonly SemaphoreSlim _roomsGate = new(1, 1);
        private static List<Newtonsoft.Json.Linq.JObject>? _rfRooms;
        private static DateTime _rfRoomsAt;
        private static string ImportsPath => Path.Combine(Environment.CurrentDirectory, "Data", "recflare-imports.json");

        private static async Task<Newtonsoft.Json.Linq.JArray> BulkAsync(int from, int count, bool publicOnly)
        {
            string ids = string.Join(",", Enumerable.Range(from, count));
            string url = $"{RoomsApi}/rooms/bulk?id={ids}" + (publicOnly ? "&excludePrivateRooms=True" : "");
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var txt = await Http.GetStringAsync(url);
                    return Newtonsoft.Json.Linq.JArray.Parse(txt);
                }
                catch when (attempt < 2) { await Task.Delay(1500); }
            }
            return new Newtonsoft.Json.Linq.JArray();
        }

        private static async Task<List<Newtonsoft.Json.Linq.JObject>> LoadRecflareRoomsAsync()
        {
            // the top of the id space: rooms (dorms included) are dense up there, so a doubling probe
            // then a binary search on 100-id pages finds the last page with anything in it
            int hi = 1000;
            while (hi < 2_000_000 && (await BulkAsync(hi, 100, false)).Count > 0) hi *= 2;
            int lo = hi / 2;
            while (hi - lo > 100)
            {
                int mid = (lo + hi) / 2;
                if ((await BulkAsync(mid, 100, false)).Count > 0) lo = mid; else hi = mid;
            }
            int max = hi + 200;
            var pages = Enumerable.Range(0, max / 100 + 1).Select(i => 1 + i * 100).ToList();
            var bag = new ConcurrentBag<Newtonsoft.Json.Linq.JObject>();
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(pages.Select(async start =>
            {
                await gate.WaitAsync();
                try { foreach (var r in await BulkAsync(start, 100, true)) if (r is Newtonsoft.Json.Linq.JObject o) bag.Add(o); }
                finally { gate.Release(); }
            }));
            return bag.OrderBy(r => (string?)r["Name"], StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Dictionary<string, Newtonsoft.Json.Linq.JObject> ReadImports()
        {
            try { return JsonConvert.DeserializeObject<Dictionary<string, Newtonsoft.Json.Linq.JObject>>(System.IO.File.ReadAllText(ImportsPath)) ?? new(); }
            catch { return new(); }
        }

        [HttpGet("/admin/api/recflarecdn/rooms")]
        public async Task<ContentResult> Rooms(bool refresh = false)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (refresh || _rfRooms == null || DateTime.UtcNow - _rfRoomsAt > TimeSpan.FromMinutes(30))
            {
                if (!await _roomsGate.WaitAsync(0))
                    return J(new { success = false, message = "RecFlare's room list is already being read - try again in a minute." });
                try
                {
                    _rfRooms = await LoadRecflareRoomsAsync();
                    _rfRoomsAt = DateTime.UtcNow;
                    Console.WriteLine($"[recflare-cdn] read {_rfRooms.Count} public RecFlare rooms");
                }
                finally { _roomsGate.Release(); }
            }
            var imports = ReadImports();
            var rooms = _rfRooms!.Select(r =>
            {
                string id = (string?)r["RoomId"] ?? "";
                var files = new List<object>();
                string? meta = (string?)r["DataBlob"];
                if (!string.IsNullOrWhiteSpace(meta)) files.Add(new { subRoom = "(room data)", file = meta });
                foreach (var sr in r["SubRooms"] as Newtonsoft.Json.Linq.JArray ?? new())
                {
                    string? f = (string?)(sr["CurrentSave"] as Newtonsoft.Json.Linq.JObject)?["DataBlob"] ?? (string?)sr["DataBlob"];
                    files.Add(new { subRoom = (string?)sr["Name"] ?? "", file = f, scene = (string?)sr["UnitySceneId"] });
                }
                imports.TryGetValue(id, out var imp);
                return new
                {
                    id, name = (string?)r["Name"], description = (string?)r["Description"], image = (string?)r["ImageName"],
                    creatorId = (string?)r["CreatorAccountId"], isRRO = (bool?)r["IsRRO"] ?? false, files,
                    imported = imp == null ? null : new { campusRoomId = (string?)imp["campusRoomId"], name = (string?)imp["name"] }
                };
            }).ToList();
            return J(new { success = true, readAt = _rfRoomsAt, count = rooms.Count, files = rooms.Sum(r => r.files.Count), rooms });
        }

        // ---- import one RecFlare room into Campus ---------------------------------------------------
        public sealed class ImportBody { public long RoomId { get; set; } public string? Name { get; set; } }

        private static string FlatName(string recflareName) =>
            "rf_" + Regex.Replace(recflareName, @"[^A-Za-z0-9._-]", "_");

        [HttpPost("/admin/api/recflarecdn/import")]
        public async Task<ContentResult> Import([FromBody] ImportBody body)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (body == null || body.RoomId <= 0) return J(new { success = false, message = "Which room?" });
            var found = await BulkAsync((int)body.RoomId, 1, true);
            if (found.Count == 0 || found[0] is not Newtonsoft.Json.Linq.JObject rt)
                return J(new { success = false, message = "RecFlare doesn't have that public room any more." });

            // 1. its files, into RoomBlobs under flat names (RecFlare's carry a date folder)
            var got = new List<string>();
            async Task<string?> Pull(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                if (!SafeName.IsMatch(name)) throw new Exception($"unusual file name {name}");
                string local = FlatName(name);
                string dest = Path.Combine(RoomBlobFilter.BlobDirectory, local);
                if (!HaveLocally("room", local))
                {
                    using var resp = await Http.GetAsync($"{Base}/room/{name}", HttpCompletionOption.ResponseHeadersRead);
                    var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                    if (!resp.IsSuccessStatusCode || type.Contains("json") || type.Contains("html"))
                        throw new Exception($"RecFlare doesn't have {name} ({(int)resp.StatusCode})");
                    Directory.CreateDirectory(RoomBlobFilter.BlobDirectory);
                    await using (var fs = System.IO.File.Create(dest + ".part")) await resp.Content.CopyToAsync(fs);
                    if (new FileInfo(dest + ".part").Length == 0) { System.IO.File.Delete(dest + ".part"); throw new Exception($"{name} downloaded empty"); }
                    System.IO.File.Move(dest + ".part", dest, overwrite: true);
                }
                got.Add(local);
                return local;
            }
            try
            {
                string? meta = (string?)rt["DataBlob"];
                rt["DataBlob"] = string.IsNullOrWhiteSpace(meta) ? RoomBlobFilter.DefaultRoomMetaBlob : await Pull(meta);
                foreach (var sr in rt["SubRooms"] as Newtonsoft.Json.Linq.JArray ?? new())
                {
                    if (sr["CurrentSave"] is Newtonsoft.Json.Linq.JObject cs && (string?)cs["DataBlob"] is string b && b.Length > 0)
                        cs["DataBlob"] = await Pull(b);
                    if ((string?)sr["DataBlob"] is string fb && fb.Length > 0)
                        sr["DataBlob"] = await Pull(fb);
                    (sr as Newtonsoft.Json.Linq.JObject)?.Remove("AllSaves");   // RecFlare's older saves aren't fetched
                }
            }
            catch (Exception ex) { return J(new { success = false, message = ex.Message, files = got }); }

            // 2. its picture
            string? img = (string?)rt["ImageName"];
            if (!string.IsNullOrWhiteSpace(img) && !img.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync($"https://img.recflare.net/{img}");
                    string localImg = FlatName(img);
                    await System.IO.File.WriteAllBytesAsync(Path.Combine(Environment.CurrentDirectory, "Data", "Images", localImg), bytes);
                    rt["ImageName"] = localImg;
                }
                catch { rt["ImageName"] = "DefaultRoomImage.jpg"; }
            }

            // 3. credit the maker, pick a free name, add it
            string maker = "";
            try
            {
                var acc = Newtonsoft.Json.Linq.JObject.Parse(await Http.GetStringAsync($"https://accounts.recflare.net/account/{(string?)rt["CreatorAccountId"]}"));
                maker = (string?)acc["username"] ?? "";
            }
            catch { }
            string recflareId = (string?)rt["RoomId"] ?? body.RoomId.ToString();
            rt["Description"] = (((string?)rt["Description"]) ?? "").Trim() +
                                $"\n\nImported from RecFlare (room {recflareId}{(maker.Length > 0 ? ", made by @" + maker : "")}).";
            string want = string.IsNullOrWhiteSpace(body.Name) ? ((string?)rt["Name"] ?? "RecFlareRoom") : body.Name.Trim();
            string name = want;
            for (int n = 1; RoomDB.GetRoomByName(name) != null && n < 50; n++) name = n == 1 ? want + "_RF" : $"{want}_RF{n}";
            rt["Name"] = name;
            var (newId, why) = RoomDB.ImportRoomJson(rt, admin!.Id);
            if (newId == 0) return J(new { success = false, message = why ?? "The room could not be added.", files = got });

            var imports = ReadImports();
            imports[recflareId] = Newtonsoft.Json.Linq.JObject.FromObject(new { campusRoomId = newId.ToString(), name, at = DateTime.UtcNow, by = admin.Id });
            System.IO.File.WriteAllText(ImportsPath, JsonConvert.SerializeObject(imports, Formatting.Indented));
            Console.WriteLine($"[recflare-cdn] {admin.Id} imported RecFlare room {recflareId} as Campus room {newId} '{name}' ({got.Count} file(s))");
            return J(new { success = true, campusRoomId = newId, name, files = got, maker });
        }

        // ---- save a copy to your PC -------------------------------------------------------------------
        [HttpGet("/admin/api/recflarecdn/download")]
        public async Task<IActionResult> Download(string kind, string name)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!Valid(kind, name, out var why)) return J(new { success = false, message = why }, 400);
            var resp = await Http.GetAsync($"{Base}/{kind}/{name.Trim()}", HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            if (!resp.IsSuccessStatusCode) { resp.Dispose(); return J(new { success = false, message = "RecFlare doesn't have it." }, 404); }
            Response.RegisterForDispose(resp);
            return File(await resp.Content.ReadAsStreamAsync(), "application/octet-stream", Path.GetFileName(name.Trim()));
        }
    }
}
