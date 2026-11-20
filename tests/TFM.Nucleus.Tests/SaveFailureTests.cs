using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// What a failed manager save throws. A unique violation, a cancellation, and the provider's
    /// and the context's own exceptions reach the caller as they were thrown, as they do from the
    /// context, so <c>catch (UniqueViolationException ex) when (ex.Is&lt;T&gt;(...))</c> works
    /// against <c>SaveAsync</c>. Before, every failure was wrapped in "Error saving objects", and
    /// the sync <c>Save</c> wrapped a cancellation too. Any other failure is still wrapped.
    /// </summary>
    public class SaveFailureTests
    {
        public static TheoryData<bool> Paths => new() { false, true };

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task UniqueViolation_IsThrownAsItIs(bool async)
        {
            var failure = new UniqueViolationException("m", "UQ_x", typeof(TestWidget), nameof(TestWidget.Name), "Name", new Exception());

            var (thrown, provider) = await SaveFailing(failure, async);

            Assert.Same(failure, thrown);
            Assert.True(Assert.Single(provider.Contexts).RolledBack);
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task Cancellation_IsThrownAsItIs(bool async)
        {
            var failure = new OperationCanceledException();

            var (thrown, provider) = await SaveFailing(failure, async);

            Assert.Same(failure, thrown);
            Assert.True(Assert.Single(provider.Contexts).RolledBack);
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task ProviderException_IsThrownAsItIs(bool async)
        {
            var failure = new DataProviderException("m", DataProviderErrorKind.Error, new Exception());

            var (thrown, provider) = await SaveFailing(failure, async);

            Assert.Same(failure, thrown);
            Assert.True(Assert.Single(provider.Contexts).RolledBack);
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task ContextException_IsThrownAsItIs(bool async)
        {
            var failure = new DataContextException("m", DataContextErrorKind.Unspecified);

            var (thrown, provider) = await SaveFailing(failure, async);

            Assert.Same(failure, thrown);
            Assert.True(Assert.Single(provider.Contexts).RolledBack);
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task OtherFailure_IsWrapped(bool async)
        {
            var failure = new InvalidOperationException("m");

            var (thrown, provider) = await SaveFailing(failure, async);

            Assert.Equal(typeof(Exception), thrown.GetType());
            Assert.Equal("Error saving objects", thrown.Message);
            Assert.Same(failure, thrown.InnerException);
            Assert.True(Assert.Single(provider.Contexts).RolledBack);
        }

        /// <summary>Saves a new widget on a provider whose every execution throws <paramref name="failure"/>, and returns what the save threw.</summary>
        private static async Task<(Exception Thrown, FakeDataProvider Provider)> SaveFailing(Exception failure, bool async)
        {
            var provider = new FakeDataProvider { ExecuteFailure = failure };
            var manager = new ObjectDataManager(provider, new ObjectSchemaProvider(), null);
            var widget = new TestWidget { Name = "w" };

            var thrown = async
                ? await Record.ExceptionAsync(() => manager.SaveAsync<TestWidget>([widget], CancellationToken.None))
                : Record.Exception(() => manager.Save<TestWidget>([widget]));

            Assert.NotNull(thrown);
            Assert.True(widget.IsNew, "A failed save must not commit the object.");
            return (thrown, provider);
        }
    }
}
