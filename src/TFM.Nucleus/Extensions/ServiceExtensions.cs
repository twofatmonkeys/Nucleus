using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;


namespace TFM.Nucleus
{
    public static class ServiceExtensions
    {
        public static IServiceCollection AddNucleus(this IServiceCollection services)
        {
            var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "InvalidAssembly";
            var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.1";
            return services.AddNucleus(options =>
            {
                options.ReadOnlySchema = false;
                options.AppName = appName;
                options.AppVersion = appVersion;
            });
        }


        public static IServiceCollection AddNucleus(this IServiceCollection services, Action<NucleusOptions> configure)
        {
            services.Configure(configure);
            // TryAdd, so that calling AddNucleus twice does not register the services twice.
            services.TryAddSingleton<IObjectSchemaProvider, ObjectSchemaProvider>();
            services.TryAddSingleton<INucleusServiceProvider, NucleusServiceProvider>();
            return services;
        }

        /// <summary>
        /// Opt-in: persists host application log entries (at or above NucleusOptions.LogEntityMinimumLevel)
        /// into the Nucleus-managed NucleusLogEntry table via the logging pipeline.
        /// </summary>
        public static IServiceCollection AddNucleusLogging(this IServiceCollection services)
        {
            // TryAddEnumerable keyed on the implementation type: ILoggerProvider is a collection
            // service, so a plain Add would register a second provider on a repeat call and
            // persist every log line twice.
            // The factory is typed to the implementation, not to ILoggerProvider: TryAddEnumerable
            // dedupes on the implementation type and rejects a descriptor whose implementation type
            // is indistinguishable from the service type.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<Microsoft.Extensions.Logging.ILoggerProvider, NucleusLoggerProvider>(sp =>
                    new NucleusLoggerProvider(
                        sp.GetService<Microsoft.Extensions.Options.IOptions<NucleusOptions>>()?.Value)));
            return services;
        }
    }
}
