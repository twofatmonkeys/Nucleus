using System.Reflection;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A provider package exposes its registration, its configuration and the exception its
    /// registration can throw, and nothing else. Applications code against TFM.Nucleus.Data and
    /// touch a provider only to register and configure it, so any other public type is an
    /// implementation detail leaking into consumers. Widening an allow-list is a deliberate
    /// review decision.
    /// </summary>
    public class ProviderSurfaceTests
    {
        public static TheoryData<string> ProviderAssemblies() => new(AllowedTypes.Keys);

        private static readonly Dictionary<string, (Assembly Assembly, string[] Allowed)> AllowedTypes = new()
        {
            ["TFM.Nucleus.Data.SQL"] = (typeof(SQLConfiguration).Assembly,
            [
                "Microsoft.Extensions.DependencyInjection.ServiceExtensions",
                "TFM.Nucleus.Data.SQL.SQLConfiguration",
                "TFM.Nucleus.Data.SQL.SqlDataProviderException",
                "TFM.Nucleus.Data.SQL.SqlDataProviderExceptionType",
            ]),
            ["TFM.Nucleus.Data.PostgreSQL"] = (typeof(TFM.Nucleus.Data.PostgreSQL.PostgresConfiguration).Assembly,
            [
                "Microsoft.Extensions.DependencyInjection.NucleusPostgresServiceExtensions",
                "TFM.Nucleus.Data.PostgreSQL.PostgresConfiguration",
                "TFM.Nucleus.Data.PostgreSQL.PostgresDataProviderException",
            ]),
        };

        [Theory]
        [MemberData(nameof(ProviderAssemblies))]
        public void ProviderAssembly_ExportsOnlyRegistrationAndConfiguration(string assemblyName)
        {
            var (assembly, allowed) = AllowedTypes[assemblyName];

            var exported = assembly.GetExportedTypes().Select(t => t.FullName!).Order(StringComparer.Ordinal).ToArray();

            Assert.Equal(allowed.Order(StringComparer.Ordinal), exported);
        }
    }
}
