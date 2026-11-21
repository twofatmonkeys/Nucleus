namespace TFM.Nucleus.IntegrationTests.Postgres
{
    /// <summary>
    /// A subscription hears every statement that writes its table, through the table's trigger
    /// and the channel it notifies.
    /// </summary>
    [Collection(PostgresCollection.Name)]
    public sealed class PostgresChangeSubscriptionTests(PostgresFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task Subscription_FiresOnInsertUpdateAndDelete()
        {
            var manager = fixture.RequireManager();
            await using var subscription = await manager.Providers.AllProviders[0].SubscribeToChangesAsync<IntHardItem>(Ct);
            var changes = System.Threading.Channels.Channel.CreateUnbounded<Type>();
            subscription.Changed += (_, e) => changes.Writer.TryWrite(e.EntityType);

            var item = new IntHardItem { Name = "notify" };
            await manager.SaveAsync(item);
            Assert.Equal(typeof(IntHardItem), await NextChange(changes));

            item.Name = "notify again";
            await manager.SaveAsync(item);
            Assert.Equal(typeof(IntHardItem), await NextChange(changes));

            var deleted = await manager.DeleteWhereAsync<IntHardItem>(x => x.ID == item.ID, Ct);
            Assert.Equal(1, deleted);
            Assert.Equal(typeof(IntHardItem), await NextChange(changes));

            Assert.False(subscription.IsFaulted);
        }

        private static async Task<Type> NextChange(System.Threading.Channels.Channel<Type> changes)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await changes.Reader.ReadAsync(timeout.Token);
        }
    }
}
