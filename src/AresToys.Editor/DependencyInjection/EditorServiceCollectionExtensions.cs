using Microsoft.Extensions.DependencyInjection;
using AresToys.Editor.ViewModels;
using AresToys.Editor.Views;

namespace AresToys.Editor.DependencyInjection;

public static class EditorServiceCollectionExtensions
{
    public static IServiceCollection AddAresToysEditor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTransient<EditorViewModel>();
        services.AddTransient<EditorWindow>();
        return services;
    }
}
