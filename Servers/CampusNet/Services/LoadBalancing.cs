using System.Collections;
using CampusNet.Net;
using CampusNet.Protocol;

namespace CampusNet.Services;

public enum Role { NameServer, Master, Game }

/// <summary>
/// The Photon LoadBalancing application: one instance per role/port. The client walks
/// NameServer (GetRegions/Authenticate → master address) → Master (Authenticate, JoinGame → game
/// address) → Game (Authenticate, JoinGame, RaiseEvent/SetProperties/Leave). All three run in this
/// process and share the room registry, so "addresses" are just this host's ports.
/// </summary>
public sealed class LoadBalancingApp
{
    public readonly Role Role;
    public readonly PhotonUdpServer Server;
    public readonly string PublicHost;
    public readonly int MasterPort, GamePort;
    private static readonly object AppStatsSync = new();

    public LoadBalancingApp(Role role, PhotonUdpServer server, string publicHost, int masterPort, int gamePort)
    {
        Role = role; Server = server; PublicHost = publicHost; MasterPort = masterPort; GamePort = gamePort;
        server.OnConnect += p => p.Session = new Connection(server, p);
        server.OnMessage += OnMessage;
        server.OnDisconnect += OnDisconnect;
    }

    private static Connection C(PhotonUdpServer.Peer p) => (Connection)(p.Session ??= null!);

    private void OnMessage(PhotonUdpServer.Peer peer, byte[] message, bool reliable)
    {
        if (peer.Session is not Connection c) { c = new Connection(Server, peer); peer.Session = c; }
        GpBinary16.OperationRequest? op;
        try { op = c.Decode(message, out _); }
        catch (Exception ex) { Log.Warn($"{c.Tag}: decode failed: {ex.Message} [{Convert.ToHexString(message.AsSpan(0, Math.Min(48, message.Length)))}]"); return; }
        if (op == null) return;
        if (Log.Debug) Log.Dbg($"{c.Tag} <- op {op.Code} {DescribeParams(op.Params)}");
        try { Handle(c, op, reliable); }
        catch (Exception ex)
        {
            Log.Warn($"{c.Tag}: op {op.Code} failed: {ex}");
            c.SendError(op.Code, Rc.InternalServerError, ex.Message);
        }
    }

    private void OnDisconnect(PhotonUdpServer.Peer peer)
    {
        if (peer.Session is not Connection c) return;
        if (Role == Role.Game && c.Room != null) GameService.OnConnectionLost(c);
        if (Role == Role.Master && c.Proto18) ChatService.OnDisconnect(c);
    }

    private void Handle(Connection c, GpBinary16.OperationRequest op, bool reliable)
    {
        switch (op.Code)
        {
            case Op.GetRegions: HandleGetRegions(c, op); return;
            case Op.Authenticate:
            case Op.AuthenticateOnce: HandleAuthenticate(c, op); return;
        }
        if (c.UserId == null) { c.SendError(op.Code, Rc.OperationNotAllowedInCurrentState, "Not authenticated"); return; }
        if (Role != Role.Game && !c.Charge(0, $"op {op.Code}")) return;
        switch (Role)
        {
            case Role.Master: HandleMaster(c, op); return;
            case Role.Game: GameService.Handle(c, op, reliable); return;
            default: c.SendError(op.Code, Rc.InvalidOperation, "Not on this server"); return;
        }
    }

    // ---------------------------------------------------------------- name server

    private void HandleGetRegions(Connection c, GpBinary16.OperationRequest op)
    {
        c.SendResponse(Op.GetRegions, new Dictionary<byte, object?>
        {
            [P.Region] = new[] { "us" },
            [P.Address] = new[] { $"{PublicHost}:{MasterPort}" }
        });
    }

    private void HandleAuthenticate(Connection c, GpBinary16.OperationRequest op)
    {
        string? userId = null, nick = "";
        if (Role == Role.NameServer)
        {
            // Custom auth: the Campus photonAuthToken rides in ClientAuthenticationData (216); a
            // few builds put it in the params string (214) instead. Fall back to the UserId (225).
            // Rec Room sends the JWT as the BYTES of ClientAuthenticationParams (214), not a string.
            string? token = null;
            foreach (var key in new[] { P.ClientAuthenticationData, P.ClientAuthenticationParams })
            {
                var raw = op.Params.GetValueOrDefault(key);
                var str = raw as string ?? (raw is byte[] rb ? System.Text.Encoding.UTF8.GetString(rb) : null);
                if (string.IsNullOrEmpty(str)) continue;
                if (str.StartsWith("token=")) str = str[6..];
                // Rec Room's auth data is JSON: {"accountId":"6","accessToken":"<the game session JWT>"}.
                if (str.StartsWith("{"))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(str);
                        if (doc.RootElement.TryGetProperty("accessToken", out var at)) str = at.GetString() ?? str;
                    }
                    catch { }
                }
                if (Tokens.ValidateCampusJwt(str) != null) { token = str; break; }
                token ??= str;
            }
            var v = Tokens.ValidateCampusJwt(token);
            if (v != null) userId = v.Value.userId;
            else if (op.Params.GetValueOrDefault(P.UserId) is string uid && uid.Length > 0 && AllowUnsignedUserId) userId = uid;
            if (userId == null)
            {
                Log.Warn($"{c.Tag}: authentication rejected (token {(token == null ? "absent" : "invalid")}) params={DescribeParams(op.Params)} token='{token}'");
                if (op.Params.GetValueOrDefault(P.ClientAuthenticationParams) is byte[] rawp) Log.Warn($"{c.Tag}: 214 hex={Convert.ToHexString(rawp.AsSpan(0, Math.Min(64, rawp.Length)))}");
                c.SendError(op.Code, Rc.CustomAuthenticationFailed, "Invalid token");
                return;
            }
            nick = op.Params.GetValueOrDefault(P.UriPath) as string ?? "";
        }
        else
        {
            var s = Tokens.ValidateSecret(op.Params.GetValueOrDefault(P.Secret) as string);
            if (s == null)
            {
                Log.Warn($"{c.Tag}: bad secret on {Role}");
                c.SendError(op.Code, Rc.InvalidAuthentication, "Invalid secret");
                return;
            }
            userId = s.Value.userId; nick = s.Value.nick;
        }
        c.UserId = userId; c.NickName = nick ?? "";
        string secret = Tokens.MintSecret(userId, c.NickName);
        var resp = new Dictionary<byte, object?> { [P.Secret] = secret, [P.UserId] = userId };
        if (Role == Role.NameServer) resp[P.Address] = $"{PublicHost}:{MasterPort}";
        c.SendResponse(op.Code, resp);
        Log.Info($"{c.Tag}: authenticated on {Role} (app '{c.AppName}')");
        if (Role == Role.Master && !c.Proto18) SendAppStats(c);   // a LoadBalancing event; a chat client has no use for it
    }

    public static bool AllowUnsignedUserId = Environment.GetEnvironmentVariable("CAMPUSNET_ALLOW_UNSIGNED") == "1";

    // ---------------------------------------------------------------- master

    private void SendAppStats(Connection c)
    {
        int peers = Server.PeerCount;
        c.SendEvent(Ev.AppStats, new Dictionary<byte, object?>
        {
            [P.MasterPeerCount] = peers,
            [P.PeerCount] = Rooms.All().Sum(r => r.ActiveCount),
            [P.GameCount] = Rooms.Count
        }, reliable: false);
    }

    private void HandleMaster(Connection c, GpBinary16.OperationRequest op)
    {
        // Protocol-1.8 connections here are the client's Photon Chat library (event broadcasting -
        // see ChatService). Chat's op codes (0-9) never collide with LoadBalancing's (217+).
        if (c.Proto18 && ChatOp.Is(op.Code)) { ChatService.Handle(c, op); return; }
        if (c.Proto18) Log.Info($"{c.Tag}: 1.8 op {op.Code} {DescribeParams(op.Params)}");
        switch (op.Code)
        {
            case Op.JoinLobby:
                c.InLobby = true;
                c.SendResponse(op.Code, new());
                c.SendEvent(Ev.GameList, new Dictionary<byte, object?> { [P.GameList] = GameListHashtable() });
                return;
            case Op.LeaveLobby:
                c.InLobby = false;
                c.SendResponse(op.Code, new());
                return;
            case Op.CreateGame:
            case Op.JoinGame:
            {
                string name = op.Params.GetValueOrDefault(P.RoomName) as string ?? "";
                if (name.Length == 0) name = Guid.NewGuid().ToString("N");
                int joinMode = ToInt(op.Params.GetValueOrDefault(P.JoinMode));
                var room = Rooms.Get(name);
                if (op.Code == Op.JoinGame && room == null && joinMode == 0)
                {
                    c.SendError(op.Code, Rc.GameDoesNotExist, "Game does not exist"); return;
                }
                if (JoinBlocks.IsBlocked(c.UserId, name))
                {
                    Log.Info($"{c.Tag}: refused join to '{name}' - kicked from that instance");
                    c.SendError(op.Code, Rc.GameClosed, "Game closed"); return;
                }
                if (op.Code == Op.CreateGame && room != null && joinMode == 0)
                {
                    c.SendError(op.Code, Rc.GameIdAlreadyExists, "A game with the specified id already exist"); return;
                }
                if (room != null)
                {
                    lock (room.Sync)
                    {
                        bool rejoin = room.FindByUserId(c.UserId!) != null;
                        if (!rejoin && !room.IsOpen) { c.SendError(op.Code, Rc.GameClosed, "Game closed"); return; }
                        if (!rejoin && room.MaxPlayers > 0 && room.ActiveCount >= room.MaxPlayers) { c.SendError(op.Code, Rc.GameFull, "Game full"); return; }
                    }
                }
                c.SendResponse(op.Code, new Dictionary<byte, object?>
                {
                    [P.Address] = $"{PublicHost}:{GamePort}",
                    [P.Secret] = Tokens.MintSecret(c.UserId!, c.NickName),
                    [P.RoomName] = name
                });
                Log.Info($"{c.Tag}: {(op.Code == Op.CreateGame ? "create" : "join")} '{name}' -> game server");
                return;
            }
            case Op.JoinRandomGame:
            {
                var expected = op.Params.GetValueOrDefault(P.GameProperties) as Hashtable;
                Room? pick = null;
                foreach (var r in Rooms.All())
                {
                    lock (r.Sync)
                    {
                        if (!r.IsOpen || !r.IsVisible) continue;
                        if (r.MaxPlayers > 0 && r.ActiveCount >= r.MaxPlayers) continue;
                        if (expected != null && !Room.CasMatches(r.Props, expected)) continue;
                        pick = r; break;
                    }
                }
                if (pick == null) { c.SendError(op.Code, Rc.NoRandomMatchFound, "No match found"); return; }
                c.SendResponse(op.Code, new Dictionary<byte, object?>
                {
                    [P.Address] = $"{PublicHost}:{GamePort}",
                    [P.Secret] = Tokens.MintSecret(c.UserId!, c.NickName),
                    [P.RoomName] = pick.Name
                });
                return;
            }
            case Op.GetGameList:
                c.SendResponse(op.Code, new Dictionary<byte, object?> { [P.GameList] = GameListHashtable() });
                return;
            case Op.FindFriends:
            {
                // Answers "not online, no room" for everyone. It used to return the exact Photon room
                // name of ANY player id asked about - private rooms and dorms included - and any
                // logged-in client may join a room by name, so it was a way into anyone's private
                // room past invites and room bans. Rec Room finds friends through RecNet presence,
                // which honours privacy, not through this.
                var ids = op.Params.GetValueOrDefault((byte)1) as string[] ?? Array.Empty<string>();
                var online = new bool[ids.Length]; var rooms = new string[ids.Length];
                for (int i = 0; i < ids.Length; i++) rooms[i] = "";
                c.SendResponse(op.Code, new Dictionary<byte, object?>
                {
                    [1] = new TypedArray((byte)'o', online.Cast<object?>().ToArray()),
                    [2] = rooms
                });
                return;
            }
            case Op.GetLobbyStats:
                c.SendResponse(op.Code, new Dictionary<byte, object?>
                {
                    [P.LobbyName] = new[] { "" }, [P.LobbyType] = new TypedArray((byte)'b', new object?[] { (byte)0 }),
                    [P.PeerCount] = new[] { Server.PeerCount }, [P.GameCount] = new[] { Rooms.Count }
                });
                return;
            case Op.SetProperties:
            case Op.RaiseEvent:
                c.SendResponse(op.Code, new());   // harmless on the master
                return;
            case Op.ServerSettings:
                c.SendResponse(op.Code, new());
                return;
            default:
                Log.Warn($"{c.Tag}: master op {op.Code} not implemented {DescribeParams(op.Params)}");
                c.SendError(op.Code, Rc.InvalidOperation, "Operation not supported");
                return;
        }
    }

    private static Hashtable GameListHashtable()
    {
        var h = new Hashtable();
        foreach (var r in Rooms.All())
        {
            lock (r.Sync)
            {
                if (!r.IsVisible) continue;
                var props = new Hashtable();
                if (r.Props[GameProp.PropsListedInLobby] is string[] listed)
                    foreach (var k in listed) if (r.Props.ContainsKey(k)) props[k] = r.Props[k];
                props[GameProp.PlayerCount] = (byte)r.ActiveCount;
                props[GameProp.MaxPlayers] = (byte)Math.Clamp(r.MaxPlayers, 0, 255);
                props[GameProp.IsOpen] = r.IsOpen;
                props[GameProp.IsVisible] = r.IsVisible;
                h[r.Name] = props;
            }
        }
        return h;
    }

    public static int ToInt(object? v) => v switch { null => 0, byte b => b, short s => s, int i => i, long l => (int)l, bool o => o ? 1 : 0, _ => 0 };

    public static string DescribeParams(IReadOnlyDictionary<byte, object?> p)
        => "{" + string.Join(", ", p.Select(e => $"{e.Key}={GpBinary16.Describe(e.Value)}")) + "}";
}
