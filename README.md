# MeitouP2P

A C# session connector for **one host and one guest**, targeting stock Windows **MeitouClient 0.22.1**. Version 0.2 adds a native Multiplayer entry, address-based TCP connections, a readiness lobby, and file-content compatibility checks. The connector starts offline. The first selectable mode is MeitouP2P-Coop's Stoat Walk preview. [Co-op source and replay](https://github.com/Meitou-Modding-Ecosystem/MeitouP2P-Coop) · [Volunteer setup and trial](https://github.com/Meitou-Modding-Ecosystem/MeitouP2P-Coop/blob/main/docs/remote-playtest.md)

The remote preview uses a private network such as Tailscale. Network identity and encryption come from that private network. Public internet exposure and discovery are outside this preview. The two-PC volunteer result is recorded separately from local tests.

Development continues in the Meitou Modding Ecosystem organization. Contributions can extend the connector, add independent modes, or improve the reproducible tests. Include the game build and the test result with a pull request. The previous 0.20.0 and 0.20.1 implementation is preserved in the merged baseline. [0.20.x source and instructions](https://github.com/Meitou-Modding-Ecosystem/MeitouP2P/tree/7a75e47075e77e4a2b2a7d29ffc62b1016143310)

## Build just the connector

Install Git, PowerShell, Python 3 and the .NET 11 SDK. Extract the Windows x64 MeitouClient 0.22.1 release and set `MEITOU_DIR` to the directory containing `MeitouClient.dll`. Use that directory for the build and for launching the windows. The visible game also requires your own Kenshi installation. [MeitouClient 0.22.1](https://github.com/VlcekM/MeitouClient/releases/tag/v0.22.1)

```powershell
git clone https://github.com/Meitou-Modding-Ecosystem/MeitouP2P.git
cd MeitouP2P
.\build.ps1 -MeitouDir $env:MEITOU_DIR
python tools/smoke.py --connector-only
```

The build stages `artifacts/mods/MeitouP2P` and stamps the game release into its manifest. Build again when switching game releases. Copy the staged directory into the Kenshi `mods` folder used by each test client and enable it in each launcher. In disposable games, open F10. Choose **P2P Host** in one window and **P2P Join** in the other. Both use port 42870 by default. **P2P Ping** followed by **P2P Status** shows the reply. **P2P Leave** closes the session. Rejoining establishes a new peer identity.

To check the build boundary, set `MEITOU_OTHER_DIR` to an extracted 0.20.1 release and build the preserved connector baseline in a second checkout. Both peers load their own release's DLLs with the same fixture and mode list. The test checks that the host refuses the mixed pair and then connects a matching guest and pings.

```powershell
git worktree add --detach ../MeitouP2P-other 7a75e47075e77e4a2b2a7d29ffc62b1016143310
../MeitouP2P-other/build.ps1 -MeitouDir $env:MEITOU_OTHER_DIR
python tools/smoke.py --connector-only --other-build-checkout ../MeitouP2P-other
```

## Build and test with Co-op

From the connector checkout, clone Co-op as a sibling. The combined test uses two processes, the released loader, a synthetic flat world, and the game's movement system. It checks refusal of incompatible modes and stale grants, disconnect/rejoin and replacement of the host world.

```powershell
git clone https://github.com/Meitou-Modding-Ecosystem/MeitouP2P-Coop.git ../MeitouP2P-Coop
.\build.ps1 -MeitouDir $env:MEITOU_DIR -CoopDir ../MeitouP2P-Coop
python tools/smoke.py
```

The stage now contains both installable mod directories. If using a specific .NET 11 executable, pass `-Dotnet <path>` to the PowerShell scripts and `--dotnet <path>` to the smoke test. See the Co-op provider notes for the optional tagged Maestro export check. [Maestro provider and test instructions](https://github.com/Meitou-Modding-Ecosystem/MeitouP2P-Coop/blob/main/docs/maestro.md)

## Walk Stoat Scout in two windows

After the combined build, set `KENSHI_PATH` to your installed Kenshi folder. Start two stock clients on the same Windows desktop with this command.

```powershell
.\tools\stoat-windows.ps1 -Meitou $env:MEITOU_DIR -Kenshi $env:KENSHI_PATH
```

The launcher creates disposable working directories under `artifacts`, disables autosave and shares game data through a directory junction. Buildings and foliage load in both windows. Pick **Host session** on the left and **Join host** on the right. Once the guest shows a host tick, select the Scout by her body and right-click walkable ground. The host can move the Guide at the same time. In the guest window an empty-ground click clears selection.

To replay the automated check, start a fresh pair and run the following once both games and their control channels are ready. It places the windows side by side, uses screen-coordinate clicks and A/D camera input, and retains captures and transcripts in the trial folder.

```powershell
python tools/stoat-native-walk.py (Get-Content artifacts/stoat-current.txt)
```

## Register a mode

Declare `meitou.p2p` in the mode's `meitou.json` dependencies and reference the connector assembly with copying disabled. During initialization, obtain `context.Dependency<ISessionApi>("meitou.p2p")` and call `Register(context.Id, revision, handler)`. On the guest, `RequestAsync(context.Id, message)` returns the host's reply. The host's handler receives a connection-scoped peer ID and runs on a network worker. Read an immutable published value or queue work for a serial simulation tick. [Session interface](src/Session.cs)

The protocol-2 handshake compares the exact game build, selected mode, registered route revisions, ordered FCS filenames, and SHA-256 hashes of loaded FCS files and enabled mod DLL/JSON files. It reports the first incompatible component. It supports one outstanding request per guest. The lobby yields that channel to the mode when both worlds are ready.

For the Multiplayer menu, obtain `context.Dependency<IMultiplayerApi>("meitou.p2p")`, register the request handler, and call `RegisterMode` with an `IMultiplayerMode`. `Prepare` schedules the mode's world setup and returns immediately. `Prepared` becomes true only when the world is ready. `Update` runs on the game thread and receives whether both peers may play. `Stop` invalidates the mode's local state. The mode owns gameplay rules and world preparation. P2P owns Host, Join, Leave, the endpoint, and connection status. `Start(bool, int)` remains the loopback test interface.

A reconnect retains its prepared trial ID and requires Ready again. A fresh lobby after a world replacement cannot inherit the previous trial: the host must Leave and start a fresh session. Mode callbacks must stop accepting gameplay input whenever `playing` is false.

`dotnet run --project tools/SessionChecks -c Release` exercises compatibility, a second guest, cancellation, delayed replies and readiness transitions. For a visible menu-started pair, use `tools/stoat-native-walk.py <run-folder> --connected`. `tools/loopback-proxy.py` and `tools/stoat-reconnect.py` test connection loss and fresh grants using only loopback sockets. [Local menu validation and captures](https://github.com/Meitou-Modding-Ecosystem/MeitouP2P-Coop/blob/main/docs/remote-results.md)

Host binds only the specified IPv4 address. Join accepts the host's IPv4 address and port. A second guest is refused as **Session full**. The Windows 0.22.1 menu bridge is isolated from transport code. [Public interfaces](src/Session.cs)

## Package the volunteer preview

From clean matching source checkouts, run the following. The ZIP includes original compiled mods, the generated fixture, player scripts, licenses, hashes, and both source commit IDs. A package made from uncommitted sources is explicitly marked in `preview.json`.

```powershell
./tools/package-preview.ps1 -MeitouDir $env:MEITOU_DIR -CoopDir ../MeitouP2P-Coop
dotnet run --project tools/SessionChecks -c Release
```

The transport/lobby checks cover changed content, occupied ports, a second guest, failed/cancelled connections, delayed replies, interrupted requests, and readiness gating. The volunteer uses the compiled ZIP with their own game installations and does not need the SDK or Python.

When reporting a problem, include the exact build, mods in load order, what each player did and the two status lines. A standalone ping tests the connector. Co-op's grant and host-tick logs trace gameplay orders. [Stoat test results](https://github.com/Meitou-Modding-Ecosystem/MeitouP2P-Coop/blob/main/docs/stoat-results.md) · [Licensing](LICENSING.md)
