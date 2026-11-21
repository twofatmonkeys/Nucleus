using System;

namespace TFM.Nucleus.Data
{
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


        /// <summary>The token for a kind; null for <see cref="ServerFunctionKind.None"/>.</summary>
        public static IServerFunction? Get(ServerFunctionKind function) =>
            function switch
            {
                ServerFunctionKind.CurrentDateTime => CurrentDateTime,
                ServerFunctionKind.CurrentDateTimeOffset => CurrentDateTimeOffset,
                ServerFunctionKind.UtcNow => UtcNow,
                ServerFunctionKind.NewGuid => NewGuid,
                _ => null,
            };

        public override string ToString() => Kind.ToString();
    }
}
