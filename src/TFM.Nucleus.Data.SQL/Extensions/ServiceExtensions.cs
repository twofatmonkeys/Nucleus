using TFM.Nucleus;
using TFM.Nucleus.Data.SQL;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// The old names of the SQL Server registration. <see cref="NucleusSqlServerServiceExtensions"/>
    /// holds the registration itself.
    /// </summary>
    public static class ServiceExtensions
    {
        [Obsolete("Use AddNucleusSqlServerDataProvider. Every other SQL Server name in Nucleus is spelled SqlServer.")]
        public static IServiceCollection AddNucleusSQLDataProvider(this IServiceCollection services) =>
            services.AddNucleusSqlServerDataProvider();

        [Obsolete("Use AddNucleusSqlServerDataProvider. Every other SQL Server name in Nucleus is spelled SqlServer.")]
        public static IServiceCollection AddNucleusSQLDataProvider(this IServiceCollection services, Action<SQLConfiguration> config) =>
            services.AddNucleusSqlServerDataProvider(config);

        [Obsolete("Use AddNucleusSqlServerDataProvider. Every other SQL Server name in Nucleus is spelled SqlServer.")]
        public static IServiceCollection AddNucleusSQLDataProvider<TRoot>(this IServiceCollection services, Action<SQLConfiguration> config)
            where TRoot : ICoreObject =>
            services.AddNucleusSqlServerDataProvider<TRoot>(config);

        [Obsolete("Use AddNucleusSqlServerDataProvider. Every other SQL Server name in Nucleus is spelled SqlServer.")]
        public static IServiceCollection AddNucleusSQLDataProvider(this IServiceCollection services, Type rootType, Action<SQLConfiguration> config) =>
            services.AddNucleusSqlServerDataProvider(rootType, config);
    }
}
