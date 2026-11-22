using Microsoft.Data.SqlClient;
using System;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// How the SQL Server provider reads the driver's errors. SqlClient does not say whether an
    /// error is transient (<c>SqlException.IsTransient</c> is always false), so the error numbers
    /// that can pass on another attempt are listed here: those SQL Server, Azure SQL and the
    /// network report for a connection that broke or could not be made, a server that is busy or
    /// failing over, a deadlock and a snapshot conflict.
    /// </summary>
    internal static class SqlServerErrors
    {
        /// <summary>True when the failure is one another attempt may pass, such as an open that failed while the server was failing over.</summary>
        public static bool IsTransient(Exception exception) => Classify(exception).IsTransient;

        /// <summary>What the first SQL Server error in the chain is; a timeout or <see cref="DatabaseErrorClass.Other"/> when there is none.</summary>
        public static DatabaseErrorClass Classify(Exception exception)
        {
            for (Exception? e = exception; e != null; e = e.InnerException)
                if (e is SqlException sql)
                    return Classify(sql);
            return DatabaseFailures.ClassifyDefault(exception);
        }

        private static DatabaseErrorClass Classify(SqlException exception) => exception.Number switch
        {
            // -2: the client's timeout expired, for a command or a connection.
            -2 => new(DatabaseErrorKind.Timeout, true),
            1205 => new(DatabaseErrorKind.Deadlock, true),
            // 3960: a snapshot transaction's update conflicted with another transaction's.
            3960 => new(DatabaseErrorKind.SerializationFailure, true),
            2601 or 2627 => new(DatabaseErrorKind.UniqueViolation, false),
            // 547 is also a broken CHECK constraint, which another attempt does not change either.
            547 when exception.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase) => new(DatabaseErrorKind.ForeignKeyViolation, false),
            515 => new(DatabaseErrorKind.NotNullViolation, false),
            // 8152: string or binary data would be truncated; 2628 names the column.
            2628 or 8152 => new(DatabaseErrorKind.ValueTooLong, false),
            // The network: the server was not found or not reachable (53, 64, 121, 1225, 10060,
            // 10061, 11001), a connection was closed or reset (233, 10053, 10054), or the transport
            // failed (0 with severity 20 or above, which SqlClient reports for a broken connection).
            53 or 64 or 121 or 233 or 1225 or 10053 or 10054 or 10060 or 10061 or 11001 => new(DatabaseErrorKind.ConnectionFailed, true),
            0 when exception.Class >= 20 => new(DatabaseErrorKind.ConnectionFailed, true),
            // Azure SQL and availability groups: the database is failing over, moving or paused.
            // 4060 (cannot open the database) is also a wrong database name, retried to no effect.
            4060 or 4221 or 40143 or 40197 or 40613 or 42108 or 42109 => new(DatabaseErrorKind.ConnectionFailed, true),
            // Azure SQL is busy or throttling this database's requests.
            40501 or 10928 or 10929 or 49918 or 49919 or 49920 => new(DatabaseErrorKind.Other, true),
            // 18456: the login failed; another attempt fails the same way.
            18456 => new(DatabaseErrorKind.ConnectionFailed, false),
            _ => DatabaseErrorClass.Other,
        };
    }
}
