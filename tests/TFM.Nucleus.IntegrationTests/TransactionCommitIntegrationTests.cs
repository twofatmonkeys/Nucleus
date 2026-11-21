using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Testing;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// How a caller-owned transactional context ends: committed by Commit, rolled back, or doomed
    /// by a failure. The shared stack leaves RequireCommit off, so a context disposed without a
    /// commit still commits there; the strict cases boot a stack of their own with it on.
    /// </summary>
    public abstract class TransactionCommitIntegrationTests(DatabaseFixture fixture)
    {
        private const string ImplicitCommitWarning = "disposed without Commit or Rollback";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static IntWidget NewWidget(string prefix) => new() { Name = $"{prefix}-{Guid.NewGuid():N}", Quantity = 1 };

        private static ContextOptions ChildOf(IDataContext parent) => new() { UseTransaction = true, Parent = parent };

        private async Task<bool> StoredAsync(IntWidget widget) =>
            (await fixture.Manager!.LoadByIDAsync<IntWidget>(widget.ID, cancellationToken: Ct)) != null;

        private int ImplicitCommitWarnings() => fixture.Log!.Entries.Count(e => e.Contains(ImplicitCommitWarning));

        /// <summary>A second stack on the same database whose provider requires a commit. It updates the schema, so it holds the schema lock.</summary>
        private async Task WithStrictStackAsync(Func<IDataProvider, Task> test)
        {
            fixture.RequireSettings();
            using var schema = await fixture.SchemaLockAsync();
            using var stack = await fixture.StartStackAsync(options => options.RequireCommit = true);
            await test(stack.Provider);
        }

        [Fact]
        public async Task AReadInATransaction_LeavesItOpenForTheWritesAfterIt()
        {
            fixture.RequireSettings();
            var widget = NewWidget("read-then-write");

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                _ = await context.LoadAsync<IntWidget>(w => w.ID == widget.ID);
                await context.SaveAsync(widget, Ct);
                await context.CommitAsync(Ct);
            }

            Assert.True(await StoredAsync(widget));
        }

        [Fact]
        public async Task Commit_StoresTheWork_AndWarnsOfNothing()
        {
            fixture.RequireSettings();
            var warnings = ImplicitCommitWarnings();
            var first = NewWidget("commit");
            var second = NewWidget("commit");

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await context.SaveAsync(first, Ct);
                await context.SaveAsync(second, Ct);
                await context.CommitAsync(Ct);
            }

            Assert.True(await StoredAsync(first));
            Assert.True(await StoredAsync(second));
            Assert.Equal(warnings, ImplicitCommitWarnings());
        }

        [Fact]
        public async Task Commit_OnTheSynchronousPath_StoresTheWork()
        {
            fixture.RequireSettings();
            var widget = NewWidget("commit-sync");

            using (var context = fixture.Provider!.CreateContext(ContextOptions.Transactional))
            {
                context.Save(widget);
                context.Commit();
            }

            Assert.True(await StoredAsync(widget));
        }

        [Fact]
        public async Task WithoutRequireCommit_ADisposeWithoutACommit_StillCommits_AndWarns()
        {
            fixture.RequireSettings();
            var warnings = ImplicitCommitWarnings();
            var widget = NewWidget("implicit");

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
                await context.SaveAsync(widget, Ct);

            Assert.True(await StoredAsync(widget));
            Assert.Equal(warnings + 1, ImplicitCommitWarnings());
        }

        [Fact]
        public async Task AFailedValidation_DoomsTheTransaction_SoTheEarlierWriteIsRolledBack()
        {
            // The rule throws before any command runs. That failure used to leave the transaction
            // unmarked, so disposing it committed the first save.
            fixture.RequireSettings();
            var first = NewWidget("doomed");

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await context.SaveAsync(first, Ct);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    context.SaveAsync(new IntVoucher { Code = IntVoucher.InvalidCode }, Ct));
            }

            Assert.False(await StoredAsync(first));
        }

        [Fact]
        public async Task ACommitOfADoomedTransaction_RollsBack_AndSaysSo()
        {
            fixture.RequireSettings();
            var widget = NewWidget("doomed-commit");

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await context.SaveAsync(widget, Ct);
                await context.RollbackAsync(Ct);
                var thrown = await Assert.ThrowsAsync<DataContextException>(() => context.CommitAsync(Ct));
                Assert.Equal(DataContextErrorKind.TransactionDoomed, thrown.Kind);
            }

            Assert.False(await StoredAsync(widget));
        }

        [Fact]
        public async Task AStatementAfterTheCommit_IsRefused()
        {
            fixture.RequireSettings();
            var late = NewWidget("late");

            await using (var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await context.SaveAsync(NewWidget("on-time"), Ct);
                await context.CommitAsync(Ct);

                var thrown = await Assert.ThrowsAsync<DataContextException>(() => context.SaveAsync(late, Ct));
                Assert.Equal(DataContextErrorKind.TransactionCompleted, thrown.Kind);
                // A rollback after the commit, as a catch around the work would make, does nothing.
                context.TryRollback();
            }

            Assert.False(await StoredAsync(late));
        }

        [Fact]
        public async Task WithRequireCommit_ADisposeWithoutACommit_RollsBack()
        {
            var widget = NewWidget("strict");

            await WithStrictStackAsync(async provider =>
            {
                await using var context = await provider.CreateContextAsync(ContextOptions.Transactional, Ct);
                await context.SaveAsync(widget, Ct);
            });

            Assert.False(await StoredAsync(widget));
        }

        [Fact]
        public async Task WithRequireCommit_ACallerExceptionBetweenTwoSaves_StoresNothing()
        {
            var first = NewWidget("between");
            var second = NewWidget("between");

            static void TheCallersOwnFailure() => throw new InvalidOperationException("the caller's own failure, outside any try");

            await WithStrictStackAsync(async provider =>
            {
                var thrown = await Record.ExceptionAsync(async () =>
                {
                    await using var context = await provider.CreateContextAsync(ContextOptions.Transactional, Ct);
                    await context.SaveAsync(first, Ct);
                    TheCallersOwnFailure();
                    await context.SaveAsync(second, Ct);
                    await context.CommitAsync(Ct);
                });
                Assert.IsType<InvalidOperationException>(thrown);
            });

            Assert.False(await StoredAsync(first));
            Assert.False(await StoredAsync(second));
        }

        [Fact]
        public async Task WithRequireCommit_ACommittedContext_Stores()
        {
            var widget = NewWidget("strict-commit");

            await WithStrictStackAsync(async provider =>
            {
                await using var context = await provider.CreateContextAsync(ContextOptions.Transactional, Ct);
                await context.SaveAsync(widget, Ct);
                await context.CommitAsync(Ct);
            });

            Assert.True(await StoredAsync(widget));
        }

        [Fact]
        public async Task AChild_RunsInItsRootsTransaction()
        {
            // A child once opened a connection and a transaction of its own, which the root
            // committed separately: it could not see the root's uncommitted rows, and a failure
            // between the commits left part of the tree committed.
            fixture.RequireSettings();
            var rootWidget = NewWidget("tree-root");
            var childWidget = NewWidget("tree-child");

            await using (var root = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await root.SaveAsync(rootWidget, Ct);
                await using (var child = await fixture.Provider.CreateContextAsync(ChildOf(root), Ct))
                {
                    Assert.Single(await child.LoadAsync<IntWidget>(w => w.ID == rootWidget.ID));
                    await child.SaveAsync(childWidget, Ct);
                    await child.CommitAsync(Ct);
                }
                Assert.Single(await root.LoadAsync<IntWidget>(w => w.ID == childWidget.ID));
                await root.RollbackAsync(Ct);
            }

            Assert.False(await StoredAsync(rootWidget));
            Assert.False(await StoredAsync(childWidget));
        }

        [Fact]
        public async Task ATree_CommitsWhole_WhenItsRootCommits()
        {
            fixture.RequireSettings();
            var rootWidget = NewWidget("tree-commit");
            var childWidget = NewWidget("tree-commit");

            await using (var root = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await using (var child = await fixture.Provider.CreateContextAsync(ChildOf(root), Ct))
                {
                    await child.SaveAsync(childWidget, Ct);
                    await child.CommitAsync(Ct);
                }
                await root.SaveAsync(rootWidget, Ct);
                await root.CommitAsync(Ct);
            }

            Assert.True(await StoredAsync(rootWidget));
            Assert.True(await StoredAsync(childWidget));
        }

        [Fact]
        public async Task ARootCommit_WithAChildStillOpen_IsRefused()
        {
            fixture.RequireSettings();
            var widget = NewWidget("open-child");

            await using (var root = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await root.SaveAsync(widget, Ct);
                await using var child = await fixture.Provider.CreateContextAsync(ChildOf(root), Ct);
                var thrown = await Assert.ThrowsAsync<DataContextException>(() => root.CommitAsync(Ct));
                Assert.Equal(DataContextErrorKind.ChildContextOpen, thrown.Kind);
                await root.RollbackAsync(Ct);
            }

            Assert.False(await StoredAsync(widget));
        }

        [Fact]
        public async Task AFailureInAChild_DoomsTheWholeTree()
        {
            fixture.RequireSettings();
            var rootWidget = NewWidget("child-fails");

            await using (var root = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct))
            {
                await root.SaveAsync(rootWidget, Ct);
                await using (var child = await fixture.Provider.CreateContextAsync(ChildOf(root), Ct))
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        child.SaveAsync(new IntVoucher { Code = IntVoucher.InvalidCode }, Ct));
                var thrown = await Assert.ThrowsAsync<DataContextException>(() => root.CommitAsync(Ct));
                Assert.Equal(DataContextErrorKind.TransactionDoomed, thrown.Kind);
            }

            Assert.False(await StoredAsync(rootWidget));
        }

        [Fact]
        public async Task WithRequireCommit_AChildDisposedWithoutACommit_DoomsTheTree()
        {
            var rootWidget = NewWidget("child-uncommitted");

            await WithStrictStackAsync(async provider =>
            {
                await using var root = await provider.CreateContextAsync(ContextOptions.Transactional, Ct);
                await root.SaveAsync(rootWidget, Ct);
                await using (var child = await provider.CreateContextAsync(ChildOf(root), Ct))
                    await child.SaveAsync(NewWidget("child-uncommitted"), Ct);
                var thrown = await Assert.ThrowsAsync<DataContextException>(() => root.CommitAsync(Ct));
                Assert.Equal(DataContextErrorKind.TransactionDoomed, thrown.Kind);
            });

            Assert.False(await StoredAsync(rootWidget));
        }
    }
}
