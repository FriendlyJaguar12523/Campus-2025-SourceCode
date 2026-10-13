using CampusNet.Protocol;

namespace CampusNet.Services;

/// <summary>Photon Chat operation / parameter / event codes (the public Photon Chat SDK's values -
/// the game's copy of the SDK has these constants compiled away).</summary>
public static class ChatOp
{
    public const byte Subscribe = 0, Unsubscribe = 1, Publish = 2, SendPrivate = 3, ChannelHistory = 4,
        UpdateStatus = 5, AddFriends = 6, RemoveFriends = 7, GetProperties = 8, SetProperties = 9;
    public static bool Is(byte op) => op <= SetProperties;
}

public static class ChatP
{
    public const byte Channels = 0, Channel = 1, Messages = 2, Message = 3, Senders = 4, Sender = 5,
        ChannelUserCount = 6, MsgId = 8, MsgIds = 9, Status = 10, Friends = 11, SkipMessage = 12,
        HistoryLength = 14, SubscribeResults = 15, Properties = 22, ChannelSubscribers = 23,
        UserId = 225;
}

public static class ChatEv
{
    public const byte ChatMessages = 0, Users = 1, PrivateMessage = 2, FriendsList = 3, StatusUpdate = 4,
        Subscribe = 5, Unsubscribe = 6, PropertiesChanged = 7, UserSubscribed = 8, UserUnsubscribed = 9;
}

/// <summary>
/// A Photon Chat frontend, riding on the MASTER port. Chat's operation codes (0-9) never collide
/// with LoadBalancing's (217+), and the name server already hands every client the master's
/// address, so a chat client lands here with no new port and no firewall change.
///
/// What uses it: event BROADCASTING (RecRoom.Broadcasting.BroadcastingManager). The broadcaster's
/// client publishes its movement, voice, room chat, desktop screen share and CV2 events to a chat
/// CHANNEL; players in the event's other instances subscribe to that channel and draw the
/// broadcaster from what arrives. So the job here is a faithful fan-out: every Publish reaches
/// every subscriber of the channel (sender included, as Photon does), payload byte for byte.
/// </summary>
public static class ChatService
{
    private sealed class Channel
    {
        public readonly string Name;
        public readonly List<Connection> Subs = new();
        public readonly List<(int id, string sender, RawValue msg)> History = new();
        public int NextMsgId;
        public bool PublishSubscribers;
        public int MaxSubscribers;
        public long Published, Delivered;
        public Channel(string name) { Name = name; }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Channel> Channels = new(StringComparer.Ordinal);
    private static readonly Dictionary<Connection, HashSet<string>> Membership = new();

    private const int HistoryKept = 20;           // per channel, for a subscriber that asks for history
    private const int MaxChannelsPerUser = 64;
    private const int MaxChannelNameLength = 256;
    private const byte PropMaxSubscribers = 255, PropPublishSubscribers = 254;   // ChannelWellKnownProperties

    public static void Handle(Connection c, GpBinary16.OperationRequest op)
    {
        switch (op.Code)
        {
            case ChatOp.Subscribe: Subscribe(c, op); return;
            case ChatOp.Unsubscribe: Unsubscribe(c, op); return;
            case ChatOp.Publish: Publish(c, op); return;
            case ChatOp.SendPrivate: SendPrivate(c, op); return;
            default:
                // History, status, friends and properties: acknowledged. Nothing in the broadcast
                // path depends on them; an error reply would only put a warning in the client log.
                Log.Info($"{c.Tag}: chat op {op.Code} acknowledged {LoadBalancingApp.DescribeParams(op.Params)}");
                c.SendResponse(op.Code, new());
                return;
        }
    }

    private static void Subscribe(Connection c, GpBinary16.OperationRequest op)
    {
        var names = (op.Params.GetValueOrDefault(ChatP.Channels) as string[]) ?? Array.Empty<string>();
        int historyLength = LoadBalancingApp.ToInt(op.Params.GetValueOrDefault(ChatP.HistoryLength));
        var opts = op.Params.GetValueOrDefault(ChatP.Properties) as System.Collections.Hashtable;
        c.SendResponse(op.Code, new());

        var results = new bool[names.Length];
        var joined = new List<Channel>();
        lock (Sync)
        {
            var mine = Membership.TryGetValue(c, out var m) ? m : Membership[c] = new();
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i] ?? "";
                if (name.Length == 0 || name.Length > MaxChannelNameLength) continue;
                if (!mine.Contains(name) && mine.Count >= MaxChannelsPerUser) continue;
                if (!Channels.TryGetValue(name, out var ch))
                {
                    ch = Channels[name] = new Channel(name);
                    // Creation options are honoured by the subscriber that creates the channel.
                    if (opts != null)
                    {
                        ch.PublishSubscribers = opts[PropPublishSubscribers] is bool ps && ps;
                        ch.MaxSubscribers = LoadBalancingApp.ToInt(opts[PropMaxSubscribers]);
                    }
                }
                if (!ch.Subs.Contains(c))
                {
                    if (ch.MaxSubscribers > 0 && ch.Subs.Count >= ch.MaxSubscribers) continue;
                    ch.Subs.Add(c);
                }
                mine.Add(name);
                results[i] = true;
                joined.Add(ch);
            }
        }

        var ev = new Dictionary<byte, object?> { [ChatP.Channels] = names, [ChatP.SubscribeResults] = results };
        if (names.Length == 1 && joined.Count == 1 && joined[0].PublishSubscribers)
        {
            string[] subs; lock (Sync) subs = joined[0].Subs.Select(s => s.UserId ?? "").ToArray();
            ev[ChatP.ChannelSubscribers] = subs;
        }
        c.SendEvent(ChatEv.Subscribe, ev);

        foreach (var ch in joined)
        {
            List<Connection> others; int count;
            lock (Sync) { others = ch.Subs.Where(s => s != c).ToList(); count = ch.Subs.Count; }
            Log.Info($"{c.Tag}: chat subscribed '{ch.Name}' ({count} subscriber(s){(ch.PublishSubscribers ? ", published" : "")})");
            if (ch.PublishSubscribers)
                foreach (var o in others)
                    o.SendEvent(ChatEv.UserSubscribed, new() { [ChatP.Channel] = ch.Name, [ChatP.UserId] = c.UserId ?? "" });

            // History: -1 = all kept, N = the last N, 0 = none.
            if (historyLength != 0)
            {
                List<(int id, string sender, RawValue msg)> h;
                lock (Sync) h = historyLength < 0 ? ch.History.ToList() : ch.History.TakeLast(historyLength).ToList();
                if (h.Count > 0)
                    c.SendEvent(ChatEv.ChatMessages, new()
                    {
                        [ChatP.Channel] = ch.Name,
                        [ChatP.Senders] = h.Select(x => x.sender).ToArray(),
                        [ChatP.Messages] = h.Select(x => (object?)x.msg).ToArray(),
                        [ChatP.MsgId] = h[^1].id
                    });
            }
        }
        var refused = names.Where((n, i) => !results[i]).ToList();
        if (refused.Count > 0) Log.Warn($"{c.Tag}: chat subscribe refused for [{string.Join(", ", refused)}]");
    }

    private static void Unsubscribe(Connection c, GpBinary16.OperationRequest op)
    {
        var names = (op.Params.GetValueOrDefault(ChatP.Channels) as string[]) ?? Array.Empty<string>();
        c.SendResponse(op.Code, new());
        foreach (var n in names) Leave(c, n);
        c.SendEvent(ChatEv.Unsubscribe, new() { [ChatP.Channels] = names });
        Log.Info($"{c.Tag}: chat unsubscribed [{string.Join(", ", names)}]");
    }

    private static void Leave(Connection c, string name)
    {
        Channel? ch; List<Connection> others = new();
        lock (Sync)
        {
            if (Membership.TryGetValue(c, out var mine)) mine.Remove(name);
            if (!Channels.TryGetValue(name, out ch)) return;
            ch.Subs.Remove(c);
            if (ch.PublishSubscribers) others = ch.Subs.ToList();
            if (ch.Subs.Count == 0) Channels.Remove(name);
        }
        foreach (var o in others)
            o.SendEvent(ChatEv.UserUnsubscribed, new() { [ChatP.Channel] = name, [ChatP.UserId] = c.UserId ?? "" });
    }

    private static void Publish(Connection c, GpBinary16.OperationRequest op)
    {
        string name = op.Params.GetValueOrDefault(ChatP.Channel) as string ?? "";
        if (op.Raw == null || !op.Raw.TryGetValue(ChatP.Message, out var raw))
        {
            c.SendError(op.Code, Rc.InvalidOperation, "No message");
            return;
        }
        if (!c.Charge(raw.Length, "chat publish")) return;

        Channel? ch; List<Connection> targets; int id;
        var msg = new RawValue(raw);
        string sender = c.UserId ?? "";
        lock (Sync)
        {
            if (!Channels.TryGetValue(name, out ch) || !ch.Subs.Contains(c))
            {
                // Photon only lets a subscriber publish to a channel.
                c.SendError(op.Code, Rc.OperationNotAllowedInCurrentState, "Not subscribed to that channel");
                return;
            }
            id = ++ch.NextMsgId;
            ch.History.Add((id, sender, msg));
            if (ch.History.Count > HistoryKept) ch.History.RemoveAt(0);
            targets = ch.Subs.ToList();
            ch.Published++; ch.Delivered += targets.Count;
            if (ch.Published == 1 || ch.Published % 1000 == 0)
                Log.Info($"{c.Tag}: chat '{name}' published {ch.Published} message(s), {ch.Delivered} delivered, {targets.Count} subscriber(s) (last {raw.Length}B)");
        }
        c.SendResponse(op.Code, new());

        var bytes = GpBinary18.EventMessage(ChatEv.ChatMessages, new Dictionary<byte, object?>
        {
            [ChatP.Channel] = name,
            [ChatP.Senders] = new[] { sender },
            [ChatP.Messages] = new object?[] { msg },
            [ChatP.MsgId] = id
        });
        foreach (var t in targets) t.Send(bytes);
    }

    private static void SendPrivate(Connection c, GpBinary16.OperationRequest op)
    {
        string to = op.Params.GetValueOrDefault(ChatP.UserId) as string ?? "";
        if (op.Raw == null || !op.Raw.TryGetValue(ChatP.Message, out var raw) || to.Length == 0)
        {
            c.SendError(op.Code, Rc.InvalidOperation, "No message or target");
            return;
        }
        if (!c.Charge(raw.Length, "chat private")) return;
        List<Connection> targets;
        lock (Sync) targets = Membership.Keys.Where(k => k.UserId == to || k == c).ToList();
        c.SendResponse(op.Code, new());
        var p = new Dictionary<byte, object?>
        {
            [ChatP.Sender] = c.UserId ?? "",
            [ChatP.Message] = new RawValue(raw),
            [ChatP.UserId] = to,
            [ChatP.MsgId] = 0
        };
        foreach (var t in targets) t.SendEvent(ChatEv.PrivateMessage, p);
    }

    /// <summary>The connection is gone: out of every channel it was in.</summary>
    public static void OnDisconnect(Connection c)
    {
        List<string> names;
        lock (Sync)
        {
            if (!Membership.TryGetValue(c, out var mine)) return;
            names = mine.ToList();
            Membership.Remove(c);
        }
        foreach (var n in names) Leave(c, n);
        if (names.Count > 0) Log.Info($"{c.Tag}: chat disconnected, left [{string.Join(", ", names)}]");
    }
}
