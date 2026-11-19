using System.Linq.Expressions;
using TFM.Nucleus.TestSupport.Benchmarking;

namespace TFM.Nucleus.IntegrationTests.Benchmarks
{
    /// <summary>
    /// Read throughput on each load path: a flat listing (async, sync, streamed and paged), loads
    /// by ID, parent and child graphs with and without their children, lazy collections listed,
    /// included and read on first access, and child rows read and counted by their parent.
    /// Skipped unless NUCLEUS_BENCHMARK=1; gated against Benchmarks/Baselines (see
    /// <see cref="BenchmarkGate"/>).
    /// <para>
    /// The data is written once, before any timing, and deleted after the last scenario. Every
    /// scenario checks what it read, so a benchmark that silently reads nothing cannot pass.
    /// </para>
    /// </summary>
    public abstract class ReadBenchmarks(DatabaseFixture fixture) : DatabaseBenchmarks(fixture)
    {
        private const int Widgets = 1000;
        private const int Orders = 100;
        private const int LinesPerOrder = 10;
        private const int Owners = 100;
        private const int ItemsPerOwner = 10;

        /// <summary>The seeded data's prefix and the IDs the scenarios read by.</summary>
        private sealed class Data
        {
            public string Tag { get; } = Prefix();
            public List<Guid> WidgetIds { get; set; } = [];
            public List<Guid> OwnerIds { get; set; } = [];
        }

        [Fact]
        public Task ReadScenarios()
        {
            var data = new Data();
            return RunAsync("read", manager => [.. Scenarios(manager, data), .. ChildModelScenarios(manager, data)], manager => SeedAsync(manager, data));
        }

        private static async Task<Func<Task>> SeedAsync(ObjectDataManager manager, Data data)
        {
            var prefix = data.Tag;
            var widgets = Enumerable.Range(0, Widgets).Select(i => new IntBenchWidget { Name = $"{prefix}{i}", Quantity = i }).ToList();
            await manager.SaveAsync(widgets, Ct);
            data.WidgetIds = [.. widgets.Take(50).Select(w => w.ID)];

            await manager.SaveAsync<IntBenchOrder>(Enumerable.Range(0, Orders).Select(o =>
            {
                var order = new IntBenchOrder { Name = $"{prefix}{o}" };
                for (var l = 0; l < LinesPerOrder; l++)
                    order.Lines.Add(new IntBenchOrderLine { Sequence = l, Quantity = l });
                return order;
            }).ToList(), Ct);

            var owners = Enumerable.Range(0, Owners).Select(o =>
            {
                var owner = new IntBenchLazyOwner { Name = $"{prefix}{o}" };
                for (var i = 0; i < ItemsPerOwner; i++)
                    owner.Items.Add(new IntBenchLazyItem { Sequence = i });
                return owner;
            }).ToList();
            await manager.SaveAsync<IntBenchLazyOwner>(owners, Ct);
            data.OwnerIds = [.. owners.Take(20).Select(o => o.ID)];

            return async () =>
            {
                await manager.DeleteWhereAsync<IntBenchWidget>(w => w.Name!.StartsWith(prefix), Ct);
                await manager.DeleteWhereAsync<IntBenchOrder>(o => o.Name!.StartsWith(prefix), Ct);
                await manager.DeleteWhereAsync<IntBenchLazyOwner>(o => o.Name!.StartsWith(prefix), Ct);
            };
        }

        private static IEnumerable<BenchmarkScenario> Scenarios(ObjectDataManager manager, Data data)
        {
            var prefix = data.Tag;
            Expression<Func<IntBenchWidget, bool>> mine = w => w.Name!.StartsWith(prefix);
            return
            [
                new()
                {
                    Name = "flat 1000",
                    Run = async () => Assert.Equal(Widgets, (await manager.Get<IntBenchWidget>().Where(mine).ToListAsync(Ct)).Count),
                },
                new()
                {
                    Name = "flat 1000 sync",
                    Run = () =>
                    {
                        Assert.Equal(Widgets, manager.Load<IntBenchWidget>(mine.Body)!.Count);
                        return Task.CompletedTask;
                    },
                },
                new()
                {
                    Name = "stream 1000",
                    Run = async () =>
                    {
                        var count = 0;
                        await foreach (var _ in manager.LoadStreamAsync<IntBenchWidget>(mine.Body, Ct))
                            count++;
                        Assert.Equal(Widgets, count);
                    },
                },
                new()
                {
                    Name = "paged 50 of 1000 with total",
                    Run = async () =>
                    {
                        var page = await manager.LoadPagedAsync<IntBenchWidget>(500, 50, mine.Body, Ct);
                        Assert.Equal(50, page.Count);
                        Assert.Equal(Widgets, page.TotalCount);
                    },
                },
                new()
                {
                    Name = "by id x50",
                    Run = () =>
                    {
                        foreach (var id in data.WidgetIds)
                            Assert.NotNull(manager.LoadByID<IntBenchWidget>(id));
                        return Task.CompletedTask;
                    },
                },
                new()
                {
                    Name = "graph 100x10",
                    Run = async () =>
                    {
                        var orders = await manager.Get<IntBenchOrder>().Where(o => o.Name!.StartsWith(prefix)).ToListAsync(Ct);
                        Assert.Equal(Orders * LinesPerOrder, orders.Sum(o => o.Lines.Count));
                    },
                },
                new()
                {
                    Name = "graph 100 children excluded",
                    Run = async () => Assert.Equal(Orders,
                        (await manager.Get<IntBenchOrder>().Exclude(o => o.Lines).Where(o => o.Name!.StartsWith(prefix)).ToListAsync(Ct)).Count),
                },
                new()
                {
                    Name = "lazy listing 100",
                    Run = async () => Assert.Equal(Owners,
                        (await manager.Get<IntBenchLazyOwner>().Where(o => o.Name!.StartsWith(prefix)).ToListAsync(Ct)).Count),
                },
            ];
        }

        /// <summary>Scenarios of the child model: lazy collections and reads by parent.</summary>
        private static IEnumerable<BenchmarkScenario> ChildModelScenarios(ObjectDataManager manager, Data data)
        {
            var prefix = data.Tag;
            List<IntBenchLazyOwner> listed = [];
            return
            [
                new()
                {
                    Name = "lazy include 100",
                    Run = async () =>
                    {
                        var owners = await manager.Get<IntBenchLazyOwner>().Include(o => o.Items).Where(o => o.Name!.StartsWith(prefix)).ToListAsync(Ct);
                        Assert.Equal(Owners * ItemsPerOwner, owners.Sum(o => o.Items.Count));
                    },
                },
                new()
                {
                    Name = "lazy first read x20",
                    Prepare = async () =>
                    {
                        var ids = data.OwnerIds;
                        listed = await manager.Get<IntBenchLazyOwner>().Where(o => ids.Contains(o.ID)).ToListAsync(Ct);
                    },
                    Run = () =>
                    {
                        Assert.Equal(20 * ItemsPerOwner, listed.Sum(o => o.Items.Count));
                        return Task.CompletedTask;
                    },
                },
                new()
                {
                    Name = "children by parent x20",
                    Run = async () =>
                    {
                        foreach (var id in data.OwnerIds)
                            Assert.Equal(ItemsPerOwner, (await manager.Get<IntBenchLazyItem>().Where(i => i.Parent.ID == id).ToListAsync(Ct)).Count);
                    },
                },
                new()
                {
                    Name = "count by parent x20",
                    Run = async () =>
                    {
                        foreach (var id in data.OwnerIds)
                            Assert.Equal(ItemsPerOwner, await manager.Rows<IntBenchLazyItem>(i => i.Parent.ID == id).CountAsync(Ct));
                    },
                },
            ];
        }
    }
}
