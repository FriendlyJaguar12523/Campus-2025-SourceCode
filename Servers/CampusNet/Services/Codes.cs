namespace CampusNet.Services;

/// <summary>Photon LoadBalancing operation / parameter / event codes (standard values; the ones in the capture are marked).</summary>
public static class Op
{
    public const byte GetRegions = 220, Authenticate = 230, AuthenticateOnce = 231, JoinLobby = 229, LeaveLobby = 228,
        CreateGame = 227, JoinGame = 226, JoinRandomGame = 225, Leave = 254, RaiseEvent = 253, SetProperties = 252,
        GetProperties = 251, ChangeGroups = 248, FindFriends = 222, GetLobbyStats = 221, WebRpc = 219,
        ServerSettings = 218, GetGameList = 217;
}

public static class P
{
    public const byte RoomName = 255, ActorNr = 254, TargetActorNr = 253, ActorList = 252, Properties = 251,
        Broadcast = 250, PlayerProperties = 249, GameProperties = 248, Cache = 247, ReceiverGroup = 246,
        Data = 245, Code = 244, CleanupCacheOnLeave = 241, Group = 240, Remove = 239, Add = 238,
        PublishUserId = 239, EmptyRoomTtl = 236, PlayerTtl = 235, IsInactive = 233, CheckUserOnJoin = 232,
        ExpectedValues = 231, Address = 230, PeerCount = 229, GameCount = 228, MasterPeerCount = 227,
        UserId = 225, ApplicationId = 224, MatchMakingType = 223, GameList = 222, Secret = 221,
        AppVersion = 220, AzureNodeInfo = 219, AzureLocalNodeId = 218, ClientAuthenticationType = 217,
        ClientAuthenticationData = 216, JoinMode = 215, ClientAuthenticationParams = 214, LobbyName = 213,
        LobbyType = 212, LobbyStats = 211, Region = 210, UriPath = 209, RpcCallParams = 208,
        RpcCallRetCode = 207, RpcCallRetMessage = 206, CacheSliceIndex = 205, Plugins = 204,
        MasterClientId = 203, RoomOptionFlags = 191;
}

public static class Ev
{
    public const byte Join = 255, Leave = 254, PropertiesChanged = 253, ErrorInfo = 251, CacheSliceChanged = 224,
        AppStats = 226, GameList = 230, GameListUpdate = 229;
}

/// <summary>Well-known game (room) property keys.</summary>
public static class GameProp
{
    public const byte MaxPlayers = 255, IsVisible = 254, IsOpen = 253, PlayerCount = 252, Removed = 251,
        PropsListedInLobby = 250, CleanupCacheOnLeave = 249, MasterClientId = 248, ExpectedUsers = 247,
        PlayerTtl = 246, EmptyRoomTtl = 245;
}

/// <summary>Well-known actor (player) property keys.</summary>
public static class PlayerProp
{
    public const byte NickName = 255, IsInactive = 254, UserId = 253;
}

public static class Rc
{
    public const short Ok = 0, OperationNotAllowedInCurrentState = -3, InvalidOperation = -2, InternalServerError = -1,
        InvalidAuthentication = 32767, GameIdAlreadyExists = 32766, GameFull = 32765, GameClosed = 32764,
        NoRandomMatchFound = 32760, GameDoesNotExist = 32758, MaxCcuReached = 32757, InvalidRegion = 32756,
        CustomAuthenticationFailed = 32755, AuthenticationTicketExpired = 32753, PluginReportedError = 32752,
        PluginMismatch = 32751, JoinFailedPeerAlreadyJoined = 32750, JoinFailedFoundInactiveJoiner = 32749,
        JoinFailedWithRejoinerNotFound = 32748, JoinFailedFoundExcludedUserId = 32747,
        JoinFailedFoundActiveJoiner = 32746, HttpLimitReached = 32745, ExternalHttpCallFailed = 32744,
        SlotError = 32742, InvalidEncryptionParameters = 32741;
}

public static class Cache
{
    public const byte DoNotCache = 0, MergeCache = 1, ReplaceCache = 2, RemoveCache = 3, AddToRoomCache = 4,
        AddToRoomCacheGlobal = 5, RemoveFromRoomCache = 6, RemoveFromRoomCacheForActorsLeft = 7,
        SliceIncreaseIndex = 10, SliceSetIndex = 11, SlicePurgeIndex = 12, SlicePurgeUpToIndex = 13;
}

public static class Receivers { public const byte Others = 0, All = 1, MasterClient = 2; }
