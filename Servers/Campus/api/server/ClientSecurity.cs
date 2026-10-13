using System.Security.Cryptography;
using System.Text;
using JWT;
using JWT.Algorithms;
using JWT.Exceptions;
using JWT.Serializers;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server;

public class ClientSecurity
{

    public static string key = ConfigCampus.Key;

    // ---- in-game token kill-switch -----------------------------------------------------------
    // A game token minted before ConfigCampus.GameTokenValidFrom is dead (see DecodeToken). The
    // epoch is persisted so a restart doesn't un-revoke; load it at boot, set it from the admin
    // "Expire all game tokens" button.
    private static readonly string GameTokenEpochFile =
        System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "game_token_valid_from.txt");

    public static void LoadGameTokenRevocation()
    {
        try
        {
            if (System.IO.File.Exists(GameTokenEpochFile) &&
                long.TryParse(System.IO.File.ReadAllText(GameTokenEpochFile).Trim(), out var v))
                ConfigCampus.GameTokenValidFrom = v;
        }
        catch (Exception ex) { Console.WriteLine($"[token-revoke] load skipped: {ex.Message}"); }
        Console.WriteLine($"[token-revoke] in-game tokens valid from epoch {ConfigCampus.GameTokenValidFrom}");
    }

    /// <summary>Revoke EVERY in-game bearer token issued before now. Returns the new epoch.</summary>
    public static long RevokeAllGameTokens()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ConfigCampus.GameTokenValidFrom = now;
        try { System.IO.File.WriteAllText(GameTokenEpochFile, now.ToString()); }
        catch (Exception ex) { Console.WriteLine($"[token-revoke] persist FAILED: {ex.Message}"); }
        Console.WriteLine($"[token-revoke] ALL in-game tokens revoked - valid-from set to {now}");
        return now;
    }

    public static string CreateToken(ulong userId, List<string> roles, string request_url, Platforms platform)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object>
        {
            // MUST be a string. As a raw ulong this serializes to `"sub":2` (a JSON
            // number); the client casts the sub claim to String while building the
            // local Player, and an Int64 there throws InvalidCastException inside
            // Player.Awake(). Verified fix - that exception went to zero and stayed
            // there. The Photon token builders below already used .ToString().
            { "sub", userId.ToString() },
            { "role", roles },
            { "iat", now.ToUnixTimeSeconds() },
            { "iss", $"{request_url}" },
            { "client_id", $"recnet" },
            // 12 h (ConfigCampus.GameTokenHours). The client re-authenticates from its cached
            // login when a token expires, so a bounded lifetime costs the player nothing and stops
            // a leaked token being usable indefinitely.
            { "exp", now.AddHours(ConfigCampus.GameTokenHours).ToUnixTimeSeconds() },
            { "platform", platform },
            // Surface stamp: this is an IN-GAME token. Validators reject it on the website
            // (SiteController requires "website") so a game token can't be used as a site
            // session. See DecodeToken's surface enforcement + AuthHelper / SiteController.
            { "surface", "game" }
        };

        // Mic ban: the rn.privilege claim ["BanVChat"], absent otherwise (reference: packages/jwt).
        // A timed ban ends the token when the ban ends, so the client re-authenticates then and
        // the ban lifts on time; a permanent one keeps the normal lifetime.
        try
        {
            var (privileges, firstEnds) = Campus_live_rewrite.api.dbs.PlayerDB.TokenPrivileges(userId);
            if (privileges.Count > 0)
            {
                payload["rn.privilege"] = privileges;
                if (firstEnds != null && firstEnds.Value.ToUnixTimeSeconds() + 5 < (long)payload["exp"])
                    payload["exp"] = firstEnds.Value.ToUnixTimeSeconds() + 5;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[token] chat-ban claim for {userId} skipped: {ex.Message}"); }

        IJwtAlgorithm algorithm = new HMACSHA256Algorithm();
        IJsonSerializer serializer = new JsonNetSerializer();
        IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
        IJwtEncoder encoder = new JwtEncoder(algorithm, serializer, urlEncoder);

        return encoder.Encode(payload, key);
    }

    public static string Createphotonacesstoken(ulong userId)
    {
        var now = DateTimeOffset.UtcNow;
        var heartbeat = HeartbeatDB.GetPlayerHeartbeat(userId);
        // Null-safe: if the player isn't in a room yet, heartbeat.roomInstance is null.
        // Dereferencing `.photonRoomIdYeee` on null throws BEFORE the `??` fallback runs.
        string photonRoomId = heartbeat?.roomInstance?.photonRoomIdYeee ?? $"solo-{userId}";
        var payload = new Dictionary<string, object>
        {
            { "sub", userId.ToString() },
            { "scope", "makerpen" },
            { "aud", photonRoomId },
        };

        IJwtAlgorithm algorithm = new HMACSHA256Algorithm();
        IJsonSerializer serializer = new JsonNetSerializer();
        IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
        IJwtEncoder encoder = new JwtEncoder(algorithm, serializer, urlEncoder);

        return encoder.Encode(payload, key);
    }

    /// <summary>
    /// Photon auth token. The room it is scoped to (the "aud" claim) MUST match the
    /// photonRoomId handed back by connection-info - if the token says one room and the
    /// client connects to another, Photon custom auth rejects it and the load dies.
    /// Pass photonRoomOverride when answering about a specific instance.
    /// </summary>
    public static string Createphotonauthtoken(ulong userId, string? photonRoomOverride = null)
    {
        var now = DateTimeOffset.UtcNow;
        var heartbeat = HeartbeatDB.GetPlayerHeartbeat(userId);
        // Null-safe (see comment on Createphotonacesstoken).
        string photonRoomId = photonRoomOverride
                              ?? heartbeat?.roomInstance?.photonRoomIdYeee
                              ?? $"solo-{userId}";
        var payload = new Dictionary<string, object>
        {
            { "sub", userId.ToString() },
            { "rn.platid", "76561199000000000" },
            { "rn.plat", "0" },
            { "rn.deviceclass", "2" },
            { "rn.env", "prod" },
            // A real, numeric expiry. It was the hardcoded STRING "1754316353" (Aug 2025), which
            // nothing checked - a Photon token, once copied, worked forever. CampusNet now refuses
            // a numeric exp that has passed. 24 h outlives any session; the client fetches a fresh
            // token with every matchmake / connection-info anyway.
            { "exp", now.AddHours(24).ToUnixTimeSeconds() },
            { "aud", photonRoomId },
        };

        IJwtAlgorithm algorithm = new HMACSHA256Algorithm();
        IJsonSerializer serializer = new JsonNetSerializer();
        IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
        IJwtEncoder encoder = new JwtEncoder(algorithm, serializer, urlEncoder);

        return encoder.Encode(payload, key);
    }

    public static string RefreshToken(string temp_val, ulong accid, int platform, ulong platform_id, ulong ticketid, string request_url, Platforms currentplatform)
    {
        var account = PlayerDB.getfullaccount_token(accid, ticketid, 0);
        if (account == null)
            return "";

        List<string> roles = new List<string> { "gameclient" };
        if ((account.dev_Flag & DevFlag.dev) == DevFlag.dev)
        {
            roles.Add("developer");
            roles.Add("moderator");
            roles.Add("screenshare");
        }
        if (account.player.Id == 314)
        {
            roles.Add("developer");
            roles.Add("moderator");
            roles.Add("screenshare");
        }

        return CreateToken(accid, roles, request_url, currentplatform);
    }


    // ttl defaults to 30 days (the game client's long-lived token). The ADMIN web login passes a
    // short ttl (1 hour) so the admin session expires and must be re-minted every hour.
    //
    // `surface` stamps which surface the token belongs to ("website" / "admin"); leave it null
    // for the launcher / Discord-OAuth callers, whose token is handed to the game client or set
    // as the admin cookie and must keep working on both (validators only REJECT specific
    // surfaces, they don't require one unless the surface is website). See DecodeToken.
    public static string RefreshToken_Web(ulong userId, TimeSpan? ttl = null, string? surface = null)
    {
        var account = PlayerDB.GetFullAccount(userId);
        if (account == null)
            return "";

        List<string> roles = new List<string> { "gameclient" };
        if ((account.dev_Flag & DevFlag.dev) == DevFlag.dev)
        {
            roles.Add("developer");
            roles.Add("moderator");
            roles.Add("screenshare");
        }

        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object>
        {
            // String, same reason as CreateToken above - a refreshed token has to
            // carry the same claim shape as the original.
            { "sub", userId.ToString() },
            { "role", roles },
            { "iat", now.ToUnixTimeSeconds() },
            { "exp", now.Add(ttl ?? TimeSpan.FromDays(30)).ToUnixTimeSeconds() },
            { "iss", "https://gameauth.ddns.net" }
        };
        if (!string.IsNullOrEmpty(surface))
            payload["surface"] = surface;

        string key = ClientSecurity.key;
        IJwtAlgorithm algorithm = new HMACSHA256Algorithm();
        IJsonSerializer serializer = new JsonNetSerializer();
        IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
        IJwtEncoder encoder = new JwtEncoder(algorithm, serializer, urlEncoder);
        string tokenString = encoder.Encode(payload, key);

        return tokenString;
    }
    public static string HashPassword(string password)
    {
        using (var rng = new RNGCryptoServiceProvider())
        {
            byte[] salt = new byte[16];
            rng.GetBytes(salt);

            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256))
            {
                byte[] hash = pbkdf2.GetBytes(32);
                byte[] hashBytes = new byte[48];
                Array.Copy(salt, 0, hashBytes, 0, 16);
                Array.Copy(hash, 0, hashBytes, 16, 32);
                return Convert.ToBase64String(hashBytes);
            }
        }
    }

    public static bool VerifyPassword(string enteredPassword, string storedHash)
    {
        // Crash-safe: a malformed/legacy stored value (not base64, or not the 48-byte
        // salt+hash layout) must fail cleanly, not throw and 500 the login. The legacy-SHA256
        // fallback in GetAccountPassword then gets its chance to upgrade the row.
        if (string.IsNullOrEmpty(storedHash)) return false;

        byte[] hashBytes;
        try { hashBytes = Convert.FromBase64String(storedHash); }
        catch { return false; }
        if (hashBytes.Length != 48) return false;

        byte[] salt = new byte[16];
        Array.Copy(hashBytes, 0, salt, 0, 16);

        using (var pbkdf2 = new Rfc2898DeriveBytes(enteredPassword, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            byte[] hash = pbkdf2.GetBytes(32);
            byte[] stored = new byte[32];
            Array.Copy(hashBytes, 16, stored, 0, 32);
            // Constant-time compare: a byte-by-byte early return leaks, via timing, how much of
            // the hash matched, which is a foothold for recovering it. FixedTimeEquals doesn't.
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hash, stored);
        }
    }

    // requireSurface: the token's "surface" claim MUST equal this (used by the website, which
    //   accepts ONLY website-minted tokens). rejectSurfaces: the token is refused if its
    //   surface is one of these (used by game/admin to keep website tokens out). A token with
    //   no surface claim (legacy, or the launcher/Discord tokens) passes rejectSurfaces but
    //   fails requireSurface - so only the website is strict, which is the surface that was
    //   getting cross-used. Both default to no-op so the ~dozens of existing single-arg callers
    //   are unaffected.
    public static string DecodeToken(string token, string? requireSurface = null, string[]? rejectSurfaces = null)
    {
        if (string.IsNullOrEmpty(token))
            return "error_token_not_found";

        string tokenString = token.Replace("Bearer ", "");

        try
        {
            IJsonSerializer serializer = new JsonNetSerializer();
            IDateTimeProvider provider = new UtcDateTimeProvider();
            IJwtValidator validator = new JwtValidator(serializer, provider);
            IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
            IJwtAlgorithm algorithm = new HMACSHA256Algorithm();
            IJwtDecoder decoder = new JwtDecoder(serializer, validator, urlEncoder, algorithm);

            var jsonPayload = decoder.Decode(tokenString, key, verify: true);
            var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonPayload);

            // Surface isolation: keep a token minted for one surface from being accepted on
            // another. A WEBSITE token (surface="website") is valid ONLY where the caller
            // explicitly requires "website" (SiteController). EVERY other decode path - the
            // website validator aside - refuses it, including the many game endpoints that call
            // DecodeToken directly instead of through AuthHelper. Game/admin tokens and legacy
            // no-surface tokens pass unless a caller adds them to rejectSurfaces (admin also
            // rejects "game").
            {
                string? surface = payload.TryGetValue("surface", out var sfObj) ? sfObj?.ToString() : null;
                if (requireSurface != null)
                {
                    if (surface != requireSurface)
                        return "error_token_wrong_surface";
                }
                else
                {
                    // No specific surface required => a game / admin / generic context. Website
                    // tokens are never welcome here.
                    // CampusStudio tokens work on StudioController only, launcher tokens on LauncherController only.
                    if (surface == "website" || surface == "studio" || surface == "launcher")
                        return "error_token_wrong_surface";
                    if (rejectSurfaces != null && surface != null && rejectSurfaces.Contains(surface))
                        return "error_token_wrong_surface";
                }
            }

            // In-game token kill-switch: a game token minted before the revoke epoch is dead, so
            // the admin "Expire all game tokens" button logs every player out. Scoped to game
            // tokens (surface "game") so it never touches website/admin sessions. A game token
            // with no iat is treated as pre-epoch while a revoke is active.
            {
                string? surfaceForRevoke = payload.TryGetValue("surface", out var sf2) ? sf2?.ToString() : null;
                if (surfaceForRevoke == "game" && ConfigCampus.GameTokenValidFrom > 0)
                {
                    long iat = payload.TryGetValue("iat", out var iatObj) ? Convert.ToInt64(iatObj) : 0;
                    if (iat < ConfigCampus.GameTokenValidFrom)
                        return "error_token_revoked";
                }
            }

            if (payload.TryGetValue("exp", out var expObj))
            {
                var expUnix = Convert.ToInt64(expObj);
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (nowUnix > expUnix)
                {
                    return "error_token_expired";
                }
            }
            else
            {
                return "error_token_no_expiration";
            }


            if (payload.TryGetValue("sub", out var subObj))
            {
                if (ulong.TryParse(subObj.ToString(), out ulong userId))
                {
                    var playerData = PlayerDB.GetAccountById(userId);
                    if (playerData != null)
                    {
                        return JsonConvert.SerializeObject(playerData);
                    }
                    else
                    {
                        Console.WriteLine($"player not found: {userId}");
                        return "error_token_user_not_found";
                    }
                }
                else
                {
                    return "error_token_invalid_sub";
                }
            }
            else
            {
                return "error_token_no_sub";
            }
        }
        catch (SignatureVerificationException)
        {
            return "error_token_tampered";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error decoding token: {ex.Message}");
            return "error_token";
        }
    }


    public static byte[] GetHash(string inputString)
    {
        using (HashAlgorithm algorithm = SHA256.Create())
            return algorithm.ComputeHash(Encoding.UTF8.GetBytes(inputString));
    }

    public static string GetHashString(string inputString)
    {
        StringBuilder sb = new StringBuilder();
        foreach (byte b in GetHash(inputString))
            sb.Append(b.ToString("X2"));

        return sb.ToString();
    }

    private static Random random = new Random();

    public static string RandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }
}