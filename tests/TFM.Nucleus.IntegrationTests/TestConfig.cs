using Microsoft.Extensions.Configuration;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// Resolves each provider's integration-test database from user secrets or environment
    /// variables. Returns null for a provider that is not configured, so its tests skip instead of
    /// failing on machines (and CI) without that database.
    /// </summary>
    public static class TestConfig
    {
        public sealed record DatabaseSettings(string Server, string Database, string Username, string Password);

        private static readonly IConfiguration Configuration = new ConfigurationBuilder()
            .AddUserSecrets("tfm-nucleus-integrationtests")
            .AddEnvironmentVariables()
            .Build();

        /// <summary>SQL Server: Nucleus:Server etc., or NUCLEUS_TEST_SERVER etc.</summary>
        public static DatabaseSettings? TryGetSettings() => Read("Nucleus:", "NUCLEUS_TEST_");

        /// <summary>PostgreSQL: Nucleus:Postgres:Server etc., or NUCLEUS_PG_TEST_SERVER etc.</summary>
        public static DatabaseSettings? TryGetPostgresSettings() => Read("Nucleus:Postgres:", "NUCLEUS_PG_TEST_");

        private static DatabaseSettings? Read(string secretPrefix, string environmentPrefix)
        {
            string? Value(string name) =>
                Configuration[$"{secretPrefix}{name}"] ?? Configuration[$"{environmentPrefix}{name.ToUpperInvariant()}"];

            var server = Value("Server");
            var database = Value("Database");
            var username = Value("Username");
            var password = Value("Password");

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) ||
                string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return null;

            return new DatabaseSettings(server, database, username, password);
        }
    }
}
