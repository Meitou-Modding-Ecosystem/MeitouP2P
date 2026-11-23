using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MeitouP2P;

// Handlers run on a network worker. Publish immutable values or enqueue simulation work.
public interface ISessionApi
{
    Guid? HostedPeer { get; }
    string Start(bool host, int port);
    void Register(string owner, int revision, Func<Guid, string, string> reply);
    Task<string> RequestAsync(string owner, string message);
}
public sealed record ConnectionStatus(bool Active, bool Hosting, Guid? Peer, string Address, string Message, double RoundTripMs);
public interface IMultiplayerApi : ISessionApi
{
    ConnectionStatus Connection { get; }
    string Host(string address, int port, string mode);
    string Join(string address, int port, string mode);
    string Leave();
    void RegisterMode(IMultiplayerMode mode);
}
// Callbacks run on the game thread. Prepare must schedule loading and return immediately.
public interface IMultiplayerMode
{
    string Id { get; }
    string Name { get; }
    string? Unavailable { get; }
    bool Prepared { get; }
    void Prepare(bool host, Guid trial, object gameHost);
    void Update(object gameHost, bool playing);
    void Stop();
}

internal sealed class Session(string build, IReadOnlyList<string> content, Action<string> log,
    IReadOnlyDictionary<string,string>? files = null) : ISessionApi
{
    sealed class Run(bool hosting, IPEndPoint endpoint)
    {
        internal readonly bool Hosting = hosting;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly IPEndPoint Endpoint = endpoint;
        internal TcpListener? Listener;
        internal TcpClient? Peer;
        internal Guid? Id;
        internal TaskCompletionSource<string>? Pending;
        internal bool Occupied;
    }
    internal sealed record Spec(string Build, string[] Content, SortedDictionary<string,int> Modes,
        SortedDictionary<string,string> Files, string Selected);
    readonly object gate = new();
    readonly SortedDictionary<string,(int Revision,Func<Guid,string,string> Reply)> routes = new(StringComparer.Ordinal);
    Run? active;
    string state = "Offline", address = "";
    double rtt;
    public string Status { get { lock (gate) return state; } }
    public Guid? HostedPeer { get { lock (gate) return active is { Hosting: true } r ? r.Id : null; } }
    internal ConnectionStatus Snapshot { get { lock (gate) return new(active != null, active?.Hosting ?? false, active?.Id, address, state, rtt); } }
    public void Register(string owner, int revision, Func<Guid,string,string> reply)
    {
        lock (gate)
        {
            if (active != null || string.IsNullOrWhiteSpace(owner) || owner.Contains('\n') || owner == Ping)
                throw new InvalidOperationException("Register a distinct mode before opening a session.");
            routes.Add(owner, (revision, reply));
        }
    }
    internal void Remove(string owner) { lock (gate) routes.Remove(owner); }
    const string Ping = "connector.ping";
    internal Task<string> PingAsync() => RequestAsync(Ping, "");
    public string Start(bool host, int port) => Start(host, IPAddress.Loopback.ToString(), port, "");
    internal string Start(bool host, string ip, int port, string mode)
    {
        if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork || parsed.Equals(IPAddress.Any)
            || port < (host ? 0 : 1) || port > 65535) throw new ArgumentException("Enter an IPv4 address and a valid port.");
        lock (gate)
        {
            if (active != null) throw new InvalidOperationException("Leave the current session first.");
            if (mode.Length > 0 && !routes.ContainsKey(mode)) throw new ArgumentException("Mode is not installed.");
            var run = new Run(host, new(parsed, port));
            if (host)
            {
                run.Listener = new(run.Endpoint);
                try { run.Listener.Start(4); }
                catch { run.Listener.Stop(); run.Stop.Dispose(); throw; }
                run.Endpoint.Port = ((IPEndPoint)run.Listener.LocalEndpoint).Port;
            }
            var spec = new Spec(build, content.ToArray(), new(routes.ToDictionary(p => p.Key, p => p.Value.Revision), StringComparer.Ordinal),
                new(files?.ToDictionary(p => p.Key,p => p.Value) ?? new(), StringComparer.Ordinal), mode);
            active = run;
            address = run.Endpoint.ToString();
            state = (host ? "Listening on " : "Joining ") + address;
            log(state);
            _ = host ? Accept(run, spec) : Serve(run, new TcpClient(), spec);
            return state;
        }
    }
    public string Leave()
    {
        lock (gate)
        {
            if (active is { } run)
            {
                active = null;
                run.Stop.Cancel();
                run.Pending?.TrySetException(new IOException("Session closed."));
                run.Peer?.Dispose();
                run.Listener?.Stop();
                log("Session closed locally.");
            }
            return state = "Offline";
        }
    }
    async Task Accept(Run run, Spec spec)
    {
        try
        {
            while (!run.Stop.IsCancellationRequested)
            {
                var peer = await run.Listener!.AcceptTcpClientAsync(run.Stop.Token);
                bool full;
                lock (gate) { full = run.Occupied; if (!full) run.Occupied = true; }
                if (full) _ = Refuse(peer);
                else _ = Serve(run, peer, spec);
            }
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    static async Task Refuse(TcpClient peer)
    {
        using (peer)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            try
            {
                // Drain the hello before closing so TCP does not replace the refusal with a reset.
                await Read(peer.GetStream(), timeout.Token);
                await Write(peer.GetStream(), "Session full", timeout.Token);
            }
            catch (Exception) { }
    }
    async Task Serve(Run run, TcpClient peer, Spec spec)
    {
        string last = "Disconnected";
        try
        {
            lock (gate) { if (active != run) throw new OperationCanceledException(); run.Peer = peer; }
            using (var setup = CancellationTokenSource.CreateLinkedTokenSource(run.Stop.Token))
            {
                setup.CancelAfter(TimeSpan.FromSeconds(15));
                if (!run.Hosting) await peer.ConnectAsync(run.Endpoint.Address, run.Endpoint.Port, setup.Token);
                peer.NoDelay = true;
                await Handshake(peer.GetStream(), run.Hosting, spec, setup.Token);
            }
            var id = Guid.NewGuid();
            lock (gate)
            {
                if (active != run) throw new OperationCanceledException();
                run.Id = id;
                state = run.Hosting ? "Hosting a connected guest" : "Joined host";
                log($"{state}; peer {id}; compatible build {build}.");
            }
            var stream = peer.GetStream();
            while (true)
            {
                string frame = await Read(stream, run.Stop.Token);
                if (run.Hosting) await Write(stream, Answer(id, frame), run.Stop.Token);
                else lock (gate)
                {
                    if (run.Pending == null) throw new InvalidDataException("Unexpected reply.");
                    run.Pending.TrySetResult(frame);
                    run.Pending = null;
                }
            }
        }
        catch (Exception e) { last = run.Stop.IsCancellationRequested ? "Offline" : $"Disconnected ({e.GetBaseException().Message})"; }
        finally
        {
            peer.Dispose();
            lock (gate)
            {
                run.Pending?.TrySetException(new IOException(last));
                run.Pending = null;
                run.Id = null;
                run.Peer = null;
                run.Occupied = false;
                if (active == run)
                {
                    state = run.Hosting ? $"Listening on {run.Endpoint}. {last}" : last;
                    if (!run.Hosting) active = null;
                    log(state);
                }
            }
        }
    }
    static async Task Handshake(NetworkStream stream, bool host, Spec spec, CancellationToken token)
    {
        const string prefix = "MeitouP2P/2\n";
        if (!host) await Write(stream, prefix + JsonSerializer.Serialize(spec), token);
        string received = await Read(stream, token);
        if (!host) { if (received != "OK") throw new InvalidDataException(received); return; }
        string? mismatch;
        try { mismatch = received.StartsWith(prefix, StringComparison.Ordinal) ? Compare(spec, JsonSerializer.Deserialize<Spec>(received[prefix.Length..])!) : "protocol"; }
        catch (Exception) { mismatch = "handshake"; }
        string answer = mismatch == null ? "OK" : "Incompatible " + mismatch;
        await Write(stream, answer, token);
        if (mismatch != null) throw new InvalidDataException(answer);
    }
    internal static string? Compare(Spec host, Spec guest)
    {
        if (host.Build != guest.Build) return "game build";
        if (!host.Content.SequenceEqual(guest.Content)) return "FCS load order";
        if (host.Selected != guest.Selected) return "selected mode";
        if (host.Modes.Count != guest.Modes.Count || host.Modes.Any(p => !guest.Modes.TryGetValue(p.Key, out int revision) || revision != p.Value))
            return "mode revisions";
        foreach (string key in host.Files.Keys.Union(guest.Files.Keys).Order(StringComparer.Ordinal))
            if (host.Files.GetValueOrDefault(key) != guest.Files.GetValueOrDefault(key)) return "file " + key;
        return null;
    }
    string Answer(Guid peer, string frame)
    {
        int split = frame.IndexOf('\n');
        if (split < 1) return "Missing mode.";
        string owner = frame[..split];
        if (owner == Ping) return "Pong";
        Func<Guid,string,string>? handler;
        lock (gate) handler = routes.TryGetValue(owner, out var route) ? route.Reply : null;
        if (handler == null) return "Unknown mode.";
        try { return handler(peer, frame[(split + 1)..]); }
        catch (Exception e) { return $"{owner} request failed ({e.Message})"; }
    }
    public async Task<string> RequestAsync(string owner, string message)
    {
        if (owner.Contains('\n')) throw new ArgumentException("Invalid route.", nameof(owner));
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        TcpClient peer;
        lock (gate)
        {
            if (active is not { Hosting: false, Peer: { } client, Id: not null } run) throw new InvalidOperationException("Join a host first.");
            if (run.Pending != null) throw new InvalidOperationException("A request is in progress.");
            peer = client;
            run.Pending = reply;
        }
        long started = Stopwatch.GetTimestamp();
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Write(peer.GetStream(), owner + "\n" + message, limit.Token);
            string result = await reply.Task.WaitAsync(limit.Token);
            lock (gate) rtt = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return result;
        }
        catch { peer.Dispose(); throw; }
    }
    const int MaxFrame = 32768;
    static async Task<string> Read(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[2];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length > MaxFrame) throw new InvalidDataException("Frame is too large.");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return Encoding.UTF8.GetString(data);
    }
    static async Task Write(NetworkStream stream, string message, CancellationToken token)
    {
        int length = Encoding.UTF8.GetByteCount(message);
        if (length > MaxFrame) throw new InvalidDataException("Frame is too large.");
        byte[] frame = new byte[length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)length);
        Encoding.UTF8.GetBytes(message, frame.AsSpan(2));
        await stream.WriteAsync(frame, token);
    }
}
