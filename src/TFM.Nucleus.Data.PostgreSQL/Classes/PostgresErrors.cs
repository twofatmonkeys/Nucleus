using Npgsql;
using System;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>How the PostgreSQL provider reads the driver's errors.</summary>
    internal static class PostgresErrors
    {
        /// <summary>SQLSTATE 23505: a unique index or constraint was violated.</summary>
        public const string UniqueViolation = "23505";

        /// <summary>True when the failure is one the driver says may pass on a retry, such as a dropped connection.</summary>
        public static bool IsTransient(Exception exception) =>
            exception is NpgsqlException { IsTransient: true } || exception is TimeoutException;

        /// <summary>The unique violation anywhere in the chain, with the index or constraint it broke.</summary>
        public static bool TryGetUniqueViolation(Exception exception, out string? constraintName)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is PostgresException { SqlState: UniqueViolation } postgres)
                {
                    constraintName = postgres.ConstraintName;
                    return true;
                }
            }
            constraintName = null;
            return false;
        }
    }
}
