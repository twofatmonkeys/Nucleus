namespace TFM.Nucleus.IntegrationTests.Sqlite
{
    /// <summary>
    /// SQLite lets one connection write at a time. Writers that arrive together wait for the
    /// lock in turn, up to the busy timeout, instead of failing, and a write too large for one
    /// command runs its commands one after another on its own transaction instead of fanning out
    /// to connections that would wait on it, so it commits or rolls back whole.
    /// </summary>
    [Collection(SqliteCollection.Name)]
    public sealed class SqliteConcurrencyTests(SqliteFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // Each row binds a few parameters, so this many need several commands under the limit.
        private const int Oversized = 12000;

        [Fact]
        public async Task ParallelSaves_AllCommit()
        {
            var manager = fixture.RequireManager();
            var tag = $"par-{Guid.NewGuid():N}";
            const int Writers = 8, PerWriter = 25;

            try
            {
                await Task.WhenAll(Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
                {
                    for (var i = 0; i < PerWriter; i++)
                        await manager.SaveAsync(new IntHardItem { Name = $"{tag}-{w}-{i}" });
                }, Ct)));

                var saved = await manager.Get<IntHardItem>().Where(x => x.Name!.StartsWith(tag)).ToListAsync(Ct);
                Assert.Equal(Writers * PerWriter, saved.Count);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntHardItem>(x => x.Name!.StartsWith(tag));
            }
        }

        [Fact]
        public async Task AnOversizedSave_CommitsEveryRow()
        {
            var manager = fixture.RequireManager();
            var tag = $"big-{Guid.NewGuid():N}";
            var items = Enumerable.Range(0, Oversized).Select(i => new IntHardItem { Name = $"{tag}-{i}" }).ToList();

            try
            {
                await manager.SaveAsync(items);

                var saved = await manager.Get<IntHardItem>().Where(x => x.Name!.StartsWith(tag)).ToListAsync(Ct);
                Assert.Equal(Oversized, saved.Count);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntHardItem>(x => x.Name!.StartsWith(tag));
            }
        }

        [Fact]
        public async Task AnOversizedSave_ThatBreaksAUniqueIndexLate_SavesNothing()
        {
            var manager = fixture.RequireManager();
            var tag = $"dup-{Guid.NewGuid():N}";
            await manager.SaveAsync(new IntUniqueItem { Code = $"{tag}-existing" });
            // The duplicate is the last row, so the commands before it have already run when it fails.
            var items = Enumerable.Range(0, Oversized).Select(i => new IntUniqueItem { Code = $"{tag}-{i}" })
                .Append(new IntUniqueItem { Code = $"{tag}-existing" }).ToList();

            // IntUniqueItem is not [Deletable]; its tagged rows stay behind, as the unique index suite's do.
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SaveAsync(items));

            Assert.NotNull(ExceptionChain.Find<UniqueViolationException>(ex));
            var left = await manager.Get<IntUniqueItem>().Where(x => x.Code!.StartsWith(tag)).ToListAsync(Ct);
            Assert.Equal($"{tag}-existing", Assert.Single(left).Code);
        }
    }
}
