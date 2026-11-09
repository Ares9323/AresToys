using Microsoft.Extensions.DependencyInjection;

namespace AresToys.Clipboard.DependencyInjection;

public static class ClipboardServiceCollectionExtensions
{
    public static IServiceCollection AddAresToysClipboard(
        this IServiceCollection services,
        Action<CaptureGateOptions>? configureGate = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureGate is not null) services.Configure(configureGate);
        else services.AddOptions<CaptureGateOptions>();

        services.AddSingleton<IForegroundProcessProbe, ForegroundProcessProbe>();
        services.AddSingleton<IClipboardCaptureGate, ClipboardCaptureGate>();
        services.AddSingleton<IClipboardListener, ClipboardListener>();
        services.AddSingleton<IClipboardReader, Win32ClipboardReader>();
        return services;
    }
}
