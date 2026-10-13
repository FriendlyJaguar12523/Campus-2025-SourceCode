using LiteDB;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// Website support tickets. A signed-in player opens a ticket (the Support page's "talk to a
    /// real person" form); staff (dev / mod / owner / support flag) see EVERY ticket and can reply,
    /// add internal notes, claim, close and reopen. Stored in its own LiteDB file; checkpointed by
    /// DbMaintenance. Screenshots live in Data/SupportUploads (see the Attachment collection).
    ///
    /// Older tickets (before Category / read markers / claiming existed) load with those fields at
    /// their defaults and work unchanged - the category is read back out of the opening message.
    /// </summary>
    public static class SupportDB
    {
        private static readonly LiteDatabase DB =
            new LiteDatabase(Environment.CurrentDirectory + "/Data/DBs/Support.db");

        public static void Checkpoint() => DB.Checkpoint();

        private static ILiteCollection<Ticket> Tickets => DB.GetCollection<Ticket>("tickets");
        private static ILiteCollection<Attachment> Attachments => DB.GetCollection<Attachment>("attachments");
        private static readonly object Sync = new();

        public static readonly string UploadDir = Path.Combine(Environment.CurrentDirectory, "Data", "SupportUploads");

        // Limits (anti-spam) and housekeeping.
        public const int MaxOpenTicketsPerPlayer = 3;
        public static readonly TimeSpan NewTicketCooldown = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan AutoCloseAfter = TimeSpan.FromDays(7);   // waiting on the player this long
        public const int MaxAttachmentsPerMessage = 4;
        public const int MaxAttachmentBytes = 6 * 1024 * 1024;

        // Screenshot anti-spam. The upload endpoint writes files to disk, so every one of these
        // is a hard server-side check - the page's own limits are only a convenience.
        public const int MaxPendingUploads = 8;                        // uploaded but not yet sent with a message
        public const int MaxUploadsPerDay = 30;                        // per player, rolling 24 h (persisted, survives restarts)
        public const long MaxUploadBytesPerDay = 40L * 1024 * 1024;    // per player, rolling 24 h, after re-encoding
        public const int MaxPlayerAttachmentsPerTicket = 20;           // the player's screenshots on one ticket (staff exempt)
        public const long UploadDirCapBytes = 2L * 1024 * 1024 * 1024; // whole folder; uploads stop (and it logs) past this
        public static readonly TimeSpan UnusedUploadLifetime = TimeSpan.FromHours(2);
        private const int MaxImageSide = 2560;                         // stored images are downscaled to fit this
        private const long MaxDecodePixels = 40_000_000;               // refuse "decompression bombs" before decoding

        static SupportDB()
        {
            Tickets.EnsureIndex(x => x.AccountId);
            Tickets.EnsureIndex(x => x.Status);
            Attachments.EnsureIndex(x => x.UploaderId);
            Directory.CreateDirectory(UploadDir);
        }

        public sealed class TicketMessage
        {
            public ulong FromAccountId { get; set; }
            public string FromName { get; set; } = "";
            public bool Staff { get; set; }
            public string Body { get; set; } = "";
            public DateTime At { get; set; } = DateTime.UtcNow;
            public bool Internal { get; set; }          // staff-only note, never shown to the player
            public bool System { get; set; }            // "Closed by @x", "Claimed by @y" ...
            public List<string>? Attachments { get; set; }
        }

        public sealed class Ticket
        {
            public int Id { get; set; }                 // LiteDB auto-increment PK
            public ulong AccountId { get; set; }
            public string Username { get; set; } = "";
            public string Subject { get; set; } = "";
            public string Status { get; set; } = "open"; // "open" | "closed"
            public string? Category { get; set; }        // account / join / econ / report_player / report_room / bug / appeal / other
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
            public List<TicketMessage> Messages { get; set; } = new();
            public DateTime? PlayerReadAt { get; set; }  // last time the player opened it
            public DateTime? StaffReadAt { get; set; }   // last time any staff member opened it
            public ulong AssignedTo { get; set; }        // claiming staff member (0 = unclaimed)
            public string? AssignedName { get; set; }
            public DateTime? ClosedAt { get; set; }
        }

        public sealed class Attachment
        {
            [BsonId] public string Name { get; set; } = "";   // random file name, e.g. "3f9c...png"
            public ulong UploaderId { get; set; }
            public int TicketId { get; set; }                  // 0 until a message claims it
            public string ContentType { get; set; } = "image/png";
            public long Bytes { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        // ---- derived state ------------------------------------------------------------------

        private static IEnumerable<TicketMessage> Public(Ticket t) => (t.Messages ?? new()).Where(m => !m.Internal);

        /// <summary>Whose turn it is on an open ticket: "staff" (the player spoke last) or "player".</summary>
        public static string AwaitingOf(Ticket t)
        {
            if (t.Status != "open") return "none";
            var last = Public(t).LastOrDefault(m => !m.System);
            return last == null || !last.Staff ? "staff" : "player";
        }

        /// <summary>Staff said something the player hasn't opened the ticket to see yet.</summary>
        public static bool UnreadForPlayer(Ticket t) =>
            Public(t).Any(m => m.Staff && m.FromAccountId != t.AccountId && m.At > (t.PlayerReadAt ?? DateTime.MinValue));

        /// <summary>The player said something no staff member has opened the ticket to see yet.</summary>
        public static bool UnreadForStaff(Ticket t) =>
            (t.Messages ?? new()).Any(m => !m.Staff && !m.System && m.At > (t.StaffReadAt ?? DateTime.MinValue));

        // ---- tickets ------------------------------------------------------------------------

        public static Ticket Create(ulong accountId, string username, string subject, string body, string? category, List<string>? attachments)
        {
            var t = new Ticket
            {
                AccountId = accountId,
                Username = username,
                Subject = string.IsNullOrWhiteSpace(subject) ? "Support request" : subject,
                Status = "open",
                Category = category,
                PlayerReadAt = DateTime.UtcNow,
            };
            t.Messages.Add(new TicketMessage { FromAccountId = accountId, FromName = username, Staff = false, Body = body, Attachments = attachments });
            lock (Sync)
            {
                Tickets.Insert(t);   // Id auto-assigned
                ClaimAttachments(attachments, t.Id);
            }
            return t;
        }

        public static Ticket? Get(int id) => Tickets.FindById(id);

        public static List<Ticket> ForAccount(ulong accountId)
        {
            AutoCloseStale();
            return Tickets.Find(x => x.AccountId == accountId).OrderByDescending(x => x.UpdatedAt).ToList();
        }

        public static List<Ticket> All(string? status)
        {
            AutoCloseStale();
            var rows = status == "open"   ? Tickets.Find(x => x.Status == "open")
                     : status == "closed" ? Tickets.Find(x => x.Status == "closed")
                     : Tickets.FindAll();
            return rows.OrderByDescending(x => x.UpdatedAt).ToList();
        }

        /// <summary>Add a reply (or, for staff, an internal note). A public reply reopens a closed ticket.</summary>
        public static Ticket? AddMessage(int id, ulong fromId, string fromName, bool staff, string body, bool isInternal, List<string>? attachments)
        {
            lock (Sync)
            {
                var t = Tickets.FindById(id);
                if (t == null) return null;
                var now = DateTime.UtcNow;
                t.Messages.Add(new TicketMessage { FromAccountId = fromId, FromName = fromName, Staff = staff, Body = body, At = now, Internal = isInternal && staff, Attachments = attachments });
                if (!(isInternal && staff))
                {
                    t.UpdatedAt = now;
                    // A reply on a closed ticket reopens it, so nobody's follow-up is lost in a closed thread.
                    if (t.Status == "closed")
                    {
                        t.Status = "open"; t.ClosedAt = null;
                        t.Messages.Add(new TicketMessage { FromAccountId = fromId, FromName = fromName, Staff = staff, System = true, Body = $"Reopened by the reply from @{fromName}", At = now.AddMilliseconds(1) });
                    }
                }
                // Writing in a ticket means you've read it.
                if (staff && fromId != t.AccountId) t.StaffReadAt = now; else t.PlayerReadAt = now;
                Tickets.Update(t);
                ClaimAttachments(attachments, id);
                return t;
            }
        }

        public static Ticket? SetStatus(int id, string status, ulong byId, string byName, bool byStaff)
        {
            lock (Sync)
            {
                var t = Tickets.FindById(id);
                if (t == null || t.Status == status) return t;
                var now = DateTime.UtcNow;
                t.Status = status;
                t.UpdatedAt = now;
                t.ClosedAt = status == "closed" ? now : null;
                string who = byId == t.AccountId ? "the player" : "@" + byName;
                t.Messages.Add(new TicketMessage { FromAccountId = byId, FromName = byName, Staff = byStaff, System = true, At = now,
                    Body = status == "closed" ? $"Ticket closed by {who}" : $"Ticket reopened by {who}" });
                if (byStaff && byId != t.AccountId) t.StaffReadAt = now; else t.PlayerReadAt = now;
                Tickets.Update(t);
                return t;
            }
        }

        /// <summary>Claim (staffId != 0) or release (0) a ticket. Recorded in the thread for staff only.</summary>
        public static Ticket? Assign(int id, ulong staffId, string staffName, ulong byId, string byName)
        {
            lock (Sync)
            {
                var t = Tickets.FindById(id);
                if (t == null) return null;
                t.AssignedTo = staffId;
                t.AssignedName = staffId == 0 ? null : staffName;
                t.Messages.Add(new TicketMessage { FromAccountId = byId, FromName = byName, Staff = true, System = true, Internal = true, At = DateTime.UtcNow,
                    Body = staffId == 0 ? $"Released by @{byName}" : (staffId == byId ? $"Claimed by @{byName}" : $"Assigned to @{staffName} by @{byName}") });
                t.StaffReadAt = DateTime.UtcNow;
                Tickets.Update(t);
                return t;
            }
        }

        public static void MarkRead(int id, bool asStaff)
        {
            lock (Sync)
            {
                var t = Tickets.FindById(id);
                if (t == null) return;
                if (asStaff) t.StaffReadAt = DateTime.UtcNow; else t.PlayerReadAt = DateTime.UtcNow;
                Tickets.Update(t);
            }
        }

        public static int OpenCount() => Tickets.Count(x => x.Status == "open");
        public static int AwaitingStaffCount() => Tickets.Find(x => x.Status == "open").Count(t => AwaitingOf(t) == "staff");
        public static int OpenCountFor(ulong accountId) => Tickets.Count(x => x.AccountId == accountId && x.Status == "open");
        public static DateTime? LastCreatedBy(ulong accountId) =>
            Tickets.Find(x => x.AccountId == accountId).Select(x => (DateTime?)x.CreatedAt).DefaultIfEmpty(null).Max();

        // Tickets waiting on the PLAYER for AutoCloseAfter are closed with a note (they can reopen by
        // replying). Run lazily from the list reads, at most every 10 minutes.
        private static DateTime _lastSweep = DateTime.MinValue;
        private static void AutoCloseStale()
        {
            if ((DateTime.UtcNow - _lastSweep).TotalMinutes < 10) return;
            _lastSweep = DateTime.UtcNow;
            try
            {
                var cutoff = DateTime.UtcNow - AutoCloseAfter;
                foreach (var t in Tickets.Find(x => x.Status == "open").ToList())
                {
                    if (AwaitingOf(t) != "player") continue;
                    var last = Public(t).LastOrDefault(m => !m.System);
                    if (last == null || last.At > cutoff) continue;
                    lock (Sync)
                    {
                        t.Status = "closed"; t.ClosedAt = DateTime.UtcNow; t.UpdatedAt = DateTime.UtcNow;
                        t.Messages.Add(new TicketMessage { System = true, Staff = true, FromName = "Campus", At = DateTime.UtcNow,
                            Body = $"Closed automatically after {AutoCloseAfter.TotalDays:0} days without a reply. Reply to reopen it." });
                        Tickets.Update(t);
                    }
                    Console.WriteLine($"[support] ticket #{t.Id} auto-closed (no reply from @{t.Username} in {AutoCloseAfter.TotalDays:0} days)");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[support] auto-close sweep failed: {ex.Message}"); }
        }

        // ---- screenshots --------------------------------------------------------------------

        /// <summary>
        /// Why this player may not upload another screenshot right now, or null when they may.
        /// Checked BEFORE the image is read or decoded, so a refused caller costs nothing.
        /// </summary>
        public static string? UploadRefusal(ulong uploaderId)
        {
            SweepUnusedUploads();
            var since = DateTime.UtcNow.AddDays(-1);
            List<Attachment> mine;
            lock (Sync) mine = Attachments.Find(x => x.UploaderId == uploaderId).ToList();
            if (mine.Count(a => a.TicketId == 0) >= MaxPendingUploads)
                return $"You have {MaxPendingUploads} screenshots waiting to be sent. Send your message (or remove some) before adding more.";
            var today = mine.Where(a => a.CreatedAt > since).ToList();
            if (today.Count >= MaxUploadsPerDay || today.Sum(a => a.Bytes) >= MaxUploadBytesPerDay)
                return "You've attached a lot of screenshots today. Please try again tomorrow, or describe the problem in words.";
            if (UploadDirBytes() >= UploadDirCapBytes)
            {
                Console.WriteLine($"[support] screenshot upload refused: {UploadDir} is over its {UploadDirCapBytes / 1024 / 1024} MB cap");
                return "Screenshots are unavailable right now. Please describe the problem in words.";
            }
            return null;
        }

        /// <summary>
        /// Store an uploaded screenshot. The bytes are never written as uploaded: the image is
        /// decoded and re-encoded (PNG stays PNG, everything else becomes JPEG), which means a
        /// renamed file, a polyglot or a malformed image never lands on disk, EXIF (phone GPS
        /// location) is stripped, and huge images are downscaled. Returns the stored name, or
        /// null when it isn't a readable PNG/JPEG/GIF/WebP.
        /// </summary>
        public static string? SaveAttachment(ulong uploaderId, byte[] data)
        {
            string? ext = SniffImage(data, out _);
            if (ext == null || data.Length == 0 || data.Length > MaxAttachmentBytes) return null;
            byte[] stored;
            string contentType;
            try
            {
                var info = Image.Identify(data);                   // header only - no pixel decode
                if (info == null || info.Width < 1 || info.Height < 1 || (long)info.Width * info.Height > MaxDecodePixels) return null;
                using var img = Image.Load<Rgba32>(data);
                if (img.Width > MaxImageSide || img.Height > MaxImageSide)
                    img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(MaxImageSide, MaxImageSide) }));
                img.Metadata.ExifProfile = null;
                img.Metadata.XmpProfile = null;
                img.Metadata.IccProfile = null;
                using var outMs = new MemoryStream();
                // SkipMetadata: no text/EXIF/XMP chunks survive - only pixels are written back.
                if (ext == ".png") { img.SaveAsPng(outMs, new PngEncoder { SkipMetadata = true }); contentType = "image/png"; }
                else { img.SaveAsJpeg(outMs, new JpegEncoder { Quality = 88, SkipMetadata = true }); contentType = "image/jpeg"; ext = ".jpg"; }
                stored = outMs.ToArray();
            }
            catch { return null; }                                  // not actually an image

            string name = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() + ext;
            File.WriteAllBytes(Path.Combine(UploadDir, name), stored);
            lock (Sync) Attachments.Insert(new Attachment { Name = name, UploaderId = uploaderId, ContentType = contentType, Bytes = stored.Length });
            UploadDirBytes();
            Interlocked.Add(ref _dirBytes, stored.Length);
            return name;
        }

        /// <summary>The uploader removed a screenshot before sending it: delete it now.</summary>
        public static bool DeleteUnsent(ulong uploaderId, string name)
        {
            Attachment? a;
            lock (Sync)
            {
                a = Attachments.FindById(name);
                if (a == null || a.UploaderId != uploaderId || a.TicketId != 0) return false;
                Attachments.Delete(name);
            }
            try { File.Delete(Path.Combine(UploadDir, a.Name)); } catch { }
            UploadDirBytes();
            Interlocked.Add(ref _dirBytes, -a.Bytes);
            return true;
        }

        /// <summary>How many screenshots this player has already put on the ticket.</summary>
        public static int PlayerAttachmentCount(Ticket t, ulong playerId) =>
            (t.Messages ?? new()).Where(m => m.FromAccountId == playerId && !m.Staff).Sum(m => m.Attachments?.Count ?? 0);

        // Total size of the upload folder: counted from disk once, then kept up to date.
        private static long _dirBytes = -1;
        private static long UploadDirBytes()
        {
            if (Interlocked.Read(ref _dirBytes) < 0)
            {
                long total = 0;
                try { foreach (var f in new DirectoryInfo(UploadDir).EnumerateFiles()) total += f.Length; } catch { }
                Interlocked.CompareExchange(ref _dirBytes, total, -1);
            }
            return Interlocked.Read(ref _dirBytes);
        }

        // Screenshots that were uploaded but never sent with a message (the player closed the
        // form, or a script uploaded and walked away) are deleted after UnusedUploadLifetime.
        private static DateTime _lastUploadSweep = DateTime.MinValue;
        private static void SweepUnusedUploads()
        {
            if ((DateTime.UtcNow - _lastUploadSweep).TotalMinutes < 10) return;
            _lastUploadSweep = DateTime.UtcNow;
            try
            {
                var cutoff = DateTime.UtcNow - UnusedUploadLifetime;
                List<Attachment> stale;
                lock (Sync) stale = Attachments.Find(x => x.TicketId == 0 && x.CreatedAt < cutoff).ToList();
                UploadDirBytes();
                foreach (var a in stale)
                {
                    try { File.Delete(Path.Combine(UploadDir, a.Name)); } catch { }
                    lock (Sync) Attachments.Delete(a.Name);
                    Interlocked.Add(ref _dirBytes, -a.Bytes);
                }
                if (stale.Count > 0) Console.WriteLine($"[support] deleted {stale.Count} unused screenshot upload(s)");
            }
            catch (Exception ex) { Console.WriteLine($"[support] upload sweep failed: {ex.Message}"); }
        }

        /// <summary>The uploader's own not-yet-used uploads among these names (anything else is dropped).</summary>
        public static List<string> OwnUnclaimed(ulong uploaderId, IEnumerable<string>? names)
        {
            var list = new List<string>();
            foreach (var n in (names ?? Enumerable.Empty<string>()).Distinct().Take(MaxAttachmentsPerMessage))
            {
                var a = Attachments.FindById(n);
                if (a != null && a.UploaderId == uploaderId && a.TicketId == 0) list.Add(n);
            }
            return list;
        }

        private static void ClaimAttachments(List<string>? names, int ticketId)
        {
            foreach (var n in names ?? new())
            {
                var a = Attachments.FindById(n);
                if (a == null) continue;
                a.TicketId = ticketId;
                Attachments.Update(a);
            }
        }

        public static Attachment? GetAttachment(string name) => Attachments.FindById(name);

        private static string? SniffImage(byte[] d, out string contentType)
        {
            contentType = "";
            if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) { contentType = "image/png"; return ".png"; }
            if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) { contentType = "image/jpeg"; return ".jpg"; }
            if (d.Length > 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') { contentType = "image/gif"; return ".gif"; }
            if (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') { contentType = "image/webp"; return ".webp"; }
            return null;
        }
    }
}
