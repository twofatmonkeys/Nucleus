namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Well-known server-evaluated functions expressed as neutral tokens. Each provider maps a
    /// token to its own dialect (e.g. SysDateTimeOffset -> SYSDATETIMEOFFSET() on SQL Server);
    /// an unmapped token is a provider error, never silent SQL.
    /// </summary>
    public enum ServerFunctionKind
    {
        SysDateTime,
        SysDateTimeOffset,
        UtcNow,
        NewGuid,
    }

    /// <summary>A server-evaluated value in a query term; carries only the token.</summary>
    public interface IServerFunction
    {
        public ServerFunctionKind Kind { get; }
    }
}
