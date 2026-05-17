using Microsoft.Extensions.DependencyInjection;
using AresToys.Storage.Blobs;
using AresToys.Storage.Database;
using AresToys.Storage.Database.Migrations;
using AresToys.Storage.ImageEffects;
using AresToys.Storage.Items;
using AresToys.Storage.Options;
using AresToys.Storage.Paths;
using AresToys.Storage.Protection;
using AresToys.Storage.Rotation;
using AresToys.Storage.Settings;

namespace AresToys.Storage.DependencyInjection;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddAresToysStorage(
        this IServiceCollection services,
        Action<StorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null) services.Configure(configure);
        else services.AddOptions<StorageOptions>();

        services.AddSingleton<IPayloadProtector, DpapiPayloadProtector>();
        services.AddSingleton<IStoragePathResolver, StoragePathResolver>();

        services.AddSingleton<IMigration, Migration001InitialSchema>();
        services.AddSingleton<IMigration, Migration002AddItemLabel>();
        services.AddSingleton<IMigration, Migration003AddPinSortOrder>();
        services.AddSingleton<IMigration, Migration004AddItemTrigger>();
        services.AddSingleton<MigrationRunner>(sp =>
            new MigrationRunner(sp.GetServices<IMigration>()));
        services.AddSingleton<IAresToysDatabase, AresToysDatabase>();

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
