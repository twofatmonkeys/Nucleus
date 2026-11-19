using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TFM.Nucleus;
using TFM.Nucleus.Data;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Schema;
using TFM.Nucleus.Data.SQLite;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers the SQLite provider. The overloads mirror the other providers' registrations.</summary>
    public static class NucleusSqliteServiceExtensions
    {
        /// <summary>Configures the database file from the SQLITE_DATABASE environment variable.</summary>
        public static IServiceCollection AddNucleusSqliteDataProvider(this IServiceCollection services)
        {
            var database = Environment.GetEnvironmentVariable("SQLITE_DATABASE")
                ?? throw new SqliteDataProviderException("SQLITE_DATABASE environment variable is missing", DataProviderErrorKind.NotConfigured);
            return services.AddNucleusSqliteDataProvider(options => options.Database = database);
        }

        public static IServiceCollection AddNucleusSqliteDataProvider(this IServiceCollection services, Action<SqliteConfiguration> config) =>
            ProviderRegistration.Add<SqliteDataProvider, SqliteConfiguration>(services, config);

        /// <summary>
        /// Registers a SQLite provider rooted at <typeparamref name="TRoot"/>: every entity whose
        /// nearest registered root is <typeparamref name="TRoot"/> routes to this provider (its own
        /// database file). Register multiple roots to span databases.
        /// </summary>
        public static IServiceCollection AddNucleusSqliteDataProvider<TRoot>(this IServiceCollection services, Action<SqliteConfiguration> config)
            where TRoot : ICoreObject =>
            services.AddNucleusSqliteDataProvider(typeof(TRoot), config);

        /// <summary>
        /// Type-based root registration; use for open-generic roots such as
        /// <c>abstract class AppRoot&lt;T&gt; : CoreObject&lt;T&gt;</c> via <c>typeof(AppRoot&lt;&gt;)</c>.
        /// </summary>
        public static IServiceCollection AddNucleusSqliteDataProvider(this IServiceCollection services, Type rootType, Action<SqliteConfiguration> config) =>
            ProviderRegistration.AddRooted<SqliteConfiguration>(services, rootType, config, (sp, options) => new SqliteDataProvider(
                sp.GetRequiredService<IObjectSchemaProvider>(),
                sp.GetRequiredService<IOptionsMonitor<NucleusOptions>>(),
                options,
                sp.GetService<ILogger<SqliteDataProvider>>(),
                sp.GetService<ILogger<SchemaManager>>()));
    }
}
