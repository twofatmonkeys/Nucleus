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

        public int SQLPacketSize { get; set; } = 1500;

        /// <summary>
        /// The most staged rows one bulk INSERT or UPDATE statement writes. A larger batch runs as
        /// several statements in the same transaction, which keeps log growth and lock
        /// escalation in check.
        /// </summary>
        public int BulkCopyBatchSize
        {
            get => _bulkCopyBatchSize;
            set
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
                _bulkCopyBatchSize = value;
            }
        }
        private int _bulkCopyBatchSize = 50000;

        internal SqlConnection BuildNewSQLConnection(bool useTransaction)
        {
            if (ConnectionString != null)
                return BuildFromRawConnectionString(ConnectionString, useTransaction);
            var builder = BuildConnectionStringBuilder();
            builder.InitialCatalog = Database ?? throw new Exception("SQL database name is missing");
            return useTransaction ? new SqlConnection($"{builder.ConnectionString}") :
                new SqlConnection($"{builder.ConnectionString}; Enlist = false");
        }
        /// <summary>A connection to master, for creating and finding the database.</summary>
        internal SqlConnection BuildSystemSQLConnection()
        {
            if (ConnectionString != null)
            {
                var raw = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
                return BuildFromRawConnectionString(raw.ConnectionString, useTransaction: false);
            }
            var builder = BuildConnectionStringBuilder();
            builder.InitialCatalog = "master";
            return new SqlConnection($"{builder.ConnectionString}; Enlist = false");
        }

        /// <summary>Shared builder for the SQL-auth / integrated-auth paths.</summary>
        private SqlConnectionStringBuilder BuildConnectionStringBuilder()
        {
            SqlConnectionStringBuilder builder = [];
            var server = Server;
            if (server?.Contains(':') ?? throw new Exception("SQL server name is missing"))
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
                builder.UserID = Username ?? throw new Exception("SQL user name is missing");
                builder.Password = Password ?? throw new Exception("SQL password is missing");
            }
            builder.ApplicationName = AppName ?? throw new Exception("SQL application name is missing");
            builder.ConnectTimeout = 3;
            builder.ConnectRetryCount = 4;
            builder.ConnectRetryInterval = 5;
            builder.TrustServerCertificate = true;
            builder.MultipleActiveResultSets = true;
            builder.IPAddressPreference = SqlConnectionIPAddressPreference.UsePlatformDefault;
            builder.PacketSize = SQLPacketSize;
            builder.MaxPoolSize = MaxConnectionPoolSize;
            return builder;
        }

        private static SqlConnection BuildFromRawConnectionString(string connectionString, bool useTransaction)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!useTransaction)
                builder.Enlist = false;
            return new SqlConnection(builder.ConnectionString);
        }
    }
}
