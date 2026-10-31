using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;


namespace TFM.Nucleus.Data.SQL
{
    public class SQLConfiguration
    {
        public readonly Guid ID = Guid.NewGuid();

        public string? Server { get; set; }
        public string? Database { get; set; }
        public string? Username { get; set; }
        public string? Password { protected internal get; set; }
        public string? AppName { get; set; }

        /// <summary>Use Windows authentication instead of SQL authentication (Username/Password ignored).</summary>
        public bool UseIntegratedSecurity { get; set; }

        /// <summary>
        /// A raw connection string that bypasses the builder entirely (also enables Azure AD /
        /// managed-identity scenarios). Server/Database/Username/Password are ignored when set,
        /// except Database, which is still used for schema introspection queries.
        /// </summary>
        public string? ConnectionString { get; set; }

        public int MaxConnectionPoolSize { get; set; } = 100;
        public int SQLPacketSize { get; set; } = 1500;
        public int MaxThreadsForLargeQuery { get; set; } = 12;
        public int MaxParametersPerQuery { get; set; } = 2000;
        public int CommandTimeoutSeconds { get; set; } = 10;
        public int BulkCopyTimeoutSeconds { get; set; } = 60;
        public int BulkCopyBatchSize { get; set; } = 50000;


        protected internal SqlConnection BuildNewSQLConnection(bool useTransaction)
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

        //public void Configure(NucleusOptions options)
        //{
        //    AppName = options.AppName ?? throw new ArgumentNullException(nameof(options.AppName), "AppName must be set in NucleusOptions");
        //}
        //public SQLConfiguration(IOptions<NucleusOptions> nucleusOptions, ILogger<SQLConfiguration>? logger = null)
        //{
        //    if (nucleusOptions == null)
        //        throw new ArgumentNullException(nameof(nucleusOptions), "NucleusOptions cannot be null");
        //    if (nucleusOptions.Value == null)
        //        throw new ArgumentNullException(nameof(nucleusOptions.Value), "NucleusOptions value cannot be null");
        //    AppName = nucleusOptions.Value.AppName ?? throw new ArgumentNullException(nameof(nucleusOptions.Value.AppName), "AppName must be set in NucleusOptions");
        //    Configure(nucleusOptions.Value);
        //    _logger = logger;
        //}
    }

    /// <summary>Copies AppName from NucleusOptions into every (named) SQLConfiguration instance.</summary>
    public class ConfigureSQLOptions : IConfigureNamedOptions<SQLConfiguration>
    {
        private readonly NucleusOptions _nucleusOptions;
        public ConfigureSQLOptions(IOptions<NucleusOptions> nucleusOptions)
        {
            _nucleusOptions = nucleusOptions?.Value ?? throw new ArgumentNullException(nameof(nucleusOptions), "NucleusOptions cannot be null");
        }
        public void Configure(SQLConfiguration options) => Configure(Options.DefaultName, options);
        public void Configure(string? name, SQLConfiguration options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options), "SQLConfiguration cannot be null");
            options.AppName = _nucleusOptions.AppName ?? throw new ArgumentNullException(nameof(_nucleusOptions.AppName), "AppName must be set in NucleusOptions");
        }
    }
}
