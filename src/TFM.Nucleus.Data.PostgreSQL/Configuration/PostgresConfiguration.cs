using Npgsql;
using System;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>
    /// The PostgreSQL provider's settings: the shared relational settings plus what only
    /// PostgreSQL needs. <see cref="RelationalProviderOptions.Server"/> takes host or host:port.
    /// </summary>
    public class PostgresConfiguration : RelationalProviderOptions
    {
        public PostgresConfiguration() : base(maxParametersPerQuery: 30000)
        {
        }

        /// <summary>The port, when <see cref="RelationalProviderOptions.Server"/> does not give one.</summary>
        public int Port { get; set; } = 5432;

        /// <summary>The schema the tables live in. Created when it does not exist and the schema is writable.</summary>
        public string Schema { get; set; } = "public";

        /// <summary>
        /// The driver's SSL mode by name: Disable, Allow, Prefer, Require, VerifyCA or VerifyFull.
        /// Null leaves the driver's default.
        /// </summary>
        public string? SslMode { get; set; }

        /// <summary>
        /// How many times opening a connection is attempted when the failure is transient, the
        /// first attempt included. 1 or less means one attempt and no retry.
        /// </summary>
        public int ConnectAttempts { get; set; } = 3;

        /// <summary>The old name of <see cref="ConnectAttempts"/>. It always counted attempts, not retries.</summary>
        [Obsolete("Use ConnectAttempts. The value counts attempts, the first included, not retries.")]
        public int ConnectRetryCount
        {
            get => ConnectAttempts;
            set => ConnectAttempts = value;
        }

        private int _connectTimeoutSeconds = 15;

        /// <summary>
        /// Seconds to wait for a connection to open before failing, 15 by default. 0 waits
        /// indefinitely. Ignored when <see cref="RelationalProviderOptions.ConnectionString"/> is
        /// set, which carries its own.
        /// </summary>
        public int ConnectTimeoutSeconds
        {
            get => _connectTimeoutSeconds;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _connectTimeoutSeconds = value;
            }
        }

        /// <summary>The database every server has, used to find and create the configured one.</summary>
        internal const string MaintenanceDatabase = "postgres";

        internal NpgsqlConnection BuildConnection(bool useTransaction) => new(BuildConnectionString(Database, useTransaction));

        /// <summary>A connection to the maintenance database, for finding and creating the configured one.</summary>
        internal NpgsqlConnection BuildMaintenanceConnection() => new(BuildConnectionString(MaintenanceDatabase, useTransaction: false));

        internal string BuildConnectionString(string? database, bool useTransaction)
        {
            var builder = ConnectionString != null ? new NpgsqlConnectionStringBuilder(ConnectionString) : NewBuilder();
            builder.Database = database ?? throw new PostgresDataProviderException("PostgreSQL database name is missing", DataProviderErrorKind.NotConfigured);
            if (!useTransaction)
                builder.Enlist = false;
            return builder.ConnectionString;
        }

        private NpgsqlConnectionStringBuilder NewBuilder()
        {
            var server = Server ?? throw new PostgresDataProviderException("PostgreSQL server name is missing", DataProviderErrorKind.NotConfigured);
            var port = Port;
            if (server.Contains(':'))
            {
                port = int.Parse(server.Split(':')[1]);
                server = server.Split(':')[0];
            }
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = server,
                Port = port,
                Username = Username ?? throw new PostgresDataProviderException("PostgreSQL user name is missing", DataProviderErrorKind.NotConfigured),
                Password = Password ?? throw new PostgresDataProviderException("PostgreSQL password is missing", DataProviderErrorKind.NotConfigured),
                ApplicationName = AppName ?? throw new PostgresDataProviderException("PostgreSQL application name is missing", DataProviderErrorKind.NotConfigured),
                MaxPoolSize = MaxConnectionPoolSize,
                Timeout = ConnectTimeoutSeconds,
                KeepAlive = 30,
            };
            if (SslMode != null)
                builder.SslMode = Enum.Parse<SslMode>(SslMode, ignoreCase: true);
            return builder;
        }
    }
}
