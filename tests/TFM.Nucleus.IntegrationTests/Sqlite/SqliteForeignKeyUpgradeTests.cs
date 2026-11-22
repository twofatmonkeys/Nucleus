using Microsoft.Data.Sqlite;
using TFM.Nucleus.Data.SQLite;

namespace TFM.Nucleus.IntegrationTests.Sqlite
{
    /// <summary>
    /// The first start with foreign keys on a SQLite file that has none: SQLite keeps a key only in
    /// its table's definition, so every table with a reference is rebuilt with its keys, in one
    /// start, keeping its rows. A row that already points at nothing is copied and reported; rows
    /// written after are checked. Runs on a file of its own, so the shared one is never rebuilt.
    /// </summary>
    [Collection(SqliteCollection.Name)]
    public sealed class SqliteForeignKeyUpgradeTests(SqliteFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AFileWithoutKeys_IsRebuiltWithThem_InOneStart_KeepingEveryRow()
        {
            fixture.RequireSettings();
            var file = Path.Combine(Path.GetTempPath(), $"nucleus-fk-upgrade-{Guid.NewGuid():N}.db");
            var keeper = new IntKeyKeeper { Name = "Mavis" };
            var resident = new IntKeyResident { Name = "Gerald", Keeper = keeper };
            var orphan = new IntKeyResident { Name = "Brenda", Keeper = new IntKeyKeeper { Name = "never saved" } };
            try
            {
                using (var before = await fixture.StartStackAsync(o => { ((SqliteConfiguration)o).Database = file; o.ForeignKeys = false; }))
                {
                    Assert.Equal(DataProviderStatus.Ok, before.Provider.Status);
                    await before.Manager.SaveAsync(keeper, Ct);
                    // Without keys the file takes a row pointing at a keeper that was never written.
                    await before.Manager.SaveAsync<IntKeyResident>([resident, orphan], Ct);
                }

                using var after = await fixture.StartStackAsync(o => ((SqliteConfiguration)o).Database = file);

                Assert.Equal(DataProviderStatus.Ok, after.Provider.Status);
                Assert.Equal(DataProviderStatus.Ok, await after.Provider.CheckSchemaIsCurrentAsync());
                var loaded = await after.Manager.Get<IntKeyResident>().ToListAsync(Ct);
                Assert.Equal(["Brenda", "Gerald"], loaded.Select(r => r.Name).Order());
                Assert.Equal("Mavis", loaded.Single(r => r.Name == "Gerald").Keeper!.Name);
                Assert.Contains(after.Log.Entries, e => e.Contains("FK_tbl_TFM_Nucleus_IntegrationTests_IntKeyResident_Keeper") && e.Contains("1 existing rows"));

                var refused = await Assert.ThrowsAnyAsync<DatabaseException>(() =>
                    after.Manager.SaveAsync(new IntKeyResident { Name = "Colin", Keeper = new IntKeyKeeper { Name = "never saved" } }, Ct));
                Assert.Equal(DatabaseErrorKind.ForeignKeyViolation, refused.Kind);
                var stillPointedAt = await Assert.ThrowsAnyAsync<DatabaseException>(() => after.Manager.DeleteAsync(keeper, Ct));
                Assert.Equal(DatabaseErrorKind.ForeignKeyViolation, stillPointedAt.Kind);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    File.Delete(file + suffix);
            }
        }
    }
}
