using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using TFM.Nucleus.Data.PostgreSQL;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.SQLite;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// What a caller is told about a database failure: which failures are the database's, what a
    /// failed commit and a failed open are, and that a failure that may have been applied is never
    /// called transient, so <c>catch (DatabaseException { IsTransient: true })</c> never repeats a
    /// write that may have happened.
    /// </summary>
    public class DatabaseFailureTests
    {
        private static readonly DatabaseErrorClass Unnamed = DatabaseErrorClass.Other;
        private static readonly DatabaseErrorClass Dropped = new(DatabaseErrorKind.ConnectionFailed, true);

        [Fact]
        public void ACommitThatFails_ForNoNamedReason_HasAnUnknownOutcome_AndIsNotTransient()
        {
            var failure = DatabaseFailures.Create("m", new TestDbException(), Dropped, DatabaseOperation.Commit, mayHaveBeenApplied: false);

            Assert.Equal(DatabaseErrorKind.CommitOutcomeUnknown, failure.Kind);
            Assert.True(failure.MayHaveBeenApplied);
            Assert.False(failure.IsTransient);
        }

        [Theory]
        [InlineData(DatabaseErrorKind.Deadlock, true)]
        [InlineData(DatabaseErrorKind.SerializationFailure, true)]
        [InlineData(DatabaseErrorKind.UniqueViolation, false)]
        [InlineData(DatabaseErrorKind.ForeignKeyViolation, false)]
        public void ACommitTheEngineRefused_WasRolledBack(DatabaseErrorKind kind, bool transient)
        {
            var failure = DatabaseFailures.Create("m", new TestDbException(), new(kind, transient), DatabaseOperation.Commit, mayHaveBeenApplied: true);

            Assert.Equal(kind, failure.Kind);
            Assert.False(failure.MayHaveBeenApplied);
            Assert.Equal(transient, failure.IsTransient);
        }

        [Fact]
        public void AnOpenThatFails_ForNoNamedReason_IsAConnectionFailure_AppliedNothing()
        {
            var failure = DatabaseFailures.Create("m", new TestDbException(), Unnamed, DatabaseOperation.Open, mayHaveBeenApplied: true, attempts: 3);

            Assert.Equal(DatabaseErrorKind.ConnectionFailed, failure.Kind);
            Assert.False(failure.MayHaveBeenApplied);
            Assert.Equal(3, failure.Attempts);
        }

        [Theory]
        [InlineData(DatabaseOperation.Read)]
        [InlineData(DatabaseOperation.Rollback)]
        public void AReadOrARollback_NeverAppliedAnything(DatabaseOperation operation)
        {
            var failure = DatabaseFailures.Create("m", new TestDbException(), Dropped, operation, mayHaveBeenApplied: true);

            Assert.False(failure.MayHaveBeenApplied);
            Assert.True(failure.IsTransient);
        }

        [Fact]
        public void AWriteThatMayHaveBeenApplied_IsNeverTransient()
        {
            var failure = DatabaseFailures.Create("m", new TestDbException(), Dropped, DatabaseOperation.Write, mayHaveBeenApplied: true);

            Assert.Equal(DatabaseErrorKind.ConnectionFailed, failure.Kind);
            Assert.True(failure.MayHaveBeenApplied);
            Assert.False(failure.IsTransient);
        }

        [Fact]
        public void AWriteKnownNotApplied_KeepsItsTransience()
        {
            var failure = DatabaseFailures.Create("m", new TestDbException(), Dropped, DatabaseOperation.Write, mayHaveBeenApplied: false);

            Assert.True(failure.IsTransient);
            Assert.Equal(1, failure.Attempts);
            Assert.Equal(DatabaseOperation.Write, failure.Operation);
        }

        [Fact]
        public void ADriverException_AnywhereInTheChain_IsADatabaseFailure()
        {
            Assert.True(DatabaseFailures.IsDatabaseFailure(new TestDbException()));
            Assert.True(DatabaseFailures.IsDatabaseFailure(new InvalidOperationException("wrapped", new TestDbException())));
            Assert.True(DatabaseFailures.IsDatabaseFailure(new TimeoutException()));
        }

        [Fact]
        public void NucleusExceptions_Cancellations_AndDefects_AreNotDatabaseFailures()
        {
            Assert.False(DatabaseFailures.IsDatabaseFailure(new DataContextException("m", DataContextErrorKind.PoolExhausted)));
            Assert.False(DatabaseFailures.IsDatabaseFailure(new DatabaseException("m", DatabaseErrorKind.Other, false, new TestDbException())));
            Assert.False(DatabaseFailures.IsDatabaseFailure(new OperationCanceledException("m", new TestDbException())));
            Assert.False(DatabaseFailures.IsDatabaseFailure(new InvalidOperationException("a defect")));
        }

        [Fact]
        public void AFailureAfterTheTokenWasCancelled_IsTheCancellation()
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var engine = new TestDbException();

            var thrown = DatabaseFailures.Translate("m", engine, Dropped, DatabaseOperation.Read, token: cancelled.Token);

            var cancellation = Assert.IsType<OperationCanceledException>(thrown);
            Assert.Same(engine, cancellation.InnerException);
            Assert.Equal(cancelled.Token, cancellation.CancellationToken);
        }

        [Fact]
        public void TheDefaultClass_KnowsTimeouts_AndTheDriversOwnTransientFlag()
        {
            Assert.Equal(new DatabaseErrorClass(DatabaseErrorKind.Timeout, true), DatabaseFailures.ClassifyDefault(new Exception("m", new TimeoutException())));
            Assert.Equal(new DatabaseErrorClass(DatabaseErrorKind.Other, true), DatabaseFailures.ClassifyDefault(new TestDbException(transient: true)));
            Assert.Equal(DatabaseErrorClass.Other, DatabaseFailures.ClassifyDefault(new TestDbException()));
        }

        [Fact]
        public void AUniqueViolation_IsADatabaseFailure_ThatIsNeverTransient()
        {
            var violation = new UniqueViolationException("m", "UQ_x", typeof(TestWidget), nameof(TestWidget.Name), "Name", new TestDbException());

            DatabaseException failure = violation;
            Assert.Equal(DatabaseErrorKind.UniqueViolation, failure.Kind);
            Assert.False(failure.IsTransient);
            Assert.False(failure.MayHaveBeenApplied);
        }

        /// <summary>A driver exception of no engine, which says whether it is transient.</summary>
        private sealed class TestDbException(bool transient = false) : System.Data.Common.DbException("driver failure")
        {
            public override bool IsTransient => transient;
        }
    }

    /// <summary>
    /// SQL Server's errors by number. SqlClient never says an error is transient, so a number that
    /// can pass on another attempt and is not listed would never be retried, by Nucleus when it
    /// opens a connection or by a caller.
    /// </summary>
    public class SqlServerErrorTests
    {
        [Theory]
        [InlineData(-2, 16, DatabaseErrorKind.Timeout, true)]
        [InlineData(1205, 13, DatabaseErrorKind.Deadlock, true)]
        [InlineData(3960, 16, DatabaseErrorKind.SerializationFailure, true)]
        [InlineData(2601, 14, DatabaseErrorKind.UniqueViolation, false)]
        [InlineData(2627, 14, DatabaseErrorKind.UniqueViolation, false)]
        [InlineData(515, 16, DatabaseErrorKind.NotNullViolation, false)]
        [InlineData(2628, 16, DatabaseErrorKind.ValueTooLong, false)]
        [InlineData(8152, 16, DatabaseErrorKind.ValueTooLong, false)]
        [InlineData(53, 20, DatabaseErrorKind.ConnectionFailed, true)]
        [InlineData(10054, 20, DatabaseErrorKind.ConnectionFailed, true)]
        [InlineData(0, 20, DatabaseErrorKind.ConnectionFailed, true)]
        [InlineData(40613, 17, DatabaseErrorKind.ConnectionFailed, true)]
        [InlineData(40501, 20, DatabaseErrorKind.Other, true)]
        [InlineData(18456, 14, DatabaseErrorKind.ConnectionFailed, false)]
        [InlineData(208, 16, DatabaseErrorKind.Other, false)]
        public void AnError_IsClassifiedByItsNumber(int number, byte errorClass, DatabaseErrorKind kind, bool transient)
        {
            var classified = SqlServerErrors.Classify(new InvalidOperationException("wrapped", SqlExceptions.Create(number, errorClass)));

            Assert.Equal(new DatabaseErrorClass(kind, transient), classified);
        }

        [Fact]
        public void A547_IsAForeignKeyViolation_OnlyWhenItNamesOne()
        {
            Assert.Equal(DatabaseErrorKind.ForeignKeyViolation,
                SqlServerErrors.Classify(SqlExceptions.Create(547, 16, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_x\".")).Kind);
            Assert.Equal(DatabaseErrorKind.Other,
                SqlServerErrors.Classify(SqlExceptions.Create(547, 16, "The INSERT statement conflicted with the CHECK constraint \"CK_x\".")).Kind);
        }
    }

    /// <summary>PostgreSQL's errors by SQLSTATE, and the driver's own failures by what it says of them.</summary>
    public class PostgresErrorTests
    {
        [Theory]
        [InlineData("40P01", DatabaseErrorKind.Deadlock, true)]
        [InlineData("40001", DatabaseErrorKind.SerializationFailure, true)]
        [InlineData("23505", DatabaseErrorKind.UniqueViolation, false)]
        [InlineData("23503", DatabaseErrorKind.ForeignKeyViolation, false)]
        [InlineData("23502", DatabaseErrorKind.NotNullViolation, false)]
        [InlineData("22001", DatabaseErrorKind.ValueTooLong, false)]
        [InlineData("57014", DatabaseErrorKind.Timeout, true)]
        [InlineData("55P03", DatabaseErrorKind.Timeout, true)]
        [InlineData("08006", DatabaseErrorKind.ConnectionFailed, true)]
        [InlineData("57P01", DatabaseErrorKind.ConnectionFailed, true)]
        [InlineData("53300", DatabaseErrorKind.Other, true)]
        [InlineData("42P01", DatabaseErrorKind.Other, false)]
        public void AServerError_IsClassifiedByItsSqlState(string sqlState, DatabaseErrorKind kind, bool transient)
        {
            var classified = PostgresErrors.Classify(new InvalidOperationException("wrapped", new PostgresException("m", "ERROR", "ERROR", sqlState)));

            Assert.Equal(new DatabaseErrorClass(kind, transient), classified);
        }

        [Fact]
        public void ADriverTimeout_IsATransientTimeout()
        {
            Assert.Equal(new DatabaseErrorClass(DatabaseErrorKind.Timeout, true), PostgresErrors.Classify(new NpgsqlException("m", new TimeoutException())));
        }

        [Fact]
        public void ABrokenStream_IsATransientConnectionFailure()
        {
            Assert.Equal(new DatabaseErrorClass(DatabaseErrorKind.ConnectionFailed, true), PostgresErrors.Classify(new NpgsqlException("m", new IOException())));
        }
    }

    /// <summary>SQLite's errors by result code. The driver never says an error is transient.</summary>
    public class SqliteErrorTests
    {
        [Theory]
        [InlineData(5, 5, DatabaseErrorKind.Timeout, true)]
        [InlineData(5, 517, DatabaseErrorKind.Timeout, true)]
        [InlineData(6, 6, DatabaseErrorKind.Timeout, true)]
        [InlineData(19, 2067, DatabaseErrorKind.UniqueViolation, false)]
        [InlineData(19, 1555, DatabaseErrorKind.UniqueViolation, false)]
        [InlineData(19, 787, DatabaseErrorKind.ForeignKeyViolation, false)]
        [InlineData(19, 1299, DatabaseErrorKind.NotNullViolation, false)]
        [InlineData(19, 275, DatabaseErrorKind.Other, false)]
        [InlineData(14, 14, DatabaseErrorKind.ConnectionFailed, false)]
        [InlineData(1, 1, DatabaseErrorKind.Other, false)]
        public void AnError_IsClassifiedByItsResultCode(int code, int extendedCode, DatabaseErrorKind kind, bool transient)
        {
            var classified = SqliteErrors.Classify(new InvalidOperationException("wrapped", new SqliteException("m", code, extendedCode)));

            Assert.Equal(new DatabaseErrorClass(kind, transient), classified);
        }
    }

    /// <summary>
    /// SqlExceptions for tests. SqlClient has no public constructor for one, so it is built the
    /// way the driver builds it, through its internal factory; the shapes are those of SqlClient 6.
    /// </summary>
    internal static class SqlExceptions
    {
        public static SqlException Create(int number, byte errorClass, string message = "error")
        {
            const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Instance;
            var error = (SqlError)typeof(SqlError).GetConstructor(Internal, null,
                    [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)], null)!
                .Invoke([number, (byte)1, errorClass, "server", message, "", 0, null]);
            var errors = (SqlErrorCollection)typeof(SqlErrorCollection).GetConstructor(Internal, null, Type.EmptyTypes, null)!.Invoke(null);
            typeof(SqlErrorCollection).GetMethod("Add", Internal)!.Invoke(errors, [error]);
            return (SqlException)typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static, null,
                    [typeof(SqlErrorCollection), typeof(string)], null)!
                .Invoke(null, [errors, "16.0.0"])!;
        }
    }
}
