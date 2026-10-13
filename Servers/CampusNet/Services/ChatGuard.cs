using System.Collections;

namespace CampusNet.Services;

/// <summary>
/// Room chat crosses the game server as RPC event 36 carrying the text wrapped as
/// "&lt;noparse&gt;...&lt;/noparse&gt;". A normal client has already run it through the Campus
/// server's chat filter, so what arrives is masked. Each chat message is held until the Campus
/// server (POST /photon/chatguard, localhost) says whether its filter would still change it -
/// if so the text skipped the filter (a chat prompt echoed into chat by a circuit, or a client
/// that never asked), the message is dropped and the sender disconnected; the Campus server
/// times them out. If the Campus server does not answer in time the message goes through:
/// chat must never stop working because of this check. CAMPUSNET_CHAT_GUARD=0 turns it off.
/// </summary>
public static class ChatGuard
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CAMPUSNET_CHAT_GUARD") != "0";
    private static readonly string Url = Environment.GetEnvironmentVariable("CAMPUSNET_CHATGUARD_URL") ?? "http://127.0.0.1:8080/photon/chatguard";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

    private const string Open = "<noparse>", Close = "</noparse>";

    /// <summary>The chat text inside an event-36 payload, or null when it is not a chat message.</summary>
    public static string? ChatText(object? o, int depth = 0)
    {
        if (o == null || depth > 4) return null;
        if (o is string s)
            return s.Length > Open.Length + Close.Length && s.StartsWith(Open, StringComparison.Ordinal) && s.EndsWith(Close, StringComparison.Ordinal)
                ? s.Substring(Open.Length, s.Length - Open.Length - Close.Length) : null;
        if (o is Hashtable ht) { foreach (DictionaryEntry e in ht) { var t = ChatText(e.Value, depth + 1); if (t != null) return t; } return null; }
        if (o is IEnumerable en and not byte[]) { int n = 0; foreach (var x in en) { if (n++ > 64) break; var t = ChatText(x, depth + 1); if (t != null) return t; } }
        return null;
    }

    /// <summary>True only when the Campus server says the text skipped the filter; false on any doubt.</summary>
    public static async Task<bool> IsBypass(string? userId, string room, string text)
    {
        if (string.IsNullOrEmpty(userId) || !ulong.TryParse(userId, out _)) return false;
        try
        {
            using var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("id", userId),
                new KeyValuePair<string, string>("room", room),
                new KeyValuePair<string, string>("text", text),
            });
            using var resp = await Http.PostAsync(Url, body);
            return resp.IsSuccessStatusCode && (await resp.Content.ReadAsStringAsync()).Trim() == "bypass";
        }
        catch { return false; }
    }
}
