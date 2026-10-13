using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Settings > Export, the parts that need the server's data: "Export Data" (the player's own account as one JSON file) and
    /// "Export Rooms" (the rooms they CREATED, in the same folder layout the admin Archive import reads). Like Export Photos,
    /// the server only answers questions - the browser builds the zip, nothing is assembled or stored here. Auth is the WEBSITE
    /// session (campus_site cookie), and every room route re-checks that the signed-in player is the room's creator.
    ///
    /// What is deliberately NOT exported: the password hash, login tokens, device ids and IP addresses, 2FA secrets, messages
    /// with other players, and moderation records - they are either security material or other people's data.
    /// </summary>
    public class SiteExportController : ControllerBase
    {
        private const string SiteCookie = "campus_site";

        private player_info? CurrentUser()
        {
            string token = Request.Cookies[SiteCookie] ?? "";
            if (string.IsNullOrEmpty(token)) return null;
            string data = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_")) return null;
            try { return JsonConvert.DeserializeObject<player_info>(data); } catch { return null; }
        }

        private static string BlobDir => Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs");
        private static string ImageDir => Path.Combine(Environment.CurrentDirectory, "Data", "Images");

        // ---------------------------------------------------------------------------------------------
        // Export Data
        // ---------------------------------------------------------------------------------------------

        // GET /api/site/me/export - the player's own account as JSON. The browser saves it as a file.
        [HttpGet("/api/site/me/export")]
        public IActionResult ExportData()
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var full = PlayerDB.GetFullAccount(me.Id);
            if (full?.player == null) return NotFound(new { ok = false, error = "account not found" });
            var p = full.player;
            var x = full.player_Extra ?? new player_extra();

            string Name(ulong id) => PlayerDB.GetAccountById(id)?.Username ?? "";

            var sitePrefs = SitePrefsDB.Get(me.Id);

            var friends = new List<object>();
            try
            {
                foreach (var r in FriendsDB.GetRelationships(me.Id))
                {
                    ulong other = r.PlayerID == me.Id ? r.OtherPlayerID : r.PlayerID;
                    friends.Add(new
                    {
                        accountId = other,
                        username = Name(other),
                        displayName = PlayerDB.GetAccountById(other)?.DisplayName ?? "",
                        relationship = r.RelationshipType.ToString(),
                        favorited = r.Favorited.ToString(),
                        muted = r.Muted.ToString(),
                        ignored = r.Ignored.ToString(),
                        voiceVolume = r.VoiceVolume,
                        nickname = SitePrefsDB.NicknameFor(me.Id, other),
                    });
                }
            }
            catch (Exception ex) { Console.WriteLine($"[export] {me.Id} friends failed: {ex.Message}"); }

            // Settings can hold anything the game stores; leave out anything that looks like a credential.
            var settings = new Dictionary<string, string?>();
            try
            {
                foreach (var s in SettingsDB.LoadSettings(me.Id))
                {
                    if (string.IsNullOrEmpty(s.Key)) continue;
                    string k = s.Key.ToLowerInvariant();
                    if (k.Contains("token") || k.Contains("password") || k.Contains("secret")) continue;
                    settings[s.Key] = s.Value;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[export] {me.Id} settings failed: {ex.Message}"); }

            var rooms = RoomDB.col.Find(r => r.CreatorAccountId == me.Id).Select(r => new
            {
                roomId = r.RoomId, name = r.Name, description = r.Description, isDorm = r.IsDorm,
                createdAt = r.CreatedAt, accessibility = r.Accessibility.ToString(),
            }).ToList();

            var photos = new List<object>();
            try
            {
                foreach (var i in ImageMetadataDB.GetPlayerFeed(me.Id, me.Id, 0, 5000) ?? new())
                    photos.Add(new
                    {
                        id = i.SavedImageId, file = i.ImageName, description = i.Description, createdAt = i.CreatedAt,
                        takenByMe = i.PlayerId == me.Id,
                        taggedMe = i.TaggedPlayerIds != null && i.TaggedPlayerIds.Contains(me.Id),
                    });
            }
            catch (Exception ex) { Console.WriteLine($"[export] {me.Id} photos failed: {ex.Message}"); }

            var inventions = new List<object>();
            try
            {
                foreach (var v in InventionDB.GetByCreator(me.Id))
                    inventions.Add(new { id = v.InventionId, name = v.Name, description = v.Description, published = v.IsPublished, createdAt = v.CreatedAt, modifiedAt = v.ModifiedAt, tags = v.Tags });
            }
            catch (Exception ex) { Console.WriteLine($"[export] {me.Id} inventions failed: {ex.Message}"); }

            object? wishlist = null;
            try { wishlist = WishlistDB.For(me.Id).Select(w => new { storefront = w.StorefrontType, itemId = w.PurchasableItemId, customItem = w.CustomAvatarItemId, addedAt = w.CreatedAt }).ToList(); } catch { }

            var streak = LoginStreaks.Get(me.Id);

            var doc = new Dictionary<string, object?>
            {
                ["format"] = "campus-account-export",
                ["version"] = 1,
                ["exportedAt"] = DateTime.UtcNow,
                ["notIncluded"] = "Password, login tokens, device and IP records, two-factor secrets, messages with other players and moderation records are never exported.",
                ["account"] = new
                {
                    accountId = p.Id, username = p.Username, displayName = p.DisplayName, displayEmoji = p.DisplayEmoji,
                    email = p.Email, birthday = p.Birthday, bio = x.Bio,
                    level = p.Level, xp = p.XP, isJunior = p.IsJunior, avoidJuniors = p.AvoidJuniors,
                    pronounFlags = p.pronounFlags, identityFlags = p.identityFlags,
                    createdAt = p.created_at, lastLogin = p.last_login_time,
                    profileImage = p.profileImage, bannerImage = p.bannerImage,
                    platforms = (p.PlatformIds ?? new()).Select(i => i.Platform.ToString()).Distinct().ToList(),
                    photoTagging = x.PlayerPhotoTaggingSetting.ToString(),
                },
                ["balances"] = x.Balances.ToDictionary(b => Enum.IsDefined(typeof(CurrencyType), b.Key) ? ((CurrencyType)b.Key).ToString() : b.Key.ToString(), b => b.Value),
                ["avatar"] = new
                {
                    current = x.avatar,
                    savedOutfits = x.SavedOutfits,
                    ownedItems = x.AvatarItems,
                    customItems = x.CustomItems,
                    equipmentSkins = x.EquipmentSkins,
                    consumables = x.Consumables,
                },
                ["social"] = new
                {
                    friends,
                    followingAccountIds = x.SubbedTo,
                    followerCount = x.sub_count,
                    favoritedRoomIds = x.FavoritedRooms,
                    cheeredRoomIds = x.CheeredRooms,
                    recentRoomIds = p.recent_rooms,
                },
                ["progress"] = new
                {
                    dailyRewardClaims = x.DailyRewardClaims,
                    objectives = x.Objectives,
                    objectiveGroups = x.ObjectiveGroups,
                    challengeStatuses = x.ChallengeStatuses,
                    loginStreak = streak == null ? null : new { current = streak.Current, best = streak.Best, totalDays = streak.TotalDays, lastDay = streak.LastDay },
                },
                ["settings"] = settings,
                ["website"] = new { pinnedFriendIds = sitePrefs.Pinned, quickChat = sitePrefs.QuickChat },
                ["wishlist"] = wishlist,
                ["rooms"] = rooms,
                ["photos"] = photos,
                ["inventions"] = inventions,
            };

            Response.Headers["Cache-Control"] = "private, no-store";
            Console.WriteLine($"[export] {me.Id} exported their account data");
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(doc, Formatting.Indented, new Newtonsoft.Json.Converters.StringEnumConverter()),
                ContentType = "application/json"
            };
        }

        // ---------------------------------------------------------------------------------------------
        // Export Rooms (creator only)
        // ---------------------------------------------------------------------------------------------

        private static List<RoomDBClasses.RoomRoot> OwnedRooms(ulong me)
            => RoomDB.GetRoomsByCreator(me).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // GET /api/site/me/rooms - the rooms this player created (only those), to pick from.
        [HttpGet("/api/site/me/rooms")]
        public IActionResult MyRooms()
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var rooms = OwnedRooms(me.Id).Select(r => new
            {
                id = r.RoomId.ToString(),
                name = r.Name,
                isDorm = r.IsDorm,
                subrooms = (r.SubRooms ?? new()).Count,
            }).ToList();
            return Ok(new { ok = true, rooms });
        }

        // Every server file a room's export may include, by the name it has on disk.
        private sealed class ExportFile { public string Path = ""; public string Name = ""; public string Kind = ""; }

        private static string SafeName(string? s, string fallback)
        {
            var chars = (s ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.').ToArray();
            string t = new string(chars).Trim().Trim('.');
            if (t.Length > 40) t = t[..40].Trim();
            return t.Length == 0 ? fallback : t.Replace(' ', '_');
        }

        private static string? HashOf(string path)
        {
            try { using var fs = System.IO.File.OpenRead(path); return Convert.ToBase64String(SHA256.HashData(fs)); }
            catch { return null; }
        }

        // GET /api/site/me/rooms/{roomId}/manifest - RoomDetails.json plus the list of files to fetch. Layout matches the
        // admin Archive importer: <Room>/RoomDetails.json, RoomImage.<ext>, super_room_data.binpb,
        // SubRoom_<id>_<name>/persisted_room_data.binpb, plus every older save under that subroom's older_saves/.
        [HttpGet("/api/site/me/rooms/{roomId}/manifest")]
        public IActionResult RoomManifest(ulong roomId)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var room = OwnedRooms(me.Id).FirstOrDefault(r => r.RoomId == roomId);
            if (room == null) return NotFound(new { ok = false, error = "that room is not yours" });

            var files = new List<object>();
            var details = JObject.FromObject(room, JsonSerializer.CreateDefault(new JsonSerializerSettings { Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() } }));

            void Add(string zipPath, string kind, string? name, string? hashInto, JObject? hashTarget)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                string disk = Path.Combine(kind == "image" ? ImageDir : BlobDir, Path.GetFileName(name));
                if (!System.IO.File.Exists(disk)) return;
                files.Add(new { path = zipPath, name = Path.GetFileName(name) });
                if (hashTarget != null && hashInto != null) { var h = HashOf(disk); if (h != null) hashTarget[hashInto] = h; }
            }

            // room-level data blob and picture
            Add("super_room_data.binpb", "blob", room.DataBlob, "DataBlobHash", details);
            if (!string.IsNullOrWhiteSpace(room.ImageName) && !room.ImageName.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
                Add("RoomImage" + (Path.GetExtension(room.ImageName) is { Length: > 0 } ext ? ext : ".jpg"), "image", room.ImageName, null, null);

            var subs = details["SubRooms"] as JArray;
            foreach (var sub in room.SubRooms ?? new())
            {
                string folder = $"SubRoom_{sub.SubRoomId}_{SafeName(sub.Name, "subroom")}";
                var subTok = subs?.FirstOrDefault(t => (ulong?)t["SubRoomId"] == sub.SubRoomId) as JObject;
                if (sub.CurrentSave != null)
                    Add($"{folder}/persisted_room_data.binpb", "blob", sub.CurrentSave.DataBlob, "DataBlobHash", subTok?["CurrentSave"] as JObject);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (sub.CurrentSave?.DataBlob != null) seen.Add(sub.CurrentSave.DataBlob);
                int n = 0;
                foreach (var old in (sub.AllSaves ?? new()).OrderByDescending(s => s.CreatedAt))
                {
                    if (string.IsNullOrWhiteSpace(old.DataBlob) || !seen.Add(old.DataBlob)) continue;
                    n++;
                    Add($"{folder}/older_saves/{old.CreatedAt:yyyy-MM-dd_HH-mm}_{n}.binpb", "blob", old.DataBlob, null, null);
                }
            }

            Response.Headers["Cache-Control"] = "private, no-store";
            return Ok(new
            {
                ok = true,
                folder = $"{SafeName(room.Name, "room")}_{room.RoomId}",
                details = details.ToString(Formatting.Indented),
                files,
            });
        }

        // GET /api/site/me/rooms/{roomId}/file?name=<server file name> - one file of an owned room, by the name the manifest
        // listed. Only names the room itself references are served, so this can't read anything else from the server.
        [HttpGet("/api/site/me/rooms/{roomId}/file")]
        public IActionResult RoomFile(ulong roomId, [FromQuery] string? name)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var room = OwnedRooms(me.Id).FirstOrDefault(r => r.RoomId == roomId);
            if (room == null || string.IsNullOrWhiteSpace(name)) return NotFound();
            name = Path.GetFileName(name);

            bool isImage = string.Equals(name, room.ImageName, StringComparison.OrdinalIgnoreCase);
            bool isBlob = string.Equals(name, room.DataBlob, StringComparison.OrdinalIgnoreCase)
                || (room.SubRooms ?? new()).Any(s =>
                    string.Equals(s.CurrentSave?.DataBlob, name, StringComparison.OrdinalIgnoreCase)
                    || (s.AllSaves ?? new()).Any(a => string.Equals(a.DataBlob, name, StringComparison.OrdinalIgnoreCase)));
            if (!isImage && !isBlob) return NotFound();

            string disk = Path.Combine(isImage ? ImageDir : BlobDir, name);
            if (!System.IO.File.Exists(disk)) return NotFound();
            Response.Headers["Cache-Control"] = "private, no-store";
            return PhysicalFile(disk, "application/octet-stream");
        }
    }
}
