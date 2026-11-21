using System.Text.RegularExpressions;

namespace TFM.Nucleus.IntegrationTests.Postgres
{
    /// <summary>
    /// A commit that fails in a way that does not say whether it took effect is reported as such.
    /// PostgreSQL lets a session end another of the same role's (pg_terminate_backend), which makes
    /// the failure happen on demand; SQL Server needs a permission a test login rarely has.
    /// </summary>
    [Collection(PostgresCollection.Name)]
    public sealed partial class PostgresCommitFailureTests(PostgresFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ACommitOnAConnectionTheServerEnded_HasAnUnknownOutcome_AndIsNotTransient()
        {
            fixture.RequireSettings();
            var probe = new IntFailureProbe { Name = $"commit-{Guid.NewGuid():N}" };

            var context = await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct);
            DatabaseException failure;
            await using (context)
            {
                await context.SaveAsync(probe, Ct);
                // The second argument waits, up to 5 s, until the session has ended.
                await fixture.Database.ExecuteAsync($"SELECT pg_terminate_backend({BackendOf(context)}, 5000)", Ct);

                failure = await Assert.ThrowsAsync<DatabaseException>(() => context.CommitAsync(Ct));
            }

            Assert.Equal(DatabaseErrorKind.CommitOutcomeUnknown, failure.Kind);
            Assert.Equal(DatabaseOperation.Commit, failure.Operation);
            Assert.True(failure.MayHaveBeenApplied);
            Assert.False(failure.IsTransient);
            // The server ended the session before the commit arrived, so it rolled the transaction
            // back; a client whose connection broke at that moment cannot tell which happened.
            Assert.Null(await fixture.Manager!.LoadByIDAsync<IntFailureProbe>(probe.ID, cancellationToken: Ct));
        }

        /// <summary>The server process of the context's connection, which its description names.</summary>
        private static int BackendOf(IDataContext context)
        {
            var match = BackendPattern().Match(context.ToString()!);
            Assert.True(match.Success, $"The context does not name its backend: {context}");
            return int.Parse(match.Groups[1].Value);
        }

        [GeneratedRegex(@"backend (\d+)")]
        private static partial Regex BackendPattern();
    }
}
