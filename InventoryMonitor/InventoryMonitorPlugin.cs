using InventoryMonitor.Commands;
using InventoryMonitor.Config;
using InventoryMonitor.Rest;
using InventoryMonitor.Services;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace InventoryMonitor;

/// <summary>
/// TShock 6.1 plugin: read and manage player inventories over REST and in-game commands.
/// </summary>
[ApiVersion(2, 1)]
public sealed class InventoryMonitorPlugin : TerrariaPlugin
{
    public override string Name => "InventoryMonitor";
    public override string Author => "Caleb Dougal";
    public override string Description => "Monitor and manage player inventories via REST and in-game commands.";
    public override Version Version => new(1, 0, 0, 0);

    private readonly MainThreadDispatcher _dispatcher = new();
    private InvMonitorConfig _config = new();
    private InventoryManager _manager = null!;
    private SnapshotService _snapshots = null!;
    private RestEndpoints _rest = null!;
    private InvCommands _commands = null!;
    private Command? _rootCommand;
    private bool _mainThreadCaptured;

    public InventoryMonitorPlugin(Main game) : base(game)
    {
        // Load before other plugins' commands so /inv is available early. Note this also decides
        // hook invocation order, which runs in DESCENDING Order — so 1 puts our ServerLeave handler
        // ahead of TShock's (Order 0), which nulls TShock.Players[who]. SnapshotService does not
        // depend on that, but keep it in mind before lowering this.
        Order = 1;
    }

    public override void Initialize()
    {
        _config = InvMonitorConfig.LoadOrCreate();
        _manager = new InventoryManager();
        _snapshots = new SnapshotService(new SnapshotStore(_config), _config);
        _rest = new RestEndpoints(_dispatcher, _manager, _snapshots, _config);
        _commands = new InvCommands(_manager, _snapshots);

        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
        ServerApi.Hooks.NetGreetPlayer.Register(this, OnGreetPlayer);
        ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);

        _rootCommand = new Command(_commands.Handle, "inv", "inventory")
        {
            HelpText = "Read and manage player inventories. Use /inv for subcommands.",
            AllowServer = true,
        };
        TShockAPI.Commands.ChatCommands.Add(_rootCommand);

        if (TShock.RestApi is not null)
        {
            _rest.Register(TShock.RestApi);
            TShock.Log.ConsoleInfo("[InventoryMonitor] REST endpoints registered under /inventory/*.");
        }
        else
        {
            TShock.Log.ConsoleInfo("[InventoryMonitor] REST API unavailable; in-game commands only.");
        }
    }

    private void OnGameUpdate(EventArgs args)
    {
        if (!_mainThreadCaptured)
        {
            _dispatcher.CaptureMainThread();
            _mainThreadCaptured = true;
        }

        _dispatcher.Process(); // run marshalled REST work on the main thread
        _snapshots.Tick();     // take due join snapshots, age out expired ones
    }

    /// <summary>Fires off the main thread; the capture itself is deferred onto GameUpdate.</summary>
    private void OnGreetPlayer(GreetPlayerEventArgs args) => _snapshots.OnPlayerGreeted(args.Who);

    /// <summary>
    /// Fires on the server loop thread, immediately before Terraria replaces the player object.
    /// The snapshot must therefore be taken inline rather than marshalled.
    /// </summary>
    private void OnServerLeave(LeaveEventArgs args) => _snapshots.OnPlayerLeaving(args.Who);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
            ServerApi.Hooks.NetGreetPlayer.Deregister(this, OnGreetPlayer);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
            if (_rootCommand is not null)
                TShockAPI.Commands.ChatCommands.Remove(_rootCommand);
        }

        base.Dispose(disposing);
    }
}
