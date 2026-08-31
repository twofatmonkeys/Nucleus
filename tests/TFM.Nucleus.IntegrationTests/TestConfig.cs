using Microsoft.Extensions.Configuration;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// Resolves the integration-test database settings from user secrets ("Nucleus:Server" etc.)
    /// or environment variables (NUCLEUS_TEST_SERVER etc.). Returns null when unconfigured so
    /// tests skip instead of failing on machines (and CI) without a test SQL Server.
    /// </summary>
    public static class TestConfig
    {
        public sealed record DatabaseSettings(string Server, string Database, string Username, string Password);

        public static DatabaseSettings? TryGetSettings()
        {
            var configuration = new ConfigurationBuilder()
                .AddUserSecrets("tfm-nucleus-integrationtests")
                .AddEnvironmentVariables()
                .Build();

            var server = configuration["Nucleus:Server"] ?? configuration["NUCLEUS_TEST_SERVER"];
            var database = configuration["Nucleus:Database"] ?? configuration["NUCLEUS_TEST_DATABASE"];
            var username = configuration["Nucleus:Username"] ?? configuration["NUCLEUS_TEST_USERNAME"];
            var password = configuration["Nucleus:Password"] ?? configuration["NUCLEUS_TEST_PASSWORD"];

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) ||
                string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return null;

            return new DatabaseSettings(server, database, username, password);
        }
    }
}
