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
            MaxParametersPerCommand = maxParametersPerQuery;
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

        /// <summary>
        /// The most connections the provider holds to its database: its open contexts and its live
        /// change subscriptions share it, and the driver's pool is sized to it. A raw
        /// <see cref="ConnectionString"/>'s own pool size is overwritten with it.
        /// </summary>
        public int MaxConnectionPoolSize { get; set; } = 100;

        /// <summary>No longer used: an oversized write runs its parts in order on its own context.</summary>
        [Obsolete("No longer used. An oversized write the bulk engine cannot take runs its parts in order on its own context, so it commits or rolls back whole.")]
        public int MaxThreadsForLargeQuery { get; set; } = 12;

        /// <summary>
        /// Whether a transactional context must be committed with <see cref="IDataContext.Commit"/>
        /// or <see cref="IDataContext.CommitAsync"/>. When set, a context disposed without a commit
        /// rolls back. When not set (the default in v2), it commits as it always did and logs a
        /// warning; v3 makes rolling back the only behaviour, so turn this on once every context
        /// commits.
        /// </summary>
        public bool RequireCommit { get; set; }

        /// <summary>
        /// The most parameters one command binds; a larger write is split, or handed to the bulk
        /// engine. The same value the provider reports as its capability of that name.
        /// </summary>
        public int MaxParametersPerCommand { get; set; }

        /// <summary>The old name of <see cref="MaxParametersPerCommand"/>.</summary>
        [Obsolete("Use MaxParametersPerCommand, the name the provider's capabilities already use for the same value.")]
        public int MaxParametersPerQuery
        {
            get => MaxParametersPerCommand;
            set => MaxParametersPerCommand = value;
        }

        private int _poolWaitTimeoutSeconds = 3;

        /// <summary>
        /// How long opening a context or a change subscription on the asynchronous path waits for
        /// a free slot when <see cref="MaxConnectionPoolSize"/> are already held, in seconds, before
        /// it fails. 3 by default: the failure goes back to the caller, who decides whether to wait
        /// longer or retry. 0 fails at once. The synchronous path never waits. It is not a statement
        /// timeout; that is <see cref="StatementTimeoutSeconds"/>.
        /// </summary>
        public int PoolWaitTimeoutSeconds
        {
            get => _poolWaitTimeoutSeconds;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _poolWaitTimeoutSeconds = value;
            }
        }

        /// <summary>The old name of <see cref="PoolWaitTimeoutSeconds"/>. It never set a command timeout; that is <see cref="StatementTimeoutSeconds"/>.</summary>
        [Obsolete("Use PoolWaitTimeoutSeconds. This was always the wait for a free pool slot and never set a command timeout; the statement timeout is StatementTimeoutSeconds.")]
        public int CommandTimeoutSeconds
        {
            get => PoolWaitTimeoutSeconds;
            set => PoolWaitTimeoutSeconds = value;
        }

        private int _statementTimeoutSeconds = 30;

        /// <summary>
        /// The time limit of each statement a context runs, in seconds: every read, write and
        /// delete. 30 by default, the SQL Server and PostgreSQL drivers' own default, so setting
        /// nothing changes nothing. 0 means no limit. A statement over it fails with a
        /// <see cref="DatabaseException"/> of kind <see cref="DatabaseErrorKind.Timeout"/>. A
        /// context can take another (<see cref="ContextOptions.StatementTimeoutSeconds"/>). Schema
        /// statements use <see cref="SchemaCommandTimeoutSeconds"/> and bulk writes
        /// <see cref="BulkTimeoutSeconds"/>. An engine whose driver cannot limit a running
        /// statement ignores it and says so in its own settings.
        /// </summary>
        public int StatementTimeoutSeconds
        {
            get => _statementTimeoutSeconds;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _statementTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// How many times opening a connection is attempted when the failure is transient (as the
        /// provider classifies its engine's errors), the first attempt included, with an exponential
        /// back-off. 3 by default; 1 or less means one attempt and no retry. Every attempt shares the
        /// connection's connect timeout, so retrying never makes an open wait longer than it. Only
        /// opening is retried here; the operation around it is retried only under
        /// <see cref="RetryAttempts"/>, and each of its attempts opens with these attempts again.
        /// </summary>
        public int ConnectAttempts { get; set; } = 3;

        /// <summary>
        /// How many times an operation Nucleus runs on a context of its own is attempted when it
        /// fails with a transient <see cref="DatabaseException"/> (<see cref="DatabaseException.IsTransient"/>:
        /// worth another attempt, and nothing applied), the first included, with the same back-off
        /// as <see cref="ConnectAttempts"/>. It covers the manager's saves, deletes and loads, its
        /// streamed loads until their first row, row reads, and the reads of rows a load left for
        /// later. 1, the default, attempts each once, so the policy is off until it is set. Nothing
        /// run on a caller's context is attempted again, nor a commit whose outcome is unknown,
        /// nor a stream that has returned a row. When the last attempt fails transiently, the
        /// failure is reported with <see cref="DatabaseException.IsTransient"/> false and
        /// <see cref="DatabaseException.Attempts"/> set, so the caller does not retry it again.
        /// </summary>
        public int RetryAttempts { get; set; } = 1;

        private int _schemaCommandTimeoutSeconds = 900;

        /// <summary>
        /// The command timeout of each schema statement, in seconds: a table rebuild's row copy, an
        /// added column, an index created or dropped. 0 means no limit. A rebuild copies every row
        /// of a table, and the driver's default of 30 seconds is far too short for a large one. An
        /// engine whose command timeout is the wait for a lock, not a limit on a statement, ignores it.
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

        private int _bulkTimeoutSeconds = 60;

        /// <summary>
        /// The command timeout of each bulk write, in seconds, whatever mechanism the engine
        /// writes it with. 0 means no limit. An engine with no bulk engine ignores it.
        /// </summary>
        public int BulkTimeoutSeconds
        {
            get => _bulkTimeoutSeconds;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _bulkTimeoutSeconds = value;
            }
        }

        /// <summary>The old name of <see cref="BulkTimeoutSeconds"/>.</summary>
        [Obsolete("Use BulkTimeoutSeconds. The setting covers every bulk write, not one engine's bulk copy.")]
        public int BulkCopyTimeoutSeconds
        {
            get => BulkTimeoutSeconds;
            set => BulkTimeoutSeconds = value;
        }

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

        /// <summary>
        /// The schema update keeps a foreign key for each reference the entity model implies: a
        /// child's parent, a stored [LinkedObject], a [LinkedBy] key and a [ForeignKey] column,
        /// between two types this provider stores. On by default. Turning it off drops the keys
        /// Nucleus created at the next start. Has no effect while the schema is read-only, or on an
        /// engine whose provider does not create keys.
        /// </summary>
        public bool ForeignKeys { get; set; } = true;
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
