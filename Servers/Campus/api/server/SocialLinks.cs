using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Campus_live_rewrite.api.dbs;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Linked social accounts (Settings > Services): the player signs in to the service
    /// (OAuth 2 authorization code, Steam via OpenID), we keep only who they are there (id, handle,
    /// profile url) and a per-service "show on my profile" switch. Nothing is typed in by hand,
    /// so a handle on a profile is always a verified one.
    ///
    /// Each provider needs its own developer app; the credentials live ONLY in
    /// Data/social_apps.json (never in source):
    ///   { "youtube":  {"ClientId":"…apps.googleusercontent.com","ClientSecret":"…"},
    ///     "tiktok":   {"ClientId":"<client_key>","ClientSecret":"…"},
    ///     "x":        {"ClientId":"…","ClientSecret":"…"},
    ///     "twitch":   {"ClientId":"…","ClientSecret":"…"},
    ///     "github":   {"ClientId":"…","ClientSecret":"…"},
    ///     "reddit":   {"ClientId":"…","ClientSecret":"…"},
    ///     "instagram":{"ClientId":"…","ClientSecret":"…"},
    ///     "steam":    {"ApiKey":"…"} }          // optional: only for the persona name
    /// A provider with no entry shows "not set up on this server yet" on its card. The redirect
    /// URI to register with every app is https://camprec.net/api/site/social/{provider}/callback
    /// (override with "RedirectBase" at the top level of the file).
    /// Discord and Spotify keep their own link flows (DiscordController / SpotifyLink); this class
    /// only holds their "show on profile" switch and projects them into the profile rows.
    /// </summary>
    public static class SocialLinks
    {
        public sealed class Link
        {
            [LiteDB.BsonId] public string Id { get; set; } = "";     // "{account}|{provider}"
            public ulong AccountId { get; set; }
            public string Provider { get; set; } = "";
            public string ExternalId { get; set; } = "";
            public string Handle { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Url { get; set; } = "";
            public bool ShowOnProfile { get; set; } = true;
            public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
        }

        public sealed record Provider(string Key, string Label, bool Oauth);
        public static readonly Provider[] Providers =
        {
            new("discord",   "Discord",   false),   // own flow
            new("spotify",   "Spotify",   false),   // own flow
            new("youtube",   "YouTube",   true),
            new("tiktok",    "TikTok",    true),
            new("x",         "X",         true),
            new("twitch",    "Twitch",    true),
            new("steam",     "Steam",     true),    // OpenID, no app needed
            new("github",    "GitHub",    true),
            new("reddit",    "Reddit",    true),
            new("instagram", "Instagram", true),
        };
        public static Provider? Find(string key) => Providers.FirstOrDefault(p => p.Key == key);

        private sealed class AppCfg { public string ClientId { get; set; } = ""; public string ClientSecret { get; set; } = ""; public string ApiKey { get; set; } = ""; }
        private static readonly string ConfigPath = Path.Combine("Data", "social_apps.json");
        private static Dictionary<string, AppCfg> _cfg = new(); private static string _redirectBase = "https://gameauth.ddns.net"; private static DateTime _cfgRead;     // _redirectBase: the callback host registered with the services (NOT the site host, camprec.net)
        private static readonly Lazy<LiteDB.LiteDatabase> _db = new(() => new LiteDB.LiteDatabase(Path.Combine("Data", "DBs", "Socials.db")));
        private static LiteDB.ILiteCollection<Link> Links()
        {
            var c = _db.Value.GetCollection<Link>("links");
            c.EnsureIndex(x => x.AccountId);
            return c;
        }
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
        // state -> (account, provider, pkce verifier), 10 minutes, one use
        private static readonly ConcurrentDictionary<string, (ulong account, string provider, string verifier, DateTime at)> _states = new();

        private static void ReadConfig()
        {
            if (DateTime.UtcNow - _cfgRead < TimeSpan.FromMinutes(1)) return;
            _cfgRead = DateTime.UtcNow;
            try
            {
                if (!File.Exists(ConfigPath)) { _cfg = new(); return; }
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                var map = new Dictionary<string, AppCfg>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name.Equals("RedirectBase", StringComparison.OrdinalIgnoreCase)) { _redirectBase = (p.Value.GetString() ?? _redirectBase).TrimEnd('/'); continue; }
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;
                    var a = new AppCfg();
                    foreach (var f in p.Value.EnumerateObject())
                    {
                        string v = f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString() ?? "" : "";
                        if (f.Name.Equals("ClientId", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("ClientKey", StringComparison.OrdinalIgnoreCase)) a.ClientId = v;
                        else if (f.Name.Equals("ClientSecret", StringComparison.OrdinalIgnoreCase)) a.ClientSecret = v;
                        else if (f.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)) a.ApiKey = v;
                    }
                    map[p.Name.ToLowerInvariant()] = a;
                }
                _cfg = map;
            }
            catch (Exception ex) { Console.WriteLine($"[social] could not read {ConfigPath}: {ex.Message}"); }
        }

        private static AppCfg? Cfg(string provider)
        {
            ReadConfig();
            return _cfg.TryGetValue(provider, out var c) && !string.IsNullOrWhiteSpace(c.ClientId) && !string.IsNullOrWhiteSpace(c.ClientSecret) ? c : null;
        }
        private static string SteamApiKey() { ReadConfig(); return _cfg.TryGetValue("steam", out var c) ? c.ApiKey : ""; }

        public static bool Configured(string provider) => provider == "steam" || Cfg(provider) != null;

        // ---- admin panel: the app credentials, editable without touching the VPS ----------------
        public static string RedirectUriFor(string provider) => RedirectUri(provider);

        /// <summary>What the admin panel shows: ids in the clear, secrets only as "set".</summary>
        public static List<object> AdminView()
        {
            _cfgRead = DateTime.MinValue; ReadConfig();
            var list = new List<object>();
            foreach (var p in Providers)
            {
                if (!p.Oauth) continue;
                _cfg.TryGetValue(p.Key, out var c);
                list.Add(new
                {
                    key = p.Key, label = p.Label,
                    clientId = c?.ClientId ?? "",
                    secretSet = !string.IsNullOrEmpty(c?.ClientSecret),
                    apiKeySet = !string.IsNullOrEmpty(c?.ApiKey),
                    usesApiKey = p.Key == "steam",
                    configured = Configured(p.Key),
                    redirectUri = RedirectUri(p.Key)
                });
            }
            return list;
        }

        /// <summary>Write one provider's credentials into Data/social_apps.json (blank secret = keep the stored one).</summary>
        public static bool AdminSave(string provider, string? clientId, string? clientSecret, string? apiKey, out string message)
        {
            message = "";
            var p = Find(provider);
            if (p == null || !p.Oauth) { message = "Unknown provider."; return false; }
            try
            {
                var root = new Dictionary<string, object>();
                if (File.Exists(ConfigPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                    foreach (var e in doc.RootElement.EnumerateObject())
                    {
                        if (e.Value.ValueKind == JsonValueKind.Object)
                        {
                            var d = new Dictionary<string, string>();
                            foreach (var f in e.Value.EnumerateObject()) d[f.Name] = f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString() ?? "" : "";
                            root[e.Name.ToLowerInvariant()] = d;
                        }
                        else root[e.Name] = e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString() ?? "" : "";
                    }
                }
                var cur = root.TryGetValue(provider, out var o) && o is Dictionary<string, string> dd ? dd : new Dictionary<string, string>();
                if (clientId != null) cur["ClientId"] = clientId.Trim();
                if (!string.IsNullOrWhiteSpace(clientSecret)) cur["ClientSecret"] = clientSecret.Trim();
                if (!string.IsNullOrWhiteSpace(apiKey)) cur["ApiKey"] = apiKey.Trim();
                if (clientId == "" ) { cur.Remove("ClientId"); cur.Remove("ClientSecret"); }
                if (apiKey == "") cur.Remove("ApiKey");
                root[provider] = cur;
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
                _cfgRead = DateTime.MinValue; ReadConfig();
                message = Configured(provider) ? $"{p.Label} sign-in is set up." : $"{p.Label} saved (still needs both a client id and a secret).";
                Console.WriteLine($"[social] admin updated {provider} credentials");
                return true;
            }
            catch (Exception ex) { message = ex.Message; return false; }
        }
        private static string RedirectUri(string provider) => $"{(provider == "steam" ? ConfigCampus.SiteUrl : _redirectBase)}/api/site/social/{provider}/callback";

        public static Link? Get(ulong account, string provider) => Links().FindById($"{account}|{provider}");
        public static List<Link> For(ulong account) => Links().Find(x => x.AccountId == account).ToList();

        public static void Unlink(ulong account, string provider)
        {
            Links().Delete($"{account}|{provider}");
            Console.WriteLine($"[social] {account} unlinked {provider}");
        }

        /// <summary>The per-service "show on my profile" switch. Discord/Spotify rows are created on demand.</summary>
        public static void SetShow(ulong account, string provider, bool show)
        {
            var l = Get(account, provider);
            if (l == null)
            {
                if (provider != "discord" && provider != "spotify") return;
                l = new Link { Id = $"{account}|{provider}", AccountId = account, Provider = provider };
            }
            l.ShowOnProfile = show;
            Links().Upsert(l);
        }
        public static bool Shows(ulong account, string provider) => Get(account, provider)?.ShowOnProfile ?? true;

        private static void Save(ulong account, string provider, string externalId, string handle, string display, string url)
        {
            var existing = Get(account, provider);
            var l = new Link
            {
                Id = $"{account}|{provider}", AccountId = account, Provider = provider,
                ExternalId = externalId, Handle = handle, DisplayName = display, Url = url,
                ShowOnProfile = existing?.ShowOnProfile ?? true, LinkedAt = DateTime.UtcNow
            };
            Links().Upsert(l);
            Console.WriteLine($"[social] {account} linked {provider} as '{handle}' ({externalId})");
        }

        // ---- start -----------------------------------------------------------------------------

        private static string NewState(ulong account, string provider, string verifier = "")
        {
            foreach (var kv in _states) if (DateTime.UtcNow - kv.Value.at > TimeSpan.FromMinutes(10)) _states.TryRemove(kv.Key, out _);
            string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _states[state] = (account, provider, verifier, DateTime.UtcNow);
            return state;
        }

        private static string Q(params (string k, string v)[] kv) =>
            string.Join("&", kv.Select(p => Uri.EscapeDataString(p.k) + "=" + Uri.EscapeDataString(p.v)));

        /// <summary>The service's sign-in URL for this account, or null when the provider isn't set up.</summary>
        public static string? StartUrl(ulong account, string provider)
        {
            var p = Find(provider);
            if (p == null || !p.Oauth) return null;
            string redirect = RedirectUri(provider);
            if (provider == "steam")
            {
                // Plain return_to (no query string) and a realm with a trailing slash - the exact
                // shape every Steam OpenID library sends. Steam's new sign-in page answered
                // "Something went wrong while attempting to sign you in" to the ?state= variant.
                // The account is taken from the website cookie on the way back instead.
                return "https://steamcommunity.com/openid/login?" + Q(
                    ("openid.ns", "http://specs.openid.net/auth/2.0"), ("openid.mode", "checkid_setup"),
                    ("openid.return_to", redirect), ("openid.realm", ConfigCampus.SiteUrl + "/"),
                    ("openid.identity", "http://specs.openid.net/auth/2.0/identifier_select"),
                    ("openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select"));
            }
            var cfg = Cfg(provider);
            if (cfg == null) return null;
            switch (provider)
            {
                case "youtube":
                    return "https://accounts.google.com/o/oauth2/v2/auth?" + Q(("client_id", cfg.ClientId), ("redirect_uri", redirect), ("response_type", "code"),
                        ("scope", "https://www.googleapis.com/auth/youtube.readonly"), ("state", NewState(account, provider)), ("access_type", "online"), ("prompt", "select_account"));
                case "tiktok":
                    return "https://www.tiktok.com/v2/auth/authorize/?" + Q(("client_key", cfg.ClientId), ("redirect_uri", redirect), ("response_type", "code"),
                        ("scope", "user.info.basic,user.info.profile"), ("state", NewState(account, provider)));
                case "x":
                {
                    string verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                    string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                    return "https://twitter.com/i/oauth2/authorize?" + Q(("client_id", cfg.ClientId), ("redirect_uri", redirect), ("response_type", "code"),
                        ("scope", "users.read tweet.read"), ("state", NewState(account, provider, verifier)), ("code_challenge", challenge), ("code_challenge_method", "S256"));
                }
                case "twitch":
                    return "https://id.twitch.tv/oauth2/authorize?" + Q(("client_id", cfg.ClientId), ("redirect_uri", redirect), ("response_type", "code"),
                        ("scope", ""), ("state", NewState(account, provider)), ("force_verify", "true"));
                case "github":
                    return "https://github.com/login/oauth/authorize?" + Q(("client_id", cfg.ClientId), ("redirect_uri", redirect), ("scope", "read:user"), ("state", NewState(account, provider)));
                case "reddit":
                    return "https://www.reddit.com/api/v1/authorize?" + Q(("client_id", cfg.ClientId), ("redirect_uri", redirect), ("response_type", "code"),
                        ("scope", "identity"), ("state", NewState(account, provider)), ("duration", "temporary"));
                case "instagram":
                    return "https://api.instagram.com/oauth/authorize?" + Q(("client_id", cfg.ClientId), ("redirect_uri", redirect), ("response_type", "code"),
                        ("scope", "instagram_business_basic"), ("state", NewState(account, provider)));
            }
            return null;
        }

        private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // ---- callback --------------------------------------------------------------------------

        /// <summary>Outcome: linked / expired / denied / inuse / error / unconfigured.</summary>
        public static async Task<string> Complete(string provider, IQueryCollection query, ulong cookieAccount = 0)
        {
            ulong account; string verifier = "";
            if (provider == "steam")
            {
                if (cookieAccount == 0) return "expired";   // not signed in to the website any more
                account = cookieAccount;
            }
            else
            {
                string state = query["state"].ToString();
                if (string.IsNullOrEmpty(state) || !_states.TryRemove(state, out var st) || st.provider != provider || DateTime.UtcNow - st.at > TimeSpan.FromMinutes(10))
                    return "expired";
                account = st.account; verifier = st.verifier;
            }
            try
            {
                string externalId = "", handle = "", display = "", url = "";
                if (provider == "steam")
                {
                    if (query["openid.mode"].ToString() == "cancel") return "denied";
                    // Verify with Steam: replay every openid.* parameter with mode=check_authentication.
                    var form = new Dictionary<string, string>();
                    foreach (var kv in query) if (kv.Key.StartsWith("openid.")) form[kv.Key] = kv.Value.ToString();
                    form["openid.mode"] = "check_authentication";
                    using var vreq = new HttpRequestMessage(HttpMethod.Post, "https://steamcommunity.com/openid/login") { Content = new FormUrlEncodedContent(form) };
                    vreq.Headers.UserAgent.ParseAdd("Campus/1.0 (+https://camprec.net)");
                    var vr = await _http.SendAsync(vreq);
                    string vb = await vr.Content.ReadAsStringAsync();
                    if (!vb.Contains("is_valid:true")) { Console.WriteLine($"[social] steam check_authentication for {account} failed: {vb.Trim()}"); return "error"; }
                    string claimed = query["openid.claimed_id"].ToString();
                    externalId = claimed.TrimEnd('/').Split('/').LastOrDefault() ?? "";
                    if (!ulong.TryParse(externalId, out _)) return "error";
                    url = "https://steamcommunity.com/profiles/" + externalId;
                    handle = externalId; display = "Steam";
                    string key = SteamApiKey();
                    if (key.Length > 0)
                    {
                        try
                        {
                            var sr = await _http.GetStringAsync($"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={externalId}");
                            using var sd = JsonDocument.Parse(sr);
                            var pl = sd.RootElement.GetProperty("response").GetProperty("players");
                            if (pl.GetArrayLength() > 0)
                            {
                                var p0 = pl[0];
                                display = p0.TryGetProperty("personaname", out var pn) ? pn.GetString() ?? "" : "";
                                handle = display.Length > 0 ? display : externalId;
                                if (p0.TryGetProperty("profileurl", out var pu)) url = pu.GetString() ?? url;
                            }
                        }
                        catch (Exception ex) { Console.WriteLine($"[social] steam summary for {account}: {ex.Message}"); }
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(query["error"].ToString())) return "denied";
                    string code = query["code"].ToString();
                    if (string.IsNullOrEmpty(code)) return "denied";
                    var cfg = Cfg(provider);
                    if (cfg == null) return "unconfigured";
                    string redirect = RedirectUri(provider);
                    string token = await ExchangeCode(provider, cfg, code, redirect, verifier);
                    if (token.Length == 0) return "error";
                    (externalId, handle, display, url) = await Identity(provider, cfg, token);
                    if (externalId.Length == 0) return "error";
                }

                // One service account per Campus account.
                var other = Links().FindOne(l => l.Provider == provider && l.ExternalId == externalId && l.AccountId != account);
                if (other != null) return "inuse";
                Save(account, provider, externalId, handle, display, url);
                SiteNotifications.ProfileChanged(account);
                return "linked";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[social] {provider} link for {account} failed: {ex.Message}");
                return "error";
            }
        }

        private static async Task<string> ExchangeCode(string provider, AppCfg cfg, string code, string redirect, string verifier)
        {
            string tokenUrl = provider switch
            {
                "youtube" => "https://oauth2.googleapis.com/token",
                "tiktok" => "https://open.tiktokapis.com/v2/oauth/token/",
                "x" => "https://api.twitter.com/2/oauth2/token",
                "twitch" => "https://id.twitch.tv/oauth2/token",
                "github" => "https://github.com/login/oauth/access_token",
                "reddit" => "https://www.reddit.com/api/v1/access_token",
                "instagram" => "https://api.instagram.com/oauth/access_token",
                _ => ""
            };
            if (tokenUrl.Length == 0) return "";
            var form = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect };
            using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.UserAgent.ParseAdd("Campus/1.0 (+https://camprec.net)");
            switch (provider)
            {
                case "tiktok":
                    form["client_key"] = cfg.ClientId; form["client_secret"] = cfg.ClientSecret; break;
                case "x":
                    form["client_id"] = cfg.ClientId; form["code_verifier"] = verifier;
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.ClientId + ":" + cfg.ClientSecret)));
                    break;
                case "reddit":
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.ClientId + ":" + cfg.ClientSecret)));
                    break;
                default:
                    form["client_id"] = cfg.ClientId; form["client_secret"] = cfg.ClientSecret; break;
            }
            req.Content = new FormUrlEncodedContent(form);
            var resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) { Console.WriteLine($"[social] {provider} token exchange: HTTP {(int)resp.StatusCode} {Trunc(body)}"); return ""; }
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String) return at.GetString() ?? "";
                if (root.TryGetProperty("data", out var d) && d.TryGetProperty("access_token", out var at2)) return at2.GetString() ?? "";
            }
            catch { }
            Console.WriteLine($"[social] {provider} token exchange: no access_token in {Trunc(body)}");
            return "";
        }

        private static string Trunc(string s) => s.Length > 300 ? s[..300] : s;

        private static async Task<(string id, string handle, string display, string url)> Identity(string provider, AppCfg cfg, string token)
        {
            string apiUrl = provider switch
            {
                "youtube" => "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true",
                "tiktok" => "https://open.tiktokapis.com/v2/user/info/?fields=open_id,display_name,username,profile_deep_link",
                "x" => "https://api.twitter.com/2/users/me",
                "twitch" => "https://api.twitch.tv/helix/users",
                "github" => "https://api.github.com/user",
                "reddit" => "https://oauth.reddit.com/api/v1/me",
                "instagram" => "https://graph.instagram.com/me?fields=id,username",
                _ => ""
            };
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.UserAgent.ParseAdd("Campus/1.0 (+https://camprec.net)");
            if (provider == "twitch") req.Headers.Add("Client-Id", cfg.ClientId);
            var resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) { Console.WriteLine($"[social] {provider} identity: HTTP {(int)resp.StatusCode} {Trunc(body)}"); return ("", "", "", ""); }
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            switch (provider)
            {
                case "youtube":
                {
                    if (!r.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return ("", "", "", "");
                    var ch = items[0]; var sn = ch.GetProperty("snippet");
                    string id = S(ch, "id"), title = S(sn, "title"), custom = S(sn, "customUrl");
                    string handle = custom.Length > 0 ? custom.TrimStart('@') : title;
                    string url = custom.Length > 0 ? "https://www.youtube.com/" + (custom.StartsWith('@') ? custom : "@" + custom) : "https://www.youtube.com/channel/" + id;
                    return (id, handle, title, url);
                }
                case "tiktok":
                {
                    var u = r.GetProperty("data").GetProperty("user");
                    string id = S(u, "open_id"), un = S(u, "username"), dn = S(u, "display_name"), link = S(u, "profile_deep_link");
                    string handle = un.Length > 0 ? un : dn;
                    string url = un.Length > 0 ? "https://www.tiktok.com/@" + un : link;
                    return (id, handle, dn, url);
                }
                case "x":
                {
                    var d = r.GetProperty("data");
                    string un = S(d, "username");
                    return (S(d, "id"), un, S(d, "name"), "https://x.com/" + un);
                }
                case "twitch":
                {
                    var arr = r.GetProperty("data"); if (arr.GetArrayLength() == 0) return ("", "", "", "");
                    var d = arr[0]; string login = S(d, "login");
                    return (S(d, "id"), S(d, "display_name").Length > 0 ? S(d, "display_name") : login, S(d, "display_name"), "https://www.twitch.tv/" + login);
                }
                case "github":
                {
                    string login = S(r, "login");
                    return (r.TryGetProperty("id", out var gid) ? gid.GetRawText() : "", login, S(r, "name"), S(r, "html_url").Length > 0 ? S(r, "html_url") : "https://github.com/" + login);
                }
                case "reddit":
                {
                    string name = S(r, "name");
                    return (S(r, "id"), name, name, "https://www.reddit.com/user/" + name);
                }
                case "instagram":
                {
                    string un = S(r, "username");
                    return (S(r, "id"), un, un, "https://www.instagram.com/" + un);
                }
            }
            return ("", "", "", "");
        }

        // ---- profile projection ------------------------------------------------------------------

        /// <summary>The chips a profile shows: every linked service whose switch is on, in Providers order.</summary>
        public static List<object> ProfileRows(ulong account, Classes.db_class.player_extra? extra)
        {
            var rows = new List<object>();
            var links = For(account).ToDictionary(l => l.Provider, l => l);
            foreach (var p in Providers)
            {
                links.TryGetValue(p.Key, out var l);
                if (p.Key == "discord")
                {
                    string dn = extra?.DiscordUsername ?? "";
                    if ((extra?.DiscordId ?? 0) == 0 || dn.Length == 0 || l?.ShowOnProfile == false) continue;
                    rows.Add(new { platform = "discord", label = "Discord", handle = dn, url = "" });
                    continue;
                }
                if (p.Key == "spotify")
                {
                    var sp = SpotifyLink.Get(account);
                    if (sp == null || sp.Broken || string.IsNullOrEmpty(sp.ProfileUrl) || l?.ShowOnProfile == false) continue;
                    rows.Add(new { platform = "spotify", label = "Spotify", handle = string.IsNullOrEmpty(sp.DisplayName) ? "Spotify" : sp.DisplayName, url = sp.ProfileUrl });
                    continue;
                }
                if (l == null || !l.ShowOnProfile || l.Handle.Length == 0) continue;
                rows.Add(new { platform = p.Key, label = p.Label, handle = l.Handle, url = l.Url });
            }
            return rows;
        }

        /// <summary>The Services cards: one entry per provider with its state for the signed-in account.</summary>
        public static List<object> Status(ulong account, Classes.db_class.player_extra? extra)
        {
            var links = For(account).ToDictionary(l => l.Provider, l => l);
            var list = new List<object>();
            foreach (var p in Providers)
            {
                links.TryGetValue(p.Key, out var l);
                bool linked; string handle = "", url = "";
                if (p.Key == "discord") { linked = (extra?.DiscordId ?? 0) != 0; handle = extra?.DiscordUsername ?? ""; }
                else if (p.Key == "spotify") { var sp = SpotifyLink.Get(account); linked = sp != null && !sp.Broken; handle = sp?.DisplayName ?? ""; url = sp?.ProfileUrl ?? ""; }
                else { linked = l != null && l.Handle.Length > 0; handle = l?.Handle ?? ""; url = l?.Url ?? ""; }
                list.Add(new
                {
                    key = p.Key, label = p.Label, oauth = p.Oauth,
                    configured = p.Oauth ? Configured(p.Key) : true,
                    linked, handle, url,
                    show = l?.ShowOnProfile ?? true
                });
            }
            return list;
        }
    }
}
