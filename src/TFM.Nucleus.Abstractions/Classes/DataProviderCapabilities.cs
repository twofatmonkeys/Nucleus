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

        /// <summary>Maximum bindable parameters per command; queries are split above this.</summary>
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
        public static readonly ContextOptions Default = new();
        public static readonly ContextOptions Transactional = new() { UseTransaction = true };

        public bool UseTransaction { get; init; }
        public IDataContext? Parent { get; init; }
        /// <summary>
        /// Open the context so that what it reads can be watched for changes, on an engine whose
        /// change notifications are registered by the reading connection. Other engines ignore it.
        /// </summary>
        public bool WatchForChanges { get; init; }

        /// <summary>The old name of <see cref="WatchForChanges"/>.</summary>
        [Obsolete("Use WatchForChanges.")]
        public bool UseDependency
        {
            get => WatchForChanges;
            init => WatchForChanges = value;
        }
    }
}
