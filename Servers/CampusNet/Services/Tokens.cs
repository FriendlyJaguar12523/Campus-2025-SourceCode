using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CampusNet.Services;

/// <summary>
/// Two tokens live here. (1) The Campus server's photonAuthToken - an HS256 JWT (sub = account id,
/// aud = photon room id) the client presents as custom-auth data on the name server; validated with the
/// shared key. (2) The "secret" this server mints for the master/game hops (Photon hands the client an
/// opaque secret after the first Authenticate and the client presents it on the next server).
/// </summary>
public static class Tokens
{
    // Must be the SAME key the Campus server signs with. It reads CAMPUS_KEY or Data/jwt_key.txt,
    // so this reads the env vars and then that same file (/opt/campus/Data/jwt_key.txt, override
    // with CAMPUSNET_JWT_KEY_FILE): one file rotates both servers together.
    public static string Key = Environment.GetEnvironmentVariable("CAMPUSNET_JWT_KEY")
                               ?? Environment.GetEnvironmentVariable("CAMPUS_KEY")
                               ?? KeyFromFile()
                               ?? "REDACTED";

    private static string? KeyFromFile()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("CAMPUSNET_JWT_KEY_FILE") ?? "/opt/campus/Data/jwt_key.txt";
            if (!File.Exists(path)) return null;
            var k = File.ReadAllText(path).Trim();
            return k.Length >= 32 ? k : null;
        }
        catch { return null; }
    }

    private static byte[] Hmac(string data) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(data));
    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        while (s.Length % 4 != 0) s += "=";
        return Convert.FromBase64String(s);
    }

    /// <summary>Validate the Campus JWT; returns (accountId, audience/room) or null.</summary>
    public static (string userId, string? aud)? ValidateCampusJwt(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var expected = B64Url(Hmac(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[2]))) return null;
            using var doc = JsonDocument.Parse(FromB64Url(parts[1]));
            var root = doc.RootElement;
            // A numeric exp that has passed is refused (older tokens carried exp as a string, or
            // none, and are left to the signature check alone).
            if (root.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number
                && e.TryGetInt64(out var exp) && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            string? sub = root.TryGetProperty("sub", out var s) ? s.ToString() : null;
            string? aud = root.TryGetProperty("aud", out var a) ? a.ToString() : null;
            return sub == null ? null : (sub, aud);
        }
        catch { return null; }
    }

    /// <summary>Mint the master/game "secret": userId|nick|expiry, HMAC-signed.</summary>
    public static string MintSecret(string userId, string nick)
    {
        long exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
        string body = $"{userId}|{nick}|{exp}";
        return B64Url(Encoding.UTF8.GetBytes(body)) + "." + B64Url(Hmac(body));
    }

    public static (string userId, string nick)? ValidateSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        var i = secret.IndexOf('.');
        if (i <= 0) return null;
        try
        {
            string body = Encoding.UTF8.GetString(FromB64Url(secret[..i]));
            if (B64Url(Hmac(body)) != secret[(i + 1)..]) return null;
            var f = body.Split('|');
            if (f.Length != 3 || !long.TryParse(f[2], out var exp) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            return (f[0], f[1]);
        }
        catch { return null; }
    }
}
