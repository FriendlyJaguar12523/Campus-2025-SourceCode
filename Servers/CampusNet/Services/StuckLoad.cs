using System.Collections;

namespace CampusNet.Services;

/// <summary>
/// Joins that hang on the loading screen (25%, "Waiting to receive buffered message"). A joiner
/// that loads streams its transform (event 28) and sends one request to the master client
/// (receiver group 2) within seconds; the stuck ones do neither (Rec Center 2026-09-26: every
/// join after a certain point hung while the players already inside were fine). After
/// AfterSeconds with neither, the Campus server is told (POST /photon/stuckload, localhost): it
/// stops matchmaking anyone into that instance and moves the player out of it, so they are not
/// left loading forever and their next join gets a working instance. The room's creator is
/// exempt (their load builds the room). CAMPUSNET_STUCK_LOAD_SECONDS=0 turns it off.
/// </summary>
public static class StuckLoad
{
    public static readonly int AfterSeconds =
        int.TryParse(Environment.GetEnvironmentVariable("CAMPUSNET_STUCK_LOAD_SECONDS"), out var s) ? (s <= 0 ? int.MaxValue : s) : 90;
    private static readonly string Url = Environment.GetEnvironmentVariable("CAMPUSNET_STUCKLOAD_URL") ?? "http://127.0.0.1:8080/photon/stuckload";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static void Report(string userId, string room, int secs)
    {
        Log.Warn($"[stuck-load] u{userId} in '{room}' has not finished loading after {secs}s - reporting");
        if (string.IsNullOrEmpty(userId) || !ulong.TryParse(userId, out _)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var body = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("id", userId),
                    new KeyValuePair<string, string>("room", room),
                    new KeyValuePair<string, string>("seconds", secs.ToString()),
                });
                using var resp = await Http.PostAsync(Url, body);
            }
            catch (Exception ex) { Log.Warn($"[stuck-load] report for u{userId} failed: {ex.Message}"); }
        });
    }

    /// <summary>Does any string inside the payload contain this text?</summary>
    public static bool ContainsString(object? o, string needle, int depth = 0)
    {
        if (o == null || depth > 4) return false;
        if (o is string str) return str.Contains(needle, StringComparison.Ordinal);
        if (o is Hashtable ht) { foreach (DictionaryEntry e in ht) if (ContainsString(e.Value, needle, depth + 1)) return true; return false; }
        if (o is IEnumerable en and not byte[]) { int n = 0; foreach (var x in en) { if (n++ > 64) break; if (ContainsString(x, needle, depth + 1)) return true; } }
        return false;
    }
}
