using System.Collections.Concurrent;
using System.Text;

// Lightweight per-request access log for the admin "Log" tab (/admin/api/apilog).
//
// WHY THIS WAS REWRITTEN: the old version did File.AppendAllTextAsync on EVERY request -
// an open/write/close each time. Under any concurrency two requests race for the same file,
// AppendAllText throws "file in use", and the catch swallowed it - so lines were dropped
// silently. It was therefore gated behind ConfigCampus.VerboseRequestLogging (default false)
// and the admin Log tab, which tails Data/APILog.txt, showed nothing = "the logs tool doesn't
// work".
//
// Now: the authoritative source for the admin tab is an in-memory RING BUFFER (last N lines) -
// zero file contention, always fresh, survives a read-only disk. The file append is kept as a
// best-effort secondary (history + something to read offline) behind a single shared writer so
// concurrent requests never collide. This middleware is cheap enough to run UNCONDITIONALLY
// (see Program.cs), independent of the heavy VerboseRequestLogging body-logging path.
public class Log
{
    private readonly RequestDelegate _next;
    private readonly string _logFilePath;

    // Ring buffer of recent request lines. Bounded so memory can't grow without limit.
    private const int Capacity = 5000;
    private static readonly ConcurrentQueue<string> _recent = new();

    // One shared writer, guarded, so we never open/close the file per request or race on it.
    private static readonly object _fileLock = new();
    private static StreamWriter? _writer;
    private static string? _writerPath;

    public Log(RequestDelegate next, string logFilePath)
    {
        _next = next;
        _logFilePath = logFilePath;

        try
        {
            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch
        {
            // even startup logging must never crash
        }
    }

    // Which headers does the real client actually send? Every DISTINCT header name is logged once
    // per process, so a request-signature header (X-RNSIG and friends) would name itself here
    // instead of being guessed at. Costs one set lookup per request after the first few.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seenHeaders = new();

    public async Task InvokeAsync(HttpContext context)
    {
        if (Campus_live_rewrite.api.server.ConfigCampus.LogNewRequestHeaders)
        {
            foreach (var h in context.Request.Headers)
            {
                if (!_seenHeaders.TryAdd(h.Key, 0)) continue;
                string v = h.Value.ToString();
                if (string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase) || string.Equals(h.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
                    v = "<redacted>";
                Console.WriteLine($"[headers] first sighting: '{h.Key}: {(v.Length > 120 ? v.Substring(0, 120) + "…" : v)}' on {context.Request.Method} {context.Request.Path}");
            }
        }

        await _next(context);

        try
        {
            string line =
                $"{DateTime.UtcNow:o} {context.Request.Method} {context.Request.Path}{context.Request.QueryString} -> {context.Response.StatusCode}";

            // In-memory first - this is what the admin tab reads, so it works even if the disk
            // write below fails.
            _recent.Enqueue(line);
            while (_recent.Count > Capacity && _recent.TryDequeue(out _)) { }

            AppendToFile(line);
        }
        catch
        {
            // NEVER let logging crash the request.
        }
    }

    private void AppendToFile(string line)
    {
        try
        {
            lock (_fileLock)
            {
                // (Re)open the shared writer if this is the first line or the path changed.
                // FileShare.ReadWrite so /admin/api/apilog (and anyone tailing) can read while
                // we hold it open. AutoFlush so the admin tab sees lines immediately.
                if (_writer == null || _writerPath != _logFilePath)
                {
                    _writer?.Dispose();
                    var fs = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerPath = _logFilePath;
                }
                _writer.WriteLine(line);
            }
        }
        catch
        {
            // Disk write is best-effort; the ring buffer already has the line.
        }
    }

    /// <summary>
    /// The most recent request lines for the admin Log tab, oldest-first, optionally filtered
    /// to those containing <paramref name="find"/>. Reads the in-memory ring buffer, so it is
    /// instant and never blocked by disk state. Empty until the first request is logged.
    /// </summary>
    public static IReadOnlyList<string> Recent(int lines, string? find = null)
    {
        if (lines < 1) lines = 1;

        IEnumerable<string> rows = _recent.ToArray();
        if (!string.IsNullOrEmpty(find))
            rows = rows.Where(l => l.Contains(find, StringComparison.OrdinalIgnoreCase));

        var arr = rows.ToArray();
        if (arr.Length <= lines) return arr;
        return arr[^lines..];
    }

    /// <summary>True once at least one request has been logged this process.</summary>
    public static bool HasBufferedLines => !_recent.IsEmpty;
}
