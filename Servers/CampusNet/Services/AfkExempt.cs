using CampusNet.Net;

namespace CampusNet.Services;

/// <summary>
/// Room owners / co-owners and staff (developer or moderator flag) are not dropped for going quiet
/// the way everyone else is: once they join a room, the Campus server (localhost only) is asked
/// whether they own that room or are staff, and their connection's allowed silence is raised to
/// PhotonUdpServer.AfkExemptTimeoutMs (10 min). Everyone else keeps PeerTimeoutMs (60 s).
/// </summary>
public static class AfkExempt
{
    private static readonly string Url = Environment.GetEnvironmentVariable("CAMPUSNET_AFKEXEMPT_URL") ?? "http://127.0.0.1:8080/photon/afkexempt";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static void Apply(Connection c, string room)
    {
        string? user = c.UserId;
        if (string.IsNullOrEmpty(user) || !ulong.TryParse(user, out _)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                string answer = (await Http.GetStringAsync($"{Url}?id={Uri.EscapeDataString(user)}&room={Uri.EscapeDataString(room)}")).Trim();
                if (answer.Length == 0 || answer == "0") return;
                lock (c.Peer) c.Peer.TimeoutMs = PhotonUdpServer.AfkExemptTimeoutMs;
                Log.Info($"{c.Tag}: {answer} in '{room}' - kept through {PhotonUdpServer.AfkExemptTimeoutMs / 60000} min of silence");
            }
            catch { /* Campus unreachable: they keep the normal timeout */ }
        });
    }
}
