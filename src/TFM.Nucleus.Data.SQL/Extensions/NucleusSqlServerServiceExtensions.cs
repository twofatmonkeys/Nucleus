using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TFM.Nucleus;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Schema;
using TFM.Nucleus.Data.SQL;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers the SQL Server provider. The PostgreSQL and SQLite registrations have the same four overloads.</summary>
    public static class NucleusSqlServerServiceExtensions
    {
        /// <summary>Configures from the SQL_SERVER, SQL_DATABASE, SQL_USERNAME and SQL_PASSWORD environment variables.</summary>
        public static IServiceCollection AddNucleusSqlServerDataProvider(this IServiceCollection services)
        {
            var SqlServer = Environment.GetEnvironmentVariable("SQL_SERVER") ?? 
                throw new SqlDataProviderException("SQL_SERVER environment variable is missing", DataProviderErrorKind.NotConfigured);
            var SqlDatabase = Environment.GetEnvironmentVariable("SQL_DATABASE")
                ?? throw new SqlDataProviderException("SQL_DATABASE environment variable is missing", DataProviderErrorKind.NotConfigured);
            var SqlUsername = Environment.GetEnvironmentVariable("SQL_USERNAME")
                ?? throw new SqlDataProviderException("SQL_USERNAME environment variable is missing", DataProviderErrorKind.NotConfigured);
            var SqlPassword = Environment.GetEnvironmentVariable("SQL_PASSWORD")
                ?? throw new SqlDataProviderException("SQL_PASSWORD environment variable is missing", DataProviderErrorKind.NotConfigured);

            return services.AddNucleusSqlServerDataProvider(options =>
            {
                options.Server = SqlServer;
                options.Database = SqlDatabase;
                options.Username = SqlUsername;
                options.Password = SqlPassword;
            });
        }

        /// <summary>Registers the SQL Server provider with the given configuration.</summary>
        public static IServiceCollection AddNucleusSqlServerDataProvider(this IServiceCollection services, Action<SQLConfiguration> config) =>
            ProviderRegistration.Add<SQLDataProvider, SQLConfiguration>(services, config);

        /// <summary>
        /// Registers a SQL Server provider rooted at <typeparamref name="TRoot"/>: every entity
        /// whose nearest registered root is <typeparamref name="TRoot"/> routes to this provider
        /// (its own connection, database and schema). Register multiple roots to span databases.
        /// </summary>
        public static IServiceCollection AddNucleusSqlServerDataProvider<TRoot>(this IServiceCollection services, Action<SQLConfiguration> config)
            where TRoot : ICoreObject =>
            services.AddNucleusSqlServerDataProvider(typeof(TRoot), config);

        /// <summary>
        /// Type-based root registration; use for open-generic roots such as
        /// <c>abstract class AppRoot&lt;T&gt; : CoreObject&lt;T&gt;</c> via <c>typeof(AppRoot&lt;&gt;)</c>.
        /// </summary>
        public static IServiceCollection AddNucleusSqlServerDataProvider(this IServiceCollection services, Type rootType, Action<SQLConfiguration> config) =>
            ProviderRegistration.AddRooted<SQLConfiguration>(services, rootType, config, (sp, options) => new SQLDataProvider(
                sp.GetRequiredService<IObjectSchemaProvider>(),
                sp.GetRequiredService<IOptionsMonitor<NucleusOptions>>(),
                options,
                sp.GetService<ILogger<SQLDataProvider>>(),
                sp.GetService<ILogger<SchemaManager>>()));
    }
}
