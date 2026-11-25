using System.Text.Json;

namespace MeitouP2P;

// The game thread owns callbacks. The network thread exchanges only immutable lobby messages.
internal sealed class Lobby(Session session, Action<string> log)
{
    const string Route = "connector.lobby";
    sealed record Request(bool Ready, Guid Trial, bool Prepared);
    sealed record Reply(Guid Trial, bool Playing, string Status, bool RestartRequired = false);
    readonly object gate = new();
    readonly List<IMultiplayerMode> modes = [];
    internal IReadOnlyList<IMultiplayerMode> Modes => modes;
    internal IMultiplayerMode? Mode { get; private set; }
    internal bool Hosting { get; private set; }
    internal bool Enabled { get; private set; }
    internal bool Playing { get; private set; }
    internal bool Ready { get; private set; }
    internal bool HadPeer { get; private set; }
    internal string? StopReason { get; private set; }
    internal bool Preparing => preparedTrial != Guid.Empty;
    internal bool GuestReady { get { lock (gate) return guest.Ready; } }
    internal string Status { get; private set; } = "Choose Host or Join.";
    internal bool CanStart { get { lock (gate) return Hosting && peer != null && guest.Ready && trial == Guid.Empty; } }
    Guid? peer, trialPeer;
    Guid trial, preparedTrial;
    Request guest = new(false, Guid.Empty, false);
    Reply published = new(Guid.Empty, false, "Waiting for host");
    Task<string>? poll;
    long nextPoll, nextLog;
    bool startRequested, restartRequired;
    internal void Register() => session.Register(Route, 1, Receive);
    internal void RegisterMode(IMultiplayerMode mode)
    {
        if (modes.Any(m => m.Id == mode.Id)) throw new ArgumentException("Duplicate selectable mode.");
        modes.Add(mode);
    }
    internal void Begin(bool host, string mode)
    {
        Mode = modes.Single(m => m.Id == mode);
        if (Mode.Unavailable is { } reason) throw new InvalidOperationException(reason);
        Hosting = host;
        Enabled = true;
        Ready = Playing = false;
        HadPeer = false;
        StopReason = null;
        peer = null;
        trialPeer = null;
        startRequested = restartRequired = false;
        trial = preparedTrial = Guid.Empty;
        poll = null;
        guest = new(false, Guid.Empty, false);
        published = new(Guid.Empty, false, "Waiting for guest");
        Status = host ? "Waiting for guest" : "Connecting";
    }
    internal void MarkReady() { if (Enabled && !Hosting) Ready = true; }
    internal void StartTrial() { if (CanStart) startRequested = true; }
    internal void Rejoin(string ip, int port)
    {
        if (!Enabled || Hosting || Mode == null) return;
        session.Leave();
        poll = null;
        Ready = Playing = false;
        peer = null;
        session.Start(false, ip, port, Mode.Id);
    }
    internal void End()
    {
        session.Leave();
        Mode?.Stop();
        Enabled = Playing = Ready = startRequested = false;
        poll = null;
        lock (gate) { guest = new(false, Guid.Empty, false); published = new(Guid.Empty, false, "Ended"); }
        Status = "Session ended.";
        StopReason = null;
    }
    string Receive(Guid who, string body)
    {
        var request = JsonSerializer.Deserialize<Request>(body) ?? throw new InvalidDataException("Empty lobby request.");
        lock (gate)
        {
            // A new connection cannot inherit the previous peer's readiness or playing reply.
            if (who != peer) return JsonSerializer.Serialize(new Reply(Guid.Empty, false, "Checking new guest"));
            // A reconnect retains its prepared trial id. A fresh lobby after world replacement
            // must not silently join the host's old world and inherit that trial.
            if (request.Ready && published.Trial != Guid.Empty && who != trialPeer && request.Trial != published.Trial)
            {
                restartRequired = true;
                return JsonSerializer.Serialize(new Reply(Guid.Empty, false, "Start a fresh host trial after a world replacement or new guest.", true));
            }
            guest = request;
            return JsonSerializer.Serialize(!request.Ready ? new Reply(Guid.Empty, false, "Choose Ready") :
                published with { Playing = published.Playing && request.Prepared && request.Trial == published.Trial });
        }
    }
    internal void Update(object host)
    {
        if (!Enabled || Mode == null) return;
        try
        {
            lock (gate) if (restartRequired) throw new InvalidOperationException("Start a fresh host trial after a world replacement or new guest.");
            var connection = session.Snapshot;
            if (connection.Peer != null) HadPeer = true;
            if (peer != connection.Peer)
            {
                lock (gate) { peer = connection.Peer; guest = new(false, Guid.Empty, false); }
                Playing = false;
                poll = null;
                if (!Hosting) Ready = false;
                log("Lobby peer changed; gameplay permission cleared.");
            }
            Mode.Update(host, Playing && peer != null);
            if (peer == null) { Status = connection.Message; return; }
            if (Hosting)
            {
                Request remote;
                lock (gate) remote = guest;
                if (startRequested)
                {
                    startRequested = false;
                    if (CanStart)
                    {
                        trial = Guid.NewGuid();
                        lock (gate) trialPeer = peer;
                        Mode.Prepare(true, trial, host);
                        preparedTrial = trial;
                        log($"Starting trial {trial} ({Mode.Id}).");
                    }
                }
                Playing = trial != Guid.Empty && Mode.Prepared && remote.Ready && remote.Prepared && remote.Trial == trial;
                Status = Playing ? "Playing" : trial != Guid.Empty ? "Preparing both worlds" : remote.Ready ? "Guest ready — Start trial" : "Waiting for guest Ready";
                lock (gate) published = new(remote.Ready ? trial : Guid.Empty, Playing, Status);
            }
            else if (!Playing)
            {
                if (poll is { IsCompleted: false }) return;
                if (poll != null)
                {
                    var reply = JsonSerializer.Deserialize<Reply>(poll.GetAwaiter().GetResult())!;
                    poll = null;
                    if (reply.RestartRequired) throw new InvalidOperationException(reply.Status);
                    Status = reply.Status;
                    if (reply.Trial != Guid.Empty && reply.Trial != preparedTrial)
                    {
                        Mode.Prepare(false, reply.Trial, host);
                        trial = preparedTrial = reply.Trial;
                    }
                    Playing = reply.Playing && Mode.Prepared && trial == reply.Trial;
                }
                if (!Playing && Environment.TickCount64 >= nextPoll)
                {
                    nextPoll = Environment.TickCount64 + 150;
                    poll = session.RequestAsync(Route, JsonSerializer.Serialize(new Request(Ready, preparedTrial, Mode.Prepared)));
                }
            }
            if (Environment.TickCount64 >= nextLog)
            {
                nextLog = Environment.TickCount64 + 5000;
                log($"Lobby {Status}; trial {trial}; round trip {connection.RoundTripMs:F0} ms.");
            }
        }
        catch (Exception e)
        {
            string reason = e.GetBaseException().Message;
            if (!Hosting && e.GetBaseException() is IOException or System.Net.Sockets.SocketException or OperationCanceledException or ObjectDisposedException)
            {
                session.Leave();
                Playing = Ready = false;
                poll = null;
                Status = "Connection lost. Rejoin or Leave. " + reason;
                log(Status);
                return;
            }
            End();
            StopReason = reason;
            Status = "Trial stopped: " + reason;
            log(Status);
        }
    }
}
