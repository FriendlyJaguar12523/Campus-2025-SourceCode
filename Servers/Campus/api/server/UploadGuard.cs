using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LiteDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Only files the GAME produced are accepted as uploads; anything else is refused, counted
    /// as a strike against the account, and after ConfigCampus.UploadStrikesToBan strikes the
    /// account, its devices and its IP are banned for ConfigCampus.UploadBanDays and every
    /// request from its IPs is throttled (TrafficGuardMiddleware, "upload-penalty") - from the
    /// ban, not the first strike.
    ///
    /// "Produced by the game" is decided from the BYTES, per file type:
    ///
    ///   Image (photos, polaroids, custom-item art)
    ///     Unity's ImageConversion encoders leave a fixed shape that no camera, phone, browser
    ///     or editor reproduces:
    ///       JPEG  SOI, APP0 = JFIF 1.01 / no density / no thumbnail, then only DQT / SOF0
    ///             (baseline, 3 components) / DHT / DRI / SOS, EOI as the LAST two bytes. An
    ///             APP1 (Exif), APP2 (ICC), COM, or progressive SOF2 is something else.
    ///       PNG   IHDR / IDAT.. / IEND and nothing else (no pHYs / sRGB / gAMA / tEXt / iCCP),
    ///             8-bit RGB or RGBA, non-interlaced, valid CRCs, and the IDAT stream must
    ///             actually inflate to exactly one image's worth of scanlines.
    ///   RoomSave / RoomMetadata / Invention / Holotar
    ///     Protobuf messages. The whole buffer must parse as ONE top-level message - every tag
    ///     a sane field number with a known wire type, every length inside the buffer, exact
    ///     consumption - and open with the field the game's serializer writes first. A video,
    ///     an image, a zip or random bytes fails within the first few tags (an MP4 starts with
    ///     field 0, a PNG with a 25-million field number); the chance of arbitrary data walking
    ///     cleanly to the end is nil.
    ///   Video / Unknown
    ///     The game never uploads these on this build; refused outright.
    ///
    /// The same validators drive SweepStorage, which quarantines (moves out of the served
    /// folders) anything already on disk that would be refused today - the MP4s that were
    /// posted as inventions/holotars and the room blobs posted as polaroids are the reason
    /// this exists.
    /// </summary>
    public static class UploadGuard
    {
        public enum Kind { Image, WebImage, RoomSave, RoomMetadata, Invention, Holotar, Video, Unknown }

        public static Kind KindOf(Images.FileType_data t) => t switch
        {
            Images.FileType_data.Image        => Kind.Image,
            Images.FileType_data.RoomSave     => Kind.RoomSave,
            Images.FileType_data.RoomMetadata => Kind.RoomMetadata,
            Images.FileType_data.Invention    => Kind.Invention,
            Images.FileType_data.Holotar      => Kind.Holotar,
            Images.FileType_data.Video        => Kind.Video,
            _                                 => Kind.Unknown,
        };

        // ------------------------------------------------------------------ gate ----

        /// <summary>
        /// Run before an upload is stored. Null = accepted. Otherwise the upload has been
        /// refused, a strike recorded (with the ban/penalty side effects when the count is
        /// reached), and the returned result is what the endpoint should answer.
        /// </summary>
        public static ContentResult? Gate(HttpContext ctx, player_info account, Kind kind, byte[]? data, string endpoint)
        {
            if (!ConfigCampus.UploadGuardEnabled) return null;

            string ip = LoginGuard.ClientIp(ctx);
            string ua = ctx.Request.Headers["User-Agent"].ToString();
            string why;

            // An upload that did not come through the game's HTTP stack is not from the game,
            // whatever its bytes look like.
            if (ConfigCampus.RequireGameUserAgent && ua != "BestHTTP")
                why = $"user-agent '{Trunc(ua, 40)}' is not the game";
            else if (data == null || data.Length == 0)
                why = "empty file";
            else if (data.Length > ConfigCampus.UploadMaxBytes)
                why = $"{data.Length} bytes exceeds the {ConfigCampus.UploadMaxBytes} byte cap";
            else if (Validate(kind, data, out why))
                return null;

            int strikes = Strike(account.Id, ip, kind, why, endpoint, data?.Length ?? 0);
            bool banned = strikes >= ConfigCampus.UploadStrikesToBan;
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = banned
                        ? $"Uploads that don't come from the game are not allowed. This account is banned for {ConfigCampus.UploadBanDays} days."
                        : $"That file was not made by the game and was rejected ({strikes}/{ConfigCampus.UploadStrikesToBan} - at {ConfigCampus.UploadStrikesToBan} the account is banned for {ConfigCampus.UploadBanDays} days).",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = banned ? 403 : 400
            };
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        // ------------------------------------------------------------- validators ----

        public static bool Validate(Kind kind, byte[] d, out string why)
        {
            switch (kind)
            {
                case Kind.Image:        return IsGameImage(d, strict: true, out why);
                case Kind.WebImage:     return IsGameImage(d, strict: false, out why);
                case Kind.RoomSave:     return ProtobufWalk(d, firstField: 1, firstWire: 0, out why);
                case Kind.Invention:    return ProtobufWalk(d, firstField: 1, firstWire: 0, out why);
                case Kind.RoomMetadata: return ProtobufWalk(d, firstField: 2, firstWire: 0, out why);
                case Kind.Holotar:      return ProtobufWalk(d, firstField: 1, firstWire: -1, out why);
                case Kind.Video:        why = "the game does not upload video"; return false;
                default:                why = "unknown file type"; return false;
            }
        }

        public static bool IsGameImage(byte[] d, bool strict, out string why)
        {
            if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return IsGameJpeg(d, strict, out why);
            if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return IsGamePng(d, strict, out why);
            why = $"not a JPEG or PNG (starts {BitConverter.ToString(d, 0, Math.Min(8, d.Length))})";
            return false;
        }

        // Unity's exact APP0: "JFIF\0", version 1.01, units 0, density 1x1, no thumbnail.
        private static readonly byte[] UnityJfif = { 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 };

        private static bool IsGameJpeg(byte[] d, bool strict, out string why)
        {
            why = "";
            if (d.Length < 4 || d[d.Length - 2] != 0xFF || d[d.Length - 1] != 0xD9) { why = "JPEG does not end at EOI (trailing data)"; return false; }
            int pos = 2; bool sawApp0 = false, sawSof = false, sawSos = false; int n = 0;
            while (pos + 4 <= d.Length)
            {
                if (d[pos] != 0xFF) { why = $"JPEG marker expected at {pos}"; return false; }
                byte m = d[pos + 1];
                if (m == 0xFF) { pos++; continue; }
                int len = (d[pos + 2] << 8) | d[pos + 3];
                if (len < 2 || pos + 2 + len > d.Length) { why = $"JPEG segment {m:X2} overruns"; return false; }
                n++;
                if (n == 1)
                {
                    if (m != 0xE0) { why = $"JPEG first segment is {m:X2}, not APP0/JFIF"; return false; }
                    if (strict && (len != 16 || !d.AsSpan(pos + 4, 14).SequenceEqual(UnityJfif))) { why = "JPEG APP0 is not Unity's JFIF header"; return false; }
                    sawApp0 = true;
                }
                else switch (m)
                {
                    case 0xDB: case 0xC4: case 0xDD: break;                       // DQT, DHT, DRI
                    case 0xC0:                                                    // SOF0 baseline
                        if (len < 8) { why = "JPEG SOF0 truncated"; return false; }
                        {
                            int h = (d[pos + 5] << 8) | d[pos + 6], w = (d[pos + 7] << 8) | d[pos + 8], nc = d[pos + 9];
                            if (w < 8 || h < 8 || w > ConfigCampus.UploadMaxImageDim || h > ConfigCampus.UploadMaxImageDim) { why = $"JPEG {w}x{h} outside allowed size"; return false; }
                            if (strict && nc != 3) { why = $"JPEG has {nc} components, Unity writes 3"; return false; }
                        }
                        sawSof = true; break;
                    case 0xDA: sawSos = true; break;                              // SOS - entropy data follows
                    case 0xC2: why = "progressive JPEG (not a Unity encode)"; return false;
                    case 0xE1: why = "JPEG carries Exif (APP1) - not a Unity encode"; return false;
                    case 0xE2: why = "JPEG carries an ICC profile (APP2) - not a Unity encode"; return false;
                    case 0xFE: why = "JPEG carries a comment - not a Unity encode"; return false;
                    default:
                        if (strict) { why = $"JPEG segment {m:X2} is not something Unity writes"; return false; }
                        break;
                }
                if (sawSos) break;
                pos += 2 + len;
            }
            if (!sawApp0 || !sawSof || !sawSos) { why = "JPEG is missing APP0/SOF0/SOS"; return false; }
            return true;
        }

        private static readonly uint[] CrcTable = MakeCrcTable();
        private static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
        private static uint Crc32(byte[] d, int off, int len)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = off; i < off + len; i++) c = CrcTable[(c ^ d[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static bool IsGamePng(byte[] d, bool strict, out string why)
        {
            why = "";
            if (d.Length < 8 + 25 || d[4] != 0x0D || d[5] != 0x0A || d[6] != 0x1A || d[7] != 0x0A) { why = "PNG signature broken"; return false; }
            int pos = 8; bool ihdr = false, iend = false; int w = 0, h = 0, bpp = 0; var idat = new List<(int off, int len)>();
            while (pos + 12 <= d.Length)
            {
                int len = (d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3];
                if (len < 0 || pos + 12 + len > d.Length) { why = "PNG chunk overruns"; return false; }
                string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
                if (Crc32(d, pos + 4, 4 + len) != (uint)((d[pos + 8 + len] << 24) | (d[pos + 9 + len] << 16) | (d[pos + 10 + len] << 8) | d[pos + 11 + len]))
                { why = $"PNG {type} chunk CRC is wrong"; return false; }
                switch (type)
                {
                    case "IHDR":
                        if (ihdr || len != 13) { why = "PNG IHDR malformed"; return false; }
                        w = (d[pos + 8] << 24) | (d[pos + 9] << 16) | (d[pos + 10] << 8) | d[pos + 11];
                        h = (d[pos + 12] << 24) | (d[pos + 13] << 16) | (d[pos + 14] << 8) | d[pos + 15];
                        int bd = d[pos + 16], ct = d[pos + 17], comp = d[pos + 18], filt = d[pos + 19], il = d[pos + 20];
                        if (w < 1 || h < 1 || w > ConfigCampus.UploadMaxImageDim || h > ConfigCampus.UploadMaxImageDim) { why = $"PNG {w}x{h} outside allowed size"; return false; }
                        if (comp != 0 || filt != 0) { why = "PNG uses an unknown compression/filter method"; return false; }
                        if (strict)
                        {
                            if (bd != 8 || (ct != 2 && ct != 6)) { why = $"PNG bit depth {bd} / colour type {ct} is not what Unity writes (8-bit RGB/RGBA)"; return false; }
                            if (il != 0) { why = "interlaced PNG - not a Unity encode"; return false; }
                        }
                        bpp = ct switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 } * (bd == 16 ? 2 : 1);
                        if (bpp == 0) { why = $"PNG colour type {ct} unknown"; return false; }
                        if (bd < 8) bpp = 1; // sub-byte depths pack; the scanline check below is by whole bytes
                        ihdr = true; break;
                    case "IDAT":
                        if (!ihdr) { why = "PNG IDAT before IHDR"; return false; }
                        idat.Add((pos + 8, len)); break;
                    case "IEND":
                        iend = true; break;
                    default:
                        if (strict) { why = $"PNG carries a {type} chunk - Unity writes IHDR/IDAT/IEND only"; return false; }
                        if (type == "PLTE" || type == "tRNS" || type == "pHYs" || type == "sRGB" || type == "gAMA" || type == "iCCP" || type == "cHRM" || type == "bKGD" || type == "tIME" || type == "tEXt" || type == "zTXt" || type == "iTXt" || type == "sBIT" || type == "eXIf") break;
                        why = $"PNG carries an unexpected {type} chunk"; return false;
                }
                pos += 12 + len;
                if (iend) break;
            }
            if (!ihdr || !iend || idat.Count == 0) { why = "PNG is missing IHDR/IDAT/IEND"; return false; }
            if (pos != d.Length) { why = $"{d.Length - pos} bytes after PNG IEND"; return false; }
            if (strict && idat.Any(c => c.len > 32768)) { why = "PNG IDAT chunk larger than Unity writes"; return false; }
            if (d[idat[0].off] != 0x78) { why = "PNG IDAT is not a zlib stream"; return false; }

            // The image data must inflate to exactly h scanlines of (1 filter byte + w*bpp). For
            // sub-byte depths (non-strict only) the row is shorter; accept >= to stay lenient there.
            long expect = (long)h * (1 + (long)w * bpp);
            try
            {
                using var joined = new MemoryStream();
                foreach (var (off, len) in idat) joined.Write(d, off, len);
                joined.Position = 0;
                using var z = new ZLibStream(joined, CompressionMode.Decompress);
                var buf = new byte[65536]; long total = 0; int r;
                while ((r = z.Read(buf, 0, buf.Length)) > 0) { total += r; if (total > expect) break; }
                if (strict ? total != expect : total > expect) { why = $"PNG pixel data inflates to {total} bytes, expected {expect} for {w}x{h}"; return false; }
            }
            catch (Exception ex) { why = $"PNG pixel data does not inflate ({ex.GetType().Name})"; return false; }
            return true;
        }

        /// <summary>
        /// Walk the buffer as one protobuf message (see the class remarks). firstField /
        /// firstWire pin the opening tag (-1 = any wire type).
        /// </summary>
        public static bool ProtobufWalk(byte[] d, int firstField, int firstWire, out string why)
        {
            why = "";
            if (d.Length == 0) { why = "empty"; return false; }
            int pos = 0, n = 0;
            while (pos < d.Length)
            {
                if (!Varint(d, ref pos, out ulong tag)) { why = $"protobuf: bad tag at {pos}"; return false; }
                int field = (int)(tag >> 3), wt = (int)(tag & 7);
                if (field < 1 || field > 512) { why = $"protobuf: field number {field} at {pos} is not plausible"; return false; }
                if (n == 0 && (field != firstField || (firstWire >= 0 && wt != firstWire)))
                { why = $"protobuf: opens with field {field}/wire {wt}, the game writes field {firstField}"; return false; }
                switch (wt)
                {
                    case 0: if (!Varint(d, ref pos, out _)) { why = $"protobuf: bad varint at {pos}"; return false; } break;
                    case 1: pos += 8; break;
                    case 5: pos += 4; break;
                    case 2:
                        if (!Varint(d, ref pos, out ulong len) || len > (ulong)(d.Length - pos)) { why = $"protobuf: length-delimited field {field} overruns at {pos}"; return false; }
                        pos += (int)len; break;
                    default: why = $"protobuf: wire type {wt} at {pos}"; return false;
                }
                if (pos > d.Length) { why = "protobuf: overrun"; return false; }
                n++;
            }
            return true;
        }

        private static bool Varint(byte[] d, ref int pos, out ulong v)
        {
            v = 0; int shift = 0;
            for (int i = 0; i < 10; i++)
            {
                if (pos >= d.Length) return false;
                byte b = d[pos++];
                v |= (ulong)(b & 0x7F) << shift; shift += 7;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- strikes ----

        public class StrikeEntry
        {
            [BsonId] public ObjectId Id { get; set; } = ObjectId.NewObjectId();
            public ulong AccountId { get; set; }
            public string Ip { get; set; } = "";       // AES-encrypted like every other stored IP
            public string Kind { get; set; } = "";
            public string Reason { get; set; } = "";
            public string Endpoint { get; set; } = "";
            public int Bytes { get; set; }
            public long At { get; set; }
        }

        private static ILiteCollection<StrikeEntry> Col
        {
            get
            {
                var col = PlayerDB.PlayerDBFile.GetCollection<StrikeEntry>("upload_strikes");
                col.EnsureIndex(x => x.AccountId);
                col.EnsureIndex(x => x.At);
                return col;
            }
        }

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static long WindowStart => Now - (long)ConfigCampus.UploadStrikeWindowDays * 86400;

        // IPs under penalty (throttled everywhere) -> unix expiry. Only a BANNED account's
        // addresses (UploadStrikesToBan strikes) go here. It used to be every striking IP from the
        // first strike, and the throttle (UploadPenaltyMaxPerWindow per FloodWindowSeconds) is
        // below what a room load fires, so one refused photo left a player on an endless loading
        // screen for UploadStrikeWindowDays (accounts 64 and 71, 2026-09-26). A strike below the
        // ban is still recorded and still counts toward it. Rebuilt from the strike table at
        // startup so a restart does not forgive a ban.
        private static readonly ConcurrentDictionary<string, long> _penalised = new();

        public static void LoadPenalties()
        {
            int n = 0, banned = 0;
            foreach (var g in Col.Find(x => x.At >= WindowStart).GroupBy(s => s.AccountId))
            {
                n += g.Count();
                if (g.Count() < ConfigCampus.UploadStrikesToBan) continue;
                banned++;
                long until = g.Max(s => s.At) + (long)ConfigCampus.UploadStrikeWindowDays * 86400;
                var ips = g.Select(s => s.Ip).Where(ip => !string.IsNullOrEmpty(ip)).ToList();
                try { ips.AddRange(PlayerDB.GetFullAccount(g.Key)?.player_Extra?.IpAddresses ?? new List<string>()); } catch { }
                foreach (var ip in ips.Distinct())
                    _penalised.AddOrUpdate(ip, until, (_, cur) => Math.Max(cur, until));
            }
            Console.WriteLine($"[upload-guard] {(ConfigCampus.UploadGuardEnabled ? "ENABLED" : "disabled")}: {n} strike(s) in the last {ConfigCampus.UploadStrikeWindowDays} days, {banned} account(s) at the ban, {_penalised.Count} IP(s) under penalty");
        }

        /// <summary>Is every request from this IP to be throttled hard? (see TrafficGuardMiddleware)</summary>
        public static bool IsPenalisedIp(string ip)
        {
            if (_penalised.IsEmpty || string.IsNullOrEmpty(ip)) return false;
            if (!_penalised.TryGetValue(Utils.EncryptAes(ip.Trim()), out long until)) return false;
            if (until > Now) return true;
            _penalised.TryRemove(Utils.EncryptAes(ip.Trim()), out _);
            return false;
        }

        public static int StrikeCount(ulong accountId) => Col.Count(x => x.AccountId == accountId && x.At >= WindowStart);

        /// <summary>Record a refused upload; returns the account's strike count in the window.</summary>
        public static int Strike(ulong accountId, string ip, Kind kind, string reason, string endpoint, int bytes)
        {
            string encIp = string.IsNullOrWhiteSpace(ip) ? "" : Utils.EncryptAes(ip.Trim());
            Col.Insert(new StrikeEntry { AccountId = accountId, Ip = encIp, Kind = kind.ToString(), Reason = reason, Endpoint = endpoint, Bytes = bytes, At = Now });
            int count = StrikeCount(accountId);

            Console.WriteLine($"[upload-guard] REFUSED {kind} from account {accountId} ip {ip} on {endpoint} ({bytes} bytes): {reason} - strike {count}/{ConfigCampus.UploadStrikesToBan}");
            Webhook.Send(accountId, "mod", "Upload refused",
                $"({accountId}) posted a {kind} that did not come from the game on `{endpoint}` ({bytes} bytes): {reason}. Strike {count}/{ConfigCampus.UploadStrikesToBan}.", 15158332);

            if (count >= ConfigCampus.UploadStrikesToBan && count % ConfigCampus.UploadStrikesToBan == 0)
                Ban(accountId, ip, count);
            return count;
        }

        private static void Ban(ulong accountId, string ip, int strikes)
        {
            int seconds = ConfigCampus.UploadBanDays * 86400;
            string reason = $"Uploading files that did not come from the game ({strikes} strikes)";
            try
            {
                PlayerDB.Set_moderation_block(accountId, Moderation_Detail.ReportCategory.Cheating, seconds, 0, false,
                    $"Uploading files that did not come from the game. Banned for {ConfigCampus.UploadBanDays} days.", null, true, 0,
                    logKind: "upload-ban", logNote: $"{strikes} strikes; IP and devices banned too");
                if (!string.IsNullOrWhiteSpace(ip)) BanDB.BanIp(ip, accountId, 0, reason, seconds);
                int devices = 0;
                foreach (var b in PlayerDB.GetDeviceBindings(accountId).Select(b => b.DeviceId).Distinct())
                    if (BanDB.BanDevice(b, accountId, 0, reason, seconds)) devices++;
                // The throttle starts here: this upload's address and every address the account
                // has ever used.
                var full = PlayerDB.GetFullAccount(accountId);
                if (!string.IsNullOrWhiteSpace(ip))
                    _penalised.AddOrUpdate(Utils.EncryptAes(ip.Trim()), Now + (long)ConfigCampus.UploadStrikeWindowDays * 86400, (_, cur) => Math.Max(cur, Now + (long)ConfigCampus.UploadStrikeWindowDays * 86400));
                foreach (var enc in full?.player_Extra?.IpAddresses ?? new List<string>())
                    _penalised.AddOrUpdate(enc, Now + (long)ConfigCampus.UploadStrikeWindowDays * 86400, (_, cur) => Math.Max(cur, Now + (long)ConfigCampus.UploadStrikeWindowDays * 86400));
                Console.WriteLine($"[upload-guard] BANNED account {accountId} for {ConfigCampus.UploadBanDays} days (ip {ip}, {devices} device(s)) after {strikes} refused uploads");
                Webhook.Send(accountId, "mod", "Upload ban",
                    $"({accountId}) banned for {ConfigCampus.UploadBanDays} days: {strikes} uploads that did not come from the game. IP + {devices} device(s) banned too.", 10038562);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[upload-guard] ban of {accountId} failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ sweep ----

        /// <summary>
        /// Re-check everything already stored with today's validators and quarantine what
        /// fails (moved to Data/Quarantine/&lt;folder&gt;/, never deleted outright so a false
        /// positive can be put back by hand). Returns the number quarantined.
        /// </summary>
        public static int SweepStorage(bool dryRun)
        {
            string data = Path.Combine(Environment.CurrentDirectory, "Data");
            string quarantine = Path.Combine(data, "Quarantine");
            int moved = 0, checkedN = 0;
            var jobs = new (string dir, Func<string, Kind?> kindOf)[]
            {
                (Images.ImgSavePathPolaroid, _ => Kind.Image),
                // player_images mixes in-game photos with website profile pictures (pfp_*) and
                // room images the admin tooling wrote; only the game's own names are held to
                // the strict shape, the rest must still be a real image.
                // Admin panel imports (adm_*, AdminImagesController) are exempt: an admin chose them.
                (Images.ImgSavePath, n => n.StartsWith("adm_", StringComparison.OrdinalIgnoreCase) ? null
                                        : n.StartsWith("pfp_", StringComparison.OrdinalIgnoreCase) ? Kind.WebImage : Kind.Image),
                (Images.RoomSavePath, n => n.StartsWith("default_room", StringComparison.OrdinalIgnoreCase) ? null
                                          : n.EndsWith(".room", StringComparison.OrdinalIgnoreCase) ? Kind.RoomSave
                                          : n.EndsWith(".META", StringComparison.OrdinalIgnoreCase) ? Kind.RoomMetadata : null),
                (Images.InvSavePath, n => n.EndsWith(".inv", StringComparison.OrdinalIgnoreCase) ? Kind.Invention : null),
                (Images.CdnSavePath, n => n.EndsWith(".htr", StringComparison.OrdinalIgnoreCase) ? Kind.Holotar
                                        : n.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ? Kind.Video : null),
            };
            foreach (var (dir, kindOf) in jobs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    string name = Path.GetFileName(path);
                    var kind = kindOf(name);
                    if (kind == null) continue;
                    checkedN++;
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(path); } catch { continue; }
                    if (Validate(kind.Value, bytes, out string why)) continue;
                    moved++;
                    Console.WriteLine($"[upload-guard] sweep: {(dryRun ? "would quarantine" : "quarantined")} {Path.GetFileName(dir)}/{name} ({bytes.Length} bytes, {kind}): {why}");
                    if (dryRun) continue;
                    try
                    {
                        string qdir = Path.Combine(quarantine, Path.GetFileName(dir));
                        Directory.CreateDirectory(qdir);
                        File.Move(path, Path.Combine(qdir, name), overwrite: true);
                    }
                    catch (Exception ex) { Console.WriteLine($"[upload-guard] sweep: could not move {name}: {ex.Message}"); }
                }
            }
            Console.WriteLine($"[upload-guard] sweep done: {checkedN} file(s) checked, {moved} {(dryRun ? "would be " : "")}quarantined under Data/Quarantine");
            return moved;
        }
    }
}
