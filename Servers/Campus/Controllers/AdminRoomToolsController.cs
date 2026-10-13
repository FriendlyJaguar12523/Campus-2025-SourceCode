using System.Text.RegularExpressions;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Admin panel "Room Editor" tab: everything about ONE room in one place -
    ///   * the platform flags it is shown for,
    ///   * each subroom: name, player cap, accessibility, sandbox (building allowed), scene,
    ///   * each subroom's save file (.room blob): download it, upload a new one (published at once), restore an older save,
    ///     edit the save's notes / version numbers, see whether the file is really on disk,
    ///   * each subroom's permissions (what Everyone / Host / Moderator / Contributor / Co-owner / Creator may do in it),
    ///   * moving or copying subrooms between rooms (a whole map into another room, say).
    /// A room's blob files live in Data/CDN/RoomBlobs and are served by /cdn/room/ - the game caches them for a year by NAME, so an
    /// upload always gets a NEW name (it never overwrites a file players may already hold).
    /// </summary>
    public class AdminRoomToolsController : ControllerBase
    {
        private static readonly Regex SafeBlob = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,200}$", RegexOptions.Compiled);
        private static ContentResult J(object o, int status = 200) => new() { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = status };
        private static string BlobDir => RoomBlobFilter.BlobDirectory;

        private static readonly string[] PermissionNames =
        {
            RoomPermissions.MakerPen, RoomPermissions.SaveInventions, RoomPermissions.SpawnInventions, RoomPermissions.PlayGizmos,
            RoomPermissions.ResetButton, RoomPermissions.DeleteAll, RoomPermissions.Invite,
        };
        private static readonly (int role, string name)[] Roles = { (0, "Everyone"), (10, "Host"), (20, "Moderator"), (25, "Contributor"), (30, "Co-owner"), (255, "Creator") };

        private static object FileInfoOf(string? blob)
        {
            if (string.IsNullOrWhiteSpace(blob) || !SafeBlob.IsMatch(blob)) return new { name = blob, exists = false, size = 0L };
            var fi = new FileInfo(Path.Combine(BlobDir, blob));
            return new { name = blob, exists = fi.Exists && fi.Length > 0, size = fi.Exists ? fi.Length : 0L };
        }

        private static object SaveRow(RoomDBClasses.CurrentSave s) => new
        {
            id = s.SubRoomDataSaveId, file = FileInfoOf(s.DataBlob), description = s.Description ?? "", created = s.CreatedAt,
            persistenceVersion = s.PersistenceVersion, omVersion = s.OMVersion, ugcSubVersion = s.UgcSubVersion, savedBy = s.SavedByAccountId,
        };

        // ---- find rooms ---------------------------------------------------------------------------
        [HttpGet("/admin/api/roomtools/find")]
        public ContentResult Find(string q = "")
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            q = (q ?? "").Trim().TrimStart('^');
            if (q.Length == 0) return J(new { success = true, rooms = new object[0] });
            var hits = new List<RoomDBClasses.RoomRoot>();
            if (ulong.TryParse(q, out var id)) { var r = RoomDB.GetRoom(id); if (r != null) hits.Add(r); }
            foreach (var r in RoomDB.GetAllRoomsIncludingDorms().Where(r => (r.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).Take(25))
                if (!hits.Any(h => h.RoomId == r.RoomId)) hits.Add(r);
            return J(new
            {
                success = true,
                rooms = hits.Take(25).Select(r => new
                {
                    id = r.RoomId, name = r.Name, creator = r.CreatorAccountId == 0 ? "" : PlayerDB.GetPlayerNameById(r.CreatorAccountId),
                    subrooms = r.SubRooms?.Count ?? 0, dorm = r.IsDorm
                })
            });
        }

        // ---- one room, everything ------------------------------------------------------------------
        [HttpGet("/admin/api/roomtools/room")]
        public ContentResult Room(ulong id)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var r = RoomDB.GetRoom(id);
            if (r == null) return J(new { success = false, message = $"No room {id}." });
            var defaults = RoomPermissions.Defaults(0);
            return J(new
            {
                success = true,
                room = new
                {
                    id = r.RoomId, name = r.Name, creatorId = r.CreatorAccountId,
                    creator = r.CreatorAccountId == 0 ? "" : PlayerDB.GetPlayerNameById(r.CreatorAccountId),
                    accessibility = (int)r.Accessibility, maxPlayers = r.MaxPlayers, dorm = r.IsDorm,
                    supportsScreens = r.SupportsScreens, supportsWalkVR = r.SupportsWalkVR, supportsTeleportVR = r.SupportsTeleportVR,
                    supportsVRLow = r.SupportsVRLow, supportsQuest2 = r.SupportsQuest2, supportsMobile = r.SupportsMobile, supportsJuniors = r.SupportsJuniors,
                    roomBlob = FileInfoOf(r.DataBlob),
                },
                permissionNames = PermissionNames,
                roles = Roles.Select(x => new { role = x.role, name = x.name }),
                subRooms = (r.SubRooms ?? new()).Select(s =>
                {
                    var merged = RoomPermissions.Merge(RoomPermissions.Defaults(0), s.Permissions);
                    var cells = new List<object>();
                    foreach (var p in PermissionNames)
                        foreach (var (role, _) in Roles)
                        {
                            var eff = merged.FirstOrDefault(e => e.Permission == p && e.Role == role);
                            var over = s.Permissions?.FirstOrDefault(e => e.Permission == p && e.Role == role);
                            var def = defaults.FirstOrDefault(e => e.Permission == p && e.Role == role);
                            cells.Add(new { permission = p, role, value = eff?.Value ?? def?.Value ?? "True", defaultValue = def?.Value ?? "True", overridden = over != null });
                        }
                    var saves = new List<RoomDBClasses.CurrentSave>();
                    if (s.AllSaves != null) saves.AddRange(s.AllSaves);
                    if (s.CurrentSave != null && !saves.Any(x => x.SubRoomDataSaveId == s.CurrentSave.SubRoomDataSaveId)) saves.Add(s.CurrentSave);
                    return new
                    {
                        id = s.SubRoomId, name = s.Name, maxPlayers = s.MaxPlayers, accessibility = (int)s.Accessibility, sandbox = s.IsSandbox,
                        scene = s.UnitySceneId, staged = s.StagedSubRoomDataSaveId,
                        current = s.CurrentSave == null ? null : SaveRow(s.CurrentSave),
                        saves = saves.OrderByDescending(x => x.CreatedAt).Take(40).Select(SaveRow),
                        permissions = cells,
                    };
                })
            });
        }

        // ---- the room: platform flags (+ the room-level data file) ---------------------------------
        [HttpPost("/admin/api/roomtools/room_settings")]
        public ContentResult RoomSettings()
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!ulong.TryParse(Request.Form["roomId"].ToString(), out var roomId) || RoomDB.GetRoom(roomId) == null) return J(new { success = false, message = "No such room." });
            bool B(string k) => Request.Form[k].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            RoomDB.SetPlatformSupport(roomId, B("screens"), B("walkVR"), B("teleportVR"), B("vrLow"), B("quest2"), B("mobile"), B("juniors"));
            string meta = Path.GetFileName((Request.Form["roomBlob"].ToString() ?? "").Trim());
            string note = "";
            if (meta.Length > 0)
            {
                var fi = new FileInfo(Path.Combine(BlobDir, meta));
                var room = RoomDB.col.FindOne(x => x.RoomId == roomId);
                if (!SafeBlob.IsMatch(meta) || !fi.Exists || fi.Length == 0) note = $" The room data file {meta} isn't in Data/CDN/RoomBlobs, so it was left as it was.";
                else if (room != null && room.DataBlob != meta) { room.DataBlob = meta; RoomDB.col.Update(room); }
            }
            RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId));
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) set the platform flags on room {roomId}");
            return J(new { success = true, message = "Saved." + note });
        }

        // ---- a subroom: name, cap, accessibility, sandbox -------------------------------------------
        [HttpPost("/admin/api/roomtools/subroom_settings")]
        public ContentResult SubroomSettings()
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!ulong.TryParse(Request.Form["roomId"].ToString(), out var roomId) || !ulong.TryParse(Request.Form["subRoomId"].ToString(), out var subId))
                return J(new { success = false, message = "Bad ids." });
            var sub = RoomDB.GetSubRoom(roomId, subId);
            if (sub == null) return J(new { success = false, message = "No such subroom." });
            string name = Request.Form["name"].ToString().Trim();
            if (name.Length == 0) name = sub.Name ?? "Home";
            int acc = int.TryParse(Request.Form["accessibility"].ToString(), out var a) && Enum.IsDefined(typeof(RoomDBClasses.RoomAccessibility), a) ? a : (int)sub.Accessibility;
            int max = int.TryParse(Request.Form["maxPlayers"].ToString(), out var m) && m >= 1 && m <= 100 ? m : sub.MaxPlayers;
            RoomDB.ModifySubroom(roomId, subId, name, (RoomDBClasses.RoomAccessibility)acc, max);
            if (Request.Form.ContainsKey("sandbox")) RoomDB.SetSubroomSandbox(roomId, subId, Request.Form["sandbox"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase));
            string scene = Request.Form["scene"].ToString().Trim();
            if (scene.Length > 0 && scene != sub.UnitySceneId && Regex.IsMatch(scene, @"^[0-9a-fA-F-]{36}$")) RoomDB.SetSubRoomScene(roomId, subId, scene);
            RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId));
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) edited subroom {subId} of room {roomId}");
            return J(new { success = true, message = "Saved." });
        }

        // ---- permissions -------------------------------------------------------------------------------
        public sealed class PermChange { public string Permission { get; set; } = ""; public int Role { get; set; } public string? Value { get; set; } }
        public sealed class PermBody { public ulong RoomId { get; set; } public ulong SubRoomId { get; set; } public List<PermChange> Changes { get; set; } = new(); public bool ResetAll { get; set; } }

        [HttpPost("/admin/api/roomtools/permissions")]
        public ContentResult Permissions([FromBody] PermBody body)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var sub = body == null ? null : RoomDB.GetSubRoom(body.RoomId, body.SubRoomId);
            if (sub == null) return J(new { success = false, message = "No such subroom." });
            List<RoomPermissions.Entry> table;
            if (body!.ResetAll) table = new();
            else
            {
                // Value null = "back to the default"; "True" / "False" = an explicit row
                var changes = body.Changes
                    .Where(c => PermissionNames.Contains(c.Permission) && Roles.Any(r => r.role == c.Role))
                    .Select(c => new RoomPermissions.Entry { Permission = c.Permission, Role = c.Role, Override = c.Value != null, Value = c.Value == "False" ? "False" : "True" })
                    .ToList();
                table = RoomPermissions.Apply(sub.Permissions, changes);
            }
            RoomDB.SetSubRoomPermissions(body.RoomId, body.SubRoomId, table);
            RoomPermissions.PushRoomUpdateToRoom(body.RoomId, RoomDB.GetRoom(body.RoomId));
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) set permissions on room {body.RoomId} subroom {body.SubRoomId}: {(body.ResetAll ? "reset to defaults" : body.Changes.Count + " change(s)")}");
            return J(new { success = true, message = body.ResetAll ? "Permissions back to the defaults." : $"Saved {body.Changes.Count} permission change(s)." });
        }

        // ---- save files ----------------------------------------------------------------------------------
        [HttpGet("/admin/api/roomtools/blob")]
        public IActionResult Blob(string name)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!SafeBlob.IsMatch(name ?? "")) return NotFound();
            string path = Path.Combine(BlobDir, name!);
            if (!System.IO.File.Exists(path)) return NotFound();
            return PhysicalFile(path, "application/octet-stream", name, enableRangeProcessing: true);
        }

        /// <summary>Upload a .room file as a subroom's new save (published at once), or name one already in Data/CDN/RoomBlobs.</summary>
        [HttpPost("/admin/api/roomtools/save_file")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
        public async Task<ContentResult> SaveFile()
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!Request.HasFormContentType) return J(new { success = false, message = "No form." });
            var form = await Request.ReadFormAsync();
            if (!ulong.TryParse(form["roomId"].ToString(), out var roomId) || !ulong.TryParse(form["subRoomId"].ToString(), out var subId))
                return J(new { success = false, message = "Bad ids." });
            var sub = RoomDB.GetSubRoom(roomId, subId);
            if (sub == null) return J(new { success = false, message = "No such subroom." });

            string blob = Path.GetFileName((form["existing"].ToString() ?? "").Trim());
            var file = form.Files.FirstOrDefault();
            Directory.CreateDirectory(BlobDir);
            if (file != null && file.Length > 0)
            {
                const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
                var rnd = System.Security.Cryptography.RandomNumberGenerator.GetBytes(25);
                blob = new string(rnd.Select(b => chars[b % chars.Length]).ToArray()) + ".room";
                string dest = Path.Combine(BlobDir, blob);
                await using (var fs = System.IO.File.Create(dest + ".part")) await file.CopyToAsync(fs);
                System.IO.File.Move(dest + ".part", dest, overwrite: true);
            }
            else
            {
                var fi = new FileInfo(Path.Combine(BlobDir, blob));
                if (!SafeBlob.IsMatch(blob) || !fi.Exists || fi.Length == 0) return J(new { success = false, message = "Choose a file to upload, or the name of one that is already in Data/CDN/RoomBlobs." });
            }
            var cur = sub.CurrentSave;
            string desc = form["description"].ToString();
            if (desc.Length == 0) desc = file != null ? $"Uploaded in the admin panel ({file.FileName})" : "Attached in the admin panel";
            RoomDB.SetRoomSubroomRoomFile(roomId, subId, blob, desc, admin!.Id, Platforms.All,
                cur?.PersistenceVersion ?? 0, cur?.OMVersion ?? 0, cur?.UgcSubVersion ?? 0);
            RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId));
            Console.WriteLine($"[admin] {admin.Username} ({admin.Id}) put {blob} on room {roomId} subroom {subId}");
            return J(new { success = true, message = $"{blob} is now this subroom's save. Players pick it up the next time they load the room.", blob });
        }

        public sealed class RestoreBody { public ulong RoomId { get; set; } public ulong SubRoomId { get; set; } public ulong SaveId { get; set; } }
        [HttpPost("/admin/api/roomtools/restore_save")]
        public ContentResult RestoreSave([FromBody] RestoreBody b)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (b == null || !RoomDB.PublishSubroomSave(b.RoomId, b.SubRoomId, b.SaveId)) return J(new { success = false, message = "That save isn't there." });
            RoomPermissions.PushRoomUpdateToRoom(b.RoomId, RoomDB.GetRoom(b.RoomId));
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) restored save {b.SaveId} on room {b.RoomId} subroom {b.SubRoomId}");
            return J(new { success = true, message = "That save is live again." });
        }

        public sealed class SaveInfoBody
        {
            public ulong RoomId { get; set; } public ulong SubRoomId { get; set; } public ulong SaveId { get; set; }
            public string? Description { get; set; } public int? PersistenceVersion { get; set; } public int? OMVersion { get; set; } public int? UgcSubVersion { get; set; }
            public string? File { get; set; }
        }
        /// <summary>Edit a save's record: its notes, version numbers and which blob file it points at.</summary>
        [HttpPost("/admin/api/roomtools/save_info")]
        public ContentResult SaveInfo([FromBody] SaveInfoBody b)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var sub = b == null ? null : RoomDB.GetSubRoom(b.RoomId, b.SubRoomId);
            if (sub == null) return J(new { success = false, message = "No such subroom." });
            var targets = new List<RoomDBClasses.CurrentSave>();
            if (sub.CurrentSave != null && sub.CurrentSave.SubRoomDataSaveId == b!.SaveId) targets.Add(sub.CurrentSave);
            targets.AddRange((sub.AllSaves ?? new()).Where(s => s.SubRoomDataSaveId == b!.SaveId));
            if (targets.Count == 0) return J(new { success = false, message = "That save isn't there." });
            string? file = string.IsNullOrWhiteSpace(b!.File) ? null : Path.GetFileName(b.File.Trim());
            if (file != null)
            {
                var fi = new FileInfo(Path.Combine(BlobDir, file));
                if (!SafeBlob.IsMatch(file) || !fi.Exists || fi.Length == 0) return J(new { success = false, message = $"{file} isn't in Data/CDN/RoomBlobs." });
            }
            foreach (var t in targets)
            {
                if (b.Description != null) t.Description = b.Description;
                if (b.PersistenceVersion.HasValue) t.PersistenceVersion = b.PersistenceVersion.Value;
                if (b.OMVersion.HasValue) t.OMVersion = b.OMVersion.Value;
                if (b.UgcSubVersion.HasValue) t.UgcSubVersion = b.UgcSubVersion.Value;
                if (file != null) t.DataBlob = file;
            }
            RoomDB.subroom_col.Update(sub);
            var room = RoomDB.col.FindOne(x => x.RoomId == b.RoomId);
            var emb = room?.SubRooms?.FirstOrDefault(s => s.SubRoomId == b.SubRoomId);
            if (room != null && emb != null) { emb.CurrentSave = sub.CurrentSave; emb.AllSaves = sub.AllSaves; RoomDB.col.Update(room); }
            RoomPermissions.PushRoomUpdateToRoom(b.RoomId, RoomDB.GetRoom(b.RoomId));
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) edited save {b.SaveId} of room {b.RoomId} subroom {b.SubRoomId}");
            return J(new { success = true, message = "Saved." });
        }

        // ---- the baked bundle of a subroom's live save -------------------------------------------------------
        private static string BakedDir => Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "BakedBulk");

        /// <summary>The baked-asset descriptors the game gets for this subroom's live save (/Room_server/unity_assets/baked/bulk), and whether each bundle is on disk.</summary>
        [HttpGet("/admin/api/roomtools/baked")]
        public ContentResult Baked(ulong roomId, ulong subRoomId)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var sub = RoomDB.GetSubRoom(roomId, subRoomId);
            string id = sub?.CurrentSave?.UnityAssetId ?? "";
            if (id.Length == 0) return J(new { success = true, assetId = "", entries = new object[0], note = "This subroom has no baked asset." });
            string path = Path.Combine(BakedDir, id + "_assets.json");
            var entries = new List<object>();
            if (System.IO.File.Exists(path))
                try
                {
                    foreach (var e in Newtonsoft.Json.Linq.JArray.Parse(System.IO.File.ReadAllText(path)))
                        entries.Add(new { target = (int?)e["Target"], version = (int?)e["Version"], file = FileInfoOf((string?)e["Filename"]) });
                }
                catch { }
            return J(new { success = true, assetId = id, entries, active = System.IO.File.Exists(path), off = System.IO.File.Exists(path + ".off") });
        }

        public sealed class BakedBody { public ulong RoomId { get; set; } public ulong SubRoomId { get; set; } public string Action { get; set; } = ""; }
        /// <summary>
        /// "remove": stop handing the game this subroom's baked bundle - it then BAKES the room from the .room data itself (no 100 MB download).
        /// "restore": hand it out again. The descriptor file is only renamed (.off), never deleted, and no bundle file is touched.
        /// </summary>
        [HttpPost("/admin/api/roomtools/baked")]
        public ContentResult BakedAction([FromBody] BakedBody b)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var sub = b == null ? null : RoomDB.GetSubRoom(b.RoomId, b.SubRoomId);
            string id = sub?.CurrentSave?.UnityAssetId ?? "";
            if (id.Length == 0 || !Regex.IsMatch(id, @"^[0-9a-fA-F-]{36}$")) return J(new { success = false, message = "This subroom has no baked asset." });
            string path = Path.Combine(BakedDir, id + "_assets.json");
            if (b!.Action == "remove")
            {
                if (!System.IO.File.Exists(path)) return J(new { success = true, message = "There was no baked bundle for this subroom - the game already bakes it itself." });
                System.IO.File.Move(path, path + ".off", overwrite: true);
            }
            else if (b.Action == "restore")
            {
                if (!System.IO.File.Exists(path + ".off")) return J(new { success = false, message = "Nothing to restore." });
                System.IO.File.Move(path + ".off", path, overwrite: true);
            }
            else return J(new { success = false, message = "Unknown action." });
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) {b.Action}d the baked bundle of room {b.RoomId} subroom {b.SubRoomId} (asset {id})");
            return J(new { success = true, message = b.Action == "remove" ? "The game will bake this subroom itself the next time it loads (no bundle download)." : "The baked bundle is handed out again." });
        }

        // ---- subrooms: delete, move, copy ------------------------------------------------------------------
        public sealed class SubRef { public ulong RoomId { get; set; } public ulong SubRoomId { get; set; } }
        [HttpPost("/admin/api/roomtools/delete_subroom")]
        public ContentResult DeleteSubroom([FromBody] SubRef b)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var room = b == null ? null : RoomDB.GetRoom(b.RoomId);
            if (room == null || RoomDB.GetSubRoom(b!.RoomId, b.SubRoomId) == null) return J(new { success = false, message = "No such subroom." });
            if ((room.SubRooms?.Count ?? 0) <= 1) return J(new { success = false, message = "That is the room's only subroom - delete the room instead." });
            RoomDB.DeleteSubroom(b.RoomId, b.SubRoomId);
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) deleted subroom {b.SubRoomId} of room {b.RoomId}");
            return J(new { success = true, message = "Subroom deleted." });
        }

        public sealed class MoveBody { public ulong FromRoomId { get; set; } public ulong ToRoomId { get; set; } public List<ulong>? SubRoomIds { get; set; } public string? Mode { get; set; } public bool DeleteEmptySource { get; set; } }
        [HttpPost("/admin/api/roomtools/move_subrooms")]
        public ContentResult MoveSubrooms([FromBody] MoveBody b)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (b == null || b.FromRoomId == b.ToRoomId) return J(new { success = false, message = "Pick two different rooms." });
            var from = RoomDB.GetRoom(b.FromRoomId); var to = RoomDB.GetRoom(b.ToRoomId);
            if (from == null) return J(new { success = false, message = $"No room {b.FromRoomId}." });
            if (to == null) return J(new { success = false, message = $"No room {b.ToRoomId}." });
            bool copy = string.Equals(b.Mode, "copy", StringComparison.OrdinalIgnoreCase);
            var ids = (b.SubRoomIds != null && b.SubRoomIds.Count > 0 ? b.SubRoomIds : (from.SubRooms ?? new()).Select(s => s.SubRoomId).ToList()).Distinct().ToList();
            var moved = new List<object>();
            var problems = new List<string>();
            foreach (var sid in ids)
            {
                var sub = RoomDB.GetSubRoom(b.FromRoomId, sid);
                if (sub == null) { problems.Add($"subroom {sid} isn't in room {b.FromRoomId}"); continue; }
                string name = sub.Name ?? "Home";
                ulong moveId = sid;
                if (copy)
                {
                    var clone = RoomDB.CloneSubroom(b.FromRoomId, sid);
                    if (clone == null) { problems.Add($"{name}: could not copy it"); continue; }
                    moveId = clone.SubRoomId;
                }
                // a name the target room already uses gets the source room's name added, so the two can be told apart
                var targetNames = (RoomDB.GetRoom(b.ToRoomId)?.SubRooms ?? new()).Select(s => s.Name ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                var cur = RoomDB.GetSubRoom(b.FromRoomId, moveId)!;
                if (targetNames.Contains(cur.Name ?? ""))
                {
                    string renamed = $"{cur.Name} ({from.Name})"; if (renamed.Length > 60) renamed = renamed[..60];
                    for (int n = 2; targetNames.Contains(renamed) && n < 30; n++) renamed = $"{cur.Name} ({from.Name}) {n}";
                    RoomDB.ModifySubroom(b.FromRoomId, moveId, renamed, cur.Accessibility, cur.MaxPlayers);
                }
                var (_, err, newId) = RoomDB.MoveSubroomToRoom(b.FromRoomId, moveId, b.ToRoomId, allowLast: true);
                if (err != null) { problems.Add($"{name}: {err}"); continue; }
                moved.Add(new { name = RoomDB.GetSubRoom(b.ToRoomId, newId)?.Name ?? name, subRoomId = newId });
            }
            bool sourceDeleted = false;
            var left = RoomDB.GetRoom(b.FromRoomId);
            if (!copy && b.DeleteEmptySource && left != null && (left.SubRooms?.Count ?? 0) == 0 && !left.IsDorm) { RoomDB.DeleteRoom(b.FromRoomId); sourceDeleted = true; }
            RoomPermissions.PushRoomUpdateToRoom(b.ToRoomId, RoomDB.GetRoom(b.ToRoomId));
            Console.WriteLine($"[admin] {admin!.Username} ({admin.Id}) {(copy ? "copied" : "moved")} {moved.Count} subroom(s) from room {b.FromRoomId} to room {b.ToRoomId}{(sourceDeleted ? " (source room deleted)" : "")}");
            return J(new
            {
                success = moved.Count > 0,
                message = (moved.Count > 0 ? $"{(copy ? "Copied" : "Moved")} {moved.Count} subroom(s) into ^{to.Name}." : "Nothing was moved.")
                          + (sourceDeleted ? $" The emptied room ^{from.Name} was deleted." : (!copy && moved.Count > 0 && (left?.SubRooms?.Count ?? 0) == 0 ? $" ^{from.Name} now has no subrooms - delete it or put one back." : ""))
                          + (problems.Count > 0 ? " Problems: " + string.Join("; ", problems) : ""),
                moved, problems, sourceDeleted
            });
        }
    }
}
