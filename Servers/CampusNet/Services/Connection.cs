using System.Text;
using CampusNet.Net;
using CampusNet.Protocol;

namespace CampusNet.Services;

/// <summary>
/// Per-peer state above the transport: protocol init, the encryption handshake, and dispatch of
/// decoded operation requests to the role (name/master/game) handler. Replies go back plaintext
/// (the client only decrypts messages carrying the 0x80 flag).
/// </summary>
public sealed class Connection
{
    public readonly PhotonUdpServer Server;
    public readonly PhotonUdpServer.Peer Peer;
    public string AppName = "";
    public bool InitDone;
    public bool Proto18;             // client spoke GpBinary 1.8 in its Init (Photon Voice/Chat libs); PUN is 1.6
    public PhotonCrypto? Crypto;
    public string? UserId;           // set by Authenticate
    public string NickName = "";
    public Room? Room;               // game server only
    public Actor? Actor;
    public bool InLobby;
    public readonly DateTime ConnectedAt = DateTime.UtcNow;
    public long Sent, Received;
    public readonly TokenBucket Ops = new(Guard.OpsPerSec, Guard.OpsBurst);
    public readonly TokenBucket EventBytes = new(Guard.EventBytesPerSec, Guard.EventBytesBurst);

    /// <summary>
    /// Charge one operation (and, for RaiseEvent, its payload) against this connection's budget.
    /// False = drop it. A connection that keeps exceeding the budget is kicked and its IP cooled -
    /// that is the RPC-spam / "cheer crash" defence: the flood never reaches the other players.
    /// </summary>
    public bool Charge(int payloadBytes, string what)
    {
        long now = Environment.TickCount64;
        bool ok = Ops.Take(1, now);
        if (ok && payloadBytes > 0) ok = EventBytes.Take(payloadBytes, now);
        if (ok) return true;
        var b = Ops.DroppedSince > 0 ? Ops : EventBytes;
        if (b.DroppedSince == 1) Log.Warn($"{Tag}: over budget ({what}) - dropping (ops {Guard.OpsPerSec}/s, event bytes {Guard.EventBytesPerSec}/s)");
        if (b.DroppedSince >= Guard.OpKickAfter)
        {
            Log.Warn($"{Tag}: kicked - {b.Dropped} operations dropped, {b.DroppedSince} in a row ({what})");
            Server.Kick(Peer, "operation flood", Guard.OpFloodBanSec);   // shorter than the transport ban: a shared NAT is punished less
        }
        return false;
    }

    public Connection(PhotonUdpServer server, PhotonUdpServer.Peer peer) { Server = server; Peer = peer; }

    public string Tag => $"{Server.Name}:{Peer}{(UserId != null ? " u" + UserId : "")}{(Actor != null ? " a" + Actor.Nr : "")}";

    // ---------------------------------------------------------------- sending

    public void SendResponse(byte opCode, short rc, string? debug, Dictionary<byte, object?> p, bool reliable = true)
    {
        var msg = Proto18 ? GpBinary18.OperationResponseMessage(opCode, rc, debug, p) : GpBinary16.OperationResponseMessage(opCode, rc, debug, p);
        Send(msg, reliable);
    }
    public void SendResponse(byte opCode, Dictionary<byte, object?> p) => SendResponse(opCode, Rc.Ok, null, p);
    public void SendError(byte opCode, short rc, string debug) => SendResponse(opCode, rc, debug, new());

    public void SendEvent(byte code, Dictionary<byte, object?> p, bool reliable = true)
        => Send(Proto18 ? GpBinary18.EventMessage(code, p) : GpBinary16.EventMessage(code, p), reliable);

    public void Send(byte[] message, bool reliable = true)
    {
        Sent++;
        if (reliable) Server.SendReliable(Peer, message); else Server.SendUnreliable(Peer, message);
    }

    public void Disconnect(string why) => Server.Disconnect(Peer, why);

    // ---------------------------------------------------------------- receiving

    /// <summary>Decode one message; returns the operation to handle, or null when handled here (init / encryption).</summary>
    public GpBinary16.OperationRequest? Decode(byte[] message, out bool encryptedRequest)
    {
        encryptedRequest = false;
        Received++;
        if (message.Length < 2 || message[0] != GpBinary16.Magic)
        {
            Log.Warn($"{Tag}: bad message magic {message[0]:x2}"); return null;
        }
        byte type = message[1];
        bool encrypted = (type & GpBinary16.EncryptedFlag) != 0;
        type &= 0x7F;
        ReadOnlySpan<byte> body = message.AsSpan(2);

        if (type == GpBinary16.MsgInit)
        {
            // protoMajor, protoMinor, 1e 41 06 19 00, app name (32 bytes)
            Proto18 = body.Length >= 2 && body[1] >= 8;
            AppName = body.Length >= 39 ? Encoding.ASCII.GetString(body.Slice(7, 32)).TrimEnd('\0') : "";
            InitDone = true;
            Send(GpBinary16.InitResponse());
            Log.Info($"{Tag}: init app='{AppName}' protocol 1.{(Proto18 ? 8 : 6)}");
            return null;
        }

        byte[] plain;
        if (encrypted)
        {
            if (Crypto == null) { Log.Warn($"{Tag}: encrypted message before key exchange"); return null; }
            var dec = Crypto.TryDecryptAdaptive(body.ToArray(), b => LooksLikeOperation(b));
            if (dec == null) { Log.Warn($"{Tag}: could not decrypt message type {type} ({body.Length} bytes) - no candidate key parsed"); return null; }
            plain = dec; encryptedRequest = true;
        }
        else plain = body.ToArray();

        if (type == GpBinary16.MsgInternalOperationRequest)
        {
            var iop = ReadOp(plain);
            if (iop.Code == 0)   // InitEncryption: param 1 = client's DH public key
            {
                Crypto = new PhotonCrypto();
                if (iop.Params.TryGetValue(1, out var k) && k is byte[] clientKey) Crypto.DeriveSharedKey(clientKey);
                var kp = new Dictionary<byte, object?> { [1] = Crypto.PublicKey };
                Send(Proto18 ? GpBinary18.OperationResponseMessage(0, 0, null, kp, internalOp: true) : GpBinary16.OperationResponseMessage(0, 0, null, kp, internalOp: true));
                Log.Info($"{Tag}: key exchange done");
            }
            else if (iop.Code == 1)  // Ping (internal) - answer with the client's timestamp echoed
            {
                Send(Proto18 ? GpBinary18.OperationResponseMessage(1, 0, null, iop.Params, internalOp: true) : GpBinary16.OperationResponseMessage(1, 0, null, iop.Params, internalOp: true));
            }
            else Log.Warn($"{Tag}: internal op {iop.Code} ignored");
            return null;
        }
        if (type == GpBinary16.MsgOperationRequest)
            return ReadOp(plain);

        Log.Warn($"{Tag}: unexpected message type {type}");
        return null;
    }

    private GpBinary16.OperationRequest ReadOp(byte[] plain)
    {
        var op = Proto18 ? GpBinary18.ReadOperationRequest(plain) : GpBinary16.ReadOperationRequest(plain);
        op.PayloadBytes = plain.Length;
        return op;
    }

    private bool LooksLikeOperation(byte[] b)
    {
        try { var op = ReadOp(b); return op.Params.Count <= 64; }
        catch { return false; }
    }
}

public static class Log
{
    public static bool Debug = Environment.GetEnvironmentVariable("CAMPUSNET_DEBUG") == "1";
    public static void Info(string s) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {s}");
    public static void Warn(string s) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} WARN {s}");
    public static void Dbg(string s) { if (Debug) Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} dbg {s}"); }
}
