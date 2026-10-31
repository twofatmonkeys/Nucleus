using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TFM.Nucleus;
using TFM.Nucleus.Data.Schema;
using TFM.Nucleus.Data.SQL;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ServiceExtensions
    {
        public static IServiceCollection AddNucleusSQLDataProvider(this IServiceCollection services)
        {
            var SqlServer = Environment.GetEnvironmentVariable("SQL_SERVER") ?? 
                throw new SqlDataProviderException("SQL_SERVER environment variable is missing", SqlDataProviderExceptionType.NotConfigured);
            var SqlDatabase = Environment.GetEnvironmentVariable("SQL_DATABASE")
                ?? throw new SqlDataProviderException("SQL_DATABASE environment variable is missing", SqlDataProviderExceptionType.NotConfigured);
            var SqlUsername = Environment.GetEnvironmentVariable("SQL_USERNAME")
                ?? throw new SqlDataProviderException("SQL_USERNAME environment variable is missing", SqlDataProviderExceptionType.NotConfigured);
            var SqlPassword = Environment.GetEnvironmentVariable("SQL_PASSWORD")
                ?? throw new SqlDataProviderException("SQL_PASSWORD environment variable is missing", SqlDataProviderExceptionType.NotConfigured);

            return services.AddNucleusSQLDataProvider(options =>
            {
                options.Server = SqlServer;
                options.Database = SqlDatabase;
                options.Username = SqlUsername;
                options.Password = SqlPassword;
            });
        }

        public static IServiceCollection AddNucleusSQLDataProvider(this IServiceCollection services, Action<SQLConfiguration> config)
        {
            services.Configure(config);
            services.AddSingleton<IConfigureOptions<SQLConfiguration>, ConfigureSQLOptions>();
            services.AddSingleton<IDataProvider, SQLDataProvider>();
            return services;
        }

        /// <summary>
        /// Registers a SQL Server provider rooted at <typeparamref name="TRoot"/>: every entity
        /// whose nearest registered root is <typeparamref name="TRoot"/> routes to this provider
        /// (its own connection, database and schema). Register multiple roots to span databases.
        /// </summary>
        public static IServiceCollection AddNucleusSQLDataProvider<TRoot>(this IServiceCollection services, Action<SQLConfiguration> config)
            where TRoot : ICoreObject =>
            services.AddNucleusSQLDataProvider(typeof(TRoot), config);

        /// <summary>
        /// Type-based root registration; use for open-generic roots such as
        /// <c>abstract class AppRoot&lt;T&gt; : CoreObject&lt;T&gt;</c> via <c>typeof(AppRoot&lt;&gt;)</c>.
        /// </summary>
        public static IServiceCollection AddNucleusSQLDataProvider(this IServiceCollection services, Type rootType, Action<SQLConfiguration> config)
        {
            var optionsName = rootType.FullName ?? rootType.Name;
            services.Configure(optionsName, config);
            services.AddSingleton<IConfigureOptions<SQLConfiguration>, ConfigureSQLOptions>();
            services.AddSingleton<IDataProvider>(sp => new SQLDataProvider(
                sp.GetRequiredService<IObjectSchemaProvider>(),
                sp.GetRequiredService<IOptionsMonitor<NucleusOptions>>(),
                new NamedOptionsMonitor<SQLConfiguration>(sp.GetRequiredService<IOptionsMonitor<SQLConfiguration>>(), optionsName),
                sp.GetService<ILogger<SQLDataProvider>>(),
                sp.GetService<ILogger<SchemaManager>>())
            {
                RootType = rootType,
            });
            return services;
        }
    }

    /// <summary>Presents one named options instance as an IOptionsMonitor's current value.</summary>
    internal sealed class NamedOptionsMonitor<TOptions>(IOptionsMonitor<TOptions> inner, string name) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue => inner.Get(name);
        public TOptions Get(string? optionsName) => inner.Get(optionsName ?? name);
        public IDisposable? OnChange(Action<TOptions, string?> listener) => inner.OnChange(listener);
    }
}
