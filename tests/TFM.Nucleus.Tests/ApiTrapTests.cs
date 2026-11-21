using TFM.Nucleus.Data.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Registration resolves the application's name and version: what the caller set wins, and
    /// anything left unset comes from the entry assembly. The explicit overload used to leave
    /// AppName null, and every provider then refused to start.
    /// </summary>
    public class AppIdentityTests
    {
        private static NucleusOptions Resolve(Action<IServiceCollection> register)
        {
            var services = new ServiceCollection();
            register(services);
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IOptions<NucleusOptions>>().Value;
        }

        private static string EntryName => Assembly.GetEntryAssembly()?.GetName().Name ?? "InvalidAssembly";

        [Fact]
        public void WithNoOptions_BothComeFromTheEntryAssembly_AndTheSchemaIsWritable()
        {
            var options = Resolve(s => s.AddNucleus());

            Assert.Equal(EntryName, options.AppName);
            Assert.Equal(AppIdentityFromEntryAssembly.VersionOf(Assembly.GetEntryAssembly()), options.AppVersion);
            Assert.False(options.ReadOnlySchema);
        }

        [Fact]
        public void WithOptionsThatSetNeither_BothComeFromTheEntryAssembly()
        {
            var options = Resolve(s => s.AddNucleus(o => o.ReadOnlySchema = true));

            Assert.Equal(EntryName, options.AppName);
            Assert.False(string.IsNullOrWhiteSpace(options.AppVersion));
            Assert.True(options.ReadOnlySchema);
        }

        [Fact]
        public void WhatTheCallerSets_IsKept()
        {
            var options = Resolve(s => s.AddNucleus(o =>
            {
                o.AppName = "PortlyPrimate.Web";
                o.AppVersion = "4.2";
            }));

            Assert.Equal("PortlyPrimate.Web", options.AppName);
            Assert.Equal("4.2", options.AppVersion);
        }

        [Fact]
        public void OneSetAndOneUnset_KeepsTheOne_AndFillsTheOther()
        {
            var options = Resolve(s => s.AddNucleus(o => o.AppName = "PortlyPrimate.Admin"));

            Assert.Equal("PortlyPrimate.Admin", options.AppName);
            Assert.Equal(AppIdentityFromEntryAssembly.VersionOf(Assembly.GetEntryAssembly()), options.AppVersion);
        }

        [Fact]
        public void RegisteringTwice_ResolvesOnce()
        {
            var services = new ServiceCollection();
            services.AddNucleus();
            services.AddNucleus();

            Assert.Single(services, d => d.ServiceType == typeof(IPostConfigureOptions<NucleusOptions>));
        }

        [Fact]
        public void AnInformationalVersion_LosesItsBuildMetadata()
        {
            // This test assembly's informational version carries "+<commit>" when SourceLink stamps it.
            var version = AppIdentityFromEntryAssembly.VersionOf(typeof(AppIdentityTests).Assembly);

            Assert.DoesNotContain('+', version);
            Assert.NotEmpty(version);
        }
    }

    /// <summary>
    /// The option once named CommandTimeoutSeconds is the wait for a free context, and a missing
    /// connection setting is the provider's own exception on every engine.
    /// </summary>
    public class ProviderOptionTrapTests
    {
        [Fact]
        public void PoolWaitTimeout_IsWhatTheOldNameSet()
        {
            var options = new SQLConfiguration();
            Assert.Equal(10, options.PoolWaitTimeoutSeconds);

#pragma warning disable CS0618 // the obsolete name is the subject of the test
            options.CommandTimeoutSeconds = 25;
            Assert.Equal(25, options.PoolWaitTimeoutSeconds);
            options.PoolWaitTimeoutSeconds = 0;
            Assert.Equal(0, options.CommandTimeoutSeconds);
#pragma warning restore CS0618
            Assert.Throws<ArgumentOutOfRangeException>(() => options.PoolWaitTimeoutSeconds = -1);
        }

        [Fact]
        public void AMissingSqlServerSetting_IsNotConfigured()
        {
            var options = new SQLConfiguration { Server = "db", Username = "user", Password = "secret", AppName = "tests" };

            var ex = Assert.Throws<SqlDataProviderException>(() => options.BuildNewSQLConnection(useTransaction: false));

            Assert.Equal(DataProviderErrorKind.NotConfigured, ex.Kind);
            Assert.Contains("database name", ex.Message);
        }
    }

    /// <summary>
    /// A predicate may be handed to a load whole. The untyped overloads took its Body, and the
    /// lambda itself compiled and then failed to translate.
    /// </summary>
    public class WholeLambdaPredicateTests
    {
        [Fact]
        public void Classify_ReadsALambdaAsItsBody()
        {
            System.Linq.Expressions.Expression<Func<TestWidget, bool>> none = w => false;
            System.Linq.Expressions.Expression<Func<TestWidget, bool>> some = w => w.Quantity > 3;

            Assert.Equal(PredicateShape.MatchesNone, ExpressionParser.Classify(none));
            Assert.Equal(ExpressionParser.Classify(some.Body), ExpressionParser.Classify(some));
        }
    }
}
