using Microsoft.Extensions.DependencyInjection;
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
            services.AddSingleton<IObjectSchemaProvider, ObjectSchemaProvider>();
            services.AddSingleton<INucleusServiceProvider, NucleusServiceProvider>();
            return services;
        }

        /// <summary>
        /// Opt-in: persists host application log entries (at or above NucleusOptions.LogEntityMinimumLevel)
        /// into the Nucleus-managed NucleusLogEntry table via the logging pipeline.
        /// </summary>
        public static IServiceCollection AddNucleusLogging(this IServiceCollection services)
        {
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(sp =>
                new NucleusLoggerProvider(
                    sp.GetService<Microsoft.Extensions.Options.IOptions<NucleusOptions>>()?.Value));
            return services;
        }
    }
}
