namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// INucleusRuntime.SaveAsync, which CoreObject.SaveAsync(token) routes through, hands the
    /// caller's token to the context it saves on. Before, it called the manager's overload
    /// without one, so a save could not be cancelled.
    /// </summary>
    public class RuntimeSaveTokenTests
    {
        [Fact]
        public async Task SaveAsync_PassesTheCallersTokenToTheContext()
        {
            var provider = new FakeDataProvider();
            INucleusRuntime runtime = new ObjectDataManager(provider, new ObjectSchemaProvider(), null);
            using var cts = new CancellationTokenSource();

            await runtime.SaveAsync(new TestWidget { Name = "w" }, cts.Token);

            Assert.Equal(cts.Token, Assert.Single(provider.ContextTokens));
            Assert.Equal(cts.Token, Assert.Single(Assert.Single(provider.Contexts).ExecutionTokens));
        }
    }
}
