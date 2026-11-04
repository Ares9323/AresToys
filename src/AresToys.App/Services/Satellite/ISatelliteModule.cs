namespace AresToys.App.Services.Satellite;

/// <summary>Names a module goes by on the satellite command line (<c>--modules=wormholes,launcher</c>) and in
/// <see cref="ISatelliteModule.Name"/>. Lower case.</summary>
public static class SatelliteModuleNames
{
    public const string Wormholes = "wormholes";
    public const string Launcher = "launcher";
    public const string Peek = "peek";
}

/// <summary>A feature that runs inside the satellite process (the non elevated copy of AresToys). To add one:
/// implement this interface and register it in <see cref="SatelliteHost.AddModules"/>, together with the services it
/// needs. The host builds a reduced container with the settings store, theme, localization and icon cache; anything
/// else the module brings itself.
/// <para>Only the modules whose <see cref="Name"/> was passed in <c>--modules=</c> are used. <see cref="Register"/>,
/// <see cref="Start"/> and <see cref="Stop"/> all run on the UI thread. Request handlers and event handlers added to
/// the channel run on the thread pool: marshal to the dispatcher before touching WPF.</para></summary>
#pragma warning disable CA1716 // Stop is the lifecycle name the satellite pieces agreed on; nobody implements this from VB.
public interface ISatelliteModule
{
    /// <summary>Lower case module name, one of <see cref="SatelliteModuleNames"/>.</summary>
    string Name { get; }

    /// <summary>Called once per connection, before the channel reads its first line from the main app: add the request
    /// handlers (<see cref="SatelliteChannel.Handle"/>) and event subscriptions here, so no request can arrive before
    /// they exist. Do not show windows or start work yet.</summary>
    void Register(SatelliteChannel channel, IServiceProvider services);

    /// <summary>Called after the channel is connected and every module is registered: create the windows and start
    /// the work.</summary>
    void Start();

    /// <summary>Called when the satellite shuts down (quit, main app gone, channel closed): close the windows and
    /// release what <see cref="Start"/> created. Must not throw.</summary>
    void Stop();
}
#pragma warning restore CA1716
