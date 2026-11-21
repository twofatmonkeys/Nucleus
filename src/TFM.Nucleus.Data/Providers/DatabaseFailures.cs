using System;
using System.Data.Common;
using System.Threading;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// What a provider says a database failure is: its kind, and whether the failure itself is one
    /// that can pass on another attempt. Whether the operation may have been applied is decided by
    /// where it failed, not by the engine, so it is not part of this.
    /// </summary>
    public readonly record struct DatabaseErrorClass(DatabaseErrorKind Kind, bool IsTransient)
    {
        /// <summary>A failure of no kind Nucleus knows, not worth another attempt.</summary>
        public static DatabaseErrorClass Other => new(DatabaseErrorKind.Other, false);
    }

    /// <summary>
    /// Turns a database failure into a <see cref="DatabaseException"/>. A provider classifies its
    /// engine's errors (<see cref="RelationalDataProvider.ClassifyFailure"/>); this decides, from
    /// what Nucleus was doing, what the caller is told: a failed commit whose outcome is unknown,
    /// an open that failed for no named reason, and whether the operation may have been applied.
    /// </summary>
    public static class DatabaseFailures
    {
        /// <summary>
        /// True when <paramref name="exception"/> is a failure of the database: a driver exception
        /// anywhere in its chain, or a timeout, and not already one of Nucleus's own exceptions or
        /// a cancellation. Anything else, such as a defect in Nucleus or the caller's code,
        /// propagates as it was thrown.
        /// </summary>
        public static bool IsDatabaseFailure(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (exception is NucleusException or OperationCanceledException)
                return false;
            if (exception is TimeoutException)
                return true;
            for (Exception? e = exception; e != null; e = e.InnerException)
                if (e is DbException)
                    return true;
            return false;
        }

        /// <summary>
        /// The class a provider that names no engine errors gives a failure: a timeout anywhere in
        /// the chain is a transient timeout, a driver exception that calls itself transient is a
        /// transient failure of no named kind, and anything else is <see cref="DatabaseErrorClass.Other"/>.
        /// </summary>
        public static DatabaseErrorClass ClassifyDefault(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            for (Exception? e = exception; e != null; e = e.InnerException)
            {
                if (e is TimeoutException)
                    return new DatabaseErrorClass(DatabaseErrorKind.Timeout, true);
                if (e is DbException { IsTransient: true })
                    return new DatabaseErrorClass(DatabaseErrorKind.Other, true);
            }
            return DatabaseErrorClass.Other;
        }

        /// <summary>
        /// The exception for a database failure: an <see cref="OperationCanceledException"/>
        /// carrying it when <paramref name="token"/> was cancelled (an engine can report a command
        /// it cancelled as its own error), and otherwise a <see cref="DatabaseException"/>.
        /// </summary>
        public static Exception Translate(string message, Exception exception, DatabaseErrorClass classification,
            DatabaseOperation operation, bool mayHaveBeenApplied = false, int attempts = 1, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (token.IsCancellationRequested)
                return new OperationCanceledException(message, exception, token);
            return Create(message, exception, classification, operation, mayHaveBeenApplied, attempts);
        }

        /// <summary>
        /// A <see cref="DatabaseException"/> for the failure, with what Nucleus was doing deciding
        /// the rest (see <see cref="Resolve"/>).
        /// </summary>
        public static DatabaseException Create(string message, Exception exception, DatabaseErrorClass classification,
            DatabaseOperation operation, bool mayHaveBeenApplied, int attempts = 1)
        {
            ArgumentNullException.ThrowIfNull(exception);
            var (kind, transient, applied) = Resolve(classification, operation, mayHaveBeenApplied);
            return new DatabaseException(message, kind, transient, exception)
            {
                MayHaveBeenApplied = applied,
                Operation = operation,
                Attempts = Math.Max(1, attempts),
            };
        }

        /// <summary>
        /// What the caller is told about a failure the engine classified, given what Nucleus was
        /// doing. A commit that failed is <see cref="DatabaseErrorKind.CommitOutcomeUnknown"/> and
        /// may have been applied, unless the engine rejected the transaction (a deadlock, a
        /// serialization failure, or a rule checked at commit), which rolls it back. An open that
        /// failed for no named reason is <see cref="DatabaseErrorKind.ConnectionFailed"/>. Nothing
        /// an open, a read or a rollback does is applied.
        /// </summary>
        public static (DatabaseErrorKind Kind, bool IsTransient, bool MayHaveBeenApplied) Resolve(
            DatabaseErrorClass classification, DatabaseOperation operation, bool mayHaveBeenApplied)
        {
            var (kind, transient) = classification;
            switch (operation)
            {
                case DatabaseOperation.Commit:
                    return RejectsTransaction(kind)
                        ? (kind, transient, false)
                        : (DatabaseErrorKind.CommitOutcomeUnknown, false, true);
                case DatabaseOperation.Open:
                    return (kind == DatabaseErrorKind.Other ? DatabaseErrorKind.ConnectionFailed : kind, transient, false);
                case DatabaseOperation.Read:
                case DatabaseOperation.Rollback:
                    return (kind, transient, false);
                default:
                    return (kind, transient, mayHaveBeenApplied);
            }
        }

        /// <summary>A failure after which the engine has rolled the transaction back itself.</summary>
        private static bool RejectsTransaction(DatabaseErrorKind kind) => kind is
            DatabaseErrorKind.Deadlock or DatabaseErrorKind.SerializationFailure or DatabaseErrorKind.UniqueViolation or
            DatabaseErrorKind.ForeignKeyViolation or DatabaseErrorKind.NotNullViolation or DatabaseErrorKind.ValueTooLong;
    }
}
