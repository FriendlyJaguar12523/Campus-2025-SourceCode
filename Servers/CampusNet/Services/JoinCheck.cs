namespace CampusNet.Services;

/// <summary>
/// Any logged-in client can join a Photon room by NAME - nothing checks that the Campus server
/// actually put them there, so a player who learns a room name can walk into a private room or
/// dorm past invites, room bans and the junior filter. Enforcing that blind could lock people out
/// of legitimate joins (event broadcasts, reconnects, party follow), so this first runs in LOG-ONLY
/// mode: after each join it asks the Campus server (localhost only) which Photon room the player was
/// matchmade into and warns on a mismatch. Once the log shows no false alarms, set
/// CAMPUSNET_ENFORCE_JOINS=1 to refuse them.
/// </summary>
public static class JoinCheck
{
    private static readonly string Url = Environment.GetEnvironmentVariable("CAMPUSNET_PLACEMENT_URL") ?? "http://127.0.0.1:8080/photon/placement";
    public static readonly bool Enforce = Environment.GetEnvironmentVariable("CAMPUSNET_ENFORCE_JOINS") == "1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>The Photon room the Campus server placed this player in ("" = none known, null = lookup failed).</summary>
    public static async Task<string?> PlacementOf(string userId)
    {
        try { return (await Http.GetStringAsync($"{Url}?id={Uri.EscapeDataString(userId)}")).Trim(); }
        catch { return null; }
    }

    public static void Observe(string? userId, string room, string tag)
    {
        if (string.IsNullOrEmpty(userId) || !ulong.TryParse(userId, out _)) return;
        _ = Task.Run(async () =>
        {
            var placed = await PlacementOf(userId);
            if (placed == null) return;   // Campus unreachable: say nothing rather than cry wolf
            if (!string.Equals(placed, room, StringComparison.Ordinal))
                Log.Warn($"[joincheck] {tag} u{userId} joined '{room}' but Campus placed them in '{(placed.Length == 0 ? "(nowhere)" : placed)}'{(Enforce ? "" : " (log only)")}");
        });
    }
}
