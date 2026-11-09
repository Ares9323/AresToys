using Microsoft.Extensions.DependencyInjection;

namespace AresToys.Capture.DependencyInjection;

public static class CaptureServiceCollectionExtensions
{
    public static IServiceCollection AddAresToysCapture(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ICaptureSource, BitBltCaptureSource>();
        return services;
    }
}
