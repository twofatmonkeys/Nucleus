using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// READ_COMMITTED_SNAPSHOT and the reads that must not depend on it. A database the provider
    /// creates has it on, an existing one without it gets a warning at initialise, and a delete's
    /// check for linked collection members waits for a member another transaction is inserting
    /// whatever the database's setting, so it is refused once that member commits.
    /// The tests that create a database skip when the test login cannot create one, and drop it
    /// after; the shared test database is never altered.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public sealed class ReadCommittedSnapshotTests(SqlServerFixture fixture)
    {
        private const string Warning = "READ_COMMITTED_SNAPSHOT is off";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ADeleteOfAOne_WaitsForAMemberBeingInserted_ThenIsRefused()
        {
            await AssertDeleteWaitsForAnUncommittedMemberAsync(fixture.RequireManager());
        }

        [Fact]
        public async Task ACreatedDatabase_HasReadCommittedSnapshotOn_AndTheCheckStillWaits()
        {
            await WithNewDatabaseAsync(async name =>
            {
                using var stack = await fixture.StartStackAsync(c => c.Database = name);

                Assert.True(await IsOnAsync(name));
                Assert.DoesNotContain(stack.Log.Entries, e => e.Contains(Warning, StringComparison.Ordinal));
                // Without its locking hint the check would read past the uncommitted member here.
                await AssertDeleteWaitsForAnUncommittedMemberAsync(stack.Manager);
            });
        }

        [Fact]
        public async Task ADatabaseWithReadCommittedSnapshotOff_IsLeftOff_WithAWarning()
        {
            await WithNewDatabaseAsync(async name =>
            {
                using (var created = await fixture.StartStackAsync(c => { c.Database = name; c.ReadCommittedSnapshotOnCreate = false; }))
                    Assert.False(await IsOnAsync(name));

                // An existing database: initialise reports the setting and does not change it.
                using var existing = await fixture.StartStackAsync(c => c.Database = name);
                Assert.Contains(existing.Log.Entries, e => e.Contains(Warning, StringComparison.Ordinal) && e.Contains(name, StringComparison.Ordinal));
                Assert.False(await IsOnAsync(name));
            });
        }

        /// <summary>
        /// A transaction inserts a member of a saved one and holds it uncommitted; a delete of the
        /// one waits for it, and once it commits the delete is refused and nothing is deleted.
        /// </summary>
        private static async Task AssertDeleteWaitsForAnUncommittedMemberAsync(IObjectDataManager manager)
        {
            var name = $"rc-{Guid.NewGuid():N}";
            var campaign = new IntCampaign { Name = name };
            await manager.SaveAsync(campaign, Ct);
            try
            {
                await using (var context = await manager.CreateContextAsync<IntVoucher>(ContextOptions.Transactional, Ct))
                {
                    await new IntVoucher { Code = name, CampaignId = campaign.ID }.SaveAsync(context, Ct);

                    var delete = Task.Run(() => manager.DeleteAsync(campaign, Ct), Ct);
                    Assert.NotSame(delete, await Task.WhenAny(delete, Task.Delay(TimeSpan.FromSeconds(3), Ct)));

                    await context.CommitAsync(Ct);
                    var refused = await Assert.ThrowsAnyAsync<Exception>(() => delete.WaitAsync(TimeSpan.FromSeconds(30), Ct));
                    Assert.Contains("still has IntVoucher members", refused.ToString());
                }
                Assert.Equal(1, await manager.Rows<IntCampaign>(c => c.ID == campaign.ID).CountAsync(Ct));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntVoucher>(v => v.Code == name, Ct);
                await manager.DeleteWhereAsync<IntCampaign>(c => c.Name == name, Ct);
            }
        }

        /// <summary>
        /// Runs <paramref name="test"/> with the name of a database that does not exist yet, and
        /// drops it after. Skips when the test login cannot create a database.
        /// </summary>
        private async Task WithNewDatabaseAsync(Func<string, Task> test)
        {
            fixture.RequireSettings();
            var canCreate = Convert.ToInt32(await fixture.Database.ScalarAsync("SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')", Ct));
            Assert.SkipUnless(canCreate == 1, "The SQL Server test login cannot create a database.");
            var name = $"NucleusRcsi_{Guid.NewGuid():N}";
            try
            {
                await test(name);
            }
            finally
            {
                // The stacks' pooled connections stay open after they are disposed.
                SqlConnection.ClearAllPools();
                await fixture.Database.ExecuteAsync(
                    $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END",
                    CancellationToken.None);
            }
        }

        private async Task<bool> IsOnAsync(string database) =>
            Convert.ToBoolean(await fixture.Database.ScalarAsync(
                $"SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = N'{database}'", Ct));
    }
}
