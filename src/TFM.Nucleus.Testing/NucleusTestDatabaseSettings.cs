using Microsoft.Extensions.Configuration;

namespace TFM.Nucleus.Testing;

/// <summary>
/// Where a provider's integration-test database is, read from the shared user secrets store or
/// from environment variables. Every repo that tests against Nucleus reads the same store, so one
/// local configuration serves them all.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> leaves the password out, so a settings value that ends up in an
/// assertion message or a log never prints it.
/// </remarks>
public sealed record NucleusTestDatabaseSettings(string Server, string Database, string Username, string Password)
{
    /// <summary>The user secrets id every Nucleus test project reads: <c>dotnet user-secrets set --id tfm-nucleus-integrationtests ...</c>.</summary>
    public const string UserSecretsId = "tfm-nucleus-integrationtests";

    private static readonly Lazy<IConfiguration> Configuration = new(() => new ConfigurationBuilder()
        .AddUserSecrets(UserSecretsId)
        .AddEnvironmentVariables()
        .Build());

    /// <summary>SQL Server: the <c>Nucleus:Server</c> etc. user secrets, or <c>NUCLEUS_TEST_SERVER</c> etc. Null when any part is missing.</summary>
    public static NucleusTestDatabaseSettings? TryGetSqlServer() => Read("Nucleus:", "NUCLEUS_TEST_");

    /// <summary>PostgreSQL: the <c>Nucleus:Postgres:Server</c> etc. user secrets, or <c>NUCLEUS_PG_TEST_SERVER</c> etc. Null when any part is missing.</summary>
    public static NucleusTestDatabaseSettings? TryGetPostgres() => Read("Nucleus:Postgres:", "NUCLEUS_PG_TEST_");

    /// <summary>The settings without the password.</summary>
    public override string ToString() => $"{Username}@{Server}/{Database}";

    private static NucleusTestDatabaseSettings? Read(string secretPrefix, string environmentPrefix)
    {
        string? Value(string name) =>
            Configuration.Value[$"{secretPrefix}{name}"] ?? Configuration.Value[$"{environmentPrefix}{name.ToUpperInvariant()}"];

        var server = Value("Server");
        var database = Value("Database");
        var username = Value("Username");
        var password = Value("Password");

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) ||
            string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        return new NucleusTestDatabaseSettings(server, database, username, password);
    }
}
