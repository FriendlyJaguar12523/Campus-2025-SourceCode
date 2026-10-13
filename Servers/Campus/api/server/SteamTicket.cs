using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Offline verification of a Steam login's platform_auth ticket - the SteamID64 a login may be
    /// trusted as. Port of RecFlare auth src/steam-ticket.ts (reference server).
    ///
    /// A Steam login posts platform_auth = {"Ticket":"&lt;hex&gt;","AppId":"471710"}. The ticket's
    /// ownership section is signed by Steam's "System" RSA key (RSA-SHA1), so it verifies OFFLINE -
    /// no publisher Web API key (which only Rec Room has for app 471710), no network call - and the
    /// SteamID64 inside is one Steam itself vouched for. The posted platform_id field is NOT: it is
    /// whatever the client typed, and a SteamID64 is public (it's on every Steam profile). Before
    /// this, a cached_login with someone else's SteamID and account id and no device id minted a
    /// token for their account. Byte layout per DoctorMcKay's steam-appticket.
    /// </summary>
    public static class SteamTicket
    {
        // Steam's "System" public key (SPKI DER), from @doctormckay/steam-crypto system.pem.
        private const string SystemPublicKeySpki =
            "MIGdMA0GCSqGSIb3DQEBAQUAA4GLADCBhwKBgQDf7BrWLBBmLBc1OhSwfFkRf53T" +
            "2Ct64+AVzRkeRuh7h3SiGEYxqQMUeYKO6UWiSRKpI2hzic9pobFhRr3Bvr/WARvY" +
            "gdTckPv+T1JzZsuVcNfFjrocejN1oWI0Rrtgt4Bo+hOneoo3S57G9F1fOpn5nsQ6" +
            "6WOiu4gZKODnFMBCiQIBEQ==";

        public readonly record struct Result(bool Ok, ulong SteamId, uint AppId, string Reason);

        public readonly record struct Parsed(ulong SteamId, uint AppId, long ExpiresAtMs, int SignedStart, int SignedEnd, int SignatureAt);

        /// <summary>
        /// The SteamID64 a platform_auth payload proves, or Ok=false (with why) when it is missing,
        /// malformed, expired, or not signed by Steam. Only ever returns an id Steam signed.
        /// </summary>
        public static Result Verify(string? platformAuth, DateTimeOffset? now = null)
        {
            if (string.IsNullOrWhiteSpace(platformAuth)) return Fail("no Steam ticket");
            string? hex;
            try { hex = (string?)JObject.Parse(platformAuth)["Ticket"]; }
            catch { return Fail("platform_auth is not JSON"); }
            if (string.IsNullOrEmpty(hex)) return Fail("no Ticket in platform_auth");
            byte[] buf;
            try { buf = Convert.FromHexString(hex); }
            catch { return Fail("Ticket is not hex"); }
            var t = Parse(buf);
            if (t == null) return Fail("malformed Steam ticket");
            long nowMs = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            if (t.Value.ExpiresAtMs != 0 && t.Value.ExpiresAtMs < nowMs) return Fail("expired Steam ticket");
            if (!VerifySignature(buf, t.Value)) return Fail("Steam ticket signature does not verify");
            return new Result(true, t.Value.SteamId, t.Value.AppId, "");
        }

        private static Result Fail(string why) => new(false, 0, 0, why);

        /// <summary>The ticket's fields and signed range WITHOUT checking the signature (null = malformed).</summary>
        public static Parsed? Parse(byte[] buf)
        {
            int pos = 0, limit = buf.Length;
            uint U32() { uint v = BitConverter.ToUInt32(buf, pos); pos += 4; return v; }
            ushort U16() { ushort v = BitConverter.ToUInt16(buf, pos); pos += 2; return v; }
            ulong U64() { ulong v = BitConverter.ToUInt64(buf, pos); pos += 8; return v; }
            try
            {
                if (U32() == 20)
                {
                    // full ticket: GC token + session header come before the ownership ticket
                    pos += 8;                       // gcToken
                    pos += 8;                       // steamID (read from the ownership section instead)
                    U32();                          // tokenGenerated
                    if (U32() != 24) return null;   // session header length
                    pos += 8;                       // unknown1, unknown2
                    U32();                          // session external IP
                    pos += 4;                       // filler
                    U32();                          // client connection time
                    U32();                          // client connection count
                    if (U32() + pos != limit) return null;   // ownership-section length check
                }
                else pos -= 4;                      // a bare ownership ticket

                int ownStart = pos;
                int ownLen = (int)U32();            // includes itself
                if (ownStart + ownLen != limit && ownStart + ownLen + 128 != limit) return null;
                U32();                              // version
                ulong steamId = U64();
                uint appId = U32();
                U32(); U32(); U32(); U32();         // ownership external / internal IP, flags, generated
                long expiresAtMs = (long)U32() * 1000;
                int licenses = U16();
                for (int i = 0; i < licenses; i++) U32();
                int dlcs = U16();
                for (int i = 0; i < dlcs; i++)
                {
                    U32();                          // dlc appId
                    int dl = U16();
                    for (int j = 0; j < dl; j++) U32();
                }
                U16();                              // reserved
                if (pos + 128 != limit) return null;  // a signature is required
                return new Parsed(steamId, appId, expiresAtMs, ownStart, ownStart + ownLen, pos);
            }
            catch (ArgumentException) { return null; }      // ran off the end
            catch (IndexOutOfRangeException) { return null; }
        }

        public static bool VerifySignature(byte[] buf, Parsed t)
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(SystemPublicKeySpki), out _);
                return rsa.VerifyData(buf.AsSpan(t.SignedStart, t.SignedEnd - t.SignedStart),
                                      buf.AsSpan(t.SignatureAt, 128), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
            }
            catch { return false; }
        }
    }
}
