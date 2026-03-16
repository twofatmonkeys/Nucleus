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
    }
}
