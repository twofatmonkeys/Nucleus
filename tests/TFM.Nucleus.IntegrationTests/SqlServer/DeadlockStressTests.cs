using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// Runs the child model's save, read, removal and delete cycle from many workers at once, each
    /// on rows of its own, and fails on any deadlock (error 1205), naming the step that lost.
    /// Opt-in: it skips unless NUCLEUS_DEADLOCK_STRESS is set (to the number of workers, or 1 for
    /// the default).
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class DeadlockStressTests(SqlServerFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static int Workers =>
            Environment.GetEnvironmentVariable("NUCLEUS_DEADLOCK_STRESS") is { } value && int.TryParse(value, out var n)
                ? (n > 1 ? n : 8)
                : 0;

        private static bool IsDeadlock(Exception? ex)
        {
            for (; ex is not null; ex = ex.InnerException)
                if (ex is SqlException { Number: 1205 })
                    return true;
            return false;
        }

        [Fact]
        public async Task TheChildModelCycle_FromManyWorkers_NeverDeadlocks()
        {
            Assert.SkipWhen(Workers == 0, "Set NUCLEUS_DEADLOCK_STRESS to run.");
            var manager = fixture.RequireManager();
            // A test in another run that drops and recreates an index on these tables deadlocks
            // with any workload on them, which says nothing about Nucleus; hold it off.
            using var schema = await fixture.SchemaLockAsync();
            const int iterations = 30;
            var deadlocks = new ConcurrentDictionary<string, int>();
            var others = new ConcurrentQueue<string>();
            var tag = $"dl-{Guid.NewGuid():N}";

            async Task Step(string step, Func<Task> work)
            {
                try
                {
                    await work();
                }
                catch (Exception ex) when (IsDeadlock(ex))
                {
                    deadlocks.AddOrUpdate(step, 1, (_, c) => c + 1);
                }
            }

            async Task Worker(int worker)
            {
                for (var i = 0; i < iterations; i++)
                {
                    var name = $"{tag}-{worker}-{i}";
                    try
                    {
                        var roster = new IntRoster { Name = name };
                        foreach (var code in new[] { "a", "b" })
                        {
                            var line = new IntRosterLine { Code = $"{name}-{code}", Hours = 4 };
                            line.Breaks.Add(new IntRosterBreak { Minutes = 30 });
                            roster.Lines.Add(line);
                        }
                        await Step("save new roster", () => manager.SaveAsync(roster, Ct));
                        var note = new IntLineNote { Text = name, Line = roster.Lines.First() };
                        await Step("save note", () => manager.SaveAsync(note, Ct));
                        await Step("load note and parent", async () =>
                        {
                            var loaded = (await manager.Get<IntLineNote>().Where(n => n.Text == name).ToListAsync(Ct)).SingleOrDefault();
                            _ = loaded?.Line?.Parent.Name;
                        });
                        await Step("load roster", () => manager.Get<IntRoster>().Where(r => r.Name == name).ToListAsync(Ct));
                        await Step("save roster with a removal", () =>
                        {
                            roster.Lines.Remove(roster.Lines.Last());
                            return manager.SaveAsync(roster, Ct);
                        });
                        await Step("delete note by text", () => manager.DeleteWhereAsync<IntLineNote>(n => n.Text == name, Ct));
                        await Step("delete roster by name", () => manager.DeleteWhereAsync<IntRoster>(r => r.Name == name, Ct));
                    }
                    catch (Exception ex)
                    {
                        Exception? sql = ex;
                        while (sql is not null and not SqlException)
                            sql = sql.InnerException;
                        others.Enqueue(sql is SqlException s
                            ? $"[{ex.Message.Split('\n')[0]}] SqlException {s.Number}: {s.Message.Split('\n')[0]}"
                            : $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                    }
                }
            }

            try
            {
                await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(() => Worker(w), Ct)));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntLineNote>(n => n.Text!.StartsWith(tag), Ct);
                await manager.DeleteWhereAsync<IntRoster>(r => r.Name!.StartsWith(tag), Ct);
            }

            var report = string.Join("; ", deadlocks.OrderBy(d => d.Key).Select(d => $"{d.Key}: {d.Value}"));
            Assert.True(deadlocks.IsEmpty && others.IsEmpty,
                $"{Workers} workers x {iterations} cycles. Deadlocks by step: [{report}]. Other failures: {others.Count} " +
                string.Join(" | ", others.GroupBy(o => o).Select(g => $"{g.Count()}x {g.Key}")));
        }
    }
}
