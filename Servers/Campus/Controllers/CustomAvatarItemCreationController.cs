using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// In-game custom clothing creation (the costume maker) - reference: api/routes/avatar.ts.
    ///
    ///   POST   /api/customAvatarItems/v1        multipart: metadata (JSON) + thumbnailImage + design (PNG)
    ///   PUT    /api/customAvatarItems/v1/{id}   JSON: Name / Description / Price / Accessibility (null = keep)
    ///   DELETE /api/customAvatarItems/v1/{id}
    ///
    /// All three answer the item in the client's { Value, Success, Error, error_id } envelope. The
    /// two PNGs land in Data/Images/custom_shirt_imgs (served by /img/&lt;name&gt;), the record is
    /// indexed by CustomItemsCache under its creator and persisted to CreatedUGC.json - so it shows
    /// in the creator's wardrobe (/econ/customAvatarItems/v1/owned), profile (fromCreator) and
    /// renders through the base item + design texture like every other design-based UGC shirt.
    /// The metadata's key case is whatever the client sends ("name" lower-case, the rest Pascal).
    /// </summary>
    [ApiController]
    public class CustomAvatarItemCreationController : ControllerBase
    {
        private static readonly string ImgDir = Path.Combine(Environment.CurrentDirectory, "Data", "Images", "custom_shirt_imgs");

        private static ContentResult Envelope(object? value, bool success, string? error, int status = 200) => new()
        {
            Content = JsonConvert.SerializeObject(new { Value = value, Success = success, Error = error, error_id = (string?)null }),
            ContentType = "application/json",
            StatusCode = status
        };

        private static JToken? Get(JObject o, string name) =>
            o.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        /// <summary>OutfitType of the base item, learned from any catalog item built on the same base.</summary>
        private static int OutfitTypeForBase(int baseAvatarItemId)
        {
            foreach (var it in CustomItemsCache.AllRecs)
                if (it.BaseAvatarItemId == baseAvatarItemId && it.OutfitType >= 0)
                    return it.OutfitType;
            return 0;
        }

        [HttpPost("/api/customAvatarItems/v1")]
        [RequestSizeLimit(32 * 1024 * 1024)]
        public async Task<ContentResult> Create()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            if (!Request.HasFormContentType)
                return Envelope(null, false, "multipart form expected", 400);
            var form = await Request.ReadFormAsync();

            Console.WriteLine($"[ugc-create] {account.Id} fields=[{string.Join(",", form.Keys)}] files=[{string.Join(",", form.Files.Select(f => $"{f.Name}:{f.FileName}:{f.Length}"))}]");

            string metaRaw = form["metadata"].ToString();
            if (string.IsNullOrWhiteSpace(metaRaw)) return Envelope(null, false, "metadata is required", 400);
            JObject meta;
            try { meta = JObject.Parse(metaRaw); }
            catch { return Envelope(null, false, "metadata is not valid JSON", 400); }

            string name = ((string?)Get(meta, "Name") ?? "").Trim();
            if (name.Length == 0) return Envelope(null, false, "Name is required", 400);
            int baseId = (int?)Get(meta, "BaseAvatarItemId") ?? 0;
            if (baseId == 0) return Envelope(null, false, "BaseAvatarItemId is required", 400);
            string color = (string?)Get(meta, "BaseAvatarItemColor") ?? "#F55C1A";

            // The two uploads: named parts first, else by position (thumb, design).
            var thumb = form.Files.GetFile("thumbnailImage") ?? form.Files.GetFile("thumbnail") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            var design = form.Files.GetFile("design") ?? form.Files.GetFile("designImage") ?? (form.Files.Count > 1 ? form.Files[1] : null);
            if (design == null && thumb == null) return Envelope(null, false, "design and thumbnailImage are required", 400);
            design ??= thumb; thumb ??= design;

            // Both images must be the game's own encodes (see UploadGuard) before anything is
            // written.
            byte[] thumbBytes, designBytes;
            using (var ms = new MemoryStream()) { await thumb!.CopyToAsync(ms); thumbBytes = ms.ToArray(); }
            using (var ms = new MemoryStream()) { await design!.CopyToAsync(ms); designBytes = ms.ToArray(); }
            var refused = UploadGuard.Gate(HttpContext, account, UploadGuard.Kind.Image, thumbBytes, "customAvatarItems/v1:thumbnail")
                       ?? (ReferenceEquals(thumb, design) ? null : UploadGuard.Gate(HttpContext, account, UploadGuard.Kind.Image, designBytes, "customAvatarItems/v1:design"));
            if (refused != null) return refused;

            string id = Guid.NewGuid().ToString();
            string thumbName = $"{id}-thumb.png", designName = $"{id}-design.png";
            try
            {
                Directory.CreateDirectory(ImgDir);
                await System.IO.File.WriteAllBytesAsync(Path.Combine(ImgDir, thumbName), thumbBytes);
                await System.IO.File.WriteAllBytesAsync(Path.Combine(ImgDir, designName), designBytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ugc-create] {account.Id}: saving images failed - {ex.Message}");
                return Envelope(null, false, "could not store the images", 500);
            }

            string now = DateTime.UtcNow.ToString("o");
            var item = new JObject
            {
                ["Accessibility"]        = (int?)Get(meta, "Accessibility") ?? 0,
                ["BaseAvatarItemColor"]  = color,
                ["BaseAvatarItemId"]     = baseId,
                ["CreatedAt"]            = now,
                ["CreatorAccountId"]     = account.Id,
                ["CurrentSaves"]         = new JArray(),
                ["CustomAvatarItemId"]   = id,
                ["CustomBadgeMetadata"]  = null,
                ["Description"]          = (string?)Get(meta, "Description") ?? "",
                ["DesignFilename"]       = designName,
                ["ForceCannotPublish"]   = false,
                ["IsFeatured"]           = false,
                ["IsRecRoomApproved"]    = false,
                ["ModifiedAt"]           = now,
                ["Name"]                 = name,
                ["OutfitType"]           = (int?)Get(meta, "OutfitType") ?? OutfitTypeForBase(baseId),
                ["PreviewOrientation"]   = (int?)Get(meta, "PreviewOrientation") ?? 0,
                ["Price"]                = (int?)Get(meta, "Price") ?? 0,
                ["RankedEntityId"]       = id,
                ["RankingContext"]       = null,
                ["Tags"]                 = new JArray(),
                ["ThumbnailImageFilename"] = thumbName,
                ["PurchaseInfo"]         = null
            };
            CustomItemsCache.AddCreated(item);
            Console.WriteLine($"[ugc-create] {account.Id} created '{name}' ({id}) on base {baseId} {color}, accessibility {(int?)item["Accessibility"]}, price {(int?)item["Price"]}");
            return Envelope(item, true, null);
        }

        [HttpPut("/api/customAvatarItems/v1/{id}")]
        public async Task<ContentResult> Edit(string id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var item = CustomItemsCache.Get(id);
            if (item == null) return Envelope(null, false, "No such item", 404);
            if ((ulong?)item["CreatorAccountId"] != account.Id) return Envelope(null, false, "Not your item", 403);

            JObject body;
            try
            {
                using var sr = new StreamReader(Request.Body);
                string raw = await sr.ReadToEndAsync();
                body = string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw);
                // Some client builds PUT the edit as a form instead of JSON.
                if (body.Count == 0 && Request.HasFormContentType)
                    foreach (var kv in Request.Form) body[kv.Key] = kv.Value.ToString();
            }
            catch { return Envelope(null, false, "A JSON body is required", 400); }

            void Set(string field, Func<JToken, JToken?> conv)
            {
                var v = Get(body, field);
                if (v == null || v.Type == JTokenType.Null) return;
                var c = conv(v); if (c != null) item[field] = c;
            }
            Set("Name", v => { var s = ((string?)v)?.Trim(); return string.IsNullOrEmpty(s) ? null : s; });
            Set("Description", v => (string?)v ?? "");
            Set("Price", v => int.TryParse(v.ToString(), out var n) ? n : null);
            Set("Accessibility", v => int.TryParse(v.ToString(), out var n) ? n : null);
            item["ModifiedAt"] = DateTime.UtcNow.ToString("o");
            CustomItemsCache.CreatedChanged(item);   // Get() hands out a copy; the edit has to be stored back
            Console.WriteLine($"[ugc-create] {account.Id} edited '{(string?)item["Name"]}' ({id}): accessibility {(int?)item["Accessibility"]}, price {(int?)item["Price"]}");
            return Envelope(item, true, null);
        }

        [HttpDelete("/api/customAvatarItems/v1/{id}")]
        public ContentResult Delete(string id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var item = CustomItemsCache.Get(id);
            if (item == null) return Envelope(null, false, "No such item", 404);
            if ((ulong?)item["CreatorAccountId"] != account.Id) return Envelope(null, false, "Not your item", 403);
            CustomItemsCache.Remove(id);
            foreach (var f in new[] { (string?)item["ThumbnailImageFilename"], (string?)item["DesignFilename"] })
                if (!string.IsNullOrEmpty(f)) { try { System.IO.File.Delete(Path.Combine(ImgDir, f!)); } catch { } }
            Console.WriteLine($"[ugc-create] {account.Id} deleted '{(string?)item["Name"]}' ({id})");
            return Envelope(item, true, null);
        }
    }
}
