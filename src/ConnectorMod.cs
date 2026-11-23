using System.Reflection;
using Meitou.Modding;

namespace MeitouP2P;

public sealed class ConnectorMod : IMod, IMultiplayerApi, IModDependents
{
    private Session session = null!;
    private Task<string>? ping;
    private Lobby lobby = null!;
    public ConnectionStatus Connection => session.Snapshot;
    public string Host(string address, int port, string mode) => Open(true, address, port, mode);
    public string Join(string address, int port, string mode) => Open(false, address, port, mode);
    public string Leave() { lobby.End(); return session.Status; }
    public void RegisterMode(IMultiplayerMode mode) => lobby.RegisterMode(mode);
    private string Open(bool host, string address, int port, string mode)
    {
        if (Connection.Active || lobby.Enabled) throw new InvalidOperationException("Leave the current session first.");
        lobby.Begin(host, mode);
        try { return session.Start(host, address, port, mode); }
        catch { lobby.End(); throw; }
    }
    public Guid? HostedPeer => session.HostedPeer;
    public string Start(bool host, int port) => session.Start(host, port);
    public void Register(string owner, int revision, Func<Guid, string, string> reply) => session.Register(owner, revision, reply);
    public void DependentFailed(string modId) => session.Remove(modId);
    public Task<string> RequestAsync(string owner, string message) => session.RequestAsync(owner, message);

    public void Initialize(ModContext context)
    {
        var game = typeof(IMod).Assembly;
        var build = game.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? game.ManifestModule.ModuleVersionId.ToString();
        session = new Session(build, context.Data.Files.ToArray(), context.Log, Fingerprints.Read(context));
        lobby = new Lobby(session, context.Log);
        lobby.Register();
        NativeMenu.Install(context, this, lobby);
        context.AddDebugCommand("P2P Host", c => Start(true, c.Field), true);
        context.AddDebugCommand("P2P Join", c => Start(false, c.Field), true);
        context.AddDebugCommand("P2P Leave", _ => { ping = null; return Leave(); });
        context.AddDebugCommand("P2P Ping", _ => { ping = session.PingAsync(); return "See P2P Status for reply."; });
        context.AddDebugCommand("P2P Status", _ => ping == null ? session.Status : $"{session.Status} | {ReplyStatus(ping)}");
    }

    private string Start(bool host, string field)
    {
        int port = 42870;
        if (field.Length > 0 && (!int.TryParse(field, out port) || port < (host ? 0 : 1) || port > 65535))
            return "Enter a valid loopback port.";
        ping = null;
        return session.Start(host, port);
    }

    public static string ReplyStatus(Task<string> reply) => reply.IsCompletedSuccessfully ? reply.Result
        : reply.IsFaulted ? reply.Exception!.GetBaseException().Message : reply.IsCanceled ? "Cancelled" : "Waiting for reply";
}
