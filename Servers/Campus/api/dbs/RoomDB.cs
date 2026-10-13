using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using LiteDB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using Campus_live_rewrite.api.server.Classes.db_class;
using static Campus_live_rewrite.api.server.Classes.db_class.RoomDBClasses;
using System.Linq;

namespace Campus_live_rewrite.api.dbs
{
    public class RoomDB
    {
        public static LiteDatabase rooms_db = new LiteDatabase(Environment.CurrentDirectory + $"/Data/DBs/Rooms.db");
        private static readonly object _roomLock = new();

        public static readonly ILiteCollection<RoomRoot> col = rooms_db.GetCollection<RoomRoot>("Main_rooms");
        public static readonly ILiteCollection<SubRoom> subroom_col = rooms_db.GetCollection<SubRoom>("Main_subrooms");
        public static async Task ImportRooms(string path, ulong? ownerOverride = null)
        {
            string jsonData = await File.ReadAllTextAsync(path);

            var hotRooms = JsonConvert.DeserializeObject<Resultslist<RoomDBClasses.RoomRoot>>(jsonData);

            if (hotRooms?.Results == null)
                return;

            foreach (var room in hotRooms.Results)
            {
                // Re-own every imported room to the given account (the @Importer account), so all
                // uploaded rooms land under one owner regardless of who created them originally.
                if (ownerOverride.HasValue) room.CreatorAccountId = ownerOverride.Value;
                AddRoom(room, isImportingFromJson: true);
            }
        }

        // Rec Room exports carry JSON nulls for value-type fields (e.g. "PublishedAt": null); the
        // server's RoomRoot types those as non-nullable DateTime, so a plain ToObject throws and the
        // whole room import dies before any blob is placed. Ignoring nulls leaves such fields at
        // their default instead of throwing.
        private static readonly Newtonsoft.Json.JsonSerializer _lenientRoomSerializer =
            Newtonsoft.Json.JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        public class RoomExportResult
        {
            public int Rooms, Blobs, Images;
            public readonly List<string> Names = new();
            public readonly List<string> Errors = new();
        }

        /// <summary>
        /// Import Rec Room's OFFICIAL room-export format (the one the game's export tool produces):
        ///
        ///   {Room}/RoomDetails.json              # a SINGLE RoomRoot object (NOT {Results:[...]})
        ///   {Room}/super_room_data(.original).binpb   # bytes for RoomRoot.DataBlob (a .meta)
        ///   {Room}/RoomImage.jpg                 # bytes for RoomRoot.ImageName
        ///   {Room}/SubRoom_{SubRoomId}_{Name}/persisted_room_data(.original).binpb
        ///                                        # bytes for that subroom's CurrentSave.DataBlob (a .room)
        ///
        /// The OLD ImportRooms() only understood the server's own {Results:[...]} dump AND expected the
        /// .room/.meta blobs to already exist under their content-hash names. A real export has neither:
        /// RoomDetails.json is a bare object, and the blob bytes live in the .binpb files under DIFFERENT
        /// names. So a dragged-in export imported 0 rooms (or a room with no build = bare scene).
        ///
        /// The blob mapping is verified by hash: RoomRoot.DataBlob's recorded DataBlobHash matches
        /// super_room_data.binpb, and each CurrentSave.DataBlobHash matches that subroom's
        /// persisted_room_data.ORIGINAL.binpb (the pre-migration copy is the canonical blob the manifest
        /// records; the plain .binpb is a migrated variant). We pick, per blob, the .binpb whose SHA-256
        /// equals the recorded hash and write it under the DataBlob name into RoomBlobs, so the client's
        /// integrity check passes and the room actually loads its build.
        /// </summary>
        /// <summary>
        /// One room record in RecNet JSON (the admin RecFlare CDN tab's Import: a room off RecFlare's
        /// rooms service) with its .room/.meta blobs ALREADY written to RoomBlobs under the names the
        /// record carries. New room / subroom / save ids are assigned by AddRoom; versions are clamped
        /// like ImportRoomExport. Returns the new room id, or 0 with why.
        /// </summary>
        /// <summary>
        /// Make an IMPORTED room wholly the importer's. The record's account ids belong to the server
        /// it came from (RecFlare, or Rec Room for an export): its role list named THEIR creator as
        /// Creator (255) - which, read here, made whichever Campus player shares that number the
        /// room's creator. So: one role, the importer as Creator (like a newly created room), and the
        /// importer on every subroom and save. Bans and co-owners from the other server go too.
        /// </summary>
        public static void ClaimForOwner(RoomDBClasses.RoomRoot room, ulong owner)
        {
            room.CreatorAccountId = owner;
            room.Roles = new List<RoleClass>
            {
                new RoleClass { AccountId = (long)owner, InvitedRole = 0, Role = RoomDBClasses.Role_data.Creator }
            };
            foreach (var sub in room.SubRooms ?? new List<SubRoom>())
            {
                sub.CreatorAccountId = (long)owner;
                if (sub.CurrentSave != null) sub.CurrentSave.SavedByAccountId = owner;
                foreach (var sv in sub.AllSaves ?? new List<RoomDBClasses.CurrentSave>()) sv.SavedByAccountId = owner;
            }
        }

        public static (ulong id, string? error) ImportRoomJson(JToken rt, ulong owner)
        {
            var room = rt.ToObject<RoomDBClasses.RoomRoot>(_lenientRoomSerializer);
            if (room == null) return (0, "could not read the room record");
            if (Campus_live_rewrite.api.server.ConfigCampus.ClampImportedRoomVersionToClient)
            {
                room.MinUgcSubVersion = null;
                room.UgcSubVersion = null;
                room.PersistenceVersion = 0;
            }
            ClaimForOwner(room, owner);
            room.LoadScreens ??= new();
            room.Stats ??= new();
            var id = AddRoom(room, isImportingFromJson: true);
            return id == 0 ? (0UL, $"a room called {room.Name} already exists") : (id, (string?)null);
        }

        public static async Task<RoomExportResult> ImportRoomExport(string dir, ulong? ownerOverride = null)
        {
            var res = new RoomExportResult();
            string blobDir = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs");
            string imgDir = Path.Combine(Environment.CurrentDirectory, "Data", "Images");
            Directory.CreateDirectory(blobDir);
            Directory.CreateDirectory(imgDir);

            var manifests = Directory.EnumerateFiles(dir, "RoomDetails.json", SearchOption.AllDirectories).ToList();
            if (manifests.Count == 0) { res.Errors.Add("no RoomDetails.json found"); return res; }

            foreach (var manifest in manifests)
            {
                try
                {
                    string baseDir = Path.GetDirectoryName(manifest)!;
                    var tok = JToken.Parse(await File.ReadAllTextAsync(manifest));
                    var roomTokens =
                        tok is JObject o && o["Results"] is JArray ra ? ra.Cast<JToken>().ToList() :
                        tok is JArray arr ? arr.Cast<JToken>().ToList() :
                        new List<JToken> { tok };

                    foreach (var rt in roomTokens)
                    {
                        var room = rt.ToObject<RoomRoot>(_lenientRoomSerializer);
                        if (room == null) { res.Errors.Add($"{Path.GetFileName(baseDir)}: could not parse RoomDetails.json"); continue; }

                        // Downgrade the room's declared build version to the client's level so the
                        // client doesn't reject it with "playing on an older version of Rec Room than
                        // this room was saved with." Working base rooms declare no minimum; the subroom
                        // CurrentSave versions are zeroed separately in AddRoom. See ConfigCampus.
                        if (Campus_live_rewrite.api.server.ConfigCampus.ClampImportedRoomVersionToClient)
                        {
                            room.MinUgcSubVersion = null;
                            room.UgcSubVersion = null;
                            room.PersistenceVersion = 0;
                        }

                        // DataBlobHash is NOT modelled on RoomRoot/CurrentSave (commented out), so read
                        // the recorded hashes straight off the raw JSON to hash-match the .binpb files.
                        string? roomHash = (string?)rt["DataBlobHash"];
                        var subHashById = new Dictionary<ulong, string?>();
                        if (rt["SubRooms"] is JArray sra)
                            foreach (var st in sra)
                                if (ulong.TryParse((string?)st["SubRoomId"], out var sid))
                                    subHashById[sid] = (string?)st["CurrentSave"]?["DataBlobHash"];

                        // Room-level blob (super room data) -> RoomBlobs/<DataBlob>.
                        if (!string.IsNullOrWhiteSpace(room.DataBlob))
                        {
                            var src = PickBlobByHash(baseDir, new[] { "super_room_data.original.binpb", "super_room_data.binpb" }, roomHash);
                            if (src != null) { File.Copy(src, Path.Combine(blobDir, room.DataBlob), true); res.Blobs++; }
                            else res.Errors.Add($"{room.Name}: super_room_data.binpb not found for DataBlob {room.DataBlob}");
                        }

                        // Each subroom's build blob -> RoomBlobs/<CurrentSave.DataBlob>.
                        foreach (var sub in room.SubRooms ?? new List<SubRoom>())
                        {
                            var cs = sub.CurrentSave;
                            if (cs == null || string.IsNullOrWhiteSpace(cs.DataBlob)) continue;
                            string? subDir = FindSubRoomDir(baseDir, sub.SubRoomId);
                            if (subDir == null) { res.Errors.Add($"{room.Name}/{sub.Name}: SubRoom_{sub.SubRoomId}_* folder missing"); continue; }
                            subHashById.TryGetValue(sub.SubRoomId, out var subHash);
                            var src = PickBlobByHash(subDir, new[] { "persisted_room_data.original.binpb", "persisted_room_data.binpb" }, subHash);
                            if (src != null) { File.Copy(src, Path.Combine(blobDir, cs.DataBlob), true); res.Blobs++; }
                            else res.Errors.Add($"{room.Name}/{sub.Name}: persisted_room_data.binpb not found for DataBlob {cs.DataBlob}");
                        }

                        // Room thumbnail -> Images/<ImageName>. Skip when the room has no custom image
                        // (ImageName is the shared DefaultRoomImage.jpg) so we don't clobber the global
                        // fallback every other room falls back to.
                        if (!string.IsNullOrWhiteSpace(room.ImageName) &&
                            !room.ImageName.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
                        {
                            var img = Directory.EnumerateFiles(baseDir, "RoomImage.*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                            if (img != null) { try { File.Copy(img, Path.Combine(imgDir, room.ImageName), true); res.Images++; } catch { } }
                        }

                        if (ownerOverride.HasValue) ClaimForOwner(room, ownerOverride.Value);
                        var id = AddRoom(room, isImportingFromJson: true);
                        if (id != 0) { res.Rooms++; res.Names.Add(room.Name); }
                        else res.Errors.Add($"{room.Name}: not added (a room with that name may already exist)");
                    }
                }
                catch (Exception ex) { res.Errors.Add($"{Path.GetFileName(Path.GetDirectoryName(manifest))}: {ex.Message}"); }
            }
            return res;
        }

        /// <summary>
        /// Clamp the declared build version of every @Importer-owned room down to the client's level
        /// (no minimum), so already-imported rooms stop tripping the client's "playing on an older
        /// version of Rec Room" gate. Idempotent; only touches imported rooms (base rooms are owned
        /// by other accounts and already declare no minimum). Returns how many rooms were changed.
        /// </summary>
        public static int ClampImportedRoomsToClient(ulong importerId)
        {
            if (!Campus_live_rewrite.api.server.ConfigCampus.ClampImportedRoomVersionToClient || importerId == 0) return 0;
            int n = 0;
            lock (_roomLock)
            {
                foreach (var room in col.Find(r => r.CreatorAccountId == importerId).ToList())
                {
                    bool ch = false;
                    if (room.MinUgcSubVersion != null) { room.MinUgcSubVersion = null; ch = true; }
                    if (room.UgcSubVersion != null) { room.UgcSubVersion = null; ch = true; }
                    if (room.PersistenceVersion != null && room.PersistenceVersion != 0) { room.PersistenceVersion = 0; ch = true; }
                    if (ch) { col.Update(room); n++; }
                }
            }
            return n;
        }

        /// <summary>
        /// Wrap a loose .room blob (a single subroom's build data, with no RoomDetails.json) in a
        /// minimal, joinable @Importer-owned room so it can actually be played. The blob must already
        /// be present in Data/CDN/RoomBlobs under `roomFile`. The room-level blob points at the shared
        /// default_room.META (empty room variables). Scene id is taken from the blob when present, else
        /// the standard MakerPen sandbox scene. Returns the new RoomId, or 0 if a room with that name
        /// already exists / creation failed.
        /// </summary>
        public static ulong CreateRoomFromBlob(string roomFile, string roomName, ulong ownerId, string? sceneId = null, int persistenceVersion = 0)
        {
            const string SandboxScene = "a75f7547-79eb-47c6-8986-6767abcb4f92";
            string scene = string.IsNullOrWhiteSpace(sceneId) ? SandboxScene : sceneId!;

            var room = new RoomRoot
            {
                Name = roomName,
                Description = "Imported from a loose .room blob.",
                CreatorAccountId = ownerId,
                Accessibility = RoomAccessibility.Public,
                State = RoomState.Active,
                PublishState = 0,
                CreatedAt = DateTime.UtcNow,
                PublishedAt = DateTime.UtcNow,
                DataBlob = "default_room.META",       // shared empty room-level (super) blob
                ImageName = "DefaultRoomImage.jpg",
                MaxPlayers = 20,
                MaxPlayerCalculationMode = 0,
                AgeRating = 0,
                MinLevel = 0,
                CloningAllowed = false,
                SupportsScreens = true,
                SupportsWalkVR = true,
                SupportsTeleportVR = true,
                SupportsVRLow = true,
                SupportsQuest2 = true,
                SupportsMobile = true,
                SupportsJuniors = false,
                IsRRO = false,
                UgcVersion = 1,
                UgcSubVersion = null,          // no minimum -> passes the client version gate
                MinUgcSubVersion = null,
                PersistenceVersion = persistenceVersion,   // the blob's real save-format version
                LoadScreens = new List<LoadScreen> { new LoadScreen { ImageName = "", Title = roomName, Subtitle = "" } },
                Stats = new Stats(),
                Tags = new List<TagClass>(),
                Roles = new List<RoleClass>(),
                PromoImages = new List<string>(),
                PromoExternalContent = new List<string>(),
                RestrictedCircuitsAllowListNames = new List<string>(),
                SubRooms = new List<SubRoom>
                {
                    new SubRoom
                    {
                        Name = roomName,
                        UnitySceneId = scene,
                        IsSandbox = true,
                        MaxPlayers = 20,
                        Accessibility = RoomAccessibility.Public,
                        ShouldAutoStageSaves = true,
                        AllSaves = new List<CurrentSave>(),
                        CurrentSave = new CurrentSave
                        {
                            DataBlob = roomFile,
                            Description = "Imported from a loose .room blob.",
                            CreatedAt = DateTime.UtcNow,
                            SavedByAccountId = ownerId,
                            PersistenceVersion = 0,
                            OMVersion = 0,
                            UgcSubVersion = 0,
                            ReferencedUnityAssetIds = new List<string>()
                        }
                    }
                }
            };

            return AddRoom(room, isImportingFromJson: true);
        }

        /// <summary>True if any room (room-level DataBlob) or subroom (CurrentSave/AllSaves DataBlob) already points at this blob.</summary>
        public static bool AnyRoomUsesBlob(string blobName)
        {
            try
            {
                if (col.Exists(r => r.DataBlob == blobName)) return true;
                foreach (var s in subroom_col.FindAll())
                {
                    if (s.CurrentSave != null && string.Equals(s.CurrentSave.DataBlob, blobName, StringComparison.OrdinalIgnoreCase)) return true;
                    if (s.AllSaves != null && s.AllSaves.Any(x => string.Equals(x.DataBlob, blobName, StringComparison.OrdinalIgnoreCase))) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Remove @Importer rooms that were wrapped around a loose .room blob whose save version is
        /// newer than the client can load - they only freeze the game and drop the player to the dorm.
        /// The blob file stays hosted. Only touches loose-blob rooms (our marker), never real imports.
        /// </summary>
        public static int RemoveTooNewLooseRooms(ulong importerId)
        {
            if (importerId == 0) return 0;
            int max = Campus_live_rewrite.api.server.ConfigCampus.ClientMaxRoomPersistenceVersion;
            string blobDir = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs");
            int removed = 0;
            lock (_roomLock)
            {
                foreach (var room in col.Find(r => r.CreatorAccountId == importerId).ToList())
                {
                    var subs = subroom_col.Find(s => s.RoomId == room.RoomId).ToList();
                    bool loose = subs.Count == 1 && subs[0].CurrentSave != null &&
                                 (subs[0].CurrentSave.Description ?? "").Contains("loose .room", StringComparison.OrdinalIgnoreCase);
                    if (!loose) continue;
                    int ver = BlobPersistenceVersion(Path.Combine(blobDir, subs[0].CurrentSave!.DataBlob ?? ""));
                    if (ver > max)
                    {
                        subroom_col.DeleteMany(x => x.RoomId == room.RoomId);
                        col.DeleteMany(x => x.RoomId == room.RoomId);
                        removed++;
                        Console.WriteLine($"[room-version] removed too-new loose room '{room.Name}' (id {room.RoomId}, save v{ver} > client max {max})");
                    }
                }
            }
            return removed;
        }

        /// <summary>The .room blob's persistence version = field 1 (a varint) at the very start. 0 if unreadable.</summary>
        public static int BlobPersistenceVersion(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                int first = fs.ReadByte();
                if (first != 0x08) return 0;            // expect field 1, wiretype 0 (varint)
                int result = 0, shift = 0, b;
                while ((b = fs.ReadByte()) >= 0)
                {
                    result |= (b & 0x7f) << shift; shift += 7;
                    if ((b & 0x80) == 0) break;
                    if (shift > 28) break;
                }
                return result;
            }
            catch { return 0; }
        }

        public enum LooseWrapStatus { Created, TooNew, AlreadyUsed, NameExists, Error }

        /// <summary>
        /// Host a single loose .room blob in RoomBlobs and, when it's old enough for the client and not
        /// already linked to a room, wrap it in a joinable @Importer room. Reusable by the admin upload
        /// AND the staging scan. Returns what happened.
        /// </summary>
        public static (LooseWrapStatus status, ulong roomId, int version, string roomName, string? error) WrapLooseRoom(string onDiskPath, ulong ownerId)
        {
            string bn = Path.GetFileName(onDiskPath);
            try
            {
                string blobDir = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs");
                Directory.CreateDirectory(blobDir);
                string dest = Path.Combine(blobDir, bn);
                if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(onDiskPath).Length)
                    File.Copy(onDiskPath, dest, true);

                if (AnyRoomUsesBlob(bn)) return (LooseWrapStatus.AlreadyUsed, 0, 0, bn, null);
                int ver = BlobPersistenceVersion(dest);
                if (ver > Campus_live_rewrite.api.server.ConfigCampus.ClientMaxRoomPersistenceVersion)
                    return (LooseWrapStatus.TooNew, 0, ver, bn, null);

                string roomName = Path.GetFileNameWithoutExtension(bn);
                string? scene = SceneIdFromBlob(dest);
                ulong id = CreateRoomFromBlob(bn, roomName, ownerId, scene, ver);
                return id != 0 ? (LooseWrapStatus.Created, id, ver, roomName, null)
                               : (LooseWrapStatus.NameExists, 0, ver, roomName, null);
            }
            catch (Exception ex) { return (LooseWrapStatus.Error, 0, 0, bn, ex.Message); }
        }

        /// <summary>Best-effort: pull the first GUID (the subroom's UnitySceneId) out of a .room blob's header.</summary>
        public static string? SceneIdFromBlob(string path)
        {
            try
            {
                byte[] head = new byte[Math.Min(4096, (int)new FileInfo(path).Length)];
                using (var fs = File.OpenRead(path)) fs.Read(head, 0, head.Length);
                string ascii = System.Text.Encoding.ASCII.GetString(head);
                var m = System.Text.RegularExpressions.Regex.Match(ascii, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                return m.Success ? m.Value : null;
            }
            catch { return null; }
        }

        /// <summary>True if the directory looks like a Rec Room room export (has RoomDetails.json + a room blob).</summary>
        public static bool LooksLikeRoomExport(string dir)
        {
            try
            {
                return Directory.EnumerateFiles(dir, "RoomDetails.json", SearchOption.AllDirectories).Any()
                    && Directory.EnumerateFiles(dir, "*room_data*.binpb", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }

        // Pick, among candidate filenames in dir, the one whose SHA-256 matches expectedHashB64.
        // Falls back to the first existing candidate (candidates are listed best-first) when no hash
        // is recorded or none match - better to serve a plausible blob than nothing.
        private static string? PickBlobByHash(string dir, string[] candidateNames, string? expectedHashB64)
        {
            var existing = candidateNames.Select(n => Path.Combine(dir, n)).Where(File.Exists).ToList();
            if (existing.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(expectedHashB64))
            {
                foreach (var p in existing)
                {
                    try
                    {
                        using var fs = File.OpenRead(p);
                        var hash = Convert.ToBase64String(SHA256.Create().ComputeHash(fs));
                        if (string.Equals(hash, expectedHashB64, StringComparison.Ordinal)) return p;
                    }
                    catch { }
                }
            }
            return existing[0];
        }

        private static string? FindSubRoomDir(string baseDir, ulong subRoomId)
        {
            try
            {
                string prefix = $"SubRoom_{subRoomId}_";
                return Directory.EnumerateDirectories(baseDir, "SubRoom_*", SearchOption.AllDirectories)
                    .FirstOrDefault(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        public static void ExportRooms()
        {
            var allRooms = col.FindAll().ToList();
            var allSubrooms = subroom_col.FindAll().ToList();

            // Option 1: Export rooms with their subrooms attached
            foreach (var room in allRooms)
            {
                room.SubRooms = allSubrooms.Where(s => s.RoomId == room.RoomId).ToList();
            }

            // Build final result
            var exportData = new Resultslist<RoomDBClasses.RoomRoot>
            {
                Results = allRooms,        // No mapping, just raw rooms
                TotalResults = allRooms.Count
            };

            string outputPath = Path.Combine(Environment.CurrentDirectory, "Data/exported_rooms.json");
            string json = JsonConvert.SerializeObject(exportData, Formatting.Indented);
            File.WriteAllText(outputPath, json);

            Console.WriteLine($"Exported {allRooms.Count} rooms with subrooms → {outputPath}");
        }

        public static void FixDuplicateDorms()
        {
            
            

            // Get all dorm rooms
            var dormRooms = col.Find(x => x.IsDorm).ToList();

            // Group dorms by creator
            var groupedDorms = dormRooms
                .GroupBy(r => r.CreatorAccountId)
                .Where(g => g.Count() > 1);

            foreach (var group in groupedDorms)
            {
                // Load player data to get DormRoomId
                var player = PlayerDB.GetFullAccount(group.FirstOrDefault().CreatorAccountId);
                if (player == null)
                    continue;

                // Try to keep the dorm that matches player.DormRoomId
                var dormToKeep = group.FirstOrDefault(r => r.RoomId == player.player_Extra.DormRoomID);

                // Fallback: keep oldest dorm if DormRoomId is invalid/missing
                if (dormToKeep == null)
                {
                    dormToKeep = group
                        .OrderBy(r => r.CreatedAt)
                        .First();
                }

                // Delete all other dorms
                var roomsToDelete = group
                    .Where(r => r.RoomId != dormToKeep.RoomId)
                    .ToList();

                foreach (var room in roomsToDelete)
                {
                    subroom_col.DeleteMany(x => x.RoomId == room.RoomId);
                    col.DeleteMany(x => x.RoomId == room.RoomId);

                    Console.WriteLine(
                        $"Deleted duplicate dorm room: {room.Name} (ID: {room.RoomId}) for creator {room.CreatorAccountId}"
                    );
                }

                Console.WriteLine(
                    $"Kept dorm room: {dormToKeep.Name} (ID: {dormToKeep.RoomId}) for creator {group.Key}"
                );
            }
        }




        public static string get_subroom_from_id(ulong room_id, ulong subroom_id)
        {
            try
            {
                var subroom = subroom_col.FindOne(x => x.RoomId == room_id && x.SubRoomId == subroom_id);

                return subroom?.Name ?? "Unknown Room";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in get_subroom_from_id: {ex.Message}");
                return "Unknown Room";
            }
        }

        public static ulong AddRoom(RoomDBClasses.RoomRoot room, bool isImportingFromJson = false)
        {
            try
            {
                if (room == null)
                    throw new ArgumentNullException(nameof(room));

                lock (_roomLock)
                {
                    // Skip duplicate check if DormRoom
                    if (room.Name != "DormRoom")
                    {
                        var existingRoom = col.FindOne(x => x.Name == room.Name);
                        if (existingRoom != null)
                            return 0;
                    }

                    var highest = col.Query().OrderByDescending(x => x.RoomId).Limit(1).FirstOrDefault();
                    room.RoomId = highest != null ? highest.RoomId + 1 : 1;

                    if (room.RoomId == 0)
                    {
                        throw new Exception($"Imported room '{room.Name}' has RoomId = 0");
                    }
                    //room.DataBlobHash = null;
                    if (string.IsNullOrEmpty(room.RankedEntityId))
                        room.RankedEntityId = room.RoomId.ToString();
                    if (room.UgcVersion == null)
                    {
                        room.UgcVersion = 1;
                    }

                    if (room.PersistenceVersion == null)
                    {
                        room.PersistenceVersion = 0;
                    }
                    if (room.NeedsSnapshotId == false)
                    {
                        room.CurrentSnapshotId = null;
                    }
                    // Ensure required fields exist
                    if (room.LoadScreens == null) throw new Exception("room.LoadScreens is null!");
                    if (room.Stats == null) throw new Exception("room.Stats is null!");

                    // --- Insert the room directly, no mapping ---
                    col.Insert(room);

                    // --- Insert subrooms directly, no mapping ---
                    if (room.SubRooms != null && room.SubRooms.Count > 0)
                    {
                        // ----------------------------
                        // Get next SubRoomId safely
                        // ----------------------------
                        var lastSubroom = subroom_col.Query()
                            .OrderByDescending(x => x.SubRoomId)
                            .Limit(1)
                            .FirstOrDefault();

                        ulong nextSubId = lastSubroom?.SubRoomId + 1 ?? 1;


                        // ----------------------------
                        // Get next SubRoomDataSaveId globally
                        // ----------------------------
                        ulong nextSaveId = 1;

                        var allExistingSubrooms = subroom_col.FindAll().ToList();

                        var maxSaveId = allExistingSubrooms
                            .SelectMany(x =>
                            {
                                var saves = new List<RoomDBClasses.CurrentSave>();

                                if (x.CurrentSave != null)
                                    saves.Add(x.CurrentSave);

                                if (x.AllSaves != null)
                                    saves.AddRange(x.AllSaves);

                                return saves;
                            })
                            .Select(x => x.SubRoomDataSaveId)
                            .DefaultIfEmpty(0UL)
                            .Max();

                        nextSaveId = maxSaveId + 1;


                        // ----------------------------
                        // Insert SubRooms
                        // ----------------------------
                        foreach (var sub in room.SubRooms)
                        {
                            try
                            {
                                ulong subId = nextSubId++;

                                if (isImportingFromJson && subId == 0)
                                    throw new Exception($"Imported subroom '{sub.Name}' has SubRoomId = 0");

                                sub.SubRoomId = subId;
                                sub.RoomId = room.RoomId;

                                // Fix CurrentSave
                                if (sub.CurrentSave != null)
                                {
                                    sub.CurrentSave.SubRoomDataSaveId = nextSaveId++;
                                    sub.CurrentSave.SubRoomId = sub.SubRoomId;
                                    sub.CurrentSave.UgcSubVersion = 0;
                                    sub.CurrentSave.PersistenceVersion = 0;
                                }

                                // ...and AllSaves the same way.
                                //
                                // Only CurrentSave was re-keyed. When a room is CLONED the
                                // subroom objects come straight off the source room, so every
                                // AllSaves entry kept the SOURCE subroom's SubRoomId and save
                                // id. GetRoomSaveById looks a save up by SubRoomDataSaveId, so
                                // the clone's saves pointed at another room's ids - the save
                                // either could not be found or resolved to the wrong subroom.
                                //
                                // Harmless on this server today because no template room has a
                                // save to copy, but it breaks the moment one does.
                                if (sub.AllSaves != null)
                                {
                                    foreach (var save in sub.AllSaves)
                                    {
                                        save.SubRoomDataSaveId = nextSaveId++;
                                        save.SubRoomId = sub.SubRoomId;
                                    }
                                }

                                subroom_col.Insert(sub);

                                Console.WriteLine($"Added subroom: {sub.Name} ({sub.SubRoomId})");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error adding subroom '{sub.Name}' for room '{room.Name}': {ex.Message}");
                            }
                        }
                    }

                    Console.WriteLine($"added room: {room.Name} ({room.RoomId})");
                    return room.RoomId;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding room '{room.Name}': {ex.Message}");
                return 0;
            }
        }

        public static RoomDBClasses.RoomRoot? GetRoom(ulong room_id)
        {
            // Find the main room
            var room = col.FindOne(x => x.RoomId == room_id);
            if (room == null) return null;

            // Fetch subrooms and attach them directly
            var subrooms = subroom_col.Find(x => x.RoomId == room_id).ToList();

            // Attach subrooms directly, no new object mapping
            room.SubRooms = subrooms;

            // Return the database object as-is
            return room;
        }

        public static bool DoesPlayerDormExist(ulong playerId)
        {
            var playercol = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            var player = playercol.FindOne(x => x.player.Id == playerId);

            if (player?.player_Extra == null)
                return false;

            ulong dormId = (ulong)player.player_Extra.DormRoomID;

            if (dormId == 0 || dormId == 1)
                return false;

            var dormRoom = RoomDB.GetRoom(dormId);
            return dormRoom != null;
        }

        // The dorm template the client asks for by name. Rec Room added multi-dorm
        // ("My Dorms" -> Create new Dorm) after this server was written, so no room
        // called CustomDormRoomTemplate was ever seeded - the lookup 404'd and the
        // client reported "Failed to find ^CustomDormRoomTemplate from server".
        // Room 1 IS the dorm template (Name "DormRoom", IsDorm, one "Home" subroom) and
        // is already what clone_dorm_room copies, so point the new name at it.
        private const ulong DormTemplateRoomId = 1;

        public static RoomDBClasses.RoomRoot? GetRoomByName(string roomName)
        {
            if (!string.IsNullOrEmpty(roomName))
            {
                // The client prefixes template/base room lookups with '^' ("^Park",
                // "^CustomDormRoomTemplate") but the rooms are stored unprefixed, so an
                // exact match never hit. Strip it before comparing.
                roomName = roomName.TrimStart('^');

                if (roomName.Equals("CustomDormRoomTemplate", StringComparison.OrdinalIgnoreCase)
                    || roomName.Equals("DormRoomTemplate", StringComparison.OrdinalIgnoreCase))
                {
                    var dormTemplate = GetRoom(DormTemplateRoomId);
                    if (dormTemplate != null)
                    {
                        dormTemplate.DataBlob ??= "";
                        return dormTemplate;
                    }
                }

                
                foreach (RoomDBClasses.RoomRoot subroom in col.FindAll())
                {
                    if (roomName.Equals(subroom.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        var room = GetRoom(subroom.RoomId);
                        if (room.DataBlob is null)
                        {
                            room.DataBlob = "";
                        }
                        return room;
                    }
                }
            }
            return null;
        }

        // "<Player>'s DormRoom" instead of the literal "DormRoom" every dorm used to
        // get. With every dorm sharing one name the client had nothing distinguishing
        // to show and fell back to just the accessibility tag - a bare "[PRIVATE]".
        public static string DormRoomName(ulong player_id)
        {
            string owner = PlayerDB.GetPlayerNameById(player_id);
            return string.IsNullOrWhiteSpace(owner) || owner == "Unknown"
                ? "DormRoom"
                : $"{owner}'s DormRoom";
        }

        /// <summary>
        /// Keep the player's dorm ROOM named after their current @name ("kyle's DormRoom"). Called
        /// after a username change; the room's stored Name is what the room list, the in-game
        /// "go to dorm" tile and the website would otherwise keep showing under the old handle.
        /// The live dorm INSTANCE name (`@kyle's Dorm`) is rebuilt from the username on every
        /// dorm matchmake, so it follows on the next visit by itself.
        /// </summary>
        public static void RenameDormForPlayer(ulong player_id)
        {
            try
            {
                ulong dormId = PlayerDB.GetDormId(player_id);
                if (dormId == 0) return;
                var dorm = col.FindOne(x => x.RoomId == dormId);
                if (dorm == null || !dorm.IsDorm) return;
                string wanted = DormRoomName(player_id);
                if (dorm.Name == wanted) return;
                dorm.Name = wanted;
                col.Update(dorm);
                Console.WriteLine($"[dorm] renamed dorm {dormId} of {player_id} -> \"{wanted}\"");
            }
            catch (Exception ex) { Console.WriteLine($"[dorm] rename for {player_id} failed: {ex.Message}"); }
        }

        /// <summary>Startup pass: bring every existing dorm's name in line with its owner's current @name.</summary>
        public static void RenameAllDormsToOwners()
        {
            int fixedCount = 0;
            foreach (var dorm in col.Find(x => x.IsDorm).ToList())
            {
                if (dorm.CreatorAccountId == 0) continue;
                string wanted = DormRoomName(dorm.CreatorAccountId);
                if (wanted == "DormRoom" || dorm.Name == wanted) continue;
                dorm.Name = wanted; col.Update(dorm); fixedCount++;
            }
            if (fixedCount > 0) Console.WriteLine($"[dorm] startup: renamed {fixedCount} dorm(s) to their owners' current @names");
        }

        public static ulong clone_dorm_room(ulong player_id)
        {
            RoomDBClasses.RoomRoot room_data = GetRoom(1);
            var deep = JsonConvert.SerializeObject(room_data);
            var room_data2 = JsonConvert.DeserializeObject<RoomDBClasses.RoomRoot>(deep);
            if (room_data2 == null)
            {
                throw new Exception("Base dorm room not found.");
            }
            if (room_data2.SubRooms == null || room_data2.SubRooms.Count == 0)
            {
                throw new Exception("Base dorm room has no subrooms.");
            }
            if (room_data2.Stats == null)
            {
                throw new Exception("Base dorm room stats are null.");
            }
            room_data2.CreatorAccountId = player_id;
            room_data2.CloningAllowed = false;
            // Was: the literal "DormRoom" for every player, so the client had nothing to
            // show but the accessibility tag - hence a bare "[PRIVATE]". Name it after
            // the owner so it reads "<Player>'s DormRoom [Private]" instead.
            room_data2.Name = DormRoomName(player_id);
            room_data2.CreatedAt = DateTime.Now;
            room_data2.Accessibility = RoomDBClasses.RoomAccessibility.Private;
            room_data2.IsRRO = false;
            room_data2.IsDorm = true;
            room_data2.Stats.CheerCount = 0;
            room_data2.Stats.FavoriteCount = 0;
            room_data2.Stats.VisitorCount = 0;
            room_data2.Stats.VisitCount = 0;
            room_data2.Roles = new List<RoomDBClasses.RoleClass>
            {
                new RoomDBClasses.RoleClass
                {
                    AccountId = (int)player_id,
                    InvitedRole = 0,
                    Role = RoomDBClasses.Role_data.Creator
                }
            };
            ulong id = AddRoom(room_data2);
            return id;
        }

        public static void GiveCoOwnerToAllRooms(ulong playerId)
        {
            var allRooms = col.FindAll().ToList();

            foreach (var room in allRooms)
            {
                if (room.CreatorAccountId == playerId)
                    continue;

                if (room.Roles == null)
                    room.Roles = new List<RoleClass>();

                var existingRole = room.Roles.FirstOrDefault(r => (ulong)r.AccountId == playerId);

                if (existingRole != null)
                {
                    if (existingRole.Role != RoomDBClasses.Role_data.Creator)
                    {
                        existingRole.Role = RoomDBClasses.Role_data.CoOwner;
                    }
                }
                else
                {
                    room.Roles.Add(new RoleClass
                    {
                        AccountId = (int)playerId,
                        InvitedRole = 0,
                        Role = RoomDBClasses.Role_data.CoOwner
                    });
                }

                col.Update(room);
            }

            Console.WriteLine($"Updated all rooms to grant Co-Owner to player {playerId}.");
        }

        public static RoomDBClasses.RoomRoot? IncreaseRoomVisit(ulong roomid)
        {
            
            var room = col.FindOne(x => x.RoomId == roomid);

            if (room != null)
            {
                if (room.Stats == null)
                    room.Stats = new Stats();

                if (room.Stats.VisitCount == 0)
                    room.Stats.VisitCount = 1;
                else
                    room.Stats.VisitCount++;

                col.Update(room);
                return room;
            }

            return null;
        }

        /// <summary>
        /// The hot-rooms candidate set: every non-dorm room, WITHOUT subrooms. The list is
        /// filtered, sorted and paged by the caller, which then attaches subrooms to just the
        /// page it returns (AttachSubRooms) - loading them for all ~2,500 candidates to serve 30
        /// was what made this endpoint take over a minute.
        /// </summary>
        public static List<RoomDBClasses.RoomRoot> GetHotRooms()
        {
            return col.Query()
                .Where(r => !r.IsDorm)
                .ToList();
        }

        /// <summary>
        /// Populate SubRooms on the given rooms. One indexed lookup per room (RoomId is indexed),
        /// so call it on the rooms you are actually returning, not on the whole table.
        /// </summary>
        public static void AttachSubRooms(IEnumerable<RoomDBClasses.RoomRoot> rooms)
        {
            foreach (var room in rooms)
                room.SubRooms = subroom_col.Find(s => s.RoomId == room.RoomId).ToList();
        }

        /// <summary>
        /// Every non-dorm room with subrooms attached. Subrooms are read in ONE pass and grouped in
        /// memory rather than one query per room. Admin/maintenance use - the client-facing lists
        /// should page first and use AttachSubRooms on the page.
        /// </summary>
        public static List<RoomDBClasses.RoomRoot> GetAllRooms()
        {
            var mainRooms = col.Query()
                .Where(r => !r.IsDorm)
                .ToList();

            var byRoom = subroom_col.FindAll()
                .GroupBy(s => s.RoomId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var room in mainRooms)
                room.SubRooms = byRoom.TryGetValue(room.RoomId, out var subs) ? subs : new List<SubRoom>();

            return mainRooms;
        }

        /// <summary>Every room, dorms included, with its subrooms (the admin RecFlare CDN scan).</summary>
        public static List<RoomDBClasses.RoomRoot> GetAllRoomsIncludingDorms()
        {
            var all = col.FindAll().ToList();
            var byRoom = subroom_col.FindAll()
                .GroupBy(s => s.RoomId)
                .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var room in all)
                room.SubRooms = byRoom.TryGetValue(room.RoomId, out var subs) ? subs : new List<SubRoom>();
            return all;
        }


        public static void UpdateRoomCheerCount(ulong roomid, int delta)
        {
            
            var room = col.FindOne(x => x.RoomId == roomid);
            if (room != null)
            {
                room.Stats.CheerCount = Math.Max(0, room.Stats.CheerCount + delta);
                col.Update(room);
            }
        }

        public static void UpdateRoomFavoriteCount(ulong roomid, int delta)
        {
            
            var room = col.FindOne(x => x.RoomId == roomid);
            if (room != null)
            {
                room.Stats.FavoriteCount = Math.Max(0, room.Stats.FavoriteCount + delta);
                col.Update(room);
            }
        }



        /// <summary>
        /// Attaches each room's LIVE subrooms from the subroom collection.
        ///
        /// Rooms carry an embedded SubRooms array, but it is a STALE SNAPSHOT taken when
        /// the room was created/cloned - saves are written to the subroom collection, not
        /// back into it. GetRoom() has always overwritten it with live data; the list
        /// queries did not, so a listing reported the original subroom with
        /// CurrentSave = null even for rooms that had been saved many times.
        ///
        /// That is what made the client treat saved rooms as "never been saved": it hid
        /// them from Create -> My Rooms, and prompted "Do you want to save?" on exit
        /// because as far as the listing was concerned no save existed.
        /// </summary>
        // ---- Room access control ----------------------------------------------
        //
        // RoomAccessibility: Private = 0, Public = 1, Unlisted = 2, Dev_only = 3,
        // Dev_Unlisted = 4. Private is the DEFAULT for a freshly created room, which is
        // why almost every player room is private.
        //
        // There is no separate invite system on this server - room ROLES are the invite.
        // Give someone Host/Moderator/CoOwner/Creator on a room and they can see and join
        // it; without a role a private room is invisible and unjoinable to them.

        /// <summary>The player's role on this room, or null if they have none.</summary>
        public static RoomDBClasses.Role_data? PlayerRoleOn(RoomDBClasses.RoomRoot? room, ulong playerId)
        {
            if (room == null) return null;
            if (room.CreatorAccountId == playerId) return RoomDBClasses.Role_data.Creator;

            var role = room.Roles?.FirstOrDefault(r => (ulong)r.AccountId == playerId);
            return role?.Role;
        }

        /// <summary>
        /// True when the player holds a role that counts as being part of the room -
        /// owner, co-owner, moderator or host. Explicitly false for Banned and None.
        /// </summary>
        public static bool PlayerIsMemberOf(RoomDBClasses.RoomRoot? room, ulong playerId)
        {
            var role = PlayerRoleOn(room, playerId);
            if (role == null) return false;

            switch (role.Value)
            {
                case RoomDBClasses.Role_data.Creator:
                case RoomDBClasses.Role_data.CoOwner:
                case RoomDBClasses.Role_data.TemporaryCoOwner:
                case RoomDBClasses.Role_data.Contributor:
                case RoomDBClasses.Role_data.Moderator:
                case RoomDBClasses.Role_data.Host:
                    return true;
                default:
                    return false;   // None / Banned
            }
        }

        /// <summary>Banned from this specific room (an elapsed timed ban is lifted on the way).</summary>
        public static bool PlayerIsBannedFrom(RoomDBClasses.RoomRoot? room, ulong playerId)
            => ActiveRoomBan(room, playerId) != null;

        /// <summary>
        /// The ban currently keeping this player out of the room, or null. The source of truth is
        /// RoomBans: an active record (Status=0, BanEndTime not elapsed) IS the ban. The Role_data.Banned
        /// marker on the role row is a cache the join gates read, kept in sync here.
        ///
        /// Fixes the "random bans" bug: a Role.Banned row left over from old data (half-finished
        /// unban, pre-migration kick-stored-as-ban that LiftKickBans didn't catch, a role dedup race)
        /// used to be treated as a permanent ban even with no RoomBans record behind it. Now a Banned
        /// role with no active RoomBans record is swept here and the player is treated as not banned.
        /// A timed ban whose BanEndTime has passed is still lifted the same way.
        /// </summary>
        public static RoomDBClasses.RoomBanDetails? ActiveRoomBan(RoomDBClasses.RoomRoot? room, ulong playerId)
        {
            if (room == null) return null;

            // RoomBans is the authoritative record. Find the most recent active one for this player.
            var ban = room.RoomBans?
                .Where(b => b.AccountId == (long)playerId && b.Status == 0)
                .OrderByDescending(b => b.BanStartTime)
                .FirstOrDefault();

            // No record = not banned, whatever the Roles cache says. Sweep stale Banned role rows so
            // the join gates (which still read the Role cache) stop refusing a player who isn't banned.
            if (ban == null)
            {
                bool hasStaleBannedRole = room.Roles?.Any(r => (ulong)r.AccountId == playerId && r.Role == RoomDBClasses.Role_data.Banned) == true;
                if (hasStaleBannedRole)
                {
                    var stored = col.FindOne(x => x.RoomId == room.RoomId);
                    if (stored != null)
                    {
                        int removed = stored.Roles?.RemoveAll(r => (ulong)r.AccountId == playerId && r.Role == RoomDBClasses.Role_data.Banned) ?? 0;
                        if (stored.RoomBanDetails?.AccountId == (long)playerId) stored.RoomBanDetails = null;
                        if (removed > 0) col.Update(stored);
                    }
                    room.Roles?.RemoveAll(r => (ulong)r.AccountId == playerId && r.Role == RoomDBClasses.Role_data.Banned);
                    if (room.RoomBanDetails?.AccountId == (long)playerId) room.RoomBanDetails = null;
                    Console.WriteLine($"[roomban] room {room.RoomId}: swept stale Banned role on {playerId} (no active ban record)");
                }
                return null;
            }

            // Timed ban that has expired: close it (both tables) so the player is let back in.
            if (ban.BanEndTime.HasValue && ban.BanEndTime.Value <= DateTime.UtcNow)
            {
                var stored = col.FindOne(x => x.RoomId == room.RoomId);
                if (stored != null)
                {
                    stored.Roles?.RemoveAll(r => (ulong)r.AccountId == playerId && r.Role == RoomDBClasses.Role_data.Banned);
                    foreach (var b in stored.RoomBans ?? new()) if (b.AccountId == (long)playerId && b.Status == 0) b.Status = 1;
                    if (stored.RoomBanDetails?.AccountId == (long)playerId) stored.RoomBanDetails = null;
                    col.Update(stored);
                }
                room.Roles?.RemoveAll(r => (ulong)r.AccountId == playerId && r.Role == RoomDBClasses.Role_data.Banned);
                ban.Status = 1;
                Console.WriteLine($"[roomban] room {room.RoomId}: ban on {playerId} elapsed, lifted");
                return null;
            }

            // Active ban. Make sure the Role cache reflects it (one time repair for data where the
            // record exists but the role row doesn't - the join gates would otherwise let them in).
            bool hasBannedRole = room.Roles?.Any(r => (ulong)r.AccountId == playerId && r.Role == RoomDBClasses.Role_data.Banned) == true;
            if (!hasBannedRole)
            {
                var stored = col.FindOne(x => x.RoomId == room.RoomId);
                if (stored != null)
                {
                    stored.Roles ??= new List<RoomDBClasses.RoleClass>();
                    var existing = stored.Roles.FirstOrDefault(r => (ulong)r.AccountId == playerId);
                    if (existing != null) existing.Role = RoomDBClasses.Role_data.Banned;
                    else stored.Roles.Add(new RoomDBClasses.RoleClass { AccountId = (long)playerId, Role = RoomDBClasses.Role_data.Banned, InvitedRole = 0, LastChangedByAccountId = ban.BannedByAccountId });
                    col.Update(stored);
                }
            }
            return ban;
        }

        /// <summary>
        /// Ban a player from a room: the Banned role (what the join gates refuse on) plus a record.
        /// duration null = until someone unbans them. The room's creator can never be banned.
        /// Any earlier active ban on the same player is closed as Lifted first.
        /// </summary>
        public static RoomDBClasses.RoomBanDetails? BanFromRoom(ulong roomId, ulong playerId, ulong by, string? reason, TimeSpan? duration)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null || room.CreatorAccountId == playerId) return null;

            room.Roles ??= new List<RoomDBClasses.RoleClass>();
            var existing = room.Roles.FirstOrDefault(r => (ulong)r.AccountId == playerId);
            if (existing != null) { existing.Role = RoomDBClasses.Role_data.Banned; existing.InvitedRole = 0; existing.LastChangedByAccountId = (long)by; }
            else room.Roles.Add(new RoomDBClasses.RoleClass { AccountId = (long)playerId, Role = RoomDBClasses.Role_data.Banned, InvitedRole = 0, LastChangedByAccountId = (long)by });

            room.RoomBans ??= new List<RoomDBClasses.RoomBanDetails>();
            foreach (var old in room.RoomBans) if (old.AccountId == (long)playerId && old.Status == 0) old.Status = 2;
            var ban = new RoomDBClasses.RoomBanDetails
            {
                AccountId = (long)playerId,
                BannedByAccountId = (long)by,
                BanStartTime = DateTime.UtcNow,
                BanEndTime = duration.HasValue ? DateTime.UtcNow + duration.Value : null,
                Reason = reason ?? "",
                Status = 0
            };
            room.RoomBans.Add(ban);
            room.RoomBanDetails = ban;
            col.Update(room);
            Console.WriteLine($"[roomban] room {roomId} '{room.Name}': {by} banned {playerId} " +
                              $"{(duration.HasValue ? $"for {duration.Value.TotalMinutes:0} min" : "until unbanned")}" +
                              $"{(string.IsNullOrEmpty(reason) ? "" : $" - {reason}")}");
            return ban;
        }

        /// <summary>Lift a room ban. Returns the closed record, or null if they were not banned.
        /// Sweeps every Banned role row and every active ban record for this player, not just the
        /// first - so a half-finished historical state can't leave the player partly banned.</summary>
        public static RoomDBClasses.RoomBanDetails? UnbanFromRoom(ulong roomId, ulong playerId, ulong by)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return null;
            int rolesRemoved = room.Roles?.RemoveAll(r => (ulong)r.AccountId == playerId && r.Role == RoomDBClasses.Role_data.Banned) ?? 0;
            RoomDBClasses.RoomBanDetails? ban = null;
            if (room.RoomBans != null)
            {
                foreach (var b in room.RoomBans.Where(b => b.AccountId == (long)playerId && b.Status == 0).ToList())
                {
                    b.Status = 2;
                    b.UnbannedByAccountId = (long)by;
                    ban ??= b;   // return the first one closed so the caller has something to log
                }
            }
            if (rolesRemoved == 0 && ban == null) return null;
            if (room.RoomBanDetails?.AccountId == (long)playerId) room.RoomBanDetails = null;
            col.Update(room);
            Console.WriteLine($"[roomban] room {roomId}: {by} unbanned {playerId} (closed {(ban == null ? 0 : room.RoomBans!.Count(b => b.AccountId == (long)playerId && b.Status == 2 && b.UnbannedByAccountId == (long)by))} record(s), removed {rolesRemoved} role row(s))");
            return ban ?? new RoomDBClasses.RoomBanDetails { AccountId = (long)playerId, BannedByAccountId = 0, BanStartTime = DateTime.UtcNow, Status = 2, UnbannedByAccountId = (long)by, Reason = "" };
        }

        /// <summary>
        /// One-off repair: room kicks used to be stored as room bans ("Kicked by X" / "Vote
        /// kicked ..."), so every kick was a permanent ban from the map. Lift those; a real ban
        /// (issued from the ban route, any other reason) is left alone. Returns how many.
        /// </summary>
        public static int LiftKickBans()
        {
            int lifted = 0;
            foreach (var room in col.FindAll().Where(r => r.RoomBans != null && r.RoomBans.Count > 0).ToList())
            {
                bool changed = false;
                foreach (var b in room.RoomBans!.Where(b => b.Status == 0).ToList())
                {
                    string reason = b.Reason ?? "";
                    if (!reason.StartsWith("Kicked by ", StringComparison.OrdinalIgnoreCase) && !reason.StartsWith("Vote kicked", StringComparison.OrdinalIgnoreCase)) continue;
                    b.Status = 2;
                    room.Roles?.RemoveAll(r => r.AccountId == b.AccountId && r.Role == RoomDBClasses.Role_data.Banned);
                    if (room.RoomBanDetails?.AccountId == b.AccountId) room.RoomBanDetails = null;
                    Console.WriteLine($"[roomban] room {room.RoomId} '{room.Name}': lifted ban on {b.AccountId} - it was a kick ({reason})");
                    changed = true; lifted++;
                }
                if (changed) col.Update(room);
            }
            return lifted;
        }

        /// <summary>
        /// Rec Room Originals (RecCenter, the hubs, the stock games) cannot be room-banned: a ban there locks a
        /// player out of the map every new player lands in (a moderator's ban in ^RecCenter did exactly that).
        /// The ban routes now turn it into a kick; this lifts the ones already stored, once at start-up.
        /// </summary>
        public static int LiftOriginalRoomBans()
        {
            int lifted = 0;
            foreach (var room in col.FindAll().Where(r => IsRecRoomOriginal(r) && ((r.RoomBans?.Any(b => b.Status == 0) ?? false) || (r.Roles?.Any(x => x.Role == RoomDBClasses.Role_data.Banned) ?? false))).ToList())
            {
                foreach (var b in room.RoomBans?.Where(b => b.Status == 0).ToList() ?? new())
                {
                    b.Status = 2;
                    Console.WriteLine($"[roomban] room {room.RoomId} '{room.Name}': lifted ban on {b.AccountId} - Rec Room Originals can't be room-banned");
                    lifted++;
                }
                room.Roles?.RemoveAll(x => x.Role == RoomDBClasses.Role_data.Banned);
                room.RoomBanDetails = null;
                col.Update(room);
            }
            return lifted;
        }

        /// <summary>
        /// One-off repair: sweep ORPHAN Banned role entries - Role_data.Banned rows on rooms where
        /// the player has no active RoomBans record. Those entries would be read as a permanent ban
        /// by the join gates (the "random bans" players reported). Returns how many were removed.
        /// </summary>
        public static int SweepOrphanBannedRoles()
        {
            int removed = 0;
            foreach (var room in col.FindAll().Where(r => r.Roles != null && r.Roles.Count > 0).ToList())
            {
                var bannedRoles = room.Roles!.Where(r => r.Role == RoomDBClasses.Role_data.Banned).ToList();
                if (bannedRoles.Count == 0) continue;
                bool changed = false;
                foreach (var r in bannedRoles)
                {
                    bool hasActiveRecord = room.RoomBans?.Any(b => b.AccountId == r.AccountId && b.Status == 0
                                                                   && (!b.BanEndTime.HasValue || b.BanEndTime.Value > DateTime.UtcNow)) == true;
                    if (hasActiveRecord) continue;
                    room.Roles!.Remove(r);
                    if (room.RoomBanDetails?.AccountId == r.AccountId) room.RoomBanDetails = null;
                    Console.WriteLine($"[roomban] room {room.RoomId} '{room.Name}': removed orphan Banned role on {r.AccountId} (no active ban record)");
                    changed = true; removed++;
                }
                if (changed) col.Update(room);
            }
            return removed;
        }

        /// <summary>The room's active bans, newest first.</summary>
        public static List<RoomDBClasses.RoomBanDetails> ActiveRoomBans(RoomDBClasses.RoomRoot room)
        {
            var list = new List<RoomDBClasses.RoomBanDetails>();
            foreach (var r in (room.Roles ?? new()).Where(r => r.Role == RoomDBClasses.Role_data.Banned).ToList())
            {
                var b = ActiveRoomBan(room, (ulong)r.AccountId);
                if (b != null) list.Add(b);
            }
            return list.OrderByDescending(b => b.BanStartTime).ToList();
        }

        /// <summary>Creator, co-owner, moderator or host on the room, or a staff account.</summary>
        public static bool CanModerateRoom(RoomDBClasses.RoomRoot? room, ulong accountId)
        {
            if (room == null) return false;
            if (room.CreatorAccountId == accountId) return true;
            if (PlayerDB.CheckDevFlag(accountId) || PlayerDB.CheckModFlag(accountId)) return true;
            var role = PlayerRoleOn(room, accountId);
            return role is RoomDBClasses.Role_data.CoOwner or RoomDBClasses.Role_data.TemporaryCoOwner
                or RoomDBClasses.Role_data.Moderator or RoomDBClasses.Role_data.Host;
        }

        /// <summary>
        /// Is the room published?
        ///
        /// PublishState is the client's own field (RoomRoot.PublishState) and the save
        /// request carries AutoPublish. Neither was ever read by this server - every room
        /// in the DB sits at PublishState = 0, including the stock RRO rooms.
        ///
        /// So "published" is treated as: PublishState says so, OR the room is Public /
        /// Unlisted. That second clause matters - without it every existing room (Rec
        /// Center, Park, all 54 of account 1's) would read as unpublished and vanish from
        /// everyone's listings the moment this shipped.
        /// </summary>
        public static bool RoomHasTag(RoomDBClasses.RoomRoot room, string tag) =>
            room.Tags != null && room.Tags.Any(t => string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase));

        // Rooms 2.0 keep their REAL visibility on the "Home" subroom; the room-level Accessibility
        // is a stale default (Private) for them. Search / hot / CanPlayerSeeRoom all read the
        // ROOM-level field, so a published 2.0 room was invisible everywhere. This returns the
        // room's effective accessibility - its Home subroom's (or the first subroom's) - which the
        // room-level field is kept mirrored to (ModifySubroom + the startup SyncAllRoomAccessibility).
        public static RoomDBClasses.RoomAccessibility PrimaryAccessibility(RoomDBClasses.RoomRoot room)
        {
            var subs = room.SubRooms;
            if (subs == null || subs.Count == 0) return room.Accessibility;
            var home = subs.FirstOrDefault(s => string.Equals(s.Name, "Home", StringComparison.OrdinalIgnoreCase)) ?? subs[0];
            return home.Accessibility;
        }

        // The room-level Accessibility a room SHOULD carry: its Home subroom's, which is what the
        // owner actually set in-game. Rooms are NOT auto-published, so the accessibility the owner
        // chose (Public / Unlisted / Private) IS the room's published state - see IsRoomPublished.
        public static RoomDBClasses.RoomAccessibility EffectiveRoomAccessibility(RoomDBClasses.RoomRoot room)
            => PrimaryAccessibility(room);

        // Idempotent migration: give every non-RRO room its effective accessibility, so existing
        // PUBLISHED Rooms 2.0 become Public/searchable and unpublished ones match their subroom.
        // RRO rooms already carry a correct room-level Accessibility, so leave them.
        /// <summary>
        /// Idempotent: player-made rooms cloned before the clone fix carry the template's
        /// IsDeveloperOwned=true and RankedEntityId (the client hides developer-owned rooms from a
        /// player's created list). Rec Room Originals and base templates are left alone.
        /// </summary>
        public static int FixPlayerRoomFlags()
        {
            int n = 0;
            foreach (var room in col.FindAll().ToList())
            {
                if (room == null || room.IsRRO || room.IsDorm) continue;
                if (RoomHasTag(room, "base") || RoomHasTag(room, "rro") || RoomHasTag(room, "recroomoriginal")) continue;
                if (room.CreatorAccountId <= 1) continue;   // system / owner-seeded rooms
                if (room.IsDevRoom) continue;               // an admin made it a dev room on purpose
                bool changed = false;
                if (room.IsDeveloperOwned) { room.IsDeveloperOwned = false; changed = true; }
                if (room.RankedEntityId != room.RoomId.ToString()) { room.RankedEntityId = room.RoomId.ToString(); changed = true; }
                if (changed) { col.Update(room); n++; }
            }
            if (n > 0) Console.WriteLine($"[rooms] cleared template flags (IsDeveloperOwned / RankedEntityId) on {n} player room(s)");
            return n;
        }

        public static int SyncAllRoomAccessibility()
        {
            int n = 0;
            // Subrooms live in their own collection and are NOT on the rows FindAll hands back, so
            // EffectiveRoomAccessibility (the Home subroom's setting) used to see no subrooms and
            // answer the room's own field - this sync never changed anything. One pass over the
            // subroom collection, grouped by room, instead of a query per room.
            var subsByRoom = subroom_col.FindAll().GroupBy(s => s.RoomId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var room in col.FindAll().ToList())
            {
                if (room == null || room.IsRRO) continue;
                if (subsByRoom.TryGetValue(room.RoomId, out var subs)) room.SubRooms = subs;
                if (room.SubRooms == null || room.SubRooms.Count == 0) continue;
                var eff = EffectiveRoomAccessibility(room);
                if (room.Accessibility != eff)
                {
                    room.Accessibility = eff;
                    col.Update(room); n++;   // the embedded subroom copy is refreshed from the subroom collection too
                    Console.WriteLine($"[rooms] {room.RoomId} '{room.Name}': room-level accessibility -> {eff} (from its Home subroom)");
                }
            }
            return n;
        }

        public static bool IsRoomPublished(RoomDBClasses.RoomRoot? room)
        {
            if (room == null) return false;

            // Legacy Rec Room Originals (Rec Center, Park, the base rooms) are always listed.
            if (room.IsRRO || RoomHasTag(room, "base") || RoomHasTag(room, "rro") || RoomHasTag(room, "recroomoriginal"))
                return true;

            // For a user room (Rooms 2.0 / beta) the ACCESSIBILITY the owner set IS the published
            // state: Public or Unlisted = visible to non-members; Private = a draft, members-only.
            // Rooms are NOT auto-published on save, so a freshly created (Private) room stays in the
            // owner's list only until they deliberately set it Public/Unlisted.
            var acc = EffectiveRoomAccessibility(room);
            return acc == RoomDBClasses.RoomAccessibility.Public
                || acc == RoomDBClasses.RoomAccessibility.Unlisted;
        }

        /// <summary>
        /// Does the room appear in LISTS for people who hold no role on it (another player's
        /// profile, search, hot)? Only a PUBLIC room does - Unlisted is exactly that (reachable by
        /// invite or link, never listed) and Private is a draft. Rec Room Originals are always listed.
        /// </summary>
        /// <summary>A Rec Room Original - the stock rooms, not a player's creation.</summary>
        public static bool IsRecRoomOriginal(RoomDBClasses.RoomRoot? room) =>
            room != null && (room.IsRRO || RoomHasTag(room, "base") || RoomHasTag(room, "rro") || RoomHasTag(room, "recroomoriginal"));

        public static bool IsRoomListed(RoomDBClasses.RoomRoot? room)
        {
            if (room == null) return false;
            if (room.IsRRO || RoomHasTag(room, "base") || RoomHasTag(room, "rro") || RoomHasTag(room, "recroomoriginal"))
                return true;
            return EffectiveRoomAccessibility(room) == RoomDBClasses.RoomAccessibility.Public;
        }

        /// <summary>
        /// May this player SEE the room (listings, search, someone's portfolio)?
        ///
        /// Published rooms are visible to everyone, private or not - a published private
        /// room advertises itself, it just does not let strangers into your session.
        /// An UNPUBLISHED room is invisible to anyone without a role on it.
        /// </summary>
        public static bool CanPlayerSeeRoom(RoomDBClasses.RoomRoot? room, ulong playerId)
        {
            if (room == null) return false;
            if (PlayerIsBannedFrom(room, playerId)) return false;
            if (PlayerIsMemberOf(room, playerId)) return true;   // own/co-own/mod/host

            return IsRoomListed(room);   // others only see what is PUBLIC; unlisted/private stay off their lists
        }

        /// <summary>
        /// May this player ENTER the room at all - in an instance of their own if need be?
        ///
        /// Published rooms may be entered by anyone: a published PRIVATE room hands the
        /// visitor their own fresh session (see CanPlayerShareInstance), which is what lets
        /// them play it with their own friends without getting into the owner's session.
        /// Unpublished rooms are members-only.
        /// </summary>
        public static bool CanPlayerEnterRoom(RoomDBClasses.RoomRoot? room, ulong playerId)
        {
            if (room == null) return false;
            if (PlayerIsBannedFrom(room, playerId)) return false;
            if (PlayerIsMemberOf(room, playerId)) return true;

            // A dorm is never open, published or not.
            if (room.IsDorm) return false;

            return IsRoomPublished(room);
        }

        /// <summary>
        /// May this player be dropped into an instance SOMEONE ELSE is already in?
        ///
        /// Only for Public rooms, or when they hold a role on the room. This is the line
        /// between "published private" and "public": both are visible and enterable, but a
        /// published private room gives each visitor their own session instead of pooling
        /// strangers into the owner's.
        /// </summary>
        public static bool CanPlayerShareInstance(RoomDBClasses.RoomRoot? room, ulong playerId)
        {
            if (room == null) return false;
            if (PlayerIsBannedFrom(room, playerId)) return false;
            if (PlayerIsMemberOf(room, playerId)) return true;

            return room.Accessibility == RoomDBClasses.RoomAccessibility.Public;
        }

        /// <summary>Sets the room's publish state (0 = unpublished). Returns false if missing.</summary>
        public static bool SetRoomPublishState(ulong roomId, int publishState)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return false;

            room.PublishState = publishState;
            if (publishState > 0) room.PublishedAt = DateTime.UtcNow;
            // Publishing makes a room Public for everyone; unpublishing drops it back to its
            // subroom's accessibility (and it becomes members-only via CanPlayerSeeRoom).
            room.Accessibility = EffectiveRoomAccessibility(room);

            col.Update(room);
            return true;
        }

        public static List<RoomDBClasses.RoomRoot> AttachSubRooms(List<RoomDBClasses.RoomRoot> rooms)
        {
            if (rooms == null || rooms.Count == 0) return rooms ?? new List<RoomDBClasses.RoomRoot>();

            foreach (var room in rooms)
            {
                if (room == null) continue;
                room.SubRooms = subroom_col.Find(s => s.RoomId == room.RoomId).ToList();
            }

            return rooms;
        }

        /// <summary>
        /// Every room the player should see under "My Rooms": ones they created, plus any
        /// they hold a role on. A room stays listed until the player's role is removed or
        /// the room is deleted - it is never dropped for being unsaved or unpublished.
        /// </summary>
        public static List<RoomDBClasses.RoomRoot> GetRoomsForPlayer(ulong playerId)
        {
            // A role of Banned is still "a role", so a room the player had been BANNED from
            // was listed among their own rooms. Creator always counts; otherwise the role has
            // to be one that actually makes them part of the room.
            var rooms = col.FindAll()
                           .Where(r => r != null
                                    && (r.CreatorAccountId == playerId
                                        || PlayerIsMemberOf(r, playerId)))
                           .ToList();

            return AttachSubRooms(rooms);
        }

        public static List<RoomDBClasses.RoomRoot> GetRoomsByCreator(ulong playerId)
        {

            var rooms = col.Find(x => x.CreatorAccountId == playerId).ToList();

            return AttachSubRooms(rooms);
        }

        /// <summary>
        /// A single PAGE of a creator's rooms, ordered newest-first, attaching subrooms ONLY to the
        /// page returned. GetRoomsByCreator loads EVERY room the creator owns and runs a subroom query
        /// per room - fine for a normal player, but @Importer owns thousands, so a profile view was
        /// loading everything at once and overloading the server. This caps it at the DB level.
        /// </summary>
        public static List<RoomDBClasses.RoomRoot> GetRoomsByCreatorPaged(ulong playerId, int skip, int take, bool attachSubrooms = true)
        {
            if (skip < 0) skip = 0;
            if (take <= 0) take = 200;
            var page = col.Query()
                .Where(x => x.CreatorAccountId == playerId)
                .OrderByDescending(x => x.RoomId)
                .Skip(skip)
                .Limit(take)
                .ToList();
            return attachSubrooms ? AttachSubRooms(page) : page;
        }

        /// <summary>Count of a creator's rooms - cheap (no room objects loaded).</summary>
        public static int CountRoomsByCreator(ulong playerId) => col.Count(x => x.CreatorAccountId == playerId);

        /// <summary>
        /// Rooms the player has a role on (Creator/CoOwner/etc.) but did not create -
        /// what the client's "contributed to" listing expects.
        /// </summary>
        /// <summary>
        /// Every room this player has a hand in - the ones they CREATED plus the ones they hold a
        /// role on. This is what the profile's room list is drawn from.
        ///
        /// It used to exclude the player's own rooms outright (`CreatorAccountId != playerId`),
        /// which is why a profile read "32 Rooms" above a list holding exactly one: the single
        /// room they had been invited to co-own. A creator IS a contributor to their own room -
        /// the owner is simply the first role on it - so they belong in this list ahead of
        /// everyone else's. (The Room Showcase is a separate, deliberately-picked handful; it is
        /// NOT a place to dump these. See RoomController.RoomShowcaseForPlayer.)
        ///
        /// Own rooms come first, newest first; then the rooms they were invited to. Capped, because
        /// this attaches subrooms to every room it returns.
        /// </summary>
        public static List<RoomDBClasses.RoomRoot> GetRoomsContributedTo(ulong playerId)
        {
            var all = col.FindAll().ToList();

            var mine = all.Where(r => r.CreatorAccountId == playerId)
                          .OrderByDescending(r => r.CreatedAt);

            // Only a role that was ACCEPTED and can edit the room's contents. This used to match ANY entry with the
            // player's id - a pending invite (Role None, InvitedRole set) or a ban counted, so a room showed up under a
            // player's My Rooms / profile the moment someone invited them, accepted or not.
            var invited = all.Where(r => r.CreatorAccountId != playerId
                                      && r.Roles != null
                                      && r.Roles.Any(x => (ulong)x.AccountId == playerId
                                                       && (x.Role == RoomDBClasses.Role_data.Contributor
                                                           || x.Role == RoomDBClasses.Role_data.CoOwner
                                                           || x.Role == RoomDBClasses.Role_data.TemporaryCoOwner)));

            return AttachSubRooms(mine.Concat(invited)
                                      .Take(Campus_live_rewrite.api.server.ConfigCampus.ProfileRoomsCap)
                                      .ToList());
        }

        public static List<RoomDBClasses.RoomRoot> Search(string p0, bool isBase = false)
        {
            if (string.IsNullOrWhiteSpace(p0))
                return new List<RoomDBClasses.RoomRoot>();

            var terms = p0.ToLowerInvariant()
                        .Split(new char[] { ' ', '+' }, StringSplitOptions.RemoveEmptyEntries);

            // Start with the basic "Not a Dorm" filter
            var query = col.Query().Where(r => !r.IsDorm);

            // Only enforce Public accessibility if isBase is false
            if (!isBase)
            {
                query = query.Where(r => r.Accessibility == RoomDBClasses.RoomAccessibility.Public);
            }

            foreach (var term in terms)
            {
                if (term.StartsWith("#"))
                {
                    string tag = term.Substring(1);
                    
                    // Using BsonExpression ensures LiteDB looks directly into the array
                    // This looks for any element in the 'Tags' array where the 'Tag' field matches
                    query = query.Where("$.Tags[*].Tag ANY = @0", tag);
                }
                else
                {
                    string nameTerm = term;
                    query = query.Where(r => r.Name.ToLower().Contains(nameTerm));
                }
            }

            return AttachSubRooms(query.ToList());
        }

        public static ulong GetAllRoomsCount()
        {

            return (ulong)col.Count();
        }

        /// <summary>All non-dorm rooms WITHOUT attaching subrooms - for listings (admin table) that
        /// only need top-level fields. Avoids one subroom query per room (brutal at thousands of rooms).</summary>
        public static List<RoomDBClasses.RoomRoot> GetAllRoomsLite()
        {
            return col.Query().Where(r => !r.IsDorm).ToList();
        }

        public static void DeleteAllRoomsFromPlayerId(ulong creatorId)
        {

            var roomsToDelete = col.Find(x => x.CreatorAccountId == creatorId).ToList();

            foreach (var room in roomsToDelete)
            {
                subroom_col.DeleteMany(x => x.RoomId == room.RoomId);
                col.DeleteMany(x => x.RoomId == room.RoomId);
            }

            Console.WriteLine($"Deleted {roomsToDelete.Count} rooms and their subrooms for creator {creatorId}.");
        }

        public static RoomDBClasses.RoomRoot? SetRoomName(ulong roomId, string roomName)
        {
            
            var room = col.FindOne(x => x.RoomId == roomId);
            var existingRoom = col.FindOne(x => x.Name == roomName && x.RoomId != roomId);
            if (existingRoom != null)
            {
                return null;
            }
            if (room != null)
            {
                try
                {
                    room.Name = roomName;
                    col.Update(room);
                    
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            return room;
        }

      public static RoomDBClasses.RoomRoot? es()
        {
            
            var room = col.FindOne(x => x.RoomId == 53);
            if (room != null)
            {
                try
                {
                    room.CurrentSnapshotId = "98cfdcad-b680-4b33-e94c-08dd76d7c12b";
                    col.Update(room);
                    
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            return room;
        }

        /// <summary>
        /// A room whose record lacks what the game needs to SHOW it: no platform it supports (every Supports* flag false - the game treats the
        /// room as unplayable and its page stays blank), no player cap (MaxPlayers 0), null lists, no dates. Rooms imported from a bare
        /// room.json (the Splooty archive's has only the name, creator, picture and subrooms) arrive like that. Fills in what is missing and
        /// changes nothing else; true when something was changed.
        /// </summary>
        public static bool EnsureListable(ulong roomId)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return false;
            bool changed = false;
            if (!(room.SupportsScreens || room.SupportsWalkVR || room.SupportsTeleportVR || room.SupportsVRLow || room.SupportsQuest2 || room.SupportsMobile))
            {
                room.SupportsScreens = room.SupportsWalkVR = room.SupportsTeleportVR = room.SupportsVRLow = room.SupportsQuest2 = room.SupportsMobile = true;
                changed = true;
            }
            if (room.MaxPlayers <= 0)
            {
                room.MaxPlayers = Math.Max(8, (room.SubRooms ?? new()).Select(s => s.MaxPlayers).DefaultIfEmpty(0).Max());
                changed = true;
            }
            foreach (var s in room.SubRooms ?? new()) if (s.MaxPlayers <= 0) { s.MaxPlayers = room.MaxPlayers; changed = true; }
            if (room.Tags == null) { room.Tags = new(); changed = true; }
            if (room.PromoImages == null) { room.PromoImages = new(); changed = true; }
            if (room.PromoExternalContent == null) { room.PromoExternalContent = new(); changed = true; }
            if (room.RestrictedCircuitsAllowListNames == null) { room.RestrictedCircuitsAllowListNames = new(); changed = true; }
            if (room.LoadScreens == null) { room.LoadScreens = new(); changed = true; }
            if (room.CreatedAt == default) { room.CreatedAt = DateTime.UtcNow; changed = true; }
            if (room.PublishedAt == default) { room.PublishedAt = room.CreatedAt; changed = true; }
            if (changed) col.Update(room);
            return changed;
        }

        /// <summary>
        /// Player cap for the room and every one of its subrooms (the admin panel's room editor).
        /// The subroom's MaxPlayers is what matchmaking reads (HeartbeatDB.EffectiveCapacity).
        /// </summary>
        public static RoomDBClasses.RoomRoot? SetMaxPlayers(ulong roomId, int maxPlayers)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return null;
            room.MaxPlayers = maxPlayers;
            foreach (var s in room.SubRooms ?? new()) s.MaxPlayers = maxPlayers;
            col.Update(room);
            return room;
        }

        public static RoomDBClasses.RoomRoot? SetRoomDescription(ulong roomId, string NewDescription)
        {
            
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room != null)
            {
                try
                {
                    room.Description = NewDescription;
                    col.Update(room);

                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            return room;
        }

        public static RoomDBClasses.RoomRoot? SetRoomImageName(ulong roomId, string imageName)
        {
            
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room != null)
            {
                try
                {
                    room.ImageName = imageName;
                    col.Update(room);
                }
                catch (Exception ex)
                { 
                    Console.WriteLine(ex.ToString());
                }
            }
            return room;
        }

        public static RoomDBClasses.RoomRoot? SetRoomAccessibility(ulong roomId, RoomDBClasses.RoomAccessibility roomAccessibility)
        {
            
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room != null)
            {
                try
                {
                    room.Accessibility = roomAccessibility;
                    // The effective accessibility is read from the Home subroom (PrimaryAccessibility),
                    // so the subrooms must follow or "publish" changed nothing anyone could see.
                    foreach (var sr in subroom_col.Find(x => x.RoomId == roomId).ToList())
                    {
                        sr.Accessibility = roomAccessibility;
                        subroom_col.Update(sr);
                    }
                    bool published = roomAccessibility == RoomDBClasses.RoomAccessibility.Public
                                  || roomAccessibility == RoomDBClasses.RoomAccessibility.Unlisted;
                    room.PublishState = published ? 1 : 0;
                    if (published && (room.PublishedAt == default || room.PublishedAt < room.CreatedAt)) room.PublishedAt = DateTime.UtcNow;
                    col.Update(room);
                    Console.WriteLine($"[rooms] room {roomId} ({room.Name}) accessibility -> {roomAccessibility} ({(published ? "published" : "unpublished")})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            return room;
        }

        /// <summary>Admin panel "Dev room": marks the room developer-owned (the Room DTO's IsDeveloperOwned) and remembers
        /// that an admin chose it, so the start-up flag clean-up leaves it alone.</summary>
        public static bool SetDevRoom(ulong roomId, bool on)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return false;
            room.IsDevRoom = on;
            room.IsDeveloperOwned = on;
            col.Update(room);
            return true;
        }

        /// <summary>True when an admin ticked "Dev room" on this room (the game-reward pacing is off there).</summary>
        public static bool IsDevRoom(ulong roomId) => roomId != 0 && (col.FindOne(x => x.RoomId == roomId)?.IsDevRoom ?? false);

        public static RoomDBClasses.RoomRoot? SetRoomCloning(ulong roomId, bool cloningEnabled)
        {
            
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room != null)
            {
                try
                {
                    room.CloningAllowed = cloningEnabled;
                    col.Update(room);

                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            return room;
        }
        // supportsScreens=True&supportsWalkVR=False&supportsTeleportVR=True&supportsJuniors=True  
        public static RoomDBClasses.RoomRoot? SetRoomRestrictions(ulong roomId, bool supportsScreens, bool supportsWalkVR, bool supportsTeleportVR, bool supportsJuniors)
        {
            
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room != null)
            {
                try
                {
                    room.SupportsScreens = supportsScreens;
                    room.SupportsWalkVR = supportsWalkVR;
                    room.SupportsTeleportVR = supportsTeleportVR;
                    room.SupportsJuniors = supportsJuniors;
                    col.Update(room);

                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            return room;
        }

        public static bool UserCanEditRoom(ulong roomId, ulong accountId)
        {
            
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null)
                return false;

            if (room.CreatorAccountId == accountId)
                return true;

            return IsCoOwner(room, accountId);
        }


        public static RoomDBClasses.RoomRoot? SetRoomLoadscreen(ulong roomid, string? imageName, string? title, string? subtitle)
        {
            
            var room = col.FindOne(x => x.RoomId == roomid);
            if (room == null)
                return null;

            room.LoadScreens = new List<LoadScreen>
            {
                new LoadScreen
                {
                    ImageName = imageName ?? "",
                    Title = title ?? "",
                    Subtitle = subtitle ?? ""
                }
            };

            col.Update(room);
            return room;
        }

        public static RoomDBClasses.RoomRoot SetRoomWarningMask(ulong roomId, WarningMaskType warningMask)
        {
            
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room != null)
            {
                try
                {
                    room.WarningMask = warningMask;
                    col.Update(room);

                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            return room;
        }

        /// <summary>
        /// The reference's room tag edit (rooms.app.ts / applyRoomTagEdit). Two request shapes reach
        /// PUT /rooms/{id}/tags and the FIELDS say which:
        ///   - a lone `tag=<name>`: TOGGLE that owner tag (the five "main" tags pvp/quest/game/
        ///     hangout/art act as radio buttons);
        ///   - anything else is the whole-state save from room settings
        ///     (`autoTag=limitsv2&tag=roleplay&tag=social&primaryGenreTag=roleplay`): `tag` is the
        ///     COMPLETE set of owner (Type 0) tags, `autoTag` ADDS derived Type 1 tags (limitsv2,
        ///     beta - the ones Beta rooms and Limits V2 need to be recognised), `primaryGenreTag`
        ///     flags the genre.
        /// The previous implementation toggled every posted tag, so a settings save with three
        /// tags flipped them, and `autoTag` wiped the list to just "beta" as an owner tag.
        /// </summary>
        public static RoomDBClasses.RoomRoot? ApplyRoomTagEdit(ulong roomId, string? toggle, List<string>? tags, List<string> autoTags, string? primaryGenre)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return null;
            var next = (room.Tags ?? new List<TagClass>()).Where(t => t != null && !string.IsNullOrWhiteSpace(t.Tag)).ToList();
            string Key(TagClass t) => t.Tag.Trim().ToLowerInvariant();
            var mainTags = new HashSet<string> { "pvp", "quest", "game", "hangout", "art" };

            if (tags != null)
            {
                var posted = tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
                var kept = next.Where(t => t.Type != 0 && !posted.Contains(Key(t))).ToList();
                foreach (var tag in posted)
                    kept.Add(next.FirstOrDefault(t => Key(t) == tag) ?? new TagClass { Tag = tag, Type = 0 });
                next = kept;
            }
            else if (!string.IsNullOrWhiteSpace(toggle))
            {
                string lower = toggle.Trim().ToLowerInvariant();
                var existing = next.FirstOrDefault(t => Key(t) == lower);
                if (existing != null) next.Remove(existing);
                else
                {
                    if (mainTags.Contains(lower)) next = next.Where(t => !mainTags.Contains(Key(t))).ToList();
                    next.Add(new TagClass { Tag = lower, Type = 0 });
                }
            }

            foreach (var auto in autoTags)
            {
                string lower = auto.Trim().ToLowerInvariant();
                if (lower.Length == 0) continue;
                var existing = next.FirstOrDefault(t => Key(t) == lower);
                if (existing != null) existing.Type = 1;
                else next.Add(new TagClass { Tag = lower, Type = 1 });
            }

            if (!string.IsNullOrWhiteSpace(primaryGenre))
            {
                string lower = primaryGenre.Trim().ToLowerInvariant();
                foreach (var t in next) t.IsPrimaryGenre = null;
                var chosen = next.FirstOrDefault(t => Key(t) == lower);
                if (chosen != null) chosen.IsPrimaryGenre = true;
                else next.Add(new TagClass { Tag = lower, Type = 0, IsPrimaryGenre = true });
            }

            room.Tags = next;
            col.Update(room);
            return room;
        }

        public static RoomDBClasses.RoomRoot? SetRoomTags(List<string> tags, ulong roomId, int type = 0)
        {
            
            col.EnsureIndex(x => x.RoomId);

            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null)
                return null;

            if (tags == null || tags.Count == 0)
                return room;

            // Normalize input tags: trim, lowercase, remove empty, distinct
            var newTags = tags
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            if (room.Tags == null)
                room.Tags = new List<TagClass>();

            foreach (var newTag in newTags)
            {
                var existingTag = room.Tags.FirstOrDefault(t =>
                    t.Tag.Equals(newTag, StringComparison.OrdinalIgnoreCase));

                if (existingTag != null)
                {
                    // TagClass exists -> remove it
                    room.Tags.Remove(existingTag);
                }
                else
                {
                    // TagClass does not exist -> add it
                    room.Tags.Add(new TagClass
                    {
                        Tag = newTag,
                        Type = type
                    });
                }
            }

            col.Update(room);
            return room;
        }



        public static bool DoesRoomExist(ulong roomId)
        {
            
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room != null)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public static ulong? CloneRoom(ulong roomId, string RoomName, ulong playerId)
        {
            

            // ✅ Deep clone to detach from DB references
            RoomDBClasses.RoomRoot? original = GetRoom(roomId);
            RoomDBClasses.RoomRoot? room_data = JsonConvert.DeserializeObject<RoomDBClasses.RoomRoot>(JsonConvert.SerializeObject(original));

            if (room_data?.Name == "MakerRoom")
                goto clone_room_data;

            if (room_data?.Name == "TheBackDoor")
                return null;

            if (room_data?.Name == "DormRoom")
                return null;

            if (room_data.CloningAllowed &&
                (room_data.Accessibility == RoomDBClasses.RoomAccessibility.Public ||
                 room_data.Accessibility == RoomDBClasses.RoomAccessibility.Unlisted))
                goto clone_room_data;

            if (RoomName == "MakerRoom")
                goto clone_room_data;

            if (!room_data.CloningAllowed && room_data.CreatorAccountId != playerId && !room_data.IsDorm)
                return null;

            var existingRoom = col.FindOne(x => x.Name == room_data.Name && x.RoomId != room_data.RoomId);
            if (existingRoom != null)
                return null;

            clone_room_data:
            room_data.RoomId = 0;
            room_data.CreatorAccountId = playerId;
            room_data.CloningAllowed = false;
            room_data.Name = RoomName;
            room_data.ImageName = "DefaultRoomImage.jpg";
            room_data.CreatedAt = DateTime.Now;
            room_data.Accessibility = RoomDBClasses.RoomAccessibility.Private;
            room_data.PublishState = 0;
            // A player's room is not one of Rec Room's own. The template's flags came across on the
            // copy: IsDeveloperOwned=true is what kept every player-made room out of the profile's
            // created-rooms list (the client hides developer-owned rooms there), and the template's
            // RankedEntityId meant dozens of rooms shared one ranking key.
            room_data.IsDeveloperOwned = false;
            room_data.IsDevRoom = false;
            room_data.IsRecRoomApproved = false;
            room_data.PublishedAt = default;
            room_data.RankedEntityId = "";
            // The subrooms carry the accessibility the owner actually sets (PrimaryAccessibility
            // reads the Home subroom), and a template's are Public - so a fresh room was "published"
            // the moment it was created and every profile listed it. A new room is a private draft
            // until its owner publishes it from room settings.
            if (room_data.SubRooms != null)
                foreach (var sr in room_data.SubRooms) sr.Accessibility = RoomDBClasses.RoomAccessibility.Private;
            room_data.IsRRO = false;
            room_data.IsDorm = false;
            room_data.Stats.CheerCount = 0;
            room_data.Stats.FavoriteCount = 0;
            room_data.Stats.VisitorCount = 0;
            room_data.Stats.VisitCount = 0;
            room_data.Tags = new List<TagClass>();
            room_data.Roles = new List<RoleClass>
            {
                new RoleClass
                {
                    AccountId = (int)playerId,
                    InvitedRole = 0,
                    Role = RoomDBClasses.Role_data.Creator
                }
            };

            ulong NewRoomId = RoomDB.AddRoom(room_data);
            Console.WriteLine($"[clone_room] New room ID returned from add_room: {NewRoomId}");
            if (NewRoomId != 0)
            {
                var made = col.FindOne(x => x.RoomId == NewRoomId);
                if (made != null && made.RankedEntityId != NewRoomId.ToString())
                {
                    made.RankedEntityId = NewRoomId.ToString();
                    col.Update(made);
                }
            }

            return NewRoomId;
        }

        public static object? GetRoomSaveById(
    ulong roomId,
    ulong subroomId,
    ulong subRoomDataSaveId, int target, int version)
        {
            var subroom = subroom_col.FindOne(x =>
                x.RoomId == roomId &&
                x.SubRoomId == subroomId);

            if (subroom == null)
                return null;

            var save =
                (subroom.CurrentSave != null &&
                 subroom.CurrentSave.SubRoomDataSaveId == subRoomDataSaveId)
                ? subroom.CurrentSave
                : subroom.AllSaves?
                    .FirstOrDefault(x => x.SubRoomDataSaveId == subRoomDataSaveId);

            if (save == null)
                return null;

            return MapSave(save, subroomId, target, version);
        }

        private class UnityAssetInfo
        {
            public string UnityAssetId { get; set; }
            public int Target { get; set; }
            public int Version { get; set; }
            public string Filename { get; set; }
            public string Hash { get; set; }
        }

        private static object MapSave(RoomDBClasses.CurrentSave save, ulong subroomid, int target, int version)
        {
            string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "BakedBulk", $"{save.UnityAssetId}_assets.json");

            UnityAssetInfo matchingAsset = null;

            if (!string.IsNullOrEmpty(save.UnityAssetId) && System.IO.File.Exists(path))
            {
                var unityAssets = JsonConvert.DeserializeObject<List<UnityAssetInfo>>(System.IO.File.ReadAllText(path));
                matchingAsset = unityAssets.FirstOrDefault(u => u.Target == target && u.Version == version);
            }

            var result = new Dictionary<string, object>
            {
                ["UnitySubAssets"] = new List<object>(),
                ["ReferencedUnityAssets"] = new List<object>(),
                ["SubRoomDataSaveId"] = save.SubRoomDataSaveId,
                ["SubRoomId"] = subroomid,
                ["DataBlob"] = save.DataBlob,
                ["ReferencedUnityAssetIds"] = save.ReferencedUnityAssetIds ?? new List<string>(),
                ["PersistenceVersion"] = save.PersistenceVersion,
                ["OMVersion"] = save.OMVersion,
                ["UgcSubVersion"] = save.UgcSubVersion,
                ["SavedByAccountId"] = save.SavedByAccountId,
                ["SavedOnPlatform"] = save.SavedOnPlatform,
                ["SavedOnDeviceClass"] = save.SavedOnDeviceClass,
                ["Description"] = save.Description,
                ["Tags"] = new List<object>(),
                ["ModerationState"] = save.ModerationState,
                ["CreatedAt"] = save.CreatedAt
            };

            // Only include UnityAssetId if it exists
            if (!string.IsNullOrEmpty(save.UnityAssetId))
            {
                result["UnityAssetId"] = save.UnityAssetId;
            }

            // Only include UnityAsset if matching asset exists
            if (matchingAsset != null)
            {
                result["UnityAsset"] = matchingAsset.Filename;
            }

            return result;
        }

        /// <summary>
        /// Sets a subroom's UnitySceneId - the scene the CLIENT loads for that subroom.
        ///
        /// This is what decides which map you actually walk into, and it is not the same
        /// thing as the .room save blob:
        ///
        ///   - a Rec Room Original's geometry ships INSIDE the client and is selected by
        ///     this id. Such rooms have no save at all and never needed one.
        ///   - a player-built room loads a template scene by this id, then applies its
        ///     save blob on top.
        ///
        /// Imported room data carries the id the ORIGINAL service used, which does not
        /// necessarily mean the same scene in this client build. Rooms whose id resolves to
        /// a template the client happens to have - a75f7547-79eb-47c6-8986-6767abcb4f92 is
        /// "Basement" here - load that blank template instead of their real map, which looks
        /// exactly like a missing save but has nothing to do with blobs.
        ///
        /// Changing this only rewrites one field; nothing is downloaded and no file moves, so
        /// a wrong value costs nothing but another attempt.
        /// </summary>
        public static RoomDBClasses.SubRoom? GetSubRoom(ulong roomId, ulong subRoomId) =>
            subroom_col.FindOne(x => x.RoomId == roomId && x.SubRoomId == subRoomId);

        public static bool SetSubRoomPermissions(ulong roomId, ulong subRoomId, List<Campus_live_rewrite.api.server.RoomPermissions.Entry> table)
        {
            var sub = GetSubRoom(roomId, subRoomId);
            if (sub == null) return false;
            sub.Permissions = table;
            subroom_col.Update(sub);
            return true;
        }

        public static bool SetSubRoomScene(ulong roomId, ulong subRoomId, string sceneId)
        {
            var sub = subroom_col.FindOne(x => x.RoomId == roomId && x.SubRoomId == subRoomId);
            if (sub == null) return false;

            string before = sub.UnitySceneId ?? "(none)";
            sub.UnitySceneId = sceneId;
            subroom_col.Update(sub);

            Console.WriteLine($"[rooms] room {roomId} subroom {subRoomId} scene {before} -> {sceneId}");
            return true;
        }

        public static RoomDBClasses.RoomRoot? SetRoomSubroomRoomFile(
    ulong roomId,
    ulong subRoomId,
    string roomFile,
    string roomDesc,
    ulong playerId,
    Platforms currentplatform,
    int persistenceVersion = 0,
    int omVersion = 0,
    int ugcSubVersion = 0,
    string? roomLevelBlob = null)
            => SetRoomSubroomRoomFile(roomId, subRoomId, roomFile, roomDesc, playerId, currentplatform, persistenceVersion, omVersion, ugcSubVersion, roomLevelBlob, true, out _);

        /// <summary>
        /// Record a save. PUBLISHED and UNPUBLISHED saves are kept apart (reference rooms
        /// POST /subrooms/{id}/data): a save lands in the subroom's history and, unless the
        /// client asked to AutoPublish, is only STAGED (StagedSubRoomDataSaveId) - players keep
        /// loading the last published CurrentSave until the owner publishes it
        /// (publish_save). Dorms, and a subroom that has never been published, publish at once.
        /// </summary>
        public static RoomDBClasses.RoomRoot? SetRoomSubroomRoomFile(
    ulong roomId,
    ulong subRoomId,
    string roomFile,
    string roomDesc,
    ulong playerId,
    Platforms currentplatform,
    int persistenceVersion,
    int omVersion,
    int ugcSubVersion,
    string? roomLevelBlob,
    bool autoPublish,
    out CurrentSave? created)
        {
            created = null;
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null)
                return null;

            var subroom = subroom_col.FindOne(x =>
                x.RoomId == roomId &&
                x.SubRoomId == subRoomId);

            if (subroom == null)
                return room;

            subroom.AllSaves ??= new List<CurrentSave>();

            // The history holds EVERY save, the published one included (older rows kept the
            // published save only on CurrentSave - fold it in once).
            if (subroom.CurrentSave != null && !subroom.AllSaves.Any(s => s.SubRoomDataSaveId == subroom.CurrentSave.SubRoomDataSaveId))
                subroom.AllSaves.Add(subroom.CurrentSave);

            long maxId = subroom.AllSaves.Count > 0 ? subroom.AllSaves.Max(s => (long)s.SubRoomDataSaveId) : 0;
            if (subroom.CurrentSave != null) maxId = Math.Max(maxId, (long)subroom.CurrentSave.SubRoomDataSaveId);

            // 💡 Create fresh save object
            var newSave = new CurrentSave
            {
                SubRoomDataSaveId = (ulong)(maxId + 1),
                SubRoomId = subroom.SubRoomId,
                DataBlob = roomFile,
                Description = roomDesc ?? "",
                SavedByAccountId = playerId,
                SavedOnPlatform = currentplatform,
                SavedOnDeviceClass = DeviceClasses.Screen,
                CreatedAt = DateTime.UtcNow,

                // These arrive on every save request and used to be dropped, so every
                // save was stored as 0/0/0. The client compares the versions it wrote
                // against what the room reports back; 0 never matched, so the room looked
                // modified the moment it loaded and leaving always prompted
                // "do you want to save?" even with no changes made.
                PersistenceVersion = persistenceVersion,
                OMVersion = omVersion,
                UgcSubVersion = ugcSubVersion
            };

            subroom.AllSaves.Add(newSave);
            created = newSave;

            bool publishNow = autoPublish || room.IsDorm || subroom.CurrentSave == null;
            if (publishNow)
            {
                subroom.CurrentSave = newSave;
                subroom.StagedSubRoomDataSaveId = null;
            }
            else
            {
                subroom.StagedSubRoomDataSaveId = (long)newSave.SubRoomDataSaveId;
            }
            subroom.CreatorAccountId ??= (long)playerId;   // client NREs on a null creator

            subroom_col.Update(subroom);
            Console.WriteLine($"[rooms] save {newSave.SubRoomDataSaveId} on room {roomId} subroom {subRoomId} by {playerId}: {(publishNow ? "PUBLISHED" : "staged (unpublished)")}");

            // Mirror onto the embedded copy the room document carries, so a room read that does
            // not re-attach subrooms still shows the staged / published state.
            var emb = room.SubRooms?.FirstOrDefault(s => s.SubRoomId == subRoomId);
            if (emb != null)
            {
                emb.AllSaves = subroom.AllSaves;
                emb.CurrentSave = subroom.CurrentSave;
                emb.StagedSubRoomDataSaveId = subroom.StagedSubRoomDataSaveId;
                col.Update(room);
            }

            // The save request also carries a ROOM-level blob (RoomData.Filename), which
            // was never read - so RoomRoot.DataBlob kept pointing at whatever stale .meta
            // it shipped with and never picked up the freshly uploaded one.
            bool roomChanged = false;
            if (!string.IsNullOrWhiteSpace(roomLevelBlob) && room.DataBlob != roomLevelBlob)
            {
                room.DataBlob = roomLevelBlob;
                roomChanged = true;
            }

            if (ugcSubVersion > 0 && room.UgcSubVersion != ugcSubVersion)
            {
                room.UgcSubVersion = ugcSubVersion;
                roomChanged = true;
            }

            if (roomChanged)
                col.Update(room);

            return room;
        }

        public static RoomDBClasses.RoomRoot? d(
    ulong roomId,
    ulong subRoomId,
    string roomFile)
        {

            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null)
                return null;

            var subroom = subroom_col.FindOne(x =>
                x.RoomId == roomId &&
                x.SubRoomId == subRoomId);

            subroom.CurrentSave.DataBlob = "80c450a999404dbcb792d6d3d2f62a56.room";

            subroom_col.Update(subroom);

            return room;
        }

        public static RoomDBClasses.RoomRoot? CreateSubroomForRoom(ulong roomId, string newSubroomName)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null)
            {
                Console.WriteLine("Room not found.");
                return null;
            }

            var roomSubrooms = subroom_col.Find(x => x.RoomId == roomId).ToList();
            ulong nextSubRoomId = roomSubrooms.Count == 0 ? 1 : roomSubrooms.Max(sr => sr.SubRoomId) + 1;
            var lastSubroom = roomSubrooms
                .OrderByDescending(sr => sr.SubRoomId)
                .FirstOrDefault();
            var newSubroom = new SubRoom
            {
                RoomId = roomId,
                Name = newSubroomName,
                SubRoomId = nextSubRoomId,
                AllSaves = new List<CurrentSave>(), 
                CurrentSave = null,
                Accessibility = RoomDBClasses.RoomAccessibility.Public,
                UnitySceneId = lastSubroom.UnitySceneId,
                MaxPlayers = lastSubroom.MaxPlayers,
            };

            subroom_col.Insert(newSubroom);

            roomSubrooms.Add(newSubroom); 
            col.Update(room); 

            return room;
        }

        /// <summary>A copy of a subroom under a fresh SubRoomId: same scene, settings and saves.</summary>
        public static SubRoom? CloneSubroom(ulong roomId, ulong subRoomId)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            var src = subroom_col.FindOne(x => x.RoomId == roomId && x.SubRoomId == subRoomId);
            if (room == null || src == null) return null;
            var all = subroom_col.Find(x => x.RoomId == roomId).ToList();
            ulong nextId = all.Count == 0 ? 1 : all.Max(sr => sr.SubRoomId) + 1;
            var copy = new SubRoom
            {
                RoomId = roomId,
                SubRoomId = nextId,
                Name = (src.Name ?? "Subroom") + " (copy)",
                Accessibility = src.Accessibility,
                CreatorAccountId = src.CreatorAccountId,
                IsSandbox = src.IsSandbox,
                MaxPlayers = src.MaxPlayers,
                ShouldAutoStageSaves = src.ShouldAutoStageSaves,
                UnitySceneId = src.UnitySceneId,
                CurrentSave = src.CurrentSave,
                AllSaves = new List<CurrentSave>(src.AllSaves ?? new List<CurrentSave>())
            };
            subroom_col.Insert(copy);
            room.SubRooms ??= new List<SubRoom>();
            room.SubRooms.Add(copy);
            col.Update(room);
            return copy;
        }

        /// <summary>Put the room's embedded subroom list in the given order (ids not named keep their relative order at the end).</summary>
        public static void ReorderSubrooms(ulong roomId, List<ulong> order)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room?.SubRooms == null) return;
            var rank = order.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);
            room.SubRooms = room.SubRooms.OrderBy(s => rank.TryGetValue(s.SubRoomId, out var r) ? r : int.MaxValue).ToList();
            col.Update(room);
        }

        /// <summary>
        /// Move a subroom into another room (POST rooms/{id}/subrooms/{sid}/move, form `newRoomId`).
        /// The subroom keeps its SubRoomId (re-stamped on its saves only if it had to change), and
        /// its scene, settings and whole save history travel with it. Refuses to move a room's
        /// only subroom (the room would have no scene to load). Returns the SOURCE room (hydrated,
        /// no longer listing the subroom) or a reason: "not_found" / "last_subroom".
        /// The caller checks the target room exists and that it may manage both rooms.
        /// </summary>
        public static (RoomDBClasses.RoomRoot? room, string? error, ulong newSubRoomId) MoveSubroomToRoom(ulong roomId, ulong subRoomId, ulong newRoomId, bool allowLast = false)
        {
            var subs = subroom_col.Find(x => x.RoomId == roomId).ToList();
            var sub = subs.FirstOrDefault(s => s.SubRoomId == subRoomId);
            if (sub == null) return (null, "not_found", 0);
            if (subs.Count <= 1 && !allowLast) return (null, "last_subroom", 0);

            // Keep its SubRoomId (the reference's move changes only the room): the ids handed out
            // in practice are unique across rooms. Only if the target somehow already has that id
            // does it take a fresh one past EVERY subroom, so it can't collide anywhere.
            var targetSubs = subroom_col.Find(x => x.RoomId == newRoomId).ToList();
            ulong newId = subRoomId;
            if (targetSubs.Any(s => s.SubRoomId == subRoomId))
                newId = subroom_col.Query().OrderByDescending(s => s.SubRoomId).FirstOrDefault()!.SubRoomId + 1;

            sub.RoomId = newRoomId;
            sub.SubRoomId = newId;
            if (sub.CurrentSave != null) sub.CurrentSave.SubRoomId = newId;
            foreach (var s in sub.AllSaves ?? new List<CurrentSave>()) s.SubRoomId = newId;
            subroom_col.Update(sub);

            // Keep the embedded copies on both room documents in step (GetRoom re-hydrates from
            // the subroom collection, but other paths read the embedded list).
            var source = col.FindOne(x => x.RoomId == roomId);
            if (source?.SubRooms != null) { source.SubRooms.RemoveAll(s => s.SubRoomId == subRoomId); col.Update(source); }
            var target = col.FindOne(x => x.RoomId == newRoomId);
            if (target != null) { target.SubRooms ??= new List<SubRoom>(); target.SubRooms.RemoveAll(s => s.SubRoomId == newId); target.SubRooms.Add(sub); col.Update(target); }

            return (GetRoom(roomId), null, newId);
        }

        /// <summary>Store a room's MaxPlayerCalculationMode (0 AllSubrooms, 1 OnlyEntrySubrooms). A stored setting only.</summary>
        public static RoomDBClasses.RoomRoot? SetMaxPlayerCalculationMode(ulong roomId, int mode)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return null;
            room.MaxPlayerCalculationMode = mode;
            col.Update(room);
            return GetRoom(roomId);
        }

        /// <summary>Make one of a subroom's saved versions the live one (restore). False when unknown.</summary>
        public static bool PublishSubroomSave(ulong roomId, ulong subRoomId, ulong saveId)
        {
            var subroom = subroom_col.FindOne(x => x.RoomId == roomId && x.SubRoomId == subRoomId);
            if (subroom == null) return false;
            var save = (subroom.AllSaves ?? new List<CurrentSave>()).FirstOrDefault(s => s.SubRoomDataSaveId == saveId)
                       ?? (subroom.CurrentSave?.SubRoomDataSaveId == saveId ? subroom.CurrentSave : null);
            if (save == null) return false;
            subroom.CurrentSave = save;
            if (subroom.StagedSubRoomDataSaveId == (long)saveId) subroom.StagedSubRoomDataSaveId = null;
            subroom_col.Update(subroom);
            var room = col.FindOne(x => x.RoomId == roomId);
            var emb = room?.SubRooms?.FirstOrDefault(s => s.SubRoomId == subRoomId);
            if (room != null && emb != null) { emb.CurrentSave = save; emb.StagedSubRoomDataSaveId = subroom.StagedSubRoomDataSaveId; col.Update(room); }
            return true;
        }

        /// <summary>The platform flags a room is shown for (and the junior flag). Nothing else on the room changes.</summary>
        public static RoomDBClasses.RoomRoot? SetPlatformSupport(ulong roomId, bool screens, bool walkVR, bool teleportVR, bool vrLow, bool quest2, bool mobile, bool juniors)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return null;
            room.SupportsScreens = screens; room.SupportsWalkVR = walkVR; room.SupportsTeleportVR = teleportVR;
            room.SupportsVRLow = vrLow; room.SupportsQuest2 = quest2; room.SupportsMobile = mobile; room.SupportsJuniors = juniors;
            col.Update(room);
            return GetRoom(roomId);
        }

        /// <summary>A subroom's sandbox flag (building allowed) - on the subroom document and on the room's embedded copy.</summary>
        public static bool SetSubroomSandbox(ulong roomId, ulong subRoomId, bool sandbox)
        {
            var sub = subroom_col.FindOne(x => x.RoomId == roomId && x.SubRoomId == subRoomId);
            if (sub == null) return false;
            sub.IsSandbox = sandbox;
            subroom_col.Update(sub);
            var room = col.FindOne(x => x.RoomId == roomId);
            var emb = room?.SubRooms?.FirstOrDefault(s => s.SubRoomId == subRoomId);
            if (room != null && emb != null) { emb.IsSandbox = sandbox; col.Update(room); }
            return true;
        }

        public static bool DeleteSubroom(ulong roomId, ulong subRoomId)
        {

            var subroom = subroom_col.FindOne(x =>
                x.RoomId == roomId &&
                x.SubRoomId == subRoomId);

            if (subroom == null)
                return false;

            subroom_col.Delete(subroom.Id);

            // the room document carries an embedded copy too
            var owner = col.FindOne(x => x.RoomId == roomId);
            if (owner?.SubRooms != null && owner.SubRooms.RemoveAll(s => s.SubRoomId == subRoomId) > 0) col.Update(owner);

            return true;
        }

        public static bool ModifySubroom(
    ulong roomId,
    ulong subRoomId,
    string name,
    RoomDBClasses.RoomAccessibility accessibility,
    int maxPlayers)
        {

            var subroom = subroom_col.FindOne(x =>
                x.RoomId == roomId &&
                x.SubRoomId == subRoomId);

            if (subroom == null)
                return false;

            subroom.Name = name;
            subroom.Accessibility = accessibility;
            subroom.MaxPlayers = maxPlayers;

            subroom_col.Update(subroom);

            // Mirror the change onto the room's embedded subroom copy AND the room-level
            // Accessibility (what Search / hot / visibility read), so setting a Rooms 2.0 room to
            // Public in-game actually makes it appear in listings/search without a stale field.
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room != null)
            {
                var emb = room.SubRooms?.FirstOrDefault(s => s.SubRoomId == subRoomId);
                if (emb != null) { emb.Name = name; emb.Accessibility = accessibility; emb.MaxPlayers = maxPlayers; }
                // Published rooms stay Public no matter what the subroom is set to; unpublished
                // rooms mirror the subroom.
                room.Accessibility = EffectiveRoomAccessibility(room);
                col.Update(room);
            }
            return true;
        }

        public static void DeleteRoom(ulong roomId)
        {  
            subroom_col.DeleteMany(x => x.RoomId == roomId);
            col.DeleteMany(x => x.RoomId == roomId);
        }

        public static RoomDBClasses.SubroomSaveResults? GetRoomSaves(ulong roomId, ulong subroomId, int skip = 0, int take = 20)
        {

            var subroom = subroom_col.FindOne(x => x.RoomId == roomId && x.SubRoomId == subroomId);
            if (subroom == null)
                return null;

            var saves = new List<RoomDBClasses.CurrentSave>();

            if (subroom.CurrentSave != null)
                saves.Add(subroom.CurrentSave);

            if (subroom.AllSaves != null && subroom.AllSaves.Count > 0)
                saves.AddRange(subroom.AllSaves);

            saves = saves
                .GroupBy(x => x.SubRoomDataSaveId)
                .Select(g => g.First())
                .OrderByDescending(x => x.CreatedAt)
                .ToList();

            var paged = saves
                .Skip(skip)
                .Take(take)
                .ToList();

            return new SubroomSaveResults
            {
                Results = paged,
                TotalCount = saves.Count
            };
        }


        /// <summary>A room's role list (by id). Empty if the room or its Roles are unset.</summary>
        public static List<RoomDBClasses.RoleClass> GetRoomRoles(ulong roomId)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            return room?.Roles ?? new List<RoomDBClasses.RoleClass>();
        }

        /// <summary>Can this player manage a room's roles? Creator, a co-owner, or a dev/admin.</summary>
        public static bool PlayerCanManageRoles(ulong roomId, ulong callerId)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return false;
            if (room.CreatorAccountId == callerId) return true;
            if (PlayerDB.CheckDevFlag(callerId)) return true;
            return IsCoOwner(room, callerId);
        }

        /// <summary>Co-owner or temporary co-owner (both edit like the creator does).</summary>
        public static bool IsCoOwner(RoomDBClasses.RoomRoot room, ulong accountId) =>
            room.Roles?.Any(r => (ulong)r.AccountId == accountId
                && (r.Role == RoomDBClasses.Role_data.Creator
                    || r.Role == RoomDBClasses.Role_data.CoOwner
                    || r.Role == RoomDBClasses.Role_data.TemporaryCoOwner)) ?? false;

        /// <summary>
        /// Does the player hold at least <paramref name="role"/> (a Role_data value: 10 Host,
        /// 20 Moderator, 30 CoOwner) in the room? The creator always passes; a banned entry never
        /// does (reference: verifyRole).
        /// </summary>
        public static bool PlayerHasRoleAtLeast(ulong roomId, ulong accountId, int role)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return false;
            if (room.CreatorAccountId == accountId) return true;
            var mine = room.Roles?.FirstOrDefault(r => (ulong)r.AccountId == accountId);
            if (mine != null && mine.Role == RoomDBClasses.Role_data.Banned) return false;
            // role <= 0 (None) is the base tier every visitor holds. The client asks for exactly
            // that ("MakerPen", role 0) when an IN-GAME role - a Rooms 2.0 permission granted from
            // the room's roles board, which lives in the room scene and never reaches this server -
            // allows the pen. Requiring a server Roles entry here made the pen drop the moment a
            // player who had just been given that permission pulled it out, until they rejoined.
            // Only a room ban (or a Banned entry) refuses it.
            if (role <= 0) return !PlayerIsBannedFrom(room, accountId);
            if (mine == null || mine.Role == RoomDBClasses.Role_data.None) return false;
            return (int)mine.Role >= role;
        }

        /// <summary>Add/update/remove a player's role on a room (by id). None removes it. Returns success.</summary>
        /// <summary>Creator, co-owner (temporary too) or contributor: may save the room's contents.</summary>
        public static bool CanEditContent(RoomDBClasses.RoomRoot? room, ulong accountId)
        {
            if (room == null) return false;
            if (room.CreatorAccountId == accountId || IsCoOwner(room, accountId)) return true;
            return room.Roles?.Any(r => (ulong)r.AccountId == accountId && r.Role == RoomDBClasses.Role_data.Contributor) ?? false;
        }

        /// <summary>
        /// Record a PENDING role invite: InvitedRole = the offered role, Role untouched. Nothing is
        /// granted until the invitee accepts (AcceptRoleInvite). A non-zero InvitedRole is what the
        /// client renders as "invited, not yet accepted".
        /// </summary>
        public static bool InviteRoomRole(ulong roomId, long accountId, RoomDBClasses.Role_data role, long invitedBy)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return false;
            room.Roles ??= new List<RoomDBClasses.RoleClass>();
            var existing = room.Roles.FirstOrDefault(r => r.AccountId == accountId);
            if (existing != null) { existing.InvitedRole = (int)role; existing.LastChangedByAccountId = invitedBy; }
            else room.Roles.Add(new RoomDBClasses.RoleClass { AccountId = accountId, Role = RoomDBClasses.Role_data.None, InvitedRole = (int)role, LastChangedByAccountId = invitedBy });
            col.Update(room);
            return true;
        }

        /// <summary>The role this player has been invited to on the room, or None.</summary>
        public static RoomDBClasses.Role_data PendingRoleInvite(ulong roomId, long accountId)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            var e = room?.Roles?.FirstOrDefault(r => r.AccountId == accountId);
            if (e == null || e.InvitedRole == 0) return RoomDBClasses.Role_data.None;
            return Enum.IsDefined(typeof(RoomDBClasses.Role_data), (byte)e.InvitedRole) ? (RoomDBClasses.Role_data)(byte)e.InvitedRole : RoomDBClasses.Role_data.None;
        }

        /// <summary>Accept (grant the invited role) or decline (drop the invite) a pending invite.</summary>
        public static bool ResolveRoleInvite(ulong roomId, long accountId, bool accept)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            var e = room?.Roles?.FirstOrDefault(r => r.AccountId == accountId);
            if (room == null || e == null || e.InvitedRole == 0) return false;
            if (accept)
            {
                e.Role = Enum.IsDefined(typeof(RoomDBClasses.Role_data), (byte)e.InvitedRole) ? (RoomDBClasses.Role_data)(byte)e.InvitedRole : RoomDBClasses.Role_data.None;
                e.InvitedRole = 0;
                if (e.Role == RoomDBClasses.Role_data.None) room.Roles!.Remove(e);
            }
            else
            {
                e.InvitedRole = 0;
                if (e.Role == RoomDBClasses.Role_data.None) room.Roles!.Remove(e);
            }
            col.Update(room);
            return true;
        }

        /// <summary>
        /// Hand a room to another player (admin panel). They become CreatorAccountId and the one
        /// Creator role, on the room and its subrooms; any ban or other role they had on it goes.
        /// The previous owner stays on as a co-owner when keepOldAsCoOwner, otherwise loses all
        /// access. Dorms aren't transferable - a dorm IS its player's.
        /// </summary>
        public static bool TransferOwnership(ulong roomId, ulong newOwner, bool keepOldAsCoOwner, long by, out string? error)
        {
            error = null;
            lock (_roomLock)
            {
                var room = col.FindOne(x => x.RoomId == roomId);
                if (room == null) { error = $"No room {roomId}."; return false; }
                if (room.IsDorm) { error = "A dorm belongs to its player - it can't be given away."; return false; }
                ulong old = room.CreatorAccountId;
                if (old == newOwner) { error = "They already own this room."; return false; }
                room.Roles ??= new List<RoleClass>();
                room.Roles.RemoveAll(r => r.Role == RoomDBClasses.Role_data.Creator || (ulong)r.AccountId == newOwner || (ulong)r.AccountId == old);
                if (keepOldAsCoOwner && old != 0)
                    room.Roles.Add(new RoleClass { AccountId = (long)old, Role = RoomDBClasses.Role_data.CoOwner, InvitedRole = 0, LastChangedByAccountId = by });
                room.Roles.Add(new RoleClass { AccountId = (long)newOwner, Role = RoomDBClasses.Role_data.Creator, InvitedRole = 0, LastChangedByAccountId = by });
                room.CreatorAccountId = newOwner;
                foreach (var b in room.RoomBans ?? new()) if (b.AccountId == (long)newOwner && b.Status == 0) { b.Status = 2; b.UnbannedByAccountId = by; }
                if (room.RoomBanDetails?.AccountId == (long)newOwner) room.RoomBanDetails = null;
                col.Update(room);
                foreach (var s in subroom_col.Find(s => s.RoomId == roomId).ToList())
                {
                    s.CreatorAccountId = (long)newOwner;
                    subroom_col.Update(s);
                }
                Console.WriteLine($"[room-owner] room {roomId} '{room.Name}': {by} moved ownership {old} -> {newOwner}" +
                                  (keepOldAsCoOwner && old != 0 ? $" ({old} kept as co-owner)" : ""));
                return true;
            }
        }

        public static bool SetRoomRole(ulong roomId, long accountId, RoomDBClasses.Role_data role, long changedBy)
        {
            var room = col.FindOne(x => x.RoomId == roomId);
            if (room == null) return false;
            room.Roles ??= new List<RoomDBClasses.RoleClass>();
            var existing = room.Roles.FirstOrDefault(r => r.AccountId == accountId);
            if (role == RoomDBClasses.Role_data.None)
            {
                if (existing != null) room.Roles.Remove(existing);
            }
            // Granted directly (no pending-invite step here), so InvitedRole stays 0: a non-zero
            // InvitedRole is what the client reads as "invited, not yet accepted".
            else if (existing != null)
            {
                existing.Role = role; existing.InvitedRole = 0; existing.LastChangedByAccountId = changedBy;
            }
            else
            {
                room.Roles.Add(new RoomDBClasses.RoleClass { AccountId = accountId, Role = role, InvitedRole = 0, LastChangedByAccountId = changedBy });
            }
            col.Update(room);
            return true;
        }

        public static bool AddRoleToRoom(string name, int accountId, RoomDBClasses.Role_data roleData, int invitedRole = 0)
        {
            
            var room = col.FindOne(x => x.Name == name);
            if (room == null)
                return false;

            room.Roles ??= new List<RoleClass>();

            room.Roles.Add(new RoleClass
            {
                AccountId = accountId,
                Role = roleData,
                InvitedRole = invitedRole
            });

            col.Update(room);
            return true;
        }

        public static bool RemoveRoleFromRoom(string name, int accountId)
        {
            

            var room = col.FindOne(x => x.Name == name);
            if (room == null)
                return false;

            if (room.Roles == null || room.Roles.Count == 0)
                return false;


            room.Roles.RemoveAll(r => r.AccountId == accountId);

            col.Update(room);
            return true;
        }

        public static RoomDBClasses.RoomRoot SetRoomCreatorId(ulong playerid, string roomname)
        {
            col.EnsureIndex(x => x.Name);

            var room = col.FindOne(x => x.Name == roomname);
            
            if (room != null)
            {
                room.CreatorAccountId = playerid;

                var ownerRole = room.Roles.FirstOrDefault(r => r.Role == Role_data.Creator);

                if (ownerRole != null)
                {
                    ownerRole.AccountId = (long)playerid;
                }
                else
                {
                    
                }

                Console.WriteLine($"Setting the creator id to {playerid}...");
                col.Update(room);
                Console.WriteLine($"Success!");
                return room;
            }

            return null;
        }

        public static void Setup()
        {
            col.EnsureIndex(x => x.CreatorAccountId);
            col.EnsureIndex(x => x.RoomId);
            col.EnsureIndex(x => x.IsDorm);
            subroom_col.EnsureIndex(x => x.SubRoomId);
            // Every "subrooms of room X" lookup filters on RoomId. Without this index each one was a
            // full scan of the subroom table, and the hot-rooms list did one per room - 2,500 scans
            // per request, 30-70 s each on the VPS.
            subroom_col.EnsureIndex(x => x.RoomId);
            col.EnsureIndex(x => x.Name);
            col.EnsureIndex("TagIndex", "$.Tags[*].Tag");
        }
    }
}
