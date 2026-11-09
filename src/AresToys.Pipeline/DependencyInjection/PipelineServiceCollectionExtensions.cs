using Microsoft.Extensions.DependencyInjection;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Profiles;
using AresToys.Pipeline.Registry;
using AresToys.Pipeline.Tasks;

namespace AresToys.Pipeline.DependencyInjection;

public static class PipelineServiceCollectionExtensions
{
    public static IServiceCollection AddAresToysPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPipelineProfileStore, SqlitePipelineProfileStore>();
        services.AddSingleton<PipelineProfileSeeder>();

        // Each baked task is registered as IPipelineTask so the registry sees them.
        services.AddSingleton<IPipelineTask, AddToHistoryTask>();
        services.AddSingleton<IPipelineTask, SaveToFileTask>();
        services.AddSingleton<IPipelineTask, UpdateItemUrlTask>();

        services.AddSingleton<IPipelineTaskRegistry, PipelineTaskRegistry>();
        services.AddSingleton<PipelineExecutor>();

        return services;
    }
}
