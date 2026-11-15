using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeitouP2P;

// A handler runs on the connector worker. Copy data or queue an intent for the game's tick.
public interface ISessionApi
{
    Guid? HostedPeer { get; }
    string Start(bool host, int port);
    void Register(string owner, int revision, Func<Guid, string, string> reply);
    Task<string> RequestAsync(string owner, string message);
}

internal sealed class Session(string build, IReadOnlyList<string> content, Action<string> log) : ISessionApi
{
    private sealed class Run(bool hosting)
    {
        public readonly bool Hosting = hosting;
        public readonly CancellationTokenSource Stop = new();
        public TcpListener? Listener;
        public TcpClient? Peer;
        public Guid? PeerId;
        public TaskCompletionSource<string>? Pending;
    }

    private readonly object gate = new();
    private readonly SortedDictionary<string, (int Revision, Func<Guid, string, string> Reply)> routes = new(StringComparer.Ordinal);
    private Run? active;
    private string state = "Offline";
    public string Status { get { lock (gate) return state; } }
    public Guid? HostedPeer { get { lock (gate) return active is { Hosting: true } run ? run.PeerId : null; } }

    public void Register(string owner, int revision, Func<Guid, string, string> reply)
    {
        lock (gate)
        {
            if (active != null || string.IsNullOrWhiteSpace(owner) || owner.Contains('\n') || owner == Ping)
                throw new InvalidOperationException("Register a distinct mode before opening a session.");
            routes.Add(owner, (revision, reply));
        }
    }

    internal void Remove(string owner) { lock (gate) routes.Remove(owner); }

    private const string Ping = "connector.ping";
    internal Task<string> PingAsync() => RequestAsync(Ping, "");

    public string Start(bool host, int port)
    {
        lock (gate)
        {
            if (active != null) return "Leave the current session first.";
            var run = new Run(host);
            if (host)
            {
                run.Listener = new TcpListener(IPAddress.Loopback, port);
                try { run.Listener.Start(1); }
                catch { run.Listener.Stop(); run.Stop.Dispose(); throw; }
                port = ((IPEndPoint)run.Listener.LocalEndpoint).Port;
            }
            // Route revisions and the exact game build matter even when two DLLs have the same assembly version.
            var spec = JsonSerializer.SerializeToUtf8Bytes(new { build, content, modes = routes.Select(p => new { p.Key, p.Value.Revision }) });
            string hello = "MeitouP2P/1 " + Convert.ToHexString(SHA256.HashData(spec));
            active = run;
            state = host ? $"Listening on 127.0.0.1:{port}" : $"Joining 127.0.0.1:{port}";
            log(state);
            _ = Task.Run(() => RunAsync(run, port, hello));
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

    private async Task RunAsync(Run run, int port, string hello)
    {
        string last = "Offline";
        var stop = run.Stop.Token;
        try
        {
            do
            {
                using var peer = run.Hosting ? await run.Listener!.AcceptTcpClientAsync(stop) : new TcpClient();
                try
                {
                    using (var setup = CancellationTokenSource.CreateLinkedTokenSource(stop))
                    {
                        setup.CancelAfter(TimeSpan.FromSeconds(5));
                        if (!run.Hosting) await peer.ConnectAsync(IPAddress.Loopback, port, setup.Token);
                        peer.NoDelay = true;
                        await Handshake(peer.GetStream(), run.Hosting, hello, setup.Token);
                    }
                    var id = Guid.NewGuid();
                    lock (gate)
                    {
                        stop.ThrowIfCancellationRequested();
                        run.Peer = peer;
                        run.PeerId = id;
                        state = run.Hosting ? "Hosting a connected guest" : "Joined host";
                        log(state);
                    }
                    var stream = peer.GetStream();
                    while (true)
                    {
                        string frame = await Read(stream, stop);
                        if (run.Hosting) await Write(stream, Answer(id, frame), stop);
                        else
                        {
                            lock (gate)
                            {
                                if (run.Pending == null) throw new InvalidDataException("Unexpected reply.");
                                run.Pending.TrySetResult(frame);
                                run.Pending = null;
                            }
                        }
                    }
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
                {
                    last = stop.IsCancellationRequested ? "Offline" : $"Disconnected ({e.Message})";
                    if (!stop.IsCancellationRequested) log(last);
                }
                finally
                {
                    lock (gate)
                    {
                        run.Pending?.TrySetException(new IOException(last));
                        run.Pending = null;
                        run.Peer = null;
                        run.PeerId = null;
                        if (active == run && run.Hosting) state = $"Listening on 127.0.0.1:{port}. {last}";
                    }
                }
            } while (run.Hosting && !stop.IsCancellationRequested);
        }
        catch (Exception e) { last = $"Offline ({e.Message})"; }
        finally
        {
            lock (gate)
            {
                run.Listener?.Stop();
                if (active == run) { active = null; state = last; }
                run.Stop.Dispose();
            }
        }
    }

    private static async Task Handshake(NetworkStream stream, bool host, string hello, CancellationToken token)
    {
        if (!host) await Write(stream, hello, token);
        string received = await Read(stream, token);
        if (host) await Write(stream, received == hello ? "OK" : "Incompatible protocol, build, mod order or mode", token);
        if (received != (host ? hello : "OK"))
            throw new InvalidDataException("Incompatible protocol, build, mod order or mode.");
    }

    private string Answer(Guid peer, string frame)
    {
        int split = frame.IndexOf('\n');
        if (split < 1) return "Missing mode.";
        string owner = frame[..split];
        if (owner == Ping) return "Pong";
        Func<Guid,string,string>? handler;
        lock (gate) handler = routes.TryGetValue(owner, out var route) ? route.Reply : null;
        if (handler == null) return "Unknown mode.";
        try { return handler(peer, frame[(split + 1)..]); }
        catch (Exception error) { return $"{owner} request failed ({error.Message})"; }
    }

    public async Task<string> RequestAsync(string owner, string message)
    {
        if (owner.Contains('\n')) throw new ArgumentException("Invalid route.", nameof(owner));
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        TcpClient peer;
        lock (gate)
        {
            if (active is not { Hosting: false, Peer: { } client } run) throw new InvalidOperationException("Join a host first.");
            if (run.Pending != null) throw new InvalidOperationException("A request is in progress.");
            peer = client;
            run.Pending = reply;
        }
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Write(peer.GetStream(), owner + "\n" + message, limit.Token);
            return await reply.Task.WaitAsync(limit.Token);
        }
        catch
        {
            // The next request must never consume a late reply to this one.
            peer.Dispose();
            throw;
        }
    }

    private const int MaxFrame = 8192;
    private static async Task<string> Read(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[2];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length > MaxFrame) throw new InvalidDataException("Frame is too large.");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return Encoding.UTF8.GetString(data);
    }

    private static async Task Write(NetworkStream stream, string message, CancellationToken token)
    {
        int length = Encoding.UTF8.GetByteCount(message);
        if (length > MaxFrame) throw new InvalidDataException("Frame is too large.");
        byte[] frame = new byte[length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)length);
        Encoding.UTF8.GetBytes(message, frame.AsSpan(2));
        await stream.WriteAsync(frame, token);
    }
}
