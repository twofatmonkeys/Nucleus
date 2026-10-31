using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TFM.Nucleus;
using TFM.Nucleus.Data;
using TFM.Nucleus.Data.PostgreSQL;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.Schema;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers the PostgreSQL provider. The overloads mirror AddNucleusSQLDataProvider's.</summary>
    public static class NucleusPostgresServiceExtensions
    {
        /// <summary>
        /// Configures from the PG_SERVER, PG_DATABASE, PG_USERNAME and PG_PASSWORD environment
        /// variables, and PG_PORT when set.
        /// </summary>
        public static IServiceCollection AddNucleusPostgresDataProvider(this IServiceCollection services)
        {
            static string Required(string name) => Environment.GetEnvironmentVariable(name)
                ?? throw new PostgresDataProviderException($"{name} environment variable is missing", DataProviderErrorKind.NotConfigured);

            var server = Required("PG_SERVER");
            var database = Required("PG_DATABASE");
            var username = Required("PG_USERNAME");
            var password = Required("PG_PASSWORD");
            var port = Environment.GetEnvironmentVariable("PG_PORT");
            int? portNumber = null;
            if (port != null)
                portNumber = int.TryParse(port, out var parsed) ? parsed
                    : throw new PostgresDataProviderException($"PG_PORT '{port}' is not a port number", DataProviderErrorKind.NotConfigured);

            return services.AddNucleusPostgresDataProvider(options =>
            {
                options.Server = server;
                options.Database = database;
                options.Username = username;
                options.Password = password;
                if (portNumber is { } p)
                    options.Port = p;
            });
        }

        public static IServiceCollection AddNucleusPostgresDataProvider(this IServiceCollection services, Action<PostgresConfiguration> config)
        {
            services.Configure(config);
            services.AddSingleton<IConfigureOptions<PostgresConfiguration>, ConfigureAppName<PostgresConfiguration>>();
            services.AddSingleton<IDataProvider, PostgresDataProvider>();
            return services;
        }

        /// <summary>
        /// Registers a PostgreSQL provider rooted at <typeparamref name="TRoot"/>: every entity
        /// whose nearest registered root is <typeparamref name="TRoot"/> routes to this provider
        /// (its own connection, database and schema). Register multiple roots to span databases.
        /// </summary>
        public static IServiceCollection AddNucleusPostgresDataProvider<TRoot>(this IServiceCollection services, Action<PostgresConfiguration> config)
            where TRoot : ICoreObject =>
            services.AddNucleusPostgresDataProvider(typeof(TRoot), config);

        /// <summary>
        /// Type-based root registration; use for open-generic roots such as
        /// <c>abstract class AppRoot&lt;T&gt; : CoreObject&lt;T&gt;</c> via <c>typeof(AppRoot&lt;&gt;)</c>.
        /// </summary>
        public static IServiceCollection AddNucleusPostgresDataProvider(this IServiceCollection services, Type rootType, Action<PostgresConfiguration> config)
        {
            var optionsName = rootType.FullName ?? rootType.Name;
            services.Configure(optionsName, config);
            services.AddSingleton<IConfigureOptions<PostgresConfiguration>, ConfigureAppName<PostgresConfiguration>>();
            services.AddSingleton<IDataProvider>(sp => new PostgresDataProvider(
                sp.GetRequiredService<IObjectSchemaProvider>(),
                sp.GetRequiredService<IOptionsMonitor<NucleusOptions>>(),
                new NamedOptionsMonitor<PostgresConfiguration>(sp.GetRequiredService<IOptionsMonitor<PostgresConfiguration>>(), optionsName),
                sp.GetService<ILogger<PostgresDataProvider>>(),
                sp.GetService<ILogger<SchemaManager>>())
            {
                RootType = rootType,
            });
            return services;
        }
    }
}
