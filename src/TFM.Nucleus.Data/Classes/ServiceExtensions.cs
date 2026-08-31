using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TFM.Nucleus;
using TFM.Nucleus.Data;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class NucleusDataServiceExtensions
    {
        /// <summary>
        /// Registers the provider-neutral data services: the provider registry (routing entity
        /// types to their registered providers) and the object data manager. Call alongside
        /// AddNucleus() and one or more provider registrations.
        /// </summary>
        public static IServiceCollection AddNucleusData(this IServiceCollection services)
        {
            services.TryAddSingleton<IDataProviderRegistry>(sp =>
                new DataProviderRegistry(sp.GetServices<IDataProvider>()));
            services.TryAddSingleton<IObjectDataManager>(sp =>
            {
                var manager = new ObjectDataManager(
                    sp.GetRequiredService<IDataProviderRegistry>(),
                    sp.GetRequiredService<IObjectSchemaProvider>(),
                    sp.GetService<ILogger<ObjectDataManager>>(),
                    sp.GetService<ILoggerFactory>(),
                    sp.GetService<Microsoft.Extensions.Options.IOptions<NucleusOptions>>()?.Value);
                // Publish the ambient runtime so the static CoreObject API routes through DI.
                NucleusRuntime.Current = manager;
                return manager;
            });
            return services;
        }
    }
}
