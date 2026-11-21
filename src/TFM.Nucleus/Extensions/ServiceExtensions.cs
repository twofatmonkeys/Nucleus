using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;


namespace TFM.Nucleus
{
    /// <summary>
    /// Fills the application name and version a caller left unset from the entry assembly: its
    /// simple name, and its informational version (without build metadata) or else its assembly
    /// version.
    /// </summary>
    internal sealed class AppIdentityFromEntryAssembly(Assembly? entryAssembly) : Microsoft.Extensions.Options.IPostConfigureOptions<NucleusOptions>
    {
        private readonly string _name = entryAssembly?.GetName().Name ?? "InvalidAssembly";
        private readonly string _version = VersionOf(entryAssembly);

        public void PostConfigure(string? name, NucleusOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.AppName))
                options.AppName = _name;
            if (string.IsNullOrWhiteSpace(options.AppVersion))
                options.AppVersion = _version;
        }

        internal static string VersionOf(Assembly? assembly)
        {
            var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // "1.2.3+commit" carries build metadata after the plus; the version is what precedes it.
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }
            return assembly?.GetName().Version?.ToString() ?? "0.0.0.1";
        }
    }

    public static class ServiceExtensions
    {
        /// <summary>
        /// Registers Nucleus with a writable schema, and the application's name and version read
        /// from the entry assembly.
        /// </summary>
        public static IServiceCollection AddNucleus(this IServiceCollection services) =>
            services.AddNucleus(options => options.ReadOnlySchema = false);

        /// <summary>
        /// Registers Nucleus with the given options. <see cref="NucleusOptions.AppName"/> and
        /// <see cref="NucleusOptions.AppVersion"/> keep the values set here; one left unset is
        /// read from the entry assembly, so a provider always has an application name.
        /// </summary>
        public static IServiceCollection AddNucleus(this IServiceCollection services, Action<NucleusOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            services.Configure(configure);
            // Read once, at registration. TryAddEnumerable, so a repeat call adds no second copy.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IPostConfigureOptions<NucleusOptions>>(
                new AppIdentityFromEntryAssembly(Assembly.GetEntryAssembly())));
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
