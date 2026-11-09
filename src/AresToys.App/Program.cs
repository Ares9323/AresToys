using System;
using Velopack;

namespace AresToys.App;

/// <summary>
/// Custom Main so <see cref="VelopackApp"/> can intercept install / uninstall / first-run /
/// update hooks before WPF spins up. WPF's auto-generated Main from App.xaml would create the
/// <see cref="System.Windows.Application"/> object first — by then the Velopack hooks need to
/// run synchronously and exit, so they'd cause the app to flash open before disappearing.
///
/// Hook handling is intentionally minimal: we don't add custom OnFirstRun / OnAfterUpdate
/// callbacks because there's nothing app-specific to do (no migrations, no welcome dialog,
/// no scheduled-task registration). If those needs appear later, plug them into the Build()
/// chain here — that's the canonical Velopack injection point.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The Low integrity helper that hosts a Windows preview handler for Peek: it touches nothing of the app
        // (no hooks, no settings, no single instance guard, no logging), so it is handled before anything else.
        if (AresToys.App.Services.Peek.PeekHandlerHost.IsHostRequest(args))
            return AresToys.App.Services.Peek.PeekHandlerHost.Run(args);

        // Velopack docs: call Build().Run() as the very first line. When invoked with one of the
        // hook arguments (--veloapp-install, --veloapp-uninstall, --veloapp-firstrun, …) it
        // performs the action and calls Environment.Exit, so we never reach the WPF startup
        // path. On normal launches it's effectively a no-op and falls through.
        VelopackApp.Build().Run();

        // Explorer New+ verbs: the exe does the whole job here and exits, with no WPF app, host,
        // database or running instance needed.
        if (AresToys.App.Services.NewPlus.NewPlusHeadless.TryRun(args)) return 0;

        // Other apps asking for AresToys's colour picker: the client asks the running instance over its pipe and
        // prints the answer, so this process never boots WPF. The exit code is the contract (0 picked, 1 cancelled,
        // 2 no running AresToys, 3 bad arguments).
        if (AresToys.App.Services.PickColorClient.IsPickColorRequest(args))
            return AresToys.App.Services.PickColorClient.Run(args);

        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }
}
