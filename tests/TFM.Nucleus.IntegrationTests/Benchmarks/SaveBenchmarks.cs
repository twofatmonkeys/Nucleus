using TFM.Nucleus.TestSupport.Benchmarking;

namespace TFM.Nucleus.IntegrationTests.Benchmarks
{
    /// <summary>
    /// Save throughput on each write path: one object at a time, a batch in one command, a batch
    /// split across commands (the sync path), a batch large enough for the bulk engine, and parent
    /// and child graphs inserted, updated, pruned and deleted. Skipped unless NUCLEUS_BENCHMARK=1;
    /// gated against Benchmarks/Baselines (see <see cref="BenchmarkGate"/>).
    /// <para>
    /// The objects a run writes are built before the timing starts and deleted after it stops, so
    /// every run writes to a table of the same size and only the save itself is timed.
    /// </para>
    /// </summary>
    public abstract class SaveBenchmarks(DatabaseFixture fixture) : DatabaseBenchmarks(fixture)
    {
        [Fact]
        public Task SaveScenarios() => RunAsync("save", manager => [.. Scenarios(manager), .. ChildModelScenarios(manager), Bulk(manager)]);

        private static IEnumerable<BenchmarkScenario> Scenarios(ObjectDataManager manager) =>
        [
            Widgets(manager, "single x20", 20, async widgets =>
            {
                foreach (var w in widgets)
                    await manager.SaveAsync(w, Ct);
                foreach (var w in widgets)
                {
                    w.Quantity++;
                    await manager.SaveAsync(w, Ct);
                }
            }),
            Widgets(manager, "batch 100", 100, async widgets =>
            {
                await manager.SaveAsync(widgets, Ct);
                widgets.ForEach(w => w.Quantity++);
                await manager.SaveAsync(widgets, Ct);
            }),
            Widgets(manager, "sync split 1000", 1000, widgets =>
            {
                manager.Save(widgets);
                widgets.ForEach(w => w.Quantity++);
                manager.Save(widgets);
                return Task.CompletedTask;
            }),
            GraphInsert(manager),
            GraphUpdate(manager),
            DeleteCascade(manager),
        ];

        /// <summary>Scenarios of the child model: removals, three levels, and the save builder alone.</summary>
        private static IEnumerable<BenchmarkScenario> ChildModelScenarios(ObjectDataManager manager) =>
        [
            GraphRemove(manager),
            NestedInsert(manager),
            BuildOnly(manager),
        ];

        /// <summary>
        /// The bulk engine, last: after 20,000-row saves the process allocates more for the same
        /// work (the thread pool and its buffer pools have grown), so nothing is measured after it.
        /// </summary>
        private static BenchmarkScenario Bulk(ObjectDataManager manager) =>
            Widgets(manager, "bulk 20000", 20000, async widgets =>
            {
                await manager.SaveAsync(widgets, Ct);
                widgets.ForEach(w => w.Quantity++);
                await manager.SaveAsync(widgets, Ct);
            });

        private static BenchmarkScenario Widgets(ObjectDataManager manager, string name, int count, Func<List<IntBenchWidget>, Task> run)
        {
            var prefix = "";
            List<IntBenchWidget> widgets = [];
            return new BenchmarkScenario
            {
                Name = name,
                Prepare = () =>
                {
                    prefix = Prefix();
                    widgets = [.. Enumerable.Range(0, count).Select(i => new IntBenchWidget { Name = $"{prefix}{i}", Quantity = i })];
                    return Task.CompletedTask;
                },
                Run = () => run(widgets),
                Reset = () => manager.DeleteWhereAsync<IntBenchWidget>(w => w.Name!.StartsWith(prefix), Ct),
            };
        }

        private static List<IntBenchOrder> NewOrders(string prefix, int orders, int lines) =>
        [
            .. Enumerable.Range(0, orders).Select(o =>
            {
                var order = new IntBenchOrder { Name = $"{prefix}{o}" };
                for (var l = 0; l < lines; l++)
                    order.Lines.Add(new IntBenchOrderLine { Sequence = l, Quantity = l });
                return order;
            }),
        ];

        private static async Task<List<IntBenchOrder>> SavedOrdersAsync(ObjectDataManager manager, string prefix, int orders, int lines)
        {
            await manager.SaveAsync<IntBenchOrder>(NewOrders(prefix, orders, lines), Ct);
            var loaded = await manager.Get<IntBenchOrder>().Where(o => o.Name!.StartsWith(prefix)).ToListAsync(Ct);
            Assert.Equal(orders * lines, loaded.Sum(o => o.Lines.Count));
            return loaded;
        }

        private static Task<int> DeleteOrders(ObjectDataManager manager, string prefix) =>
            manager.DeleteWhereAsync<IntBenchOrder>(o => o.Name!.StartsWith(prefix), Ct);

        private static BenchmarkScenario GraphInsert(ObjectDataManager manager)
        {
            var prefix = "";
            List<IntBenchOrder> orders = [];
            return new BenchmarkScenario
            {
                Name = "graph insert 50x20",
                Prepare = () =>
                {
                    prefix = Prefix();
                    orders = NewOrders(prefix, 50, 20);
                    return Task.CompletedTask;
                },
                Run = () => manager.SaveAsync<IntBenchOrder>(orders, Ct),
                Reset = () => DeleteOrders(manager, prefix),
            };
        }

        /// <summary>One line changed in each of 50 loaded orders: the cost of walking and writing the graph.</summary>
        private static BenchmarkScenario GraphUpdate(ObjectDataManager manager)
        {
            var prefix = Prefix();
            List<IntBenchOrder> orders = [];
            return new BenchmarkScenario
            {
                Name = "graph update one line x50",
                Seed = async () => await SavedOrdersAsync(manager, prefix, 50, 20),
                Prepare = async () =>
                {
                    orders = await manager.Get<IntBenchOrder>().Where(o => o.Name!.StartsWith(prefix)).ToListAsync(Ct);
                    foreach (var order in orders)
                        order.Lines[0].Quantity++;
                },
                Run = () => manager.SaveAsync<IntBenchOrder>(orders, Ct),
                TearDown = () => DeleteOrders(manager, prefix),
            };
        }

        private static BenchmarkScenario DeleteCascade(ObjectDataManager manager)
        {
            var prefix = "";
            return new BenchmarkScenario
            {
                Name = "delete cascade 50x20",
                Prepare = async () =>
                {
                    prefix = Prefix();
                    await manager.SaveAsync<IntBenchOrder>(NewOrders(prefix, 50, 20), Ct);
                },
                Run = async () => Assert.Equal(50, await DeleteOrders(manager, prefix)),
                Reset = () => DeleteOrders(manager, prefix),
            };
        }

        /// <summary>One line removed from each of 50 loaded orders, deleted by the owner's save.</summary>
        private static BenchmarkScenario GraphRemove(ObjectDataManager manager)
        {
            var prefix = "";
            List<IntBenchOrder> orders = [];
            return new BenchmarkScenario
            {
                Name = "graph remove one line x50",
                Prepare = async () =>
                {
                    prefix = Prefix();
                    orders = await SavedOrdersAsync(manager, prefix, 50, 20);
                    foreach (var order in orders)
                        order.Lines.RemoveAt(0);
                },
                Run = () => manager.SaveAsync<IntBenchOrder>(orders, Ct),
                Reset = () => DeleteOrders(manager, prefix),
            };
        }

        private static BenchmarkScenario NestedInsert(ObjectDataManager manager)
        {
            var prefix = "";
            List<IntBenchNestOrder> orders = [];
            return new BenchmarkScenario
            {
                Name = "nested insert 30x10x3",
                Prepare = () =>
                {
                    prefix = Prefix();
                    orders = [.. Enumerable.Range(0, 30).Select(o =>
                    {
                        var order = new IntBenchNestOrder { Name = $"{prefix}{o}" };
                        for (var l = 0; l < 10; l++)
                        {
                            var line = new IntBenchNestLine { Sequence = l, Quantity = l };
                            order.Lines.Add(line);
                            for (var n = 0; n < 3; n++)
                                line.Notes.Add(new IntBenchNestNote { Text = $"note {n}" });
                        }
                        return order;
                    })];
                    return Task.CompletedTask;
                },
                Run = () => manager.SaveAsync<IntBenchNestOrder>(orders, Ct),
                Reset = () => manager.DeleteWhereAsync<IntBenchNestOrder>(o => o.Name!.StartsWith(prefix), Ct),
            };
        }

        /// <summary>The save builder and the SQL it renders, with nothing executed: Nucleus's own cost.</summary>
        private static BenchmarkScenario BuildOnly(ObjectDataManager manager)
        {
            List<IntBenchOrder> orders = [];
            return new BenchmarkScenario
            {
                Name = "build only 100x10",
                Prepare = () =>
                {
                    orders = NewOrders(Prefix(), 100, 10);
                    return Task.CompletedTask;
                },
                Run = () =>
                {
                    var queries = manager.DataProvider.CreateSaveQueries(orders);
                    Assert.NotEmpty(queries);
                    foreach (var query in queries)
                        Assert.False(string.IsNullOrEmpty(query.BuildQuery()));
                    return Task.CompletedTask;
                },
            };
        }
    }
}
