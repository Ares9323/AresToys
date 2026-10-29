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
    public static void Main(string[] args)
    {
        // Velopack docs: call Build().Run() as the very first line. When invoked with one of the
        // hook arguments (--veloapp-install, --veloapp-uninstall, --veloapp-firstrun, …) it
        // performs the action and calls Environment.Exit, so we never reach the WPF startup
        // path. On normal launches it's effectively a no-op and falls through.
        VelopackApp.Build().Run();

        // Explorer New+ verbs: the exe does the whole job here and exits, with no WPF app, host,
        // database or running instance needed.
        if (AresToys.App.Services.NewPlus.NewPlusHeadless.TryRun(args)) return;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
