using CampusNet.Net;
using CampusNet.Services;

// CampusNet - a Photon-protocol (LoadBalancing over ENet-style UDP, GpBinary 1.6) game server for the
// Rec Room client. Runs the name server, master and game roles in one process on the standard ports.
//
//   CAMPUSNET_PUBLIC_HOST   the IP/host clients reach this machine on (what the name server hands out)
//   CAMPUSNET_NS_PORT / CAMPUSNET_MASTER_PORT / CAMPUSNET_GAME_PORT   default 5058 / 5055 / 5056
//   CAMPUSNET_JWT_KEY       the Campus server's HS256 key (validates the photonAuthToken)
//   CAMPUSNET_ALLOW_UNSIGNED=1   accept a plain UserId when no token is presented (testing only)
//   CAMPUSNET_DEBUG=1       log every operation

string publicHost = Environment.GetEnvironmentVariable("CAMPUSNET_PUBLIC_HOST") ?? "127.0.0.1";
int nsPort = int.Parse(Environment.GetEnvironmentVariable("CAMPUSNET_NS_PORT") ?? "5058");
int masterPort = int.Parse(Environment.GetEnvironmentVariable("CAMPUSNET_MASTER_PORT") ?? "5055");
int gamePort = int.Parse(Environment.GetEnvironmentVariable("CAMPUSNET_GAME_PORT") ?? "5056");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var ns = new PhotonUdpServer("name", nsPort);
var master = new PhotonUdpServer("master", masterPort);
var game = new PhotonUdpServer("game", gamePort);
_ = new LoadBalancingApp(Role.NameServer, ns, publicHost, masterPort, gamePort);
_ = new LoadBalancingApp(Role.Master, master, publicHost, masterPort, gamePort);
_ = new LoadBalancingApp(Role.Game, game, publicHost, masterPort, gamePort);

Log.Info($"CampusNet starting: public host {publicHost}, name/{nsPort} master/{masterPort} game/{gamePort}, DH generator {CampusNet.Protocol.PhotonCrypto.Generator}");

JoinBlocks.Start();   // kicked players stay out of the instance they were kicked from

var sweeper = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try { await Task.Delay(1000, cts.Token); } catch { break; }
        try { GameService.Sweep(); } catch (Exception ex) { Log.Warn($"sweep: {ex.Message}"); }
    }
});

await Task.WhenAll(ns.RunAsync(cts.Token), master.RunAsync(cts.Token), game.RunAsync(cts.Token), sweeper);
