using Microsoft.Extensions.DependencyInjection;
using ShareQ.Storage.Blobs;
using ShareQ.Storage.Database;
using ShareQ.Storage.Database.Migrations;
using ShareQ.Storage.ImageEffects;
using ShareQ.Storage.Items;
using ShareQ.Storage.Options;
using ShareQ.Storage.Paths;
using ShareQ.Storage.Protection;
using ShareQ.Storage.Rotation;
using ShareQ.Storage.Settings;

namespace ShareQ.Storage.DependencyInjection;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddShareQStorage(
        this IServiceCollection services,
        Action<StorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null) services.Configure(configure);
        else services.AddOptions<StorageOptions>();

        services.AddSingleton<IPayloadProtector, DpapiPayloadProtector>();
        services.AddSingleton<IStoragePathResolver, StoragePathResolver>();

        services.AddSingleton<IMigration, Migration001InitialSchema>();
        services.AddSingleton<MigrationRunner>(sp =>
            new MigrationRunner(sp.GetServices<IMigration>()));
        services.AddSingleton<IShareQDatabase, ShareQDatabase>();

        services.AddSingleton<ItemSerializer>();
        services.AddSingleton<IItemStore, ItemStore>();
        services.AddSingleton<ICategoryStore, SqliteCategoryStore>();

        services.AddSingleton<IBlobStore, FileSystemBlobStore>();

        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();

        services.AddSingleton<IRotationService, RotationService>();
        services.AddSingleton<CategoryRotationService>();

        services.AddSingleton<IImageEffectPresetStore, SqliteImageEffectPresetStore>();

        return services;
    }
}
