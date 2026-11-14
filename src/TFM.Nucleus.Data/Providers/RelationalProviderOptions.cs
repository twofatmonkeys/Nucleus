using System;
using Microsoft.Extensions.Options;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// The settings every relational provider shares: where the database is, who to sign in as,
    /// and the limits the shared execution layer applies. A provider's configuration class
    /// derives from this and adds what only its engine needs.
    /// </summary>
    public abstract class RelationalProviderOptions
    {
        protected RelationalProviderOptions(int maxParametersPerQuery)
        {
            MaxParametersPerQuery = maxParametersPerQuery;
        }

        public readonly Guid ID = Guid.NewGuid();

        public string? Server { get; set; }
        public string? Database { get; set; }
        public string? Username { get; set; }
        public string? Password { protected internal get; set; }

        /// <summary>Reported to the server as the application name; copied from NucleusOptions.AppName.</summary>
        public string? AppName { get; set; }

        /// <summary>
        /// A raw connection string that bypasses the builder entirely. Server, Database, Username
        /// and Password are ignored when set, except Database, which is still used for catalog
        /// queries and to create the database.
        /// </summary>
        public string? ConnectionString { get; set; }

        /// <summary>The most contexts open at once across the process.</summary>
        public int MaxConnectionPoolSize { get; set; } = 100;

        /// <summary>The most parallel contexts one oversized write fans out to.</summary>
        public int MaxThreadsForLargeQuery { get; set; } = 12;

        /// <summary>The most parameters one command binds; a larger write is split or bulk copied.</summary>
        public int MaxParametersPerQuery { get; set; }

        /// <summary>
        /// How long an async context waits for a free slot in the pool, in seconds. Despite the
        /// name it is not a command timeout: commands use the driver's default.
        /// </summary>
        public int CommandTimeoutSeconds { get; set; } = 10;

        private int _schemaCommandTimeoutSeconds = 900;

        /// <summary>
        /// The command timeout of each schema statement, in seconds: a table rebuild's row copy, an
        /// added column, an index created or dropped. 0 means no limit. A rebuild copies every row
        /// of a table, and the driver's default of 30 seconds is far too short for a large one.
        /// </summary>
        public int SchemaCommandTimeoutSeconds
        {
            get => _schemaCommandTimeoutSeconds;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _schemaCommandTimeoutSeconds = value;
            }
        }

        /// <summary>The command timeout of each bulk copy and bulk statement, in seconds.</summary>
        public int BulkCopyTimeoutSeconds { get; set; } = 60;

        private TableNamingPolicy _tableNaming = TableNamingPolicy.FullName;

        /// <summary>
        /// How this provider names the tables of entities that declare neither [DatabaseTable] nor
        /// [TableNaming]. <see cref="TableNamingPolicy.FullName"/> by default, which is how every
        /// table was named before policies existed. Changing it on a database that has tables
        /// makes new empty tables unless <see cref="RenameFullNameTables"/> is set or the old names
        /// are listed in [LegacyTableNames].
        /// </summary>
        public TableNamingPolicy TableNaming
        {
            get => _tableNaming;
            set => _tableNaming = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// When an entity's table is missing and a table under its full-name form exists (tbl_ and
        /// the type's full name), the schema update renames that table to the entity's table name
        /// instead of creating an empty one. Off by default. Has no effect while the schema is read-only.
        /// </summary>
        public bool RenameFullNameTables { get; set; }
    }

    /// <summary>Copies NucleusOptions.AppName into every (named) instance of a provider's options.</summary>
    public sealed class ConfigureAppName<TOptions>(IOptions<NucleusOptions> nucleusOptions) : IConfigureNamedOptions<TOptions>
        where TOptions : RelationalProviderOptions
    {
        private readonly NucleusOptions _nucleusOptions = nucleusOptions?.Value ?? throw new ArgumentNullException(nameof(nucleusOptions), "NucleusOptions cannot be null");

        public void Configure(TOptions options) => Configure(Options.DefaultName, options);

        public void Configure(string? name, TOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options), $"{typeof(TOptions).Name} cannot be null");
            options.AppName = _nucleusOptions.AppName ?? throw new ArgumentNullException(nameof(_nucleusOptions.AppName), "AppName must be set in NucleusOptions");
        }
    }

    /// <summary>Presents one named options instance as an IOptionsMonitor's current value.</summary>
    public sealed class NamedOptionsMonitor<TOptions>(IOptionsMonitor<TOptions> inner, string name) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue => inner.Get(name);
        public TOptions Get(string? optionsName) => inner.Get(optionsName ?? name);
        public IDisposable? OnChange(Action<TOptions, string?> listener) => inner.OnChange(listener);
    }
}
