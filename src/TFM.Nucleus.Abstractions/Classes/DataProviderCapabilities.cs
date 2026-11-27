using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Dialect-neutral facts about a data provider, consumed by the neutral layers for routing,
    /// validation and batching decisions only — never for rendering.
    /// </summary>
    public sealed record DataProviderCapabilities
    {
        /// <summary>The prefix used for command parameter references (e.g. "@" for SQL Server).</summary>
        public required string ParameterPrefix { get; init; }

        /// <summary>
        /// Maximum bindable parameters per command. A write over it is split into several
        /// commands; a read or a delete over it is refused, which only a long list of values
        /// that the provider binds one at a time can cause.
        /// </summary>
        public required int MaxParametersPerCommand { get; init; }

        public bool SupportsChangeNotifications { get; init; }
        public bool SupportsBulkOperations { get; init; }
        public bool SupportsOffsetPaging { get; init; }
    }

    /// <summary>
    /// Options for creating a data context, replacing the previous overload family.
    /// </summary>
    public sealed record ContextOptions
    {
        /// <summary>A context with no transaction: each statement commits as it runs. For work that only reads.</summary>
        public static readonly ContextOptions Default = new() { UseTransaction = false };

        /// <summary>A context whose work is one transaction, ended by its commit or rollback.</summary>
        public static readonly ContextOptions Transactional = new() { UseTransaction = true };

        /// <summary>
        /// Whether the context's work is one transaction. Null, the default, leaves it to what
        /// opens the context: one the manager hands out (<c>CreateContextAsync</c>) is
        /// transactional, so options that set something else, such as a statement time limit, do
        /// not drop the transaction by saying nothing about it; one a provider opens for itself is
        /// not. Set false for a context with none.
        /// </summary>
        public bool? UseTransaction { get; init; }
        public IDataContext? Parent { get; init; }
        /// <summary>
        /// Open the context so that what it reads can be watched for changes, on an engine whose
        /// change notifications are registered by the reading connection. Other engines ignore it.
        /// </summary>
        public bool WatchForChanges { get; init; }

        private readonly int? _statementTimeoutSeconds;

        /// <summary>
        /// The time limit of each statement the context runs, in seconds, in place of the
        /// provider's <c>StatementTimeoutSeconds</c>: for a context that runs a long report or job.
        /// 0 means no limit. Null, the default, takes the parent context's, or else the provider's.
        /// An engine whose driver has no statement time limit ignores it, as it does the provider's.
        /// </summary>
        public int? StatementTimeoutSeconds
        {
            get => _statementTimeoutSeconds;
            init
            {
                if (value is { } seconds)
                    ArgumentOutOfRangeException.ThrowIfNegative(seconds, nameof(StatementTimeoutSeconds));
                _statementTimeoutSeconds = value;
            }
        }

        /// <summary>The old name of <see cref="WatchForChanges"/>.</summary>
        [Obsolete("Use WatchForChanges.")]
        public bool UseDependency
        {
            get => WatchForChanges;
            init => WatchForChanges = value;
        }
    }
}
