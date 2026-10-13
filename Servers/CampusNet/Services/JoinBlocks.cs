namespace CampusNet.Services;

/// <summary>
/// Keeps a kicked player out of the Photon room they were kicked from. The Campus server records a
/// block per instance (InstanceBlocks) and its matchmaking refuses them - but a kicked / warned
/// client reconnects to its old Photon room by itself, straight through the master, without asking
/// the API, so it walked right back in (2026-09-27). The list of blocks in force is pulled from the
/// Campus server (localhost only) every couple of seconds and a join into a blocked room is refused
/// with GameClosed, which the client handles by falling back to its dorm.
/// CAMPUSNET_ENFORCE_KICKS=0 turns it off.
/// </summary>
public static class JoinBlocks
{
    private static readonly string Url = Environment.GetEnvironmentVariable("CAMPUSNET_JOINBLOCKS_URL") ?? "http://127.0.0.1:8080/photon/joinblocks";
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CAMPUSNET_ENFORCE_KICKS") != "0";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static volatile HashSet<string> _blocked = new(StringComparer.Ordinal);
    private static int _started;

    private static string Key(string userId, string room) => userId + "\n" + room;

    public static void Start()
    {
        if (!Enabled || Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            bool warned = false;
            while (true)
            {
                try
                {
                    string text = await Http.GetStringAsync(Url);
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int sp = line.IndexOf(' ');
                        if (sp > 0) set.Add(Key(line[..sp], line[(sp + 1)..]));
                    }
                    _blocked = set;
                    warned = false;
                }
                catch (Exception ex)
                {
                    // Keep the last list: a Campus restart must not open the doors.
                    if (!warned) { warned = true; Log.Warn($"[joinblocks] could not fetch kick blocks: {ex.Message}"); }
                }
                await Task.Delay(2000);
            }
        });
    }

    /// <summary>Is this player kicked from this Photon room right now?</summary>
    public static bool IsBlocked(string? userId, string room) =>
        Enabled && !string.IsNullOrEmpty(userId) && _blocked.Contains(Key(userId, room));
}
