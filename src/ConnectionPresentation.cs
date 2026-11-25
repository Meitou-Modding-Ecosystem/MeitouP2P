namespace MeitouP2P;

// Presentation only: does not open connections, poll, or grant gameplay permission.
internal sealed record ConnectionPresentation(string Title, string Guidance)
{
    internal static ConnectionPresentation Describe(Lobby lobby, ConnectionStatus connection)
    {
        if (lobby.StopReason is { } reason)
            return new("Trial stopped", (lobby.Hosting
                ? "The previous trial ended. Host a fresh session, then ask your friend to Join and Ready."
                : "The previous trial ended. Ask the host to start a fresh session, then choose Join and Ready.") + "\n" + reason);
        if (!lobby.Enabled)
            return new("Multiplayer", "Host: choose this PC's address. Guest: paste the host's address, then Join.");
        if (connection.Peer == null)
        {
            if (connection.Active && lobby.Hosting)
                return lobby.HadPeer
                    ? new("Guest disconnected", "Waiting for your guest to Rejoin and Ready. A new guest session may require a fresh trial.")
                    : new("Waiting for guest", "Hosting at " + connection.Address + ". " +
                        (System.Net.IPEndPoint.TryParse(connection.Address, out var endpoint) && System.Net.IPAddress.IsLoopback(endpoint.Address)
                            ? "This address works only on this PC. Join from a second local client."
                            : "Share this PC through Tailscale and send your friend this address."));
            if (connection.Active)
                return new("Connecting", "Contacting the host and checking compatibility. Cancel stops this attempt.");
            return new(lobby.HadPeer ? "Connection lost" : "Could not connect",
                "Check the host address, Tailscale sharing and that the host is listening. Rejoin retries; Leave returns to setup.\n" + connection.Message);
        }
        if (lobby.Playing)
            return new("Playing", "Both players are connected. Close returns to the game; Leave ends your session.");
        if (!lobby.Hosting && !lobby.Ready)
            return new("Choose Ready", "Compatible preview. Choose Ready when you want to play.");
        if (lobby.Hosting && lobby.CanStart)
            return new("Ready to start", "Your guest is ready. Choose Start trial to load both worlds.");
        if (lobby.Hosting && !lobby.GuestReady)
            return new("Waiting for Ready", "Your guest has connected with a compatible preview. Ask them to choose Ready.");
        if (lobby.Preparing)
            return new("Preparing trial", "Waiting for both worlds to finish preparing. Movement unlocks when both are ready.");
        return lobby.Hosting
            ? new("Waiting for Ready", "Your guest has connected with a compatible preview. Ask them to choose Ready.")
            : new("Waiting for host", "You are ready. The host must choose Start trial.");
    }
}
