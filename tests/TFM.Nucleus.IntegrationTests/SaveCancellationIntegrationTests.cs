namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A cancelled save through the runtime, the route CoreObject.SaveAsync(token) takes, throws
    /// OperationCanceledException as it is and writes nothing. Before, the runtime dropped the
    /// token, so the save ran to completion.
    /// </summary>
    public abstract class SaveCancellationIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task CancelledSave_ThrowsAndWritesNothing()
        {
            var manager = fixture.RequireManager();
            var name = $"cancel-{Guid.NewGuid():N}";
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ((INucleusRuntime)manager).SaveAsync(new IntWidget { Name = name }, cts.Token));

            Assert.Empty(await manager.Get<IntWidget>().Where(w => w.Name == name).ToListAsync(Ct));
        }
    }
}
