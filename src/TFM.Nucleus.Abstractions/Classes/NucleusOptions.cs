using Microsoft.Extensions.Logging;

namespace TFM.Nucleus
{
    public class NucleusOptions
    {
        public bool ReadOnlySchema { get; set; } = true;

        /// <summary>
        /// The application's name: reported to the database server as the connection's
        /// application name and written on persisted log entries. Left unset, registration reads
        /// it from the entry assembly.
        /// </summary>
        public string? AppName { get; set; }

        /// <summary>
        /// The application's version, written on persisted log entries. Left unset, registration
        /// reads it from the entry assembly: its informational version, or else its assembly version.
        /// </summary>
        public string? AppVersion { get; set; }

        /// <summary>Write entity change summaries ("Updated X from A to B") to the host's logging pipeline on save/delete.</summary>
        public bool EnableAuditLogging { get; set; } = true;

        /// <summary>The level entity change summaries are written at (category = entity type full name).</summary>
        public LogLevel AuditLogLevel { get; set; } = LogLevel.Information;

        /// <summary>Minimum level for host log entries persisted by the Nucleus logger provider (AddNucleusLogging).</summary>
        public LogLevel LogEntityMinimumLevel { get; set; } = LogLevel.Warning;
    }
}
