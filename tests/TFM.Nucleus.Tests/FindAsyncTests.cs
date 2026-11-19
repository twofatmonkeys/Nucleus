namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// <c>FindAsync</c> reads on the asynchronous path. It used to wrap the synchronous
    /// <c>LoadByID</c> in <c>Task.FromResult</c>, so the caller's thread waited on the database.
    /// </summary>
    public class FindAsyncTests
    {
        [Fact]
        public async Task FindAsync_ReadsOnAnAsyncContext()
        {
            var provider = new FakeDataProvider();
            var manager = new ObjectDataManager(provider, new ObjectSchemaProvider(), null);

            var found = await manager.Get<TestWidget>().FindAsync(Guid.NewGuid());

            Assert.Null(found);
            Assert.Single(provider.ContextTokens);
            Assert.Single(provider.Contexts);
        }

        [Fact]
        public async Task LoadByIDAsync_PassesTheCallersToken()
        {
            var provider = new FakeDataProvider();
            var manager = new ObjectDataManager(provider, new ObjectSchemaProvider(), null);
            using var cts = new CancellationTokenSource();

            await manager.LoadByIDAsync<TestWidget>(Guid.NewGuid(), cts.Token);

            Assert.Equal(cts.Token, Assert.Single(provider.ContextTokens));
        }

        [Fact]
        public async Task LoadByIDAsync_OfAnEmptyId_ReadsNothing()
        {
            var provider = new FakeDataProvider();
            var manager = new ObjectDataManager(provider, new ObjectSchemaProvider(), null);

            Assert.Null(await manager.LoadByIDAsync<TestWidget>(Guid.Empty));
            Assert.Empty(provider.Contexts);
        }
    }
}
