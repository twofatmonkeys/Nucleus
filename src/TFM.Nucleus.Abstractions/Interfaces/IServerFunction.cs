namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Well-known server-evaluated functions expressed as neutral tokens. Each provider maps a
    /// token to its own dialect (e.g. CurrentDateTimeOffset -> SYSDATETIMEOFFSET() on SQL Server,
    /// clock_timestamp() on PostgreSQL); an unmapped token is a provider error, never silent SQL.
    /// </summary>
    public enum ServerFunctionKind
    {
        /// <summary>The server's current date and time.</summary>
        CurrentDateTime = 0,
        /// <summary>The server's current date and time with its offset.</summary>
        CurrentDateTimeOffset = 1,
        /// <summary>The server's current date and time in UTC.</summary>
        UtcNow = 2,
        /// <summary>A new unique identifier made by the server.</summary>
        NewGuid = 3,
        /// <summary>No server function: the value is bound, not computed by the server.</summary>
        None = 99,
    }

    /// <summary>A server-evaluated value in a query term; carries only the token.</summary>
    public interface IServerFunction
    {
        public ServerFunctionKind Kind { get; }
    }
}
