using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TFM.Nucleus.Data.PostgreSQL;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.SQLite;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// MaxConnectionPoolSize is the one connection limit. A SQL Server or PostgreSQL provider
    /// built its transactional connections without Enlist=false and the rest with it, and both
    /// drivers pool by connection string, so it kept two pools of that size; a raw connection
    /// string kept the driver's default of 100 whatever the setting said. Now every connection to
    /// the database has one string, the startup connections are not pooled, and the provider's
    /// contexts and change subscriptions share one budget of slots. Nothing here opens a
    /// connection.
    /// </summary>
    public class ConnectionStringLimitTests
    {
        private static SQLConfiguration SqlServer() => new()
        {
            Server = "db",
            Database = "nucleus",
            Username = "user",
            Password = "secret",
            AppName = "tests",
            MaxConnectionPoolSize = 7,
        };

        private static PostgresConfiguration Postgres() => new()
        {
            Server = "db",
            Database = "nucleus",
            Username = "user",
            Password = "secret",
            AppName = "tests",
            MaxConnectionPoolSize = 7,
        };

        [Fact]
        public void SqlServer_BuildsOneUnenlistedStringSizedToTheSetting()
        {
            var options = SqlServer();

            var builder = new SqlConnectionStringBuilder(options.BuildNewSQLConnection().ConnectionString);

            Assert.False(builder.Enlist);
            Assert.Equal(7, builder.MaxPoolSize);
            Assert.True(builder.Pooling);
            Assert.Equal(options.BuildNewSQLConnection().ConnectionString, options.BuildNewSQLConnection().ConnectionString);
        }

        [Fact]
        public void SqlServer_OverwritesARawStringsPoolSizeAndEnlistment()
        {
            var options = SqlServer();
            options.ConnectionString = "Server=db;Database=nucleus;Integrated Security=true;Max Pool Size=500;Enlist=true";

            var builder = new SqlConnectionStringBuilder(options.BuildNewSQLConnection().ConnectionString);

            Assert.False(builder.Enlist);
            Assert.Equal(7, builder.MaxPoolSize);
            Assert.Equal("nucleus", builder.InitialCatalog);
        }

        [Fact]
        public void SqlServer_DoesNotPoolTheMasterConnection()
        {
            foreach (var raw in new string?[] { null, "Server=db;Database=nucleus;Integrated Security=true;Enlist=true" })
            {
                var options = SqlServer();
                options.ConnectionString = raw;

                var builder = new SqlConnectionStringBuilder(options.BuildSystemSQLConnection().ConnectionString);

                Assert.False(builder.Pooling);
                Assert.False(builder.Enlist);
                Assert.Equal("master", builder.InitialCatalog);
            }
        }

        [Fact]
        public void Postgres_BuildsOneUnenlistedStringSizedToTheSetting()
        {
            var options = Postgres();

            var builder = new NpgsqlConnectionStringBuilder(options.BuildConnection().ConnectionString);

            Assert.False(builder.Enlist);
            Assert.Equal(7, builder.MaxPoolSize);
            Assert.True(builder.Pooling);
            Assert.Equal(options.BuildConnection().ConnectionString, options.BuildConnection().ConnectionString);
        }

        [Fact]
        public void Postgres_OverwritesARawStringsPoolSizeAndEnlistment()
        {
            var options = Postgres();
            options.ConnectionString = "Host=db;Username=user;Password=secret;Maximum Pool Size=500;Enlist=true";

            var builder = new NpgsqlConnectionStringBuilder(options.BuildConnection().ConnectionString);

            Assert.False(builder.Enlist);
            Assert.Equal(7, builder.MaxPoolSize);
            Assert.Equal("nucleus", builder.Database);
        }

        [Fact]
        public void Postgres_DoesNotPoolTheMaintenanceConnection()
        {
            var builder = new NpgsqlConnectionStringBuilder(Postgres().BuildMaintenanceConnection().ConnectionString);

            Assert.False(builder.Pooling);
            Assert.False(builder.Enlist);
            Assert.Equal(PostgresConfiguration.MaintenanceDatabase, builder.Database);
        }

        /// <summary>The defaults fail fast and alike: nothing waits more than 3 seconds before the caller hears.</summary>
        [Fact]
        public void EveryProvider_WaitsThreeSecondsByDefault()
        {
            RelationalProviderOptions[] all = [new SQLConfiguration(), new PostgresConfiguration(), new SqliteConfiguration()];
            Assert.All(all, options => Assert.Equal(3, options.PoolWaitTimeoutSeconds));

            Assert.Equal(3, new SQLConfiguration().ConnectTimeoutSeconds);
            Assert.Equal(3, new PostgresConfiguration().ConnectTimeoutSeconds);
            Assert.Equal(3, new SqliteConfiguration().BusyTimeoutSeconds);
            Assert.Equal(3, new NpgsqlConnectionStringBuilder(Postgres().BuildConnection().ConnectionString).Timeout);
        }
    }

    /// <summary>A provider's connection budget, shared by its root contexts and change subscriptions.</summary>
    public class ConnectionSlotTests
    {
        private static RelationalDataProvider Provider(int size, int waitSeconds = 0)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNucleus(o => o.AppName = "slot-test");
            services.AddNucleusSqliteDataProvider(o =>
            {
                o.Database = Path.Combine(Path.GetTempPath(), "nucleus-slot-test-unused.db");
                o.MaxConnectionPoolSize = size;
                o.PoolWaitTimeoutSeconds = waitSeconds;
            });
            var built = services.BuildServiceProvider();
            return Assert.IsAssignableFrom<RelationalDataProvider>(built.GetRequiredService<IDataProvider>());
        }

        [Fact]
        public void TheBudget_IsTheSetting_AndRefusesPastItAtOnceOnTheSyncPath()
        {
            var slots = Provider(2).ConnectionSlots;

            using var first = slots.Acquire();
            using var second = slots.Acquire();

            Assert.Equal(0, slots.Available);
            var ex = Assert.Throws<DataContextException>(() => slots.Acquire());
            Assert.Equal(DataContextErrorKind.PoolExhausted, ex.Kind);
            Assert.Contains("MaxConnectionPoolSize", ex.Message);
        }

        [Fact]
        public async Task TheAsyncPath_WaitsItsTimeout_ThenFailsAsPoolExhausted()
        {
            var slots = Provider(1, waitSeconds: 1).ConnectionSlots;
            using var held = slots.Acquire();

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<DataContextException>(async () => await slots.AcquireAsync(TestContext.Current.CancellationToken));

            Assert.Equal(DataContextErrorKind.PoolExhausted, ex.Kind);
            Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task TheAsyncPath_TakesASlotThatComesFreeWhileItWaits()
        {
            var slots = Provider(1, waitSeconds: 3).ConnectionSlots;
            var held = slots.Acquire();

            var waiting = slots.AcquireAsync(TestContext.Current.CancellationToken).AsTask();
            held.Dispose();
            using var taken = await waiting;

            Assert.Equal(0, slots.Available);
        }

        [Fact]
        public void ASlot_GivesItselfBackOnce()
        {
            var slots = Provider(2).ConnectionSlots;

            var slot = slots.Acquire();
            slot.Dispose();
            slot.Dispose();

            Assert.Equal(2, slots.Available);
        }

        [Fact]
        public void EachProvider_HasABudgetOfItsOwn()
        {
            var one = Provider(1).ConnectionSlots;
            var other = Provider(1).ConnectionSlots;

            using var held = one.Acquire();

            Assert.Equal(0, one.Available);
            Assert.Equal(1, other.Available);
            using var alsoHeld = other.Acquire();
        }
    }

    /// <summary>
    /// Opening shares one connect timeout across its attempts, so retries cannot stretch a 3 s
    /// timeout into a long wait. A PostgreSQL timeout is transient, and three attempts of 15 s
    /// each, with back-off, once waited about 45 s.
    /// </summary>
    public class ConnectionOpenerBudgetTests
    {
        private static bool AnyFailure(Exception _) => true;

        [Fact]
        public async Task AQuickTransientFailure_IsRetriedWithinTheBudget()
        {
            var connection = new ScriptedConnection(timeoutSeconds: 3, _ => throw new IOException("refused"), _ => Task.CompletedTask);

            await ConnectionOpener.OpenAsync(connection, async: true, attempts: 3, AnyFailure, ConnectionOpener.TimeoutOf(connection), TestContext.Current.CancellationToken);

            Assert.Equal(2, connection.Attempts);
        }

        [Fact]
        public async Task AnAttemptStillRunningAtTheBudget_IsCutAsATimeout()
        {
            var connection = new ScriptedConnection(timeoutSeconds: 1, token => Task.Delay(Timeout.Infinite, token));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await ConnectionOpener.OpenAsync(connection, async: true, attempts: 3, AnyFailure, ConnectionOpener.TimeoutOf(connection), TestContext.Current.CancellationToken));

            Assert.Equal(1, connection.Attempts);
            Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task NoAttemptStarts_ThatTheBackOffWouldPushPastTheBudget()
        {
            var connection = new ScriptedConnection(timeoutSeconds: 1, async _ =>
            {
                // 850 ms plus the first back-off of 200 ms passes the 1 s budget.
                await Task.Delay(850);
                throw new IOException("slow refusal");
            });

            await Assert.ThrowsAsync<IOException>(async () =>
                await ConnectionOpener.OpenAsync(connection, async: true, attempts: 5, AnyFailure, ConnectionOpener.TimeoutOf(connection), TestContext.Current.CancellationToken));

            Assert.Equal(1, connection.Attempts);
        }

        [Fact]
        public async Task TheCallersCancellation_StaysACancellation()
        {
            var connection = new ScriptedConnection(timeoutSeconds: 30, token => Task.Delay(Timeout.Infinite, token));
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await ConnectionOpener.OpenAsync(connection, async: true, attempts: 3, AnyFailure, ConnectionOpener.TimeoutOf(connection), cancel.Token));
        }

        [Fact]
        public async Task TheOldOverload_HasNoBudget()
        {
            var connection = new ScriptedConnection(timeoutSeconds: 1,
                async _ => { await Task.Delay(700); throw new IOException("slow refusal"); },
                async _ => { await Task.Delay(700); throw new IOException("slow refusal"); },
                _ => Task.CompletedTask);

            await ConnectionOpener.OpenAsync(connection, async: true, attempts: 3, AnyFailure, TestContext.Current.CancellationToken);

            Assert.Equal(3, connection.Attempts);
        }

        [Fact]
        public void AZeroConnectTimeout_IsNoLimit()
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, ConnectionOpener.TimeoutOf(new ScriptedConnection(timeoutSeconds: 0)));
            Assert.Equal(TimeSpan.FromSeconds(3), ConnectionOpener.TimeoutOf(new ScriptedConnection(timeoutSeconds: 3)));
        }

        /// <summary>A connection whose opens run the given steps in turn; the last repeats.</summary>
        private sealed class ScriptedConnection(int timeoutSeconds, params Func<CancellationToken, Task>[] opens) : DbConnection
        {
            private ConnectionState _state = ConnectionState.Closed;

            public int Attempts { get; private set; }

            public override int ConnectionTimeout => timeoutSeconds;

            public override async Task OpenAsync(CancellationToken cancellationToken)
            {
                var step = opens[Math.Min(Attempts, opens.Length - 1)];
                Attempts++;
                await step(cancellationToken);
                _state = ConnectionState.Open;
            }

            public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

#pragma warning disable CS8765 // the base declares the setter's value non-null; nothing here reads it
            public override string ConnectionString { get; set; } = "";
#pragma warning restore CS8765
            public override string Database => "scripted";
            public override string DataSource => "scripted";
            public override string ServerVersion => "0";
            public override ConnectionState State => _state;
            public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
            public override void Close() => _state = ConnectionState.Closed;
            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
            protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
        }
    }
}
