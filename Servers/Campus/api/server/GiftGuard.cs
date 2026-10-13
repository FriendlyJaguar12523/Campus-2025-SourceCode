using System.Collections.Concurrent;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// One gift of the same item from the same buyer to the same player per Window (2026-09-27
    /// request). The gift button is easy to hit twice, and two taps both passed the "they already
    /// have it" check before either box existed - consumables are never checked at all - so the
    /// buyer paid twice. The reservation is taken atomically before anything is charged; a line
    /// that ends up unbought gives it back.
    /// </summary>
    public static class GiftGuard
    {
        public static TimeSpan Window = TimeSpan.FromSeconds(60);
        public const string Message = "You just gifted them that - wait a minute before sending it again.";

        private static readonly ConcurrentDictionary<string, DateTime> _recent = new();

        public static string Key(ulong buyer, ulong recipient, string item) => $"{buyer}>{recipient}:{item.ToLowerInvariant()}";

        /// <summary>True when this gift may go ahead (and it is now reserved), false inside the window.</summary>
        public static bool TryReserve(string key)
        {
            var now = DateTime.UtcNow;
            if (_recent.Count > 5000)
                foreach (var kv in _recent)
                    if (now - kv.Value >= Window) _recent.TryRemove(kv.Key, out _);
            while (true)
            {
                if (_recent.TryAdd(key, now)) return true;
                if (!_recent.TryGetValue(key, out var at)) continue;
                if (now - at < Window) return false;
                if (_recent.TryUpdate(key, now, at)) return true;
            }
        }

        /// <summary>Give a reservation back (the gift was not bought after all).</summary>
        public static void Release(string? key)
        {
            if (!string.IsNullOrEmpty(key)) _recent.TryRemove(key, out _);
        }
    }
}
