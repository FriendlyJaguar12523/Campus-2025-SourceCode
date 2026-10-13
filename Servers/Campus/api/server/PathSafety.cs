namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Confines a user-supplied path segment to a directory.
    ///
    /// The file-serving routes (/img, /cdn/room, /cdn/data) are all catch-all
    /// (<c>{*path}</c>), so the segment can contain slashes and "..". They fed that raw
    /// segment straight into Path.Combine and served whatever came out - so
    /// <c>GET /cdn/room/../../../../etc/passwd</c> returned any file on the box. This is the
    /// arbitrary-file-read that let the whole server be pulled down over HTTP.
    ///
    /// ResolveUnder returns the absolute path ONLY when it stays inside the intended root,
    /// and null the moment it would escape - covering "..", absolute inputs (/etc/passwd,
    /// C:\...), UNC paths and embedded NULs. Path.GetFullPath does the normalisation, so this
    /// is decided on the CANONICAL path, not the raw string.
    /// </summary>
    public static class PathSafety
    {
        public static string? ResolveUnder(string root, string? userRelative)
        {
            if (string.IsNullOrEmpty(userRelative)) return null;
            if (userRelative.IndexOf('\0') >= 0) return null;

            string rootFull;
            string combined;
            try
            {
                rootFull = Path.GetFullPath(root);

                // Path.Combine discards the root if userRelative is absolute or rooted, which
                // GetFullPath then normalises - the confinement check below still catches it,
                // because an absolute input resolves to somewhere outside rootFull.
                combined = Path.GetFullPath(Path.Combine(rootFull, userRelative));
            }
            catch
            {
                // Malformed path (illegal characters, over-long, etc.) - treat as an escape.
                return null;
            }

            string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            // Must be the root itself, or strictly beneath it.
            if (string.Equals(combined, rootFull, StringComparison.Ordinal))
                return combined;

            if (combined.StartsWith(rootWithSep, StringComparison.Ordinal))
                return combined;

            return null;
        }
    }
}
