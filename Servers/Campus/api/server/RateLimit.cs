using System.Collections.Concurrent;

public static class RateLimit
{
    private static readonly ConcurrentDictionary<string, DateTime> _requests = new();

    public static bool TryConsume(
        string key,
        int cooldownSeconds,
        out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        var now = DateTime.UtcNow;

        var last = _requests.GetOrAdd(key, _ => DateTime.MinValue);
        var elapsed = (now - last).TotalSeconds;

        if (elapsed < cooldownSeconds)
        {
            retryAfterSeconds = (int)Math.Ceiling(cooldownSeconds - elapsed);
            return false;
        }

        _requests[key] = now;

        Cleanup(now);
        return true;
    }

    private static void Cleanup(DateTime now)
    {
        if (_requests.Count < 10_000) return;

        foreach (var kv in _requests)
        {
            if ((now - kv.Value).TotalMinutes > 10)
                _requests.TryRemove(kv.Key, out _);
        }
    }

    // ---- sliding-window quota ------------------------------------------------------------
    //
    // TryConsume above is a COOLDOWN: a minimum gap between two calls. On its own that does
    // not stop abuse - someone can keep uploading forever as long as they wait out the gap.
    // For anything that writes files to disk you want both: a cooldown to stop bursts, and a
    // quota to cap the total over a window.

    private static readonly ConcurrentDictionary<string, List<DateTime>> _windows = new();
    private static readonly object _windowLock = new();

    /// <summary>
    /// Allows at most <paramref name="maxInWindow"/> calls per key per window. Returns false
    /// (with seconds until the oldest call ages out) once the quota is spent.
    /// </summary>
    public static bool TryConsumeQuota(
        string key,
        int maxInWindow,
        int windowSeconds,
        out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-windowSeconds);

        // Lock rather than lock-free: the read-modify-write below is not atomic, and without
        // it two concurrent uploads can both see a non-full window and both be admitted.
        lock (_windowLock)
        {
            var stamps = _windows.GetOrAdd(key, _ => new List<DateTime>());
            stamps.RemoveAll(t => t < cutoff);

            if (stamps.Count >= maxInWindow)
            {
                retryAfterSeconds = (int)Math.Ceiling((stamps[0] - cutoff).TotalSeconds);
                if (retryAfterSeconds < 1) retryAfterSeconds = 1;
                return false;
            }

            stamps.Add(now);

            if (_windows.Count > 10_000)
            {
                foreach (var kv in _windows)
                    if (kv.Value.Count == 0) _windows.TryRemove(kv.Key, out _);
            }
            return true;
        }
    }
}
