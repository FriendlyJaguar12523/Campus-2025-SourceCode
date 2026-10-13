using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Admin panel Photos tab: import an image from a file. The game's own upload path only
    /// takes pictures its camera encoded (UploadGuard - a phone photo or an edited PNG counts as
    /// a strike); an admin import skips that rule. The file only has to be a real PNG/JPEG.
    /// It is stored as "adm_&lt;random&gt;.png" next to the game's photos, and the startup sweep
    /// (UploadGuard.SweepStorage) skips that prefix, so a restart never quarantines it. The
    /// record is an ordinary saved image, so it shows on the owner's profile / feed and can be
    /// referenced anywhere an image name or id is used.
    /// </summary>
    public class AdminImagesController : Controller
    {
        public const string ImportPrefix = "adm_";
        private const int MaxBytes = 16 * 1024 * 1024;

        private ContentResult J(object o, int status = 200) => new ContentResult
        {
            Content = JsonConvert.SerializeObject(o),
            ContentType = "application/json",
            StatusCode = status
        };

        [HttpPost("/admin/api/images/import")]
        [RequestSizeLimit(MaxBytes + 1024 * 1024)]
        public async Task<ContentResult> Import()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (!Request.HasFormContentType) return J(new { success = false, message = "Send the image as a form upload." }, 400);

            var form = await Request.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return J(new { success = false, message = "Pick an image file." }, 400);
            if (file.Length > MaxBytes) return J(new { success = false, message = $"That file is over {MaxBytes / (1024 * 1024)} MB." }, 400);

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            // Not held to ANY encoder's shape (the website check refuses a JPEG whose first
            // segment is EXIF, which most phone/editor JPEGs are) - only that it really is a PNG or
            // JPEG by its signature, whatever the file is named. The game decodes either.
            bool isPng = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                         && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
            bool isJpeg = bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
            if (!isPng && !isJpeg)
                return J(new { success = false, message = $"That is not a PNG or JPEG image (starts {BitConverter.ToString(bytes, 0, Math.Min(8, bytes.Length))})." }, 400);

            // Owner: an id or a username; default = the admin doing the import.
            ulong owner = account.Id;
            string ownerRaw = form["owner"].ToString().Trim().TrimStart('@');
            if (ownerRaw.Length > 0)
            {
                if (ulong.TryParse(ownerRaw, out var oid)) owner = oid;
                else
                {
                    owner = PlayerDB.GetAccountIdByUsernameCI(ownerRaw);
                    if (owner == 0) return J(new { success = false, message = $"No player '{ownerRaw}'." }, 404);
                }
                if (PlayerDB.GetAccountById(owner) == null) return J(new { success = false, message = $"No player {owner}." }, 404);
            }
            int roomId = int.TryParse(form["roomId"], out var rid) ? Math.Max(0, rid) : 0;
            bool isPublic = !string.Equals(form["public"].ToString(), "false", StringComparison.OrdinalIgnoreCase);
            string? description = form["description"].ToString();
            if (string.IsNullOrWhiteSpace(description)) description = null;
            var type = int.TryParse(form["type"], out var t) && Enum.IsDefined(typeof(ImageMetadataDB.SavedImageTypeEnum), t)
                ? (ImageMetadataDB.SavedImageTypeEnum)t : ImageMetadataDB.SavedImageTypeEnum.ShareCamera;

            if (!Directory.Exists(Images.ImgSavePath)) Directory.CreateDirectory(Images.ImgSavePath);
            string name = $"{ImportPrefix}{ClientSecurity.RandomString(26)}.png";
            await System.IO.File.WriteAllBytesAsync(Path.Combine(Images.ImgSavePath, name), bytes);

            var rec = ImageMetadataDB.InsertImage(new ImageMetadataDB.FullSavedImage
            {
                Id = 0,
                RoomId = roomId,
                PlayerId = owner,
                ImageName = name,
                Description = description,
                Accessibility = isPublic ? ImageMetadataDB.ImageAccessibility.Public : ImageMetadataDB.ImageAccessibility.Private,
                Type = type,
                TaggedPlayerIds = new List<ulong>(),
                CreatedAt = DateTime.UtcNow
            });
            Console.WriteLine($"[admin-images] {account.Id} imported '{file.FileName}' ({bytes.Length} bytes) as image {rec.Id} '{name}' for player {owner} ({(isPublic ? "public" : "private")}, room {roomId})");
            return J(new { success = true, id = rec.Id, imageName = name, url = $"/img/{name}", owner });
        }
    }
}
