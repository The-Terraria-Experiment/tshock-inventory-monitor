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
    private RestEndpoints _rest = null!;
    private InvCommands _commands = null!;
    private Command? _rootCommand;
    private bool _mainThreadCaptured;

    public InventoryMonitorPlugin(Main game) : base(game)
    {
        // Load before other plugins' commands so /inv is available early.
        Order = 1;
    }

    public override void Initialize()
    {
        _config = InvMonitorConfig.LoadOrCreate();
        _manager = new InventoryManager(_config);
        _rest = new RestEndpoints(_dispatcher, _manager, _config);
        _commands = new InvCommands(_manager);

        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);

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
        _manager.Tick();       // advance removal-verification retry jobs
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
            if (_rootCommand is not null)
                TShockAPI.Commands.ChatCommands.Remove(_rootCommand);
        }

        base.Dispose(disposing);
    }
}
