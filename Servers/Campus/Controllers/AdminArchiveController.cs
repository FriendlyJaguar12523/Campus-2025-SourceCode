using System.Net;
using System.Text.RegularExpressions;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Admin panel "Archive" tab: rooms from https://archive.splootybean.com/Rooms/ - a plain file server (http-server) with one folder
    /// per room:
    ///
    ///   Rooms/&lt;Name&gt;/room.json                          the room record (name, subrooms, each subroom's current save)
    ///   Rooms/&lt;Name&gt;/&lt;ImageName&gt;.png                   the room picture
    ///   Rooms/&lt;Name&gt;/&lt;SubRoomId&gt;/Saves/&lt;SaveId&gt;/&lt;DataBlob&gt;.room   the saved build of each subroom (every save kept)
    ///   Rooms/&lt;Name&gt;/&lt;SubRoomId&gt;/Saves/&lt;SaveId&gt;/bakedUnityAsset-&lt;UnityAssetId&gt;-&lt;Platform&gt;-Unity_&lt;ver&gt;.json
    ///                                                    one descriptor per platform: {UnityAssetId, Target, Version, Filename, Hash}
    ///   Rooms/&lt;Name&gt;/AssetBundles/*.assetbundle           Rec Room's pre-baked room geometry (25-125 MB each, per platform and Unity version)
    ///
    /// The tab lists the folders and imports one (or every match) in a click: the .room blob of each subroom's CURRENT save goes to
    /// Data/CDN/RoomBlobs, the picture to Data/Images, and the room is added to Campus owned by the admin who pressed the button -
    /// the same result as downloading those files by hand and dropping them on the Rooms tab. The baked bundles are fetched too (the
    /// platforms picked, into Data/CDN/RoomBlobs where /cdn/room/ serves them) and their descriptors are merged into
    /// Data/APIS/BakedBulk/&lt;UnityAssetId&gt;_assets.json, which /Room_server/unity_assets/baked/bulk answers from. A platform whose
    /// bundle was not fetched gets no descriptor, so that game bakes the geometry itself instead of failing on a missing file.
    /// </summary>
    public class AdminArchiveController : ControllerBase
    {
        private const string Base = "https://archive.splootybean.com/Rooms";
        private static readonly Regex SafeFolder = new(@"^[A-Za-z0-9][A-Za-z0-9._ -]{0,100}$", RegexOptions.Compiled);
        private static readonly Regex SafeFile = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,200}$", RegexOptions.Compiled);
        private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(120),
            DefaultRequestHeaders = { { "User-Agent", "CampusServer (admin Archive tab)" } }
        };
        private static string ImportsPath => Path.Combine(Environment.CurrentDirectory, "Data", "ArchiveImports.json");

        private static ContentResult J(object o, int status = 200) => new()
        { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = status };

        private static string Url(string folder, params string[] parts) =>
            Base + "/" + Uri.EscapeDataString(folder) + string.Concat(parts.Select(p => "/" + Uri.EscapeDataString(p)));

        private static List<string>? _names; private static DateTime _namesAt;
        private static readonly SemaphoreSlim _gate = new(1, 1);

        private static Dictionary<string, JObject> ReadImports()
        {
            try { return JsonConvert.DeserializeObject<Dictionary<string, JObject>>(System.IO.File.ReadAllText(ImportsPath)) ?? new(); }
            catch { return new(); }
        }

        /// <summary>The folder names a directory page lists ("./Name/" links), or the file names with <paramref name="files"/>.</summary>
        private static async Task<List<string>> ListAsync(string url, bool files = false)
        {
            string html = await Http.GetStringAsync(url);
            var res = new List<string>();
            foreach (Match m in Regex.Matches(html, "href=\"\\./([^\"]+)\""))
            {
                string v = Uri.UnescapeDataString(m.Groups[1].Value);
                bool isDir = v.EndsWith("/");
                if (isDir == files) continue;
                v = v.TrimEnd('/');
                if (v.Length == 0 || v == "..") continue;
                res.Add(v);
            }
            return res;
        }

        [HttpGet("/admin/api/archive/rooms")]
        public async Task<ContentResult> Rooms(bool refresh = false)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (refresh || _names == null || DateTime.UtcNow - _namesAt > TimeSpan.FromMinutes(30))
            {
                if (!await _gate.WaitAsync(0)) return J(new { success = false, message = "The archive list is already being read - try again in a moment." });
                try
                {
                    _names = (await ListAsync(Base + "/")).Where(n => SafeFolder.IsMatch(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    _namesAt = DateTime.UtcNow;
                    Console.WriteLine($"[archive] read {_names.Count} room folders");
                }
                catch (Exception ex) { return J(new { success = false, message = "Could not read the archive: " + ex.Message }); }
                finally { _gate.Release(); }
            }
            var imports = ReadImports();
            // rooms imported before EnsureListable existed (blank page in the game): repair them now - harmless on the rest
            int repaired = 0;
            foreach (var imp in imports.Values)
                if (ulong.TryParse((string?)imp["campusRoomId"], out var rid) && RoomDB.EnsureListable(rid)) repaired++;
            if (repaired > 0) Console.WriteLine($"[archive] repaired {repaired} imported room(s) the game could not show");
            var rooms = _names!.Select(n =>
            {
                imports.TryGetValue(n, out var imp);
                return new { name = n, imported = imp == null ? null : new { campusRoomId = (string?)imp["campusRoomId"], name = (string?)imp["name"] } };
            }).ToList();
            return J(new { success = true, readAt = _namesAt, count = rooms.Count, rooms });
        }

        public sealed class ImportBody { public string? Folder { get; set; } public string? Name { get; set; } public string? Bundles { get; set; } }

        // Target numbers in the baked descriptors: 0 Windows, 1 iOS, 2 Android (Quest), 4 Xbox One, 5 PS4, 6 PS5, 8 Switch.
        // Version 3 = Unity 2021.3.29 - the game builds (PC and Quest) ask for Version 3; V1 is Unity 2020, V5 Unity 6.
        private static readonly HttpClient BundleHttp = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromMinutes(20),
            DefaultRequestHeaders = { { "User-Agent", "CampusServer (admin Archive tab)" } }
        };
        private static string BakedDir => Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "BakedBulk");
        private static readonly object _bakedLock = new();

        /// <summary>Which baked descriptors to take: pc+quest (the default - what the game builds ask for), pc, quest, all, none.</summary>
        private static bool WantTarget(string mode, int target, int version) => mode switch
        {
            "none" => false,
            "all" => true,
            "pc" => target == 0 && version == 3,
            "quest" => target == 2 && version == 3,
            _ => (target == 0 || target == 2) && version == 3,
        };

        /// <summary>Merge descriptors into Data/APIS/BakedBulk/&lt;id&gt;_assets.json (an entry with the same Target + Version is replaced).</summary>
        private static void MergeBaked(string unityAssetId, List<JObject> entries)
        {
            if (!Regex.IsMatch(unityAssetId, @"^[0-9a-fA-F-]{36}$")) return;
            Directory.CreateDirectory(BakedDir);
            string path = Path.Combine(BakedDir, unityAssetId + "_assets.json");
            lock (_bakedLock)
            {
                JArray arr;
                try { arr = System.IO.File.Exists(path) ? JArray.Parse(System.IO.File.ReadAllText(path)) : new JArray(); } catch { arr = new JArray(); }
                foreach (var e in entries)
                {
                    foreach (var old in arr.Where(x => (int?)x["Target"] == (int?)e["Target"] && (int?)x["Version"] == (int?)e["Version"]).ToList()) old.Remove();
                    arr.Add(e);
                }
                System.IO.File.WriteAllText(path + ".part", arr.ToString(Formatting.Indented));
                System.IO.File.Move(path + ".part", path, overwrite: true);
            }
        }

        [HttpPost("/admin/api/archive/import")]
        public async Task<ContentResult> Import([FromBody] ImportBody body)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string folder = (body?.Folder ?? "").Trim();
            // a pasted address works too: https://archive.splootybean.com/Rooms/MakeItToMidnight/
            var urlMatch = Regex.Match(folder, @"archive\.splootybean\.com/Rooms/([^/?#]+)", RegexOptions.IgnoreCase);
            if (urlMatch.Success) folder = Uri.UnescapeDataString(urlMatch.Groups[1].Value);
            if (!SafeFolder.IsMatch(folder)) return J(new { success = false, message = "That isn't a room folder name from the archive." });

            var got = new List<string>();
            JObject rt;
            try
            {
                rt = JObject.Parse(await Http.GetStringAsync(Url(folder, "room.json")));
            }
            catch (Exception ex) { return J(new { success = false, message = $"Could not read {folder}/room.json: {ex.Message}" }); }

            Directory.CreateDirectory(RoomBlobFilter.BlobDirectory);
            async Task<bool> Fetch(string url, string dest, string what)
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                string type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!resp.IsSuccessStatusCode || type.Contains("html") || type.Contains("json")) return false;
                await using (var fs = System.IO.File.Create(dest + ".part")) await resp.Content.CopyToAsync(fs);
                if (new FileInfo(dest + ".part").Length == 0) { System.IO.File.Delete(dest + ".part"); return false; }
                System.IO.File.Move(dest + ".part", dest, overwrite: true);
                got.Add(what);
                return true;
            }
            bool HaveBlob(string b) { var fi = new FileInfo(Path.Combine(RoomBlobFilter.BlobDirectory, b)); return fi.Exists && fi.Length > 0; }

            // each subroom's CURRENT save: its .room blob lives in <SubRoomId>/Saves/<SaveId>/
            try
            {
                foreach (var sr in rt["SubRooms"] as JArray ?? new())
                {
                    var cs = sr["CurrentSave"] as JObject;
                    string blob = (string?)cs?["DataBlob"] ?? (string?)sr["DataBlob"] ?? "";
                    if (blob.Length == 0) continue;
                    if (!SafeFile.IsMatch(blob)) throw new Exception($"unusual file name {blob}");
                    string subId = ((long?)sr["SubRoomId"])?.ToString() ?? (string?)sr["SubRoomId"] ?? "";
                    string saveId = ((long?)cs?["SubRoomDataSaveId"])?.ToString() ?? "";
                    string dest = Path.Combine(RoomBlobFilter.BlobDirectory, blob);
                    if (!HaveBlob(blob))
                    {
                        bool ok = saveId.Length > 0 && await Fetch(Url(folder, subId, "Saves", saveId, blob), dest, blob);
                        if (!ok)       // the save folder isn't named after the current save: look through the subroom's saves, newest first
                        {
                            var saves = (await ListAsync(Url(folder, subId, "Saves") + "/")).OrderByDescending(s => long.TryParse(s, out var n) ? n : 0);
                            foreach (var s in saves) { if (await Fetch(Url(folder, subId, "Saves", s, blob), dest, blob)) { ok = true; break; } }
                        }
                        if (!ok) throw new Exception($"{blob} (subroom {(string?)sr["Name"]}) isn't in the archive");
                    }
                    else got.Add(blob + " (already here)");
                    (sr as JObject)?.Remove("AllSaves");        // the older saves aren't imported
                }
                string? top = (string?)rt["DataBlob"];
                if (!string.IsNullOrWhiteSpace(top) && !HaveBlob(top)) throw new Exception($"room data {top} isn't here");
                if (string.IsNullOrWhiteSpace(top)) rt["DataBlob"] = RoomBlobFilter.DefaultRoomMetaBlob;
            }
            catch (Exception ex) { return J(new { success = false, message = ex.Message, files = got }); }

            // the baked asset bundles of each CURRENT save: the descriptors (bakedUnityAsset-*.json in the save's folder) say which bundle
            // file each platform / Unity version uses; the wanted ones are downloaded and their descriptors recorded
            string bundleMode = (body?.Bundles ?? "pc+quest").Trim().ToLowerInvariant();
            int bundlesGot = 0, bundlesHad = 0; long bundleBytes = 0;
            var bundleProblems = new List<string>();
            if (bundleMode != "none")
            {
                try
                {
                    foreach (var sr in rt["SubRooms"] as JArray ?? new())
                    {
                        string subId = ((long?)sr["SubRoomId"])?.ToString() ?? (string?)sr["SubRoomId"] ?? "";
                        string saveId = (((sr["CurrentSave"] as JObject)?["SubRoomDataSaveId"]) is JToken t && t.Type != JTokenType.Null) ? (string)t! : "";
                        if (subId.Length == 0 || saveId.Length == 0) continue;
                        List<string> bakedFiles;
                        try { bakedFiles = (await ListAsync(Url(folder, subId, "Saves", saveId) + "/", files: true)).Where(f => f.StartsWith("bakedUnityAsset-") && f.EndsWith(".json")).ToList(); }
                        catch { bakedFiles = new(); }
                        var byAsset = new Dictionary<string, List<JObject>>();
                        foreach (var bf in bakedFiles)
                        {
                            JObject d;
                            try { d = JObject.Parse(await Http.GetStringAsync(Url(folder, subId, "Saves", saveId, bf))); } catch { continue; }
                            string uid = (string?)d["UnityAssetId"] ?? "", fn = (string?)d["Filename"] ?? "";
                            int target = (int?)d["Target"] ?? -1, ver = (int?)d["Version"] ?? -1;
                            if (uid.Length == 0 || fn.Length == 0 || !SafeFile.IsMatch(fn) || !WantTarget(bundleMode, target, ver)) continue;
                            string dest = Path.Combine(RoomBlobFilter.BlobDirectory, fn);
                            if (HaveBlob(fn)) bundlesHad++;
                            else
                            {
                                using var resp = await BundleHttp.GetAsync(Url(folder, "AssetBundles", fn), HttpCompletionOption.ResponseHeadersRead);
                                string type = resp.Content.Headers.ContentType?.MediaType ?? "";
                                if (!resp.IsSuccessStatusCode || type.Contains("html")) { bundleProblems.Add($"{fn} (target {target}, v{ver}) isn't in the archive"); continue; }
                                await using (var fs = System.IO.File.Create(dest + ".part")) await resp.Content.CopyToAsync(fs);
                                long len = new FileInfo(dest + ".part").Length;
                                if (len == 0) { System.IO.File.Delete(dest + ".part"); bundleProblems.Add($"{fn} downloaded empty"); continue; }
                                System.IO.File.Move(dest + ".part", dest, overwrite: true);
                                bundlesGot++; bundleBytes += len; got.Add(fn);
                            }
                            if (!byAsset.TryGetValue(uid, out var list)) byAsset[uid] = list = new List<JObject>();
                            list.Add(d);
                        }
                        foreach (var kv in byAsset) MergeBaked(kv.Key, kv.Value);
                    }
                }
                catch (Exception ex) { bundleProblems.Add("bundles stopped: " + ex.Message); }
            }

            // the picture
            string? img = (string?)rt["ImageName"];
            if (!string.IsNullOrWhiteSpace(img) && SafeFile.IsMatch(img) && !img.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string imgDir = Path.Combine(Environment.CurrentDirectory, "Data", "Images");
                    Directory.CreateDirectory(imgDir);
                    string idest = Path.Combine(imgDir, img);
                    if (!System.IO.File.Exists(idest) && !await Fetch(Url(folder, img), idest, img)) rt["ImageName"] = "DefaultRoomImage.jpg";
                }
                catch { rt["ImageName"] = "DefaultRoomImage.jpg"; }
            }

            string archiveId = (string?)rt["RoomId"] ?? folder;
            rt["Description"] = (((string?)rt["Description"]) ?? "").Trim() + $"\n\nImported from the Splooty archive (room {archiveId}).";
            string want = string.IsNullOrWhiteSpace(body?.Name) ? ((string?)rt["Name"] ?? folder) : body!.Name!.Trim();
            string name = want;
            for (int n = 1; RoomDB.GetRoomByName(name) != null && n < 50; n++) name = n == 1 ? want + "_AR" : $"{want}_AR{n}";
            rt["Name"] = name;
            var (newId, why) = RoomDB.ImportRoomJson(rt, admin!.Id);
            if (newId == 0) return J(new { success = false, message = why ?? "The room could not be added.", files = got });

            // the archive's room.json has no platform flags / player cap / dates: without them the game does not show the room
            RoomDB.EnsureListable(newId);

            // AddRoom zeroes every save's persistence / UGC versions (right for rooms from a newer build, wrong here): a Rooms 2.0 save
            // (object model 2, persistence 153, ugc 155) was left as 0 / 2 / 0 - a combination no real save has. Put the archive's own numbers
            // back (never above 227, the newest the game knows).
            try
            {
                var newSubs = (RoomDB.GetRoom(newId)?.SubRooms ?? new()).ToList();
                var srcSubs = (rt["SubRooms"] as JArray ?? new()).OfType<JObject>().ToList();
                for (int i = 0; i < Math.Min(newSubs.Count, srcSubs.Count); i++)
                {
                    var src = srcSubs[i]["CurrentSave"] as JObject;
                    var sub = RoomDB.GetSubRoom(newId, newSubs[i].SubRoomId);
                    if (src == null || sub?.CurrentSave == null) continue;
                    int V(string k) => Math.Min(227, Math.Max(0, (int?)src[k] ?? 0));
                    foreach (var save in new[] { sub.CurrentSave }.Concat(sub.AllSaves ?? new()).Where(s => s.SubRoomDataSaveId == sub.CurrentSave.SubRoomDataSaveId))
                    { save.PersistenceVersion = V("PersistenceVersion"); save.OMVersion = V("OMVersion"); save.UgcSubVersion = V("UgcSubVersion"); }
                    RoomDB.subroom_col.Update(sub);
                    var room = RoomDB.col.FindOne(x => x.RoomId == newId);
                    var emb = room?.SubRooms?.FirstOrDefault(s => s.SubRoomId == sub.SubRoomId);
                    if (room != null && emb != null) { emb.CurrentSave = sub.CurrentSave; emb.AllSaves = sub.AllSaves; RoomDB.col.Update(room); }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[archive] could not restore the save versions: {ex.Message}"); }

            var imports = ReadImports();
            imports[folder] = JObject.FromObject(new { campusRoomId = newId.ToString(), name, at = DateTime.UtcNow, by = admin.Id });
            System.IO.File.WriteAllText(ImportsPath, JsonConvert.SerializeObject(imports, Formatting.Indented));
            Console.WriteLine($"[archive] {admin.Id} imported '{folder}' as Campus room {newId} '{name}' ({got.Count} file(s))");
            Console.WriteLine($"[archive] '{folder}': {bundlesGot} bundle(s) downloaded ({bundleBytes / 1048576} MB), {bundlesHad} already here{(bundleProblems.Count > 0 ? ", problems: " + string.Join("; ", bundleProblems) : "")}");
            return J(new { success = true, campusRoomId = newId, name, files = got, bundles = new { mode = bundleMode, downloaded = bundlesGot, already = bundlesHad, megabytes = bundleBytes / 1048576, problems = bundleProblems } });
        }
    }
}
