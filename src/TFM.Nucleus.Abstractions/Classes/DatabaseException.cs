using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// The database failed an operation: it could not be reached, a statement or a commit failed,
    /// or a rule of the schema was broken. Every provider throws this for a database failure, with
    /// the driver's own exception as <see cref="Exception.InnerException"/>, so a caller can decide
    /// what to do without knowing which engine stores the data:
    /// <code>catch (DatabaseException { IsTransient: true, MayHaveBeenApplied: false })</code>
    /// is a failure worth another attempt of the whole operation, known not to have been applied.
    /// A unique violation is the derived <see cref="UniqueViolationException"/>. A cancellation the
    /// caller asked for is an <see cref="OperationCanceledException"/>, never this.
    /// </summary>
    public class DatabaseException : NucleusException
    {
        private readonly bool _transient;

        /// <param name="message">What failed, in Nucleus's terms.</param>
        /// <param name="kind">What kind of failure the engine reported.</param>
        /// <param name="transient">Whether the failure itself is one that can pass on another attempt, such as a deadlock or a dropped connection.</param>
        /// <param name="innerException">The driver's exception.</param>
        public DatabaseException(string message, DatabaseErrorKind kind, bool transient, Exception? innerException)
            : base(message, innerException)
        {
            Kind = kind;
            _transient = transient;
        }

        /// <summary>What kind of failure the engine reported.</summary>
        public DatabaseErrorKind Kind { get; }

        /// <summary>
        /// True when another attempt of the whole operation may succeed and is safe: the failure is
        /// one that passes (a deadlock, a dropped connection, a timeout) and it is known not to
        /// have been applied. Always false when <see cref="MayHaveBeenApplied"/> is true.
        /// </summary>
        public bool IsTransient => _transient && !MayHaveBeenApplied;

        /// <summary>
        /// True when the database may have kept some or all of what the operation wrote: a commit
        /// whose outcome is unknown (<see cref="DatabaseErrorKind.CommitOutcomeUnknown"/>), or a
        /// write that failed on a context with no transaction after part of it may have run. False
        /// for a read, and for a write whose transaction did not commit.
        /// </summary>
        public bool MayHaveBeenApplied { get; init; }

        /// <summary>What Nucleus was doing when the database failed.</summary>
        public DatabaseOperation Operation { get; init; }

        /// <summary>
        /// How many times Nucleus attempted the step that failed. Opening a connection is attempted
        /// up to <c>ConnectAttempts</c> times when its failures are transient; every other step once.
        /// </summary>
        public int Attempts { get; init; } = 1;
    }

    /// <summary>What kind of failure the engine reported.</summary>
    public enum DatabaseErrorKind
    {
        /// <summary>A failure of no kind listed here; the inner exception says what.</summary>
        Other = 0,
        /// <summary>A connection could not be opened, or broke while in use.</summary>
        ConnectionFailed = 10,
        /// <summary>A connection did not open, a statement did not finish, or a lock was not granted, within its time limit.</summary>
        Timeout = 20,
        /// <summary>The engine chose this transaction as a deadlock victim and rolled it back.</summary>
        Deadlock = 30,
        /// <summary>The engine could not order this transaction with a concurrent one and rolled it back.</summary>
        SerializationFailure = 31,
        /// <summary>A write broke a unique index; thrown as <see cref="UniqueViolationException"/>.</summary>
        UniqueViolation = 40,
        /// <summary>A write broke a foreign key.</summary>
        ForeignKeyViolation = 41,
        /// <summary>A write left a required column empty.</summary>
        NotNullViolation = 42,
        /// <summary>A value was longer than its column allows.</summary>
        ValueTooLong = 43,
        /// <summary>A commit was sent and failed in a way that does not say whether it took effect.</summary>
        CommitOutcomeUnknown = 50,
    }

    /// <summary>What Nucleus was doing when the database failed.</summary>
    public enum DatabaseOperation
    {
        /// <summary>Not recorded.</summary>
        Unknown = 0,
        /// <summary>Opening a connection or beginning a transaction.</summary>
        Open = 10,
        /// <summary>Reading rows.</summary>
        Read = 20,
        /// <summary>Inserting or updating rows.</summary>
        Write = 30,
        /// <summary>Deleting rows.</summary>
        Delete = 40,
        /// <summary>Committing a transaction.</summary>
        Commit = 50,
        /// <summary>Rolling a transaction back.</summary>
        Rollback = 60,
        /// <summary>Reading the catalog or changing the schema.</summary>
        Schema = 70,
        /// <summary>Starting a change subscription.</summary>
        Subscribe = 80,
    }
}
