using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// The registration every relational provider shares, so a provider's Add... extensions only
    /// name its types: the options are configured, the application name is copied into them, and
    /// the provider is registered as an <see cref="IDataProvider"/>, for every entity or for the
    /// entities under one root.
    /// </summary>
    public static class ProviderRegistration
    {
        /// <summary>
        /// Registers <typeparamref name="TProvider"/> for every entity not routed to a rooted
        /// provider, configured by <paramref name="config"/>.
        /// </summary>
        public static IServiceCollection Add<TProvider, TOptions>(IServiceCollection services, Action<TOptions> config)
            where TProvider : RelationalDataProvider
            where TOptions : RelationalProviderOptions
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(config);
            services.Configure(config);
            services.AddSingleton<IConfigureOptions<TOptions>, ConfigureAppName<TOptions>>();
            services.AddSingleton<IDataProvider, TProvider>();
            return services;
        }

        /// <summary>
        /// Registers a provider rooted at <paramref name="rootType"/>: every entity whose nearest
        /// registered root it is routes to this provider, which reads its own named options.
        /// <paramref name="create"/> builds the provider from the service provider and those options.
        /// </summary>
        public static IServiceCollection AddRooted<TOptions>(IServiceCollection services, Type rootType, Action<TOptions> config,
            Func<IServiceProvider, IOptionsMonitor<TOptions>, RelationalDataProvider> create)
            where TOptions : RelationalProviderOptions
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(rootType);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(create);
            var optionsName = rootType.FullName ?? rootType.Name;
            services.Configure(optionsName, config);
            services.AddSingleton<IConfigureOptions<TOptions>, ConfigureAppName<TOptions>>();
            services.AddSingleton<IDataProvider>(sp =>
            {
                var provider = create(sp, new NamedOptionsMonitor<TOptions>(sp.GetRequiredService<IOptionsMonitor<TOptions>>(), optionsName));
                provider.RootType = rootType;
                return provider;
            });
            return services;
        }
    }
}
