using System.Security.Cryptography;
using System.Text;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// RFC 6238 TOTP (the algorithm behind Google Authenticator / Authy / 1Password's codes) - standard HMAC-SHA1,
    /// 30-second step, 6 digits. No NuGet package: it's ~20 lines on top of the BCL's own HMACSHA1.
    /// </summary>
    public static class Totp
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        private const int Digits = 6;
        private const int StepSeconds = 30;

        /// <summary>A fresh 160-bit secret, base32-encoded (what an authenticator app wants typed in or scanned).</summary>
        public static string GenerateSecret()
        {
            var bytes = RandomNumberGenerator.GetBytes(20);
            return Base32Encode(bytes);
        }

        public static string Base32Encode(byte[] data)
        {
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int bits = 0, value = 0;
            foreach (byte b in data)
            {
                value = (value << 8) | b; bits += 8;
                while (bits >= 5) { sb.Append(Base32Alphabet[(value >> (bits - 5)) & 0x1F]); bits -= 5; }
            }
            if (bits > 0) sb.Append(Base32Alphabet[(value << (5 - bits)) & 0x1F]);
            return sb.ToString();
        }

        public static byte[] Base32Decode(string s)
        {
            s = (s ?? "").Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
            var outBytes = new List<byte>(s.Length * 5 / 8);
            int bits = 0, value = 0;
            foreach (char c in s)
            {
                int idx = Base32Alphabet.IndexOf(c);
                if (idx < 0) continue;   // skip anything an app/user might paste in (dashes, etc.)
                value = (value << 5) | idx; bits += 5;
                if (bits >= 8) { outBytes.Add((byte)((value >> (bits - 8)) & 0xFF)); bits -= 8; }
            }
            return outBytes.ToArray();
        }

        private static string Hotp(byte[] key, long counter)
        {
            var counterBytes = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);
            using var h = new HMACSHA1(key);
            byte[] hash = h.ComputeHash(counterBytes);
            int offset = hash[^1] & 0x0F;
            int code = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16) | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
            return (code % (int)Math.Pow(10, Digits)).ToString(new string('0', Digits));
        }

        /// <summary>True if `code` matches the secret at the current 30s step, or one step either side (clock drift).</summary>
        public static bool Verify(string base32Secret, string? code)
        {
            code = (code ?? "").Trim();
            if (code.Length != Digits || !code.All(char.IsDigit)) return false;
            byte[] key = Base32Decode(base32Secret);
            if (key.Length == 0) return false;
            long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
            for (long d = -1; d <= 1; d++)
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hotp(key, step + d)), Encoding.ASCII.GetBytes(code)))
                    return true;
            return false;
        }

        /// <summary>otpauth:// URI for a QR code, if the admin panel ever adds one - also readable as plain text to copy.</summary>
        public static string OtpAuthUri(string secret, string account, string issuer = "Campus Admin") =>
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";
    }
}
