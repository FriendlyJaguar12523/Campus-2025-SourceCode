using System.Collections.Concurrent;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The sha256 (base64) of every avatar asset bundle THIS server actually serves, stamped onto a
    /// custom-item record's saves (UnityAssetHash / UnityAsset2Hash) as it goes out.
    ///
    /// Why: the client checks the bundle it downloads against the hash in the item definition and
    /// refuses the item when they differ (the wardrobe tile shows, equipping dies in a
    /// NullReferenceException right after the download). The bundles on disk are re-stamped for the
    /// 2021.3 client (ConvertBundles2021.py) and so have different hashes from the catalog capture;
    /// ImportRealStore.py rewrites the JSON to match, but a catalog file and a bundle folder that
    /// were deployed at different times disagree again - which is exactly what happened live
    /// (UGC.json carrying the Unity 6 hashes over 2021.3 files: every first-party item failed to
    /// load). Hashing the file we serve makes the definition follow the file, always.
    ///
    /// Bundles live under Data/CDN/AvatarBlobs, possibly in sub-folders; the index is built once by
    /// name (first hit wins), hashes are computed lazily per file and cached, and a save whose bundle
    /// we don't have is left untouched.
    /// </summary>
    public static class BundleHashes
    {
        private static readonly string Root = Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "AvatarBlobs");

        // Files in sub-folders (the avatar-pack dumps, AvatarBlobs/<n>/...), by bare name. The FLAT
        // file at the root always wins over these - that is where the converted bundles live on
        // the VPS - so the index is only the fallback for a name that has no flat copy.
        private static readonly Lazy<Dictionary<string, string>> _packs = new(() =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(Root))
                    foreach (var dir in Directory.EnumerateDirectories(Root))
                        foreach (var f in Directory.EnumerateFiles(dir, "*.assetbundle", SearchOption.AllDirectories))
                            map.TryAdd(Path.GetFileName(f), f);
            }
            catch (Exception ex) { Console.WriteLine($"[bundlehash] pack index failed: {ex.Message}"); }
            Console.WriteLine($"[bundlehash] {map.Count} packed avatar bundles indexed");
            return map;
        });

        /// <summary>
        /// The file the CDN serves for this bundle name: the flat copy under AvatarBlobs, else the
        /// packed one. Null when neither exists. Shared with the /cdn/avatar route so the hash we
        /// stamp is the hash of the bytes we send.
        /// </summary>
        public static string? Resolve(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('/') || fileName.Contains(Path.DirectorySeparatorChar)) return null;
            string flat = Path.Combine(Root, fileName);
            if (File.Exists(flat)) return flat;
            return _packs.Value.TryGetValue(fileName, out var p) ? p : null;
        }
        private static readonly ConcurrentDictionary<string, string?> _hashes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>base64 sha256 of the bundle file with this name, or null when we don't hold it.</summary>
        public static string? Of(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            return _hashes.GetOrAdd(fileName, name =>
            {
                var path = Resolve(name);
                if (path == null) return null;
                try
                {
                    using var fs = File.OpenRead(path);
                    return Convert.ToBase64String(SHA256.HashData(fs));
                }
                catch (Exception ex) { Console.WriteLine($"[bundlehash] {name}: {ex.Message}"); return null; }
            });
        }

        private static readonly string QuestRoot = Path.Combine(Root, "quest");
        private static readonly ConcurrentDictionary<string, string?> _questHashes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// base64 sha256 of the QUEST (Android) conversion of this bundle, AvatarBlobs/quest/&lt;name&gt;,
        /// or null when we don't hold it. The client hash-checks what it downloads, so a Quest client
        /// sent quest/&lt;name&gt; must also get this hash, not the PC file's (QuestBundleMiddleware):
        /// with the PC hash it downloads the file, rejects it, retries and gives up (2026-09-26).
        /// </summary>
        public static string? OfQuest(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('/') || fileName.Contains(Path.DirectorySeparatorChar)) return null;
            return _questHashes.GetOrAdd(fileName, name =>
            {
                var path = Path.Combine(QuestRoot, name);
                if (!File.Exists(path)) return null;
                try
                {
                    using var fs = File.OpenRead(path);
                    return Convert.ToBase64String(SHA256.HashData(fs));
                }
                catch (Exception ex) { Console.WriteLine($"[bundlehash] quest/{name}: {ex.Message}"); return null; }
            });
        }

        /// <summary>
        /// Hash every flat bundle in the background so the first big catalog page doesn't pay for
        /// thousands of file reads. Lazy lookups keep working while this runs.
        /// </summary>
        public static void WarmUp()
        {
            Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(Root)) return;
                    int n = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var f in Directory.EnumerateFiles(Root, "*.assetbundle", SearchOption.TopDirectoryOnly)) { Of(Path.GetFileName(f)); n++; }
                    Console.WriteLine($"[bundlehash] warmed {n} flat bundle hashes in {sw.Elapsed.TotalSeconds:0}s");
                    if (Directory.Exists(QuestRoot))
                    {
                        int q = 0; sw.Restart();
                        foreach (var f in Directory.EnumerateFiles(QuestRoot, "*.assetbundle", SearchOption.TopDirectoryOnly)) { OfQuest(Path.GetFileName(f)); q++; }
                        Console.WriteLine($"[bundlehash] warmed {q} quest bundle hashes in {sw.Elapsed.TotalSeconds:0}s");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[bundlehash] warm-up failed: {ex.Message}"); }
            });
        }

        /// <summary>Rewrite the record's save hashes to match the files on disk. Returns the same object.</summary>
        public static JObject Stamp(JObject rec)
        {
            try
            {
                if (rec["CurrentSaves"] is JArray saves)
                    foreach (var s in saves.OfType<JObject>())
                    {
                        Fix(s, "UnityAsset", "UnityAssetHash");
                        Fix(s, "UnityAsset2", "UnityAsset2Hash");
                    }
            }
            catch { }
            return rec;
        }

        private static void Fix(JObject save, string fileKey, string hashKey)
        {
            var name = save[fileKey]?.Type == JTokenType.String ? (string?)save[fileKey] : null;
            var real = Of(name);
            if (real != null && (string?)save[hashKey] != real) save[hashKey] = real;
        }
    }
}
