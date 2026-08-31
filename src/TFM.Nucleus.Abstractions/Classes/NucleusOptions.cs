using Microsoft.Extensions.Logging;

namespace TFM.Nucleus
{
    public class NucleusOptions
    {
        public bool ReadOnlySchema { get; set; } = true;
        public string? AppName { get; set; }
        public string? AppVersion { get; set; }

        /// <summary>Write entity change summaries ("Updated X from A to B") to the host's logging pipeline on save/delete.</summary>
        public bool EnableAuditLogging { get; set; } = true;

        /// <summary>The level entity change summaries are written at (category = entity type full name).</summary>
        public LogLevel AuditLogLevel { get; set; } = LogLevel.Information;

        /// <summary>Minimum level for host log entries persisted by the Nucleus logger provider (AddNucleusLogging).</summary>
        public LogLevel LogEntityMinimumLevel { get; set; } = LogLevel.Warning;
    }
}
