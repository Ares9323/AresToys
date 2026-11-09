using Microsoft.Extensions.DependencyInjection;
using AresToys.Core.Pipeline;
using AresToys.Plugins.Tasks;

namespace AresToys.Plugins.DependencyInjection;

/// <summary>
/// Registers the host-side glue for the plugin system: HTTP factory shared with plugins, the
/// <c>UploadTask</c> pipeline step. Plugin implementations themselves (uploaders, ...) come from
/// either the bundled plugin projects (referenced by the App) or the runtime plugin folder
/// loaded by <c>PluginLoader</c>.
/// </summary>
public static class PluginsServiceCollectionExtensions
{
    public static IServiceCollection AddAresToysPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient();
        services.AddSingleton<IPipelineTask, UploadTask>();

        return services;
    }
}
