using System;

namespace TFM.Nucleus.Data
{
    /// <summary>Server-function selector used on the DatabaseColumn attribute (SetValueOverride).</summary>
    public enum eServerFunction
    {
        SYSDATETIME = 0,
        SYSDATETIMEOFFSET = 1,
        NULL = 99,
    }

    /// <summary>
    /// The well-known server-function tokens. Singletons — compare by reference or by Kind.
    /// ToString is diagnostic only; providers render the SQL.
    /// </summary>
    public sealed class ServerFunction : IServerFunction
    {
        public ServerFunctionKind Kind { get; }

        private ServerFunction(ServerFunctionKind kind) => Kind = kind;

        public static readonly ServerFunction SysDateTime = new(ServerFunctionKind.SysDateTime);
        public static readonly ServerFunction SysDateTimeOffset = new(ServerFunctionKind.SysDateTimeOffset);
        public static readonly ServerFunction UtcNow = new(ServerFunctionKind.UtcNow);
        public static readonly ServerFunction NewGuid = new(ServerFunctionKind.NewGuid);

        public static IServerFunction? Get(eServerFunction function) =>
            function switch
            {
                eServerFunction.SYSDATETIME => SysDateTime,
                eServerFunction.SYSDATETIMEOFFSET => SysDateTimeOffset,
                _ => null,
            };

        public override string ToString() => Kind.ToString();
    }
}
