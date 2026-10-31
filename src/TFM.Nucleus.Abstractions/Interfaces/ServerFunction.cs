using System;

namespace TFM.Nucleus.Data
{
    /// <summary>Server-function selector used on the DatabaseColumn attribute (SetValueOverride).</summary>
    public enum eServerFunction
    {
        /// <summary>The server's current date and time.</summary>
        CurrentDateTime = 0,
        /// <summary>The server's current date and time with its offset.</summary>
        CurrentDateTimeOffset = 1,
        [Obsolete("Use CurrentDateTime.")]
        SYSDATETIME = 0,
        [Obsolete("Use CurrentDateTimeOffset.")]
        SYSDATETIMEOFFSET = 1,
        NULL = 99,
    }

    /// <summary>
    /// The well-known server-function tokens. Singletons - compare by reference or by Kind.
    /// ToString is diagnostic only; providers render the SQL.
    /// </summary>
    public sealed class ServerFunction : IServerFunction
    {
        public ServerFunctionKind Kind { get; }

        private ServerFunction(ServerFunctionKind kind) => Kind = kind;

        public static readonly ServerFunction CurrentDateTime = new(ServerFunctionKind.CurrentDateTime);
        public static readonly ServerFunction CurrentDateTimeOffset = new(ServerFunctionKind.CurrentDateTimeOffset);
        public static readonly ServerFunction UtcNow = new(ServerFunctionKind.UtcNow);
        public static readonly ServerFunction NewGuid = new(ServerFunctionKind.NewGuid);

        [Obsolete("Use CurrentDateTime.")]
        public static readonly ServerFunction SysDateTime = CurrentDateTime;
        [Obsolete("Use CurrentDateTimeOffset.")]
        public static readonly ServerFunction SysDateTimeOffset = CurrentDateTimeOffset;

        public static IServerFunction? Get(eServerFunction function) =>
            function switch
            {
                eServerFunction.CurrentDateTime => CurrentDateTime,
                eServerFunction.CurrentDateTimeOffset => CurrentDateTimeOffset,
                _ => null,
            };

        public override string ToString() => Kind.ToString();
    }
}
