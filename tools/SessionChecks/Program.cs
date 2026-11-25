using System.Net;
using System.Net.Sockets;
using MeitouP2P;

static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
static async Task Until(Func<bool> test)
{
    var until = DateTime.UtcNow.AddSeconds(8);
    while (!test()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(20); }
}
static Session Make(string build = "build-a", string hash = "a", int revision = 1)
{
    var s = new Session(build, ["base", "fixture"], _ => { }, new Dictionary<string,string> { ["fixture/roles.json"] = hash });
    s.Register("test", revision, (_, message) => message);
    return s;
}
static int Port(Session s) => IPEndPoint.Parse(s.Snapshot.Address).Port;

var host = Make();
host.Start(true, "127.0.0.1", 0, "test");
var guest = Make();
try
{
    foreach (var (bad,label) in new[] { (Make("build-b"),"game build"), (Make(hash:"b"),"file fixture/roles.json"), (Make(revision:2),"mode revisions") })
    {
        bad.Start(false,"127.0.0.1",Port(host),"test");
        await Until(() => !bad.Snapshot.Active);
        Check(bad.Status.Contains("Incompatible " + label), "refuse changed " + label);
        await Task.Delay(50);
    }
    guest.Start(false,"127.0.0.1",Port(host),"test");
    await Until(() => guest.Snapshot.Peer != null);
    Check(await guest.RequestAsync("test","walk") == "walk", "endpoint-aware request and reply");
    var second = Make();
    second.Start(false,"127.0.0.1",Port(host),"test");
    await Until(() => !second.Snapshot.Active);
    Check(second.Status.Contains("Session full"), "second guest receives session full");
    Check(await guest.RequestAsync("test","still here") == "still here", "second guest preserves existing connection");
    var occupied = Make();
    try { occupied.Start(true,"127.0.0.1",Port(host),"test"); throw new Exception("Expected occupied port"); }
    catch (SocketException) { Check(true,"occupied port reported"); }
    Guid? old = host.HostedPeer;
    guest.Leave();
    await Until(() => host.HostedPeer == null);
    guest.Start(false,"127.0.0.1",Port(host),"test");
    await Until(() => host.HostedPeer != null && guest.Snapshot.Peer != null);
    Check(old != host.HostedPeer, "reconnect changes host peer identity");
}
finally { guest.Leave(); host.Leave(); }

var holding = new TcpListener(IPAddress.Loopback,0);
holding.Start();
var stalled = Make();
stalled.Start(false,"127.0.0.1",((IPEndPoint)holding.LocalEndpoint).Port,"test");
using (var accepted = await holding.AcceptTcpClientAsync())
{
    stalled.Leave();
    Check(!stalled.Snapshot.Active,"cancel during handshake");
}
holding.Stop();
var unavailable = Make();
unavailable.Start(false,"127.0.0.1",((IPEndPoint)hostAddress()).Port,"test");
await Until(() => !unavailable.Snapshot.Active);
Check(unavailable.Status.Contains("Disconnected"),"unreachable host reports connection failure");
static EndPoint hostAddress() { var t = new TcpListener(IPAddress.Loopback,0); t.Start(); var e = t.LocalEndpoint; t.Stop(); return e; }

var slowHost = new Session("a",[],_=>{});
slowHost.Register("slow",1,(_,m)=> { Thread.Sleep(200); return m; });
var slowGuest = new Session("a",[],_=>{});
slowGuest.Register("slow",1,(_,m)=>m);
try
{
    slowHost.Start(true,0);
    slowGuest.Start(false,Port(slowHost));
    await Until(()=>slowGuest.Snapshot.Peer!=null);
    Check(await slowGuest.RequestAsync("slow","latency") == "latency" && slowGuest.Snapshot.RoundTripMs >= 150,"200 ms reply delay retains request ordering");
    var pending = slowGuest.RequestAsync("slow","old");
    slowGuest.Leave();
    try { await pending; throw new Exception("Expected pending cancellation"); }
    catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { Check(true,"disconnect invalidates outstanding request"); }
}
finally { slowHost.Leave(); slowGuest.Leave(); }

var hs = Make(); var gs = Make();
var hl = new Lobby(hs,_=>{}); var gl = new Lobby(gs,_=>{});
hl.Register(); gl.Register();
var hm = new FakeMode(); var gm = new FakeMode();
hl.RegisterMode(hm); gl.RegisterMode(gm);
hl.Begin(true,"test"); gl.Begin(false,"test");
hs.Start(true,"127.0.0.1",0,"test"); gs.Start(false,"127.0.0.1",Port(hs),"test");
Check(ConnectionPresentation.Describe(hl, new(true,true,null,"127.0.0.1:42871","Listening",0)).Title == "Waiting for guest",
    "listening without a guest is not labelled disconnected");
Check(ConnectionPresentation.Describe(gl, new(true,false,null,"127.0.0.1:42871","Connecting",0)).Title == "Connecting",
    "pending connection explains cancellation");
object game = new();
try
{
    await Until(()=> { hl.Update(game); gl.Update(game); return hs.HostedPeer != null && gs.Snapshot.Peer != null; });
    hl.Update(game); gl.Update(game);
    Check(ConnectionPresentation.Describe(gl, gs.Snapshot).Title == "Choose Ready", "compatible guest gets Ready instruction");
    gl.MarkReady();
    await Until(()=> { hl.Update(game); gl.Update(game); return hl.CanStart; });
    Check(!hm.Begun && !gm.Begun,"Ready does not load before host Start");
    Check(ConnectionPresentation.Describe(hl, hs.Snapshot).Title == "Ready to start", "ready host gets Start instruction");
    Check(ConnectionPresentation.Describe(gl, gs.Snapshot).Title == "Waiting for host", "ready guest waits for host");
    hl.StartTrial();
    await Until(()=> { hl.Update(game); gl.Update(game); return hm.Begun && gm.Begun; });
    Check(!hl.Playing && !gl.Playing,"loading worlds cannot play");
    Check(ConnectionPresentation.Describe(hl, hs.Snapshot).Title == "Preparing trial", "loading differs from waiting for Ready");
    hm.Prepared = true;
    for(int i=0;i<15;i++) { hl.Update(game); gl.Update(game); await Task.Delay(20); }
    Check(!hl.Playing && !gl.Playing,"slow guest readiness blocks grants");
    gm.Prepared = true;
    await Until(()=> { hl.Update(game); gl.Update(game); return hl.Playing && gl.Playing; });
    Check(true,"both prepared worlds enter play");
    Check(ConnectionPresentation.Describe(gl, gs.Snapshot).Title == "Playing", "prepared trial shows Playing");
    gs.Leave();
    await Until(()=> { hl.Update(game); gl.Update(game); return !hl.Playing && !gl.Playing; });
    Check(true,"connection loss leaves playing state");
    Check(ConnectionPresentation.Describe(hl, hs.Snapshot).Title == "Guest disconnected", "host explains guest loss while still listening");
    gl.Rejoin("127.0.0.1",Port(hs));
    await Until(()=> { hl.Update(game); gl.Update(game); return hs.HostedPeer != null && gs.Snapshot.Peer != null; });
    for(int i=0;i<15;i++) { hl.Update(game); gl.Update(game); await Task.Delay(20); }
    Check(!gl.Ready && !gl.Playing && !hl.Playing,"rejoin cannot inherit previous peer readiness");
    Check(ConnectionPresentation.Describe(hl, hs.Snapshot).Title == "Waiting for Ready",
        "rejoining guest must Ready even when host world is already prepared");
    gl.MarkReady();
    await Until(()=> { hl.Update(game); gl.Update(game); return hl.Playing && gl.Playing; });
    Check(true,"ready reconnect resumes prepared trial");
    gs.Leave();
    await Until(()=> { hl.Update(game); return hs.HostedPeer == null; });
    var replacement = Make(); var replacementLobby = new Lobby(replacement,_=>{});
    replacementLobby.Register(); replacementLobby.RegisterMode(new FakeMode());
    replacementLobby.Begin(false,"test"); replacement.Start(false,"127.0.0.1",Port(hs),"test");
    try
    {
        await Until(()=> { hl.Update(game); replacementLobby.Update(game); return replacement.Snapshot.Peer != null; });
        replacementLobby.MarkReady();
        await Until(()=> { hl.Update(game); replacementLobby.Update(game); return !hl.Enabled; });
        Check(!hl.Playing && !replacementLobby.Playing && hl.Status.Contains("fresh host trial"),
            "fresh guest after replacement cannot resume the previous trial");
        Check(ConnectionPresentation.Describe(hl, hs.Snapshot).Title == "Trial stopped" && hl.StopReason != null,
            "fresh-trial explanation survives returning to the menu");
    }
    finally { replacementLobby.End(); }
}
finally { hl.End(); gl.End(); }
Console.WriteLine("All transport and lobby checks passed.");

sealed class FakeMode : IMultiplayerMode
{
    public string Id => "test";
    public string Name => "Test";
    public string? Unavailable => null;
    public bool Prepared { get; set; }
    public bool Begun;
    public void Prepare(bool host,Guid trial,object gameHost) { Begun = true; }
    public void Update(object gameHost,bool playing) { }
    public void Stop() { Prepared = Begun = false; }
}
