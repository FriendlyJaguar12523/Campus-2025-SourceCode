using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace CampusNet.Protocol;

/// <summary>
/// The client's InitEncryption handshake: Diffie-Hellman over the 768-bit Oakley group (the client's
/// public key is 96 bytes - see the capture), shared secret hashed with SHA-256 into an AES-256-CBC
/// key (zero IV, PKCS7). The exact byte order Photon feeds SHA-256 isn't verifiable from a capture, so
/// the first encrypted message is tried against every candidate key and the one that yields a
/// parseable body is kept (see TryDecryptAdaptive).
/// </summary>
public sealed class PhotonCrypto
{
    // RFC 2409 Oakley group 1 (768-bit).
    private static readonly BigInteger Prime = BigInteger.Parse(
        "0FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A3620FFFFFFFFFFFFFFFF",
        NumberStyles.HexNumber);
    public static int Generator = int.TryParse(Environment.GetEnvironmentVariable("CAMPUSNET_DH_GENERATOR"), out var g) ? g : 22;

    private readonly BigInteger _priv;
    public readonly byte[] PublicKey;          // 96 bytes, big-endian unsigned
    private BigInteger _shared;
    private byte[]? _aesKey;                   // locked in once a decrypt succeeds
    private List<byte[]>? _candidates;

    public PhotonCrypto()
    {
        var rnd = RandomNumberGenerator.GetBytes(64);
        _priv = new BigInteger(rnd, isUnsigned: true, isBigEndian: true) % (Prime - 2) + 1;
        var pub = BigInteger.ModPow(Generator, _priv, Prime);
        PublicKey = ToFixed(pub, 96);
    }

    public bool Ready => _aesKey != null || _candidates != null;

    /// <summary>Derive the candidate AES keys from the client's public key.</summary>
    public void DeriveSharedKey(byte[] clientPublicKey)
    {
        var a = new BigInteger(clientPublicKey, isUnsigned: true, isBigEndian: true);
        _shared = BigInteger.ModPow(a, _priv, Prime);
        var be = _shared.ToByteArray(isUnsigned: true, isBigEndian: true);
        var le = _shared.ToByteArray(isUnsigned: true, isBigEndian: false);
        var leSigned = _shared.ToByteArray(isUnsigned: false, isBigEndian: false);
        var be96 = ToFixed(_shared, 96);
        _candidates = new List<byte[]>();
        foreach (var bytes in new[] { be, be96, le, leSigned })
        {
            var k = SHA256.HashData(bytes);
            if (!_candidates.Any(c => c.AsSpan().SequenceEqual(k))) _candidates.Add(k);
        }
        _aesKey = null;
    }

    private static byte[] ToFixed(BigInteger v, int len)
    {
        var raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == len) return raw;
        var outp = new byte[len];
        if (raw.Length > len) Buffer.BlockCopy(raw, raw.Length - len, outp, 0, len);
        else Buffer.BlockCopy(raw, 0, outp, len - raw.Length, raw.Length);
        return outp;
    }

    private static byte[] AesRun(byte[] key, byte[] data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = new byte[16]; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        using var t = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return t.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>Decrypt an encrypted message body; on first use, pick the candidate key that yields a body `validate` accepts.</summary>
    public byte[]? TryDecryptAdaptive(byte[] cipher, Func<byte[], bool> validate)
    {
        if (_aesKey != null)
        {
            try { return AesRun(_aesKey, cipher, false); } catch { return null; }
        }
        if (_candidates == null) return null;
        foreach (var k in _candidates)
        {
            byte[] plain;
            try { plain = AesRun(k, cipher, false); } catch { continue; }
            if (validate(plain)) { _aesKey = k; return plain; }
        }
        return null;
    }

    public byte[]? Encrypt(byte[] plain)
    {
        var k = _aesKey ?? _candidates?.FirstOrDefault();
        return k == null ? null : AesRun(k, plain, true);
    }
}
