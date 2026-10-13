using System.Collections;
using CampusNet.Protocol;

namespace CampusNet.Services;

/// <summary>
/// Lets everyone in the room see a gift box's message and box type, not only its owner.
///
/// A box someone opens in a shared room is spawned over Photon (ev 29, prefab "(GiftBox)") with
/// its gift package in the spawn data as UTF-8 JSON. Every OTHER client builds the box from that
/// JSON in GiftBox.Awake and only fills it in (message, name, box variant) when the package's
/// IsSignatureValid is true - otherwise it logs "Bad signature for remote gift package ID" and
/// draws a plain unlabelled box. The owner's client sends the flag as false (it re-checks the
/// signature against Rec Room's own key, which a Campus-signed package can never pass), even
/// though the Campus server hands the package out with IsSignatureValid true.
///
/// So the flag is set on the way through: "IsSignatureValid":false becomes
/// "IsSignatureValid":true  - the same length (JSON allows the trailing space), so the length
/// prefixes of the custom-type blob around it stay valid and nothing is re-encoded.
/// CAMPUSNET_GIFTBOX_VALID=0 turns it off.
/// </summary>
public static class GiftBoxRelay
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CAMPUSNET_GIFTBOX_VALID") != "0";

    private static readonly byte[][] From =
    {
        "\"IsSignatureValid\":false"u8.ToArray(),
        "\"isSignatureValid\":false"u8.ToArray(),
        "\"issignaturevalid\":false"u8.ToArray(),
    };
    private static readonly byte[][] To =
    {
        "\"IsSignatureValid\":true "u8.ToArray(),
        "\"isSignatureValid\":true "u8.ToArray(),
        "\"issignaturevalid\":true "u8.ToArray(),
    };

    // ---- capture (diagnostics) ------------------------------------------------------------
    // The first few gift-box spawns are saved raw (how the string-typed package was found).
    // CAMPUSNET_GIFTBOX_CAPTURE=1 turns it on.
    private static readonly bool CaptureOn = Environment.GetEnvironmentVariable("CAMPUSNET_GIFTBOX_CAPTURE") == "1";   // off unless asked for
    private static int _captured;
    private const int MaxCaptures = 10;

    public static void Capture(object? data, string room, string? userId)
    {
        if (!CaptureOn || Interlocked.Increment(ref _captured) > MaxCaptures) return;
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "giftbox-captures");
            Directory.CreateDirectory(dir);
            string stem = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}_u{userId}");
            var w = new GpBinary16.Writer(2048);
            w.Value(data);
            File.WriteAllBytes(stem + ".bin", w.ToArray());
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"room {room} user {userId} bytes {w.Length}");
            void Dump(object? o, string path, int depth)
            {
                if (depth > 8) return;
                switch (o)
                {
                    case null: sb.AppendLine($"{path} = null"); break;
                    case byte[] b: sb.AppendLine($"{path} = byte[{b.Length}] {Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 4096)))}"); break;
                    case CustomValue cv: sb.AppendLine($"{path} = custom '{(char)cv.Code}' ({cv.Code}) [{cv.Data.Length}] {Convert.ToHexString(cv.Data.AsSpan(0, Math.Min(cv.Data.Length, 4096)))}"); break;
                    case Hashtable ht: foreach (DictionaryEntry e in ht) Dump(e.Value, $"{path}{{{e.Key} ({e.Key?.GetType().Name})}}", depth + 1); break;
                    case TypedArray ta: sb.AppendLine($"{path} = typed '{(char)ta.ElemType}'[{ta.Items.Length}] custom={ta.CustomCode}"); for (int i = 0; i < ta.Items.Length; i++) Dump(ta.Items[i], $"{path}[{i}]", depth + 1); break;
                    case object?[] arr: sb.AppendLine($"{path} = object[{arr.Length}]"); for (int i = 0; i < arr.Length; i++) Dump(arr[i], $"{path}[{i}]", depth + 1); break;
                    default: sb.AppendLine($"{path} = {o.GetType().Name} {o}"); break;
                }
            }
            Dump(data, "data", 0);
            File.WriteAllText(stem + ".txt", sb.ToString());
            Log.Info($"[giftbox-capture] saved {stem}.bin ({w.Length} bytes) from u{userId} in '{room}'");
        }
        catch (Exception ex) { Log.Warn($"[giftbox-capture] failed: {ex.Message}"); }
    }

    /// <summary>Flip the flag everywhere inside this event's data. Returns how many were changed.</summary>
    public static int MarkValid(object? data)
    {
        if (!Enabled) return 0;
        int n = 0;
        Walk(data, 0, ref n);
        return n;
    }

    // The package rides as a STRING: ev 29 data {6: object[] { -1, TypedDictionary<int,object> {
    // 34: "{\"Id\":106133,...,\"IsSignatureValid\":false,...}" } } } (captured 2026-09-27). Strings
    // are immutable, so a string value is swapped for its patched copy inside its container.
    private static void Walk(object? o, int depth, ref int n)
    {
        if (o == null || depth > 6) return;
        switch (o)
        {
            case byte[] b: n += Patch(b); return;
            case CustomValue cv: n += Patch(cv.Data); return;
            case Hashtable ht:
                foreach (var key in ht.Keys.Cast<object>().ToList())
                {
                    if (ht[key] is string sv) { var f = Fix(sv); if (f != null) { ht[key] = f; n++; } }
                    else Walk(ht[key], depth + 1, ref n);
                }
                return;
            case TypedDictionary td:
                for (int i = 0; i < td.Entries.Count; i++)
                {
                    var e = td.Entries[i];
                    if (e.Value is string sv) { var f = Fix(sv); if (f != null) { td.Entries[i] = new KeyValuePair<object?, object?>(e.Key, f); n++; } }
                    else Walk(e.Value, depth + 1, ref n);
                }
                return;
            case TypedArray ta:
                for (int i = 0; i < ta.Items.Length; i++)
                {
                    if (ta.Items[i] is string sv) { var f = Fix(sv); if (f != null) { ta.Items[i] = f; n++; } }
                    else Walk(ta.Items[i], depth + 1, ref n);
                }
                return;
            case object?[] arr:
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] is string sv) { var f = Fix(sv); if (f != null) { arr[i] = f; n++; } }
                    else Walk(arr[i], depth + 1, ref n);
                }
                return;
            case string: return;   // a top-level string cannot be swapped; containers handle theirs
            case IEnumerable en: foreach (var x in en) Walk(x, depth + 1, ref n); return;
        }
    }

    private static readonly string[] FromS = { "\"IsSignatureValid\":false", "\"isSignatureValid\":false", "\"issignaturevalid\":false" };
    private static readonly string[] ToS = { "\"IsSignatureValid\":true", "\"isSignatureValid\":true", "\"issignaturevalid\":true" };

    /// <summary>The string with the flag set, or null when it has none to set.</summary>
    private static string? Fix(string s)
    {
        if (s.Length < 20 || s.IndexOf("ignatureValid", StringComparison.OrdinalIgnoreCase) < 0) return null;
        string r = s;
        for (int i = 0; i < FromS.Length; i++) r = r.Replace(FromS[i], ToS[i], StringComparison.Ordinal);
        return ReferenceEquals(r, s) || r == s ? null : r;
    }

    private static int Patch(byte[] b)
    {
        int n = 0;
        for (int p = 0; p < From.Length; p++)
        {
            var f = From[p];
            int i = 0;
            while ((i = b.AsSpan(i).IndexOf(f) is var k && k >= 0 ? i + k : -1) >= 0)
            {
                To[p].CopyTo(b, i);
                i += f.Length;
                n++;
            }
        }
        return n;
    }
}
