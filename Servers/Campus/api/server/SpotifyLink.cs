using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Spotify account linking for the website (Settings > Services > Spotify) and the "listening
    /// to" line under a player's online status.
    ///
    /// The Spotify app's credentials live OUTSIDE the source, in Data/spotify_app.json:
    ///   { "ClientId": "...", "ClientSecret": "...", "RedirectUri": "https://camprec.net/api/site/spotify/callback" }
    /// (RedirectUri optional - that default is used - but whatever it is must be registered on the
    /// app in the Spotify developer dashboard.) Nothing here works until that file exists; the
    /// Settings page says so instead of showing a Link button that goes nowhere.
    ///
    /// Storage: Data/DBs/Spotify.db, one row per account with the REFRESH token (long-lived), the
    /// last access token and the player's "show my Spotify to" choice. Now-playing is polled from
    /// Spotify at most once per 10 s per account, however many people are looking.
    /// </summary>
    public static class SpotifyLink
    {
        public const int VisEveryone = 0, VisFriends = 1, VisFavorites = 2, VisNoOne = 3;

        public sealed class Link
        {
            [LiteDB.BsonId] public ulong AccountId { get; set; }
            public string RefreshToken { get; set; } = "";
            public string AccessToken { get; set; } = "";
            public DateTime AccessExpiresAt { get; set; }
            public string SpotifyUserId { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string ProfileUrl { get; set; } = "";
            public int Visibility { get; set; } = VisFriends;
            public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
            /// <summary>Set when Spotify refused the refresh token (revoked): the link is dead until re-linked.</summary>
            public bool Broken { get; set; }
            /// <summary>
            /// Spotify answers 403 for this user: the Campus app is in Spotify's Development Mode,
            /// which only serves the (max 25) users allow-listed under User Management on the
            /// developer dashboard. The OAuth handshake still succeeds - the token is real - but
            /// /v1/me and /currently-playing are refused, which is the "Connected but no name and no
            /// music" symptom. Cleared the next time a call succeeds (the user got added).
            /// </summary>
            public bool NotAllowed { get; set; }
            public DateTime NotAllowedAt { get; set; }
        }

        public sealed class NowPlaying
        {
            public bool IsPlaying;
            public string Track = "";
            public string Artists = "";
            public string Album = "";
            public string AlbumArt = "";
            public string Url = "";
            public string TrackId = "";
            public long ProgressMs;
            public long DurationMs;
            public DateTime FetchedAt = DateTime.UtcNow;
        }

        private sealed class AppConfig { public string ClientId { get; set; } = ""; public string ClientSecret { get; set; } = ""; public string RedirectUri { get; set; } = ""; }

        private static readonly string ConfigPath = Path.Combine("Data", "spotify_app.json");
        private static readonly Lazy<LiteDB.LiteDatabase> _db = new(() => new LiteDB.LiteDatabase(Path.Combine("Data", "DBs", "Spotify.db")));
        private static LiteDB.ILiteCollection<Link> Links() => _db.Value.GetCollection<Link>("links");
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
        private static AppConfig? _cfg; private static DateTime _cfgRead;

        // OAuth state -> account, 10 minutes, one use.
        private static readonly ConcurrentDictionary<string, (ulong account, DateTime at)> _states = new();
        // Now-playing cache + the empty ("nothing playing") cache, per account.
        private static readonly ConcurrentDictionary<ulong, (NowPlaying? np, DateTime at)> _cache = new();
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

        private static AppConfig? Config()
        {
            if (_cfg != null && DateTime.UtcNow - _cfgRead < TimeSpan.FromMinutes(1)) return _cfg;
            try
            {
                if (!File.Exists(ConfigPath)) { _cfg = null; }
                else
                {
                    var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (c != null && string.IsNullOrWhiteSpace(c.RedirectUri)) c.RedirectUri = "https://gameauth.ddns.net/api/site/spotify/callback";     // as registered with Spotify
                    _cfg = (c != null && !string.IsNullOrWhiteSpace(c.ClientId) && !string.IsNullOrWhiteSpace(c.ClientSecret)) ? c : null;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[spotify] could not read {ConfigPath}: {ex.Message}"); _cfg = null; }
            _cfgRead = DateTime.UtcNow;
            return _cfg;
        }

        public static bool Configured => Config() != null;

        public static Link? Get(ulong accountId) => Links().FindById(accountId);

        public static void Unlink(ulong accountId)
        {
            Links().Delete(accountId);
            _cache.TryRemove(accountId, out _);
            Console.WriteLine($"[spotify] {accountId} unlinked");
        }

        public static void SetVisibility(ulong accountId, int visibility)
        {
            var l = Get(accountId);
            if (l == null) return;
            l.Visibility = Math.Clamp(visibility, VisEveryone, VisNoOne);
            Links().Update(l);
            Console.WriteLine($"[spotify] {accountId} visibility -> {l.Visibility}");
        }

        // ---- OAuth ------------------------------------------------------------------------

        /// <summary>The Spotify authorize URL for this account, or null when the app isn't configured.</summary>
        public static string? StartUrl(ulong accountId)
        {
            var cfg = Config();
            if (cfg == null) return null;
            foreach (var kv in _states) if (DateTime.UtcNow - kv.Value.at > TimeSpan.FromMinutes(10)) _states.TryRemove(kv.Key, out _);
            string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _states[state] = (accountId, DateTime.UtcNow);
            return "https://accounts.spotify.com/authorize"
                + "?client_id=" + Uri.EscapeDataString(cfg.ClientId)
                + "&response_type=code"
                + "&redirect_uri=" + Uri.EscapeDataString(cfg.RedirectUri)
                + "&scope=" + Uri.EscapeDataString("user-read-currently-playing user-read-playback-state")
                + "&state=" + state
                + "&show_dialog=true";
        }

        /// <summary>Finish the OAuth round trip. Returns the outcome key the Settings page toasts.</summary>
        public static async Task<string> Complete(string? code, string? state, string? error)
        {
            var cfg = Config();
            if (cfg == null) return "unconfigured";
            if (string.IsNullOrEmpty(state) || !_states.TryRemove(state, out var st) || DateTime.UtcNow - st.at > TimeSpan.FromMinutes(10)) return "expired";
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return "denied";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.ClientId + ":" + cfg.ClientSecret)));
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code!,
                    ["redirect_uri"] = cfg.RedirectUri
                });
                var resp = await _http.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) { Console.WriteLine($"[spotify] token exchange for {st.account} failed: HTTP {(int)resp.StatusCode} {body}"); return "error"; }
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string access = root.GetProperty("access_token").GetString() ?? "";
                string refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
                int expires = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
                if (access.Length == 0 || refresh.Length == 0) return "error";

                // Who is this? (display name for the Settings card)
                string userId = "", name = "", url = "";
                bool notAllowed = false;
                try
                {
                    using var me = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
                    me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                    var mr = await _http.SendAsync(me);
                    if ((int)mr.StatusCode == 403)
                    {
                        notAllowed = true;
                        Console.WriteLine($"[spotify] /me for {st.account}: HTTP 403 - this Spotify user is not on the app's allow-list (Development Mode)");
                    }
                    if (mr.IsSuccessStatusCode)
                    {
                        using var md = JsonDocument.Parse(await mr.Content.ReadAsStringAsync());
                        userId = md.RootElement.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
                        name = md.RootElement.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() ?? "" : userId;
                        if (md.RootElement.TryGetProperty("external_urls", out var eu) && eu.TryGetProperty("spotify", out var su)) url = su.GetString() ?? "";
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[spotify] /me for {st.account} failed: {ex.Message}"); }

                // One Spotify account per Campus account, and vice versa.
                if (userId.Length > 0)
                {
                    var other = Links().FindOne(l => l.SpotifyUserId == userId && l.AccountId != st.account);
                    if (other != null) return "inuse";
                }

                var existing = Get(st.account);
                var link = new Link
                {
                    AccountId = st.account,
                    RefreshToken = refresh,
                    AccessToken = access,
                    AccessExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, expires - 60)),
                    SpotifyUserId = userId,
                    DisplayName = name,
                    ProfileUrl = url,
                    Visibility = existing?.Visibility ?? VisFriends,
                    LinkedAt = DateTime.UtcNow,
                    NotAllowed = notAllowed,
                    NotAllowedAt = notAllowed ? DateTime.UtcNow : default
                };
                Links().Upsert(link);
                _cache.TryRemove(st.account, out _);
                Console.WriteLine($"[spotify] {st.account} linked as '{name}' ({userId}){(notAllowed ? " - NOT ALLOW-LISTED" : "")}");
                return notAllowed ? "notallowed" : "linked";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[spotify] link for {st.account} failed: {ex.Message}");
                return "error";
            }
        }

        private static async Task<string?> AccessToken(Link link)
        {
            if (link.Broken) return null;
            if (DateTime.UtcNow < link.AccessExpiresAt && link.AccessToken.Length > 0) return link.AccessToken;
            var cfg = Config();
            if (cfg == null) return null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.ClientId + ":" + cfg.ClientSecret)));
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = link.RefreshToken });
                var resp = await _http.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[spotify] refresh for {link.AccountId} failed: HTTP {(int)resp.StatusCode} {body}");
                    if ((int)resp.StatusCode == 400 && body.Contains("invalid_grant")) { link.Broken = true; Links().Update(link); }
                    return null;
                }
                using var doc = JsonDocument.Parse(body);
                link.AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
                int expires = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
                if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String) link.RefreshToken = rt.GetString() ?? link.RefreshToken;
                link.AccessExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, expires - 60));
                Links().Update(link);
                return link.AccessToken;
            }
            catch (Exception ex) { Console.WriteLine($"[spotify] refresh for {link.AccountId} threw: {ex.Message}"); return null; }
        }

        // ---- now playing --------------------------------------------------------------------

        /// <summary>What the account is playing right now (cached 10 s), or null when nothing / unlinked.</summary>
        public static async Task<NowPlaying?> Fetch(ulong accountId)
        {
            var link = Get(accountId);
            if (link == null) return null;
            // A refused (not allow-listed) link is retried every 5 minutes, not every 10 s.
            var cacheFor = link.NotAllowed ? TimeSpan.FromMinutes(5) : CacheFor;
            if (_cache.TryGetValue(accountId, out var hit) && DateTime.UtcNow - hit.at < cacheFor) return hit.np;
            NowPlaying? np = null;
            try
            {
                string? token = await AccessToken(link);
                if (token != null)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing?additional_types=track,episode");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var resp = await _http.SendAsync(req);
                    int code = (int)resp.StatusCode;
                    if (code == 200)
                    {
                        string body = await resp.Content.ReadAsStringAsync();
                        np = Parse(body);
                    }
                    else if (code == 403)
                    {
                        if (!link.NotAllowed)
                        {
                            link.NotAllowed = true; link.NotAllowedAt = DateTime.UtcNow; Links().Update(link);
                            Console.WriteLine($"[spotify] now-playing for {accountId}: HTTP 403 - user not on the app's allow-list (Development Mode); polling every 5 min until they are");
                        }
                    }
                    else if (code != 204)
                        Console.WriteLine($"[spotify] now-playing for {accountId}: HTTP {code}");
                    if ((code == 200 || code == 204) && link.NotAllowed)
                    {
                        // They got added: clear the flag and pick up the display name we never got.
                        link.NotAllowed = false; Links().Update(link);
                        _ = FillIdentity(link);
                        Console.WriteLine($"[spotify] {accountId} is allow-listed now");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[spotify] now-playing for {accountId} threw: {ex.Message}"); }
            _cache[accountId] = (np, DateTime.UtcNow);
            return np;
        }

        /// <summary>Fetch /v1/me for a link that was made while refused, so the card gets a name.</summary>
        private static async Task FillIdentity(Link link)
        {
            try
            {
                string? token = await AccessToken(link);
                if (token == null) return;
                using var me = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
                me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var mr = await _http.SendAsync(me);
                if (!mr.IsSuccessStatusCode) return;
                using var md = JsonDocument.Parse(await mr.Content.ReadAsStringAsync());
                link.SpotifyUserId = md.RootElement.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : link.SpotifyUserId;
                link.DisplayName = md.RootElement.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() ?? "" : link.SpotifyUserId;
                if (md.RootElement.TryGetProperty("external_urls", out var eu) && eu.TryGetProperty("spotify", out var su)) link.ProfileUrl = su.GetString() ?? "";
                Links().Update(link);
            }
            catch (Exception ex) { Console.WriteLine($"[spotify] identity for {link.AccountId} failed: {ex.Message}"); }
        }

        /// <summary>Track / artist / album names go through the chat censor: a swear becomes ****.</summary>
        private static string Clean(string? s) => string.IsNullOrEmpty(s) ? "" : Campus_live_rewrite.api.server.Classes.Sanitize.SanitizeMessage(s);

        private static NowPlaying? Parse(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            var np = new NowPlaying
            {
                IsPlaying = root.TryGetProperty("is_playing", out var ip) && ip.GetBoolean(),
                ProgressMs = root.TryGetProperty("progress_ms", out var pm) && pm.ValueKind == JsonValueKind.Number ? pm.GetInt64() : 0,
                Track = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                DurationMs = item.TryGetProperty("duration_ms", out var dm) && dm.ValueKind == JsonValueKind.Number ? dm.GetInt64() : 0,
                TrackId = item.TryGetProperty("id", out var tid) && tid.ValueKind == JsonValueKind.String ? tid.GetString() ?? "" : ""
            };
            if (item.TryGetProperty("external_urls", out var eu) && eu.TryGetProperty("spotify", out var su)) np.Url = su.GetString() ?? "";
            if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                np.Artists = string.Join(", ", artists.EnumerateArray().Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : null).Where(s => !string.IsNullOrEmpty(s)));
            JsonElement images = default; bool hasImages = false;
            if (item.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
            {
                np.Album = album.TryGetProperty("name", out var aln) ? aln.GetString() ?? "" : "";
                hasImages = album.TryGetProperty("images", out images);
            }
            else if (item.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.Object)   // a podcast episode
            {
                np.Album = show.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
                if (np.Artists.Length == 0 && show.TryGetProperty("publisher", out var pub)) np.Artists = pub.GetString() ?? "";
                hasImages = show.TryGetProperty("images", out images);
            }
            if (hasImages && images.ValueKind == JsonValueKind.Array)
            {
                // The smallest image that is still >= 128px, else whatever is last (smallest).
                string? pick = null;
                foreach (var im in images.EnumerateArray())
                {
                    int w = im.TryGetProperty("width", out var wp) && wp.ValueKind == JsonValueKind.Number ? wp.GetInt32() : 0;
                    string? u = im.TryGetProperty("url", out var up) ? up.GetString() : null;
                    if (u == null) continue;
                    if (pick == null || w >= 128) pick = u;
                }
                np.AlbumArt = pick ?? "";
            }
            return np.Track.Length == 0 ? null : np;
        }

        // ---- who may see it ------------------------------------------------------------------

        /// <summary>May <paramref name="viewer"/> see <paramref name="owner"/>'s Spotify? Self always; signed-out never.</summary>
        public static bool CanSee(ulong owner, ulong viewer, Link link)
        {
            if (viewer == owner) return true;
            if (viewer == 0) return false;
            switch (link.Visibility)
            {
                case VisEveryone: return true;
                case VisNoOne: return false;
                default:
                {
                    var rel = FriendsDB.GetRelationships(owner)?.FirstOrDefault(r => r.PlayerID == viewer);
                    if (rel == null || rel.RelationshipType != RelationshipType.Friend) return false;
                    return link.Visibility == VisFriends || rel.Favorited != ReciprocalStatus.None;
                }
            }
        }

        /// <summary>The "listening to" payload for a viewer, or null when there is nothing they may see.</summary>
        public static async Task<object?> PayloadFor(ulong owner, ulong viewer)
        {
            var link = Get(owner);
            if (link == null || link.Broken || !CanSee(owner, viewer, link)) return null;
            var np = await Fetch(owner);
            if (np == null) return null;
            // Progress as of NOW (the cached sample may be up to 10 s old), capped at the end.
            long progress = np.ProgressMs + (np.IsPlaying ? (long)(DateTime.UtcNow - np.FetchedAt).TotalMilliseconds : 0);
            if (np.DurationMs > 0) progress = Math.Min(progress, np.DurationMs);
            return new
            {
                is_playing = np.IsPlaying,
                track = Clean(np.Track),
                artists = Clean(np.Artists),
                album = Clean(np.Album),
                album_art = np.AlbumArt,
                url = np.Url,
                track_id = np.TrackId,
                progress_ms = progress,
                duration_ms = np.DurationMs,
                as_of = DateTime.UtcNow
            };
        }

        /// <summary>Synchronous convenience for list rows: the cached sample only, never a Spotify call.</summary>
        public static object? CachedPayloadFor(ulong owner, ulong viewer)
        {
            var link = Get(owner);
            if (link == null || link.Broken || !CanSee(owner, viewer, link)) return null;
            if (!_cache.TryGetValue(owner, out var hit) || hit.np == null) { _ = Fetch(owner); return null; }
            var np = hit.np;
            long progress = np.ProgressMs + (np.IsPlaying ? (long)(DateTime.UtcNow - np.FetchedAt).TotalMilliseconds : 0);
            if (np.DurationMs > 0) progress = Math.Min(progress, np.DurationMs);
            return new { is_playing = np.IsPlaying, track = Clean(np.Track), artists = Clean(np.Artists), album = Clean(np.Album), album_art = np.AlbumArt, url = np.Url, track_id = np.TrackId, progress_ms = progress, duration_ms = np.DurationMs, as_of = DateTime.UtcNow };
        }
    }
}
