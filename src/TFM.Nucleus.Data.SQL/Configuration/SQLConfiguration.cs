using Microsoft.Data.SqlClient;
using System;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// The SQL Server provider's settings: the shared relational settings plus what only SQL
    /// Server needs. <see cref="RelationalProviderOptions.Server"/> takes host, host:port or
    /// host\instance.
    /// </summary>
    public class SQLConfiguration : RelationalProviderOptions
    {
        public SQLConfiguration() : base(maxParametersPerQuery: 2000)
        {
        }

        /// <summary>Use Windows authentication instead of SQL authentication (Username/Password ignored).</summary>
        public bool UseIntegratedSecurity { get; set; }

        public int PacketSize { get; set; } = 1500;

        /// <summary>The old name of <see cref="PacketSize"/>.</summary>
        [Obsolete("Use PacketSize.")]
        public int SQLPacketSize
        {
            get => PacketSize;
            set => PacketSize = value;
        }

        /// <summary>
        /// The most staged rows one bulk INSERT or UPDATE statement writes. A larger batch runs as
        /// several statements in the same transaction, which keeps log growth and lock
        /// escalation in check.
        /// </summary>
        public int BulkBatchSize
        {
            get => _bulkCopyBatchSize;
            set
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
                _bulkCopyBatchSize = value;
            }
        }
        private int _bulkCopyBatchSize = 50000;

        /// <summary>The old name of <see cref="BulkBatchSize"/>.</summary>
        [Obsolete("Use BulkBatchSize.")]
        public int BulkCopyBatchSize
        {
            get => BulkBatchSize;
            set => BulkBatchSize = value;
        }

        /// <summary>
        /// Seconds to wait for a connection to open before failing, 3 by default. A busy server
        /// can take longer than that to complete a login, and the open then fails with a
        /// connection timeout that has used up the time <c>ConnectAttempts</c> retries within (the
        /// driver's own connect retries reconnect a broken idle connection, not a first open). 0
        /// waits indefinitely. Ignored when
        /// <see cref="RelationalProviderOptions.ConnectionString"/> is set, which carries its own.
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
        private int _connectTimeoutSeconds = 3;

        /// <summary>
        /// Turns on READ_COMMITTED_SNAPSHOT for a database this provider creates, true by default,
        /// so its reads take the last committed version of a row instead of waiting behind a save,
        /// as they do on the other engines. It is set right after the database is created, when
        /// nothing else is connected to it. An existing database is never changed: the setting
        /// needs every other connection to it closed and applies to every application that uses
        /// it, so initialise only logs a warning when it is off.
        /// </summary>
        public bool ReadCommittedSnapshotOnCreate { get; set; } = true;

        /// <summary>
        /// A connection to the database. Every one, transactional or not, has the same connection
        /// string, so the driver keeps one pool of <see cref="RelationalProviderOptions.MaxConnectionPoolSize"/>.
        /// </summary>
        internal SqlConnection BuildNewSQLConnection()
        {
            var builder = ConnectionString != null ? new SqlConnectionStringBuilder(ConnectionString) : BuildConnectionStringBuilder();
            if (ConnectionString == null)
                builder.InitialCatalog = Database ?? throw new SqlDataProviderException("SQL database name is missing", DataProviderErrorKind.NotConfigured);
            ApplyPoolRules(builder);
            return new SqlConnection(builder.ConnectionString);
        }

        /// <summary>
        /// A connection to master, for creating and finding the database. Not pooled: it is used
        /// at initialise only, and a pooled one would stay open, outside the limit, after it.
        /// </summary>
        internal SqlConnection BuildSystemSQLConnection()
        {
            var builder = ConnectionString != null ? new SqlConnectionStringBuilder(ConnectionString) : BuildConnectionStringBuilder();
            builder.InitialCatalog = "master";
            ApplyPoolRules(builder);
            builder.Pooling = false;
            return new SqlConnection(builder.ConnectionString);
        }

        /// <summary>
        /// No connection enlists in an ambient transaction (a context runs its own), and the pool
        /// is <see cref="RelationalProviderOptions.MaxConnectionPoolSize"/>, whatever a raw
        /// connection string says.
        /// </summary>
        private void ApplyPoolRules(SqlConnectionStringBuilder builder)
        {
            builder.Enlist = false;
            builder.MaxPoolSize = MaxConnectionPoolSize;
        }

        /// <summary>Shared builder for the SQL-auth / integrated-auth paths.</summary>
        private SqlConnectionStringBuilder BuildConnectionStringBuilder()
        {
            SqlConnectionStringBuilder builder = [];
            var server = Server;
            if (server?.Contains(':') ?? throw new SqlDataProviderException("SQL server name is missing", DataProviderErrorKind.NotConfigured))
            {
                var srv = server.Split(':')[0];
                var port = server.Split(':')[1];
                server = $"tcp:{srv},{port}";
            }
            builder.DataSource = server;
            if (UseIntegratedSecurity)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.IntegratedSecurity = false;
                builder.UserID = Username ?? throw new SqlDataProviderException("SQL user name is missing", DataProviderErrorKind.NotConfigured);
                builder.Password = Password ?? throw new SqlDataProviderException("SQL password is missing", DataProviderErrorKind.NotConfigured);
            }
            builder.ApplicationName = AppName ?? throw new SqlDataProviderException("SQL application name is missing", DataProviderErrorKind.NotConfigured);
            builder.ConnectTimeout = ConnectTimeoutSeconds;
            builder.ConnectRetryCount = 4;
            builder.ConnectRetryInterval = 5;
            builder.TrustServerCertificate = true;
            builder.MultipleActiveResultSets = true;
            builder.IPAddressPreference = SqlConnectionIPAddressPreference.UsePlatformDefault;
            builder.PacketSize = PacketSize;
            return builder;
        }
    }
}
