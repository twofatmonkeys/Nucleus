using Npgsql;
using System;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.PostgreSQL
{
    /// <summary>How the PostgreSQL provider reads the driver's errors.</summary>
    internal static class PostgresErrors
    {
        /// <summary>SQLSTATE 23505: a unique index or constraint was violated.</summary>
        public const string UniqueViolation = "23505";

        /// <summary>True when the failure is one that may pass on a retry, such as a dropped connection.</summary>
        public static bool IsTransient(Exception exception) => Classify(exception).IsTransient;

        /// <summary>
        /// What the failure is: by SQLSTATE for an error the server reported, and otherwise by
        /// the driver, which marks a failure transient when the connection broke or timed out.
        /// </summary>
        public static DatabaseErrorClass Classify(Exception exception)
        {
            for (Exception? e = exception; e != null; e = e.InnerException)
            {
                if (e is PostgresException postgres)
                    return Classify(postgres);
                if (e is NpgsqlException npgsql)
                    return npgsql.InnerException is TimeoutException
                        ? new DatabaseErrorClass(DatabaseErrorKind.Timeout, true)
                        : new DatabaseErrorClass(npgsql.IsTransient ? DatabaseErrorKind.ConnectionFailed : DatabaseErrorKind.Other, npgsql.IsTransient);
            }
            return DatabaseFailures.ClassifyDefault(exception);
        }

        private static DatabaseErrorClass Classify(PostgresException exception) => exception.SqlState switch
        {
            "40P01" => new(DatabaseErrorKind.Deadlock, true),
            "40001" => new(DatabaseErrorKind.SerializationFailure, true),
            UniqueViolation => new(DatabaseErrorKind.UniqueViolation, false),
            "23503" => new(DatabaseErrorKind.ForeignKeyViolation, false),
            "23502" => new(DatabaseErrorKind.NotNullViolation, false),
            "22001" => new(DatabaseErrorKind.ValueTooLong, false),
            // query_canceled: a statement_timeout, or a cancel the client sent for its command timeout.
            "57014" => new(DatabaseErrorKind.Timeout, true),
            // lock_not_available: a lock_timeout ran out.
            "55P03" => new(DatabaseErrorKind.Timeout, true),
            // Class 08 is a connection exception; 57P01-57P03 a server shutting down or starting.
            var state when state.StartsWith("08", StringComparison.Ordinal) || state is "57P01" or "57P02" or "57P03"
                => new(DatabaseErrorKind.ConnectionFailed, true),
            // Class 53 is insufficient resources, such as too many connections.
            var state when state.StartsWith("53", StringComparison.Ordinal) => new(DatabaseErrorKind.Other, true),
            _ => new(DatabaseErrorKind.Other, exception.IsTransient),
        };

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
