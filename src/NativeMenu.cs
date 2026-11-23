using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using HarmonyLib;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Modding;

namespace MeitouP2P;

// Only this bridge knows the 0.22.1 title/input internals. Transport and modes remain independent.
internal sealed class NativeMenu(ConnectorMod connector, Lobby lobby, Action<string> log)
{
    const string Screen = "meitou.p2p.lobby", Entry = "meitou.p2p.entry";
    static NativeMenu? active;
    object? player;
    MyGuiScreenHost? screens;
    string error = "";
    int modeIndex, ipIndex;
    readonly string[] ips = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString()).Append("127.0.0.1").Distinct()
        .OrderByDescending(ip => ip.StartsWith("100.", StringComparison.Ordinal)).ThenBy(ip => ip).ToArray();
    string lastAddress = "127.0.0.1:42871";
    internal static object? Read(object owner, string name) => AccessTools.Field(owner.GetType(), name)?.GetValue(owner)
        ?? AccessTools.Property(owner.GetType(), name)?.GetValue(owner);
    internal static void Install(ModContext context, ConnectorMod connector, Lobby lobby)
    {
        var assembly = typeof(IMod).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version?.StartsWith("0.22.1+", StringComparison.Ordinal) != true)
            throw new NotSupportedException("Multiplayer menu requires the stock Meitou 0.22.1 build.");
        var loop = assembly.GetType("Meitou.Game.GameHost", true)!;
        var router = assembly.GetType("Meitou.Game.Ui.ScreenRouter", true)!;
        var player = assembly.GetType("Meitou.Game.PlayerInterface", true)!;
        foreach (var (type, member) in new[] { (loop,"Player"), (player,"FaithfulScreens"), (player,"TitleOpen"), (player,"ClipboardWrite") })
            if (AccessTools.Field(type, member) == null && AccessTools.Property(type, member) == null)
                throw new MissingMemberException(type.FullName, member);
        var frame = AccessTools.DeclaredMethod(loop, "UpdateView", [typeof(int),typeof(int)]) ?? throw new MissingMethodException("GameHost.UpdateView");
        var bind = AccessTools.DeclaredMethod(router, "BindFaithfulScreen", [typeof(string)]) ?? throw new MissingMethodException("ScreenRouter.BindFaithfulScreen");
        context.Harmony.Patch(frame, postfix: new HarmonyMethod(typeof(NativeMenu), nameof(Frame)));
        context.Harmony.Patch(bind, postfix: new HarmonyMethod(typeof(NativeMenu), nameof(Bind)));
        active = new(connector, lobby, context.Log);
    }
    static void Frame(object __instance)
    {
        var menu = active;
        if (menu == null) return;
        try
        {
            object host = __instance;
            if (Read(host, "Player") is not { } player) return;
            menu.Attach(player);
            menu.lobbyUpdate(host);
        }
        catch (Exception e) { menu.error = e.GetBaseException().Message; menu.Report("Multiplayer menu: " + menu.error); active = null; }
    }
    void Report(string message) => log(message);
    void lobbyUpdate(object host)
    {
        lobby.Update(host);
        bool title = Read(player!, "TitleOpen") is true;
        if (!title && lobby.Enabled) { if (!screens!.IsOpen(Entry)) screens.Open(Entry); }
        else screens!.Close(Entry);
    }
    void Attach(object next)
    {
        if (ReferenceEquals(player, next)) return;
        if (screens != null) screens.Activated -= Action;
        player = next;
        screens = (MyGuiScreenHost)Read(next, "FaithfulScreens")!;
        var entry = MyGuiReader.ParseLayout("""
            <MyGUI type="Layout"><Widget type="Button" skin="Kenshi_Button1" position_real="0.72 0.06 0.26 0.06" layer="Info" name="MultiplayerButton"/></MyGUI>
            """);
        screens.RegisterPanel(Entry, entry);
        var title = screens.Layout("title");
        screens.Replace("title", title with { Roots = [..title.Roots, ..entry.Roots] });
        screens.Register(Screen, Layout(), modal: true, centreRoot: true);
        screens.SetText(Screen, "Address", ips[0] + ":42871");
        screens.Activated += Action;
        log("Native Multiplayer screens attached to the player interface.");
    }
    static MyGuiLayout Layout() => MyGuiReader.ParseLayout("""
        <MyGUI type="Layout">
          <Widget type="Window" skin="Kenshi_WindowCX" position_real="0 0 0.76 0.7" layer="Window" name="Root">
            <Property key="Caption" value="MULTIPLAYER"/>
            <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.04 0.025 0.92 0.11" name="Intro"><Property key="WordWrap" value="true"/></Widget>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.04 0.15 0.92 0.08" name="Mode"/>
            <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.04 0.255 0.92 0.05" name="AddressLabel"/>
            <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.04 0.32 0.58 0.09" name="Address"><Property key="MaxTextLength" value="64"/></Widget>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.64 0.32 0.16 0.09" name="Local"/>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.82 0.32 0.14 0.09" name="Copy"/>
            <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.04 0.44 0.92 0.19" name="Status"><Property key="WordWrap" value="true"/></Widget>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.04 0.66 0.28 0.09" name="Host"/>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.36 0.66 0.28 0.09" name="Join"/>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.68 0.66 0.28 0.09" name="Ready"/>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.04 0.78 0.28 0.09" name="Start"/>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.36 0.78 0.28 0.09" name="Rejoin"/>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.68 0.78 0.28 0.09" name="Leave"/>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.68 0.9 0.28 0.07" name="CancelButton"/>
          </Widget>
        </MyGUI>
        """);
    static void Bind(string id, UiBindings __result) => active?.Bindings(id, __result);
    void Bindings(string id, UiBindings b)
    {
        if (id is Entry or "title")
        {
            b.Disabled.Remove("MultiplayerButton");
            b.Properties["MultiplayerButton"] = new Dictionary<string,string> { ["FontPixels"] = "14", ["TextColour"] = "#eee2ca" };
            b.Text["MultiplayerButton"] = lobby.Enabled ? (connector.Connection.Peer == null ? "Session — disconnected" : "Session — " + lobby.Status) : "Multiplayer";
            return;
        }
        if (id != Screen) return;
        var c = connector.Connection;
        var mode = lobby.Modes.ElementAtOrDefault(modeIndex);
        b.Text["Intro"] = "One host + one guest. Share the host PC through Tailscale, then join its private address.";
        b.Text["Mode"] = mode?.Name ?? "No multiplayer mode installed";
        b.Text["AddressLabel"] = "Host address and port (Local cycles this PC's addresses)";
        b.Text["Status"] = error.Length > 0 ? error : (mode?.Unavailable ?? lobby.Status)
            + (c.Peer != null ? $"\nCompatible pack · round trip {c.RoundTripMs:F0} ms" : "");
        foreach (var (key, label) in new[] { ("Local","Local"),("Copy","Copy"),("Host","Host"),("Join","Join"),
            ("Ready",lobby.Ready ? "Ready (set)" : "Ready"),("Start","Start trial"),("Rejoin","Rejoin"),
            ("Leave",c.Active && !c.Hosting && c.Peer == null ? "Cancel" : "Leave"),("CancelButton","Close") }) b.Text[key] = label;
        Enable("CancelButton", true); Enable("Copy", true);
        Enable("Local", !c.Active); Enable("Mode", !lobby.Enabled && lobby.Modes.Count > 1);
        Enable("Host", !lobby.Enabled && mode?.Unavailable == null && mode != null);
        Enable("Join", !lobby.Enabled && mode?.Unavailable == null && mode != null);
        Enable("Ready", lobby.Enabled && !lobby.Hosting && c.Peer != null && !lobby.Ready);
        Enable("Start", lobby.CanStart);
        Enable("Rejoin", lobby.Enabled && !lobby.Hosting && !c.Active);
        Enable("Leave", lobby.Enabled);
        foreach (string key in new[] { "Intro", "AddressLabel", "Status" })
            b.Properties[key] = new Dictionary<string,string> { ["FontPixels"] = "14", ["TextColour"] = "#eee2ca" };
        b.Properties["Address"] = new Dictionary<string,string> { ["FontPixels"] = "14", ["TextColour"] = "#211b15" };
        // Native EditBox/Window templates draw their captions in a child, not the owner.
        foreach (var part in screens!.Layout(Screen).Roots.SelectMany(w => w.DescendantsAndSelf()))
            if (part.Properties.GetValueOrDefault("CaptionOwner") is "Address" or "Root")
                b.Properties[part.Name] = new Dictionary<string,string> { ["FontPixels"] = "14",
                    ["TextColour"] = part.Properties["CaptionOwner"] == "Address" ? "#211b15" : "#eee2ca" };
        void Enable(string key, bool enabled)
        {
            if (enabled) { b.Disabled.Remove(key); b.Actions[key] = key; }
            else b.Disabled.Add(key);
            b.Properties[key] = new Dictionary<string,string> { ["FontPixels"] = "14", ["TextColour"] = enabled ? "#eee2ca" : "#8b8375" };
        }
    }
    void Action(MyGuiEvent e)
    {
        if (e.Widget == "MultiplayerButton" && e.Screen is Entry or "title") { screens!.Open(Screen); return; }
        if (e.Screen != Screen || e.Action is "text" or "select") return;
        try
        {
            error = "";
            switch (e.Widget)
            {
                case "Mode": modeIndex = (modeIndex + 1) % lobby.Modes.Count; break;
                case "Local": ipIndex = (ipIndex + 1) % ips.Length; screens!.SetText(Screen,"Address",ips[ipIndex] + ":42871"); break;
                case "Copy": (Read(player!,"ClipboardWrite") as Action<string>)?.Invoke(connector.Connection.Active ? connector.Connection.Address : screens!.GetText(Screen,"Address")); break;
                case "CancelButton": screens!.Close(Screen); break;
                case "Host": case "Join":
                    lastAddress = screens!.GetText(Screen,"Address").Trim();
                    var endpoint = Parse(lastAddress);
                    if (e.Widget == "Host") connector.Host(endpoint.Address.ToString(), endpoint.Port, lobby.Modes[modeIndex].Id);
                    else connector.Join(endpoint.Address.ToString(), endpoint.Port, lobby.Modes[modeIndex].Id);
                    break;
                case "Ready": lobby.MarkReady(); break;
                case "Start": lobby.StartTrial(); screens!.Close(Screen); break;
                case "Rejoin":
                    lastAddress = screens!.GetText(Screen,"Address").Trim();
                    var join = Parse(lastAddress);
                    lobby.Rejoin(join.Address.ToString(),join.Port);
                    break;
                case "Leave": connector.Leave(); break;
            }
        }
        catch (Exception e2) { error = e2.GetBaseException().Message; log(error); }
    }
    static IPEndPoint Parse(string value) => IPEndPoint.TryParse(value, out var result) && result.AddressFamily == AddressFamily.InterNetwork
        && result.Port is > 0 and <= 65535 ? result : throw new ArgumentException("Use an IPv4 address and port, for example 100.101.102.103:42871.");
}
