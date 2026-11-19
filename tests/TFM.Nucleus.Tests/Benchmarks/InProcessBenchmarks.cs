using System.Linq.Expressions;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.TestSupport.Benchmarking;

namespace TFM.Nucleus.Tests.Benchmarks
{
    /// <summary>
    /// Nucleus's own cost on the save and read paths, with no database: the save graph walk, the
    /// children's validation, the write and delete builders, binding rows to a parent and child
    /// graph, materialising objects and translating a predicate. Opt-in, like every benchmark:
    /// skipped unless NUCLEUS_BENCHMARK=1, and run for an architectural change, not for each
    /// feature or fix. Allocations repeat to the byte, so they are gated against one baseline for
    /// every machine; times are reported against this machine's. Runs alone (its collection
    /// disables parallelisation), so no other test allocates during a measurement.
    /// </summary>
    [Collection(InProcessBenchmarkCollection.Name)]
    public class InProcessBenchmarks
    {
        private const int Rounds = 15;
        private const int Warmups = 5;
        private const int Parents = 100;
        private const int ChildrenPerParent = 10;
        private const int LeavesPerChild = 2;

        private static readonly IQueryRenderer Renderer = SqlServerQueryRenderer.Instance;
        private static TableSchema Table<T>() where T : ICoreObject => new(ObjectSchema.Get<T>());
        private static TableSchema? AnyTable(Type type) => new(ObjectSchema.Get(type));

        [Fact]
        public async Task InProcessScenarios()
        {
            Assert.SkipUnless(BenchmarkSettings.Enabled, "Set NUCLEUS_BENCHMARK=1 to run the benchmarks (tools/Run-Benchmarks.ps1).");
            Assert.SkipUnless(BenchmarkSettings.IsOptimized, "Benchmarks measure Release builds only: Debug code allocates differently.");
            List<BaselineTarget> targets =
            [
                BaselineTarget.ForAnyMachine("inprocess", BenchmarkMeasure.Allocations),
                BaselineTarget.ForMachine("inprocess", BenchmarkMeasure.Time),
            ];

            using var machineLock = await BenchmarkLock.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);
            await BenchmarkSuite.MeasureAndGateAsync(machineLock, Scenarios(), targets, Rounds, Warmups);
        }

        private static IEnumerable<BenchmarkScenario> Scenarios() =>
        [
            SaveGraphWalk(),
            ValidateGraph(),
            WriteBuild(),
            DeleteRemovedBuild(),
            BindGraph(),
            Materialise(),
            ParsePredicate(),
        ];

        /// <summary>A stored graph (committed, so nothing is new) with one child changed per parent.</summary>
        private static List<BenchParent> StoredGraph()
        {
            var parents = NewGraph();
            foreach (var parent in parents)
                ((ICoreObject)parent).CommitChanges();
            foreach (var parent in parents)
                parent.Children[0].Quantity++;
            return parents;
        }

        private static List<BenchParent> NewGraph() =>
        [
            .. Enumerable.Range(0, Parents).Select(p =>
            {
                var parent = new BenchParent { Name = $"parent {p}" };
                for (var c = 0; c < ChildrenPerParent; c++)
                {
                    var child = new BenchChild { Sequence = c, Quantity = c };
                    parent.Children.Add(child);
                    for (var l = 0; l < LeavesPerChild; l++)
                        child.Leaves.Add(new BenchLeaf { Text = $"leaf {l}" });
                }
                return parent;
            }),
        ];

        private static BenchmarkScenario SaveGraphWalk()
        {
            List<BenchParent> parents = [];
            return BenchmarkScenario.Of("savegraph walk 100x10x2",
                prepare: () => parents = StoredGraph(),
                run: () => Assert.Equal(Parents, SaveGraph.Walk(parents).Roots.Count));
        }

        private static BenchmarkScenario ValidateGraph()
        {
            List<BenchParent> parents = [];
            return BenchmarkScenario.Of("validate 100x10x2",
                prepare: () => parents = NewGraph(),
                run: () =>
                {
                    foreach (var parent in parents)
                        parent.Validate();
                });
        }

        /// <summary>The insert of 100 new parents and the update of 100 changed ones, rendered.</summary>
        private static BenchmarkScenario WriteBuild()
        {
            var table = Table<BenchParent>();
            List<ICoreObject> fresh = [];
            List<ICoreObject> changed = [];
            return BenchmarkScenario.Of("insert+update build 100",
                prepare: () =>
                {
                    fresh = [.. Enumerable.Range(0, 100).Select(i => new BenchParent { Name = $"new {i}" })];
                    changed = [.. Enumerable.Range(0, 100).Select(i =>
                    {
                        var parent = new BenchParent { Name = $"stored {i}" };
                        ((ICoreObject)parent).CommitChanges();
                        parent.Name = $"changed {i}";
                        return (ICoreObject)parent;
                    })];
                },
                run: () =>
                {
                    Assert.NotEmpty(InsertQuery.Create(Renderer, table, fresh)!.BuildQuery());
                    Assert.NotEmpty(UpdateQuery.Create(Renderer, table, changed)!.BuildQuery());
                });
        }

        /// <summary>The delete of 50 removed children, cascading to their leaves, rendered.</summary>
        private static BenchmarkScenario DeleteRemovedBuild()
        {
            var table = Table<BenchChild>();
            var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
            return BenchmarkScenario.Of("delete removed build 50",
                run: () => Assert.NotEmpty(DeleteWhereQuery.ForRemovedChildren(Renderer, table, AnyTable, ids)!.BuildQuery()));
        }

        /// <summary>100 parents and their 1000 children bound from rows, as a load binds them.</summary>
        private static BenchmarkScenario BindGraph()
        {
            var parentTable = Table<BenchFlatParent>();
            var childTable = Table<BenchFlatChild>();
            var query = new SelectQuery(Renderer, parentTable, AnyTable);
            DataSet? rows = null;
            return BenchmarkScenario.Of("bind 100x10",
                prepare: () =>
                {
                    rows?.Dispose();
                    rows = DataSet.Initialize(parentTable);
                    var children = DataSet.Initialize(childTable);
                    rows.LinkedDataSets.Add(children);
                    for (var p = 0; p < Parents; p++)
                    {
                        var id = Guid.NewGuid();
                        rows.FillNext(Row(rows.ColumnNames, new() { ["ID"] = id, ["Name"] = $"parent {p}", ["Timestamp"] = DateTime.UtcNow }));
                        for (var c = 0; c < ChildrenPerParent; c++)
                            children.FillNext(Row(children.ColumnNames, new() { ["ID"] = Guid.NewGuid(), ["Parent"] = id, ["Sequence"] = c, ["Quantity"] = c, ["Timestamp"] = DateTime.UtcNow }));
                    }
                },
                run: () =>
                {
                    var parents = ChildGraphBinder.BuildCollection<BenchFlatParent>(query, rows!);
                    Assert.Equal(Parents * ChildrenPerParent, parents.Sum(p => p.Children.Count));
                });
        }

        private static object?[] Row(IReadOnlyList<string> columns, Dictionary<string, object?> values) =>
            [.. columns.Select(c => values.TryGetValue(c, out var v) ? v : throw new InvalidOperationException($"No value for column {c}."))];

        private static BenchmarkScenario Materialise()
        {
            var build = ObjectSchema<BenchFlatChild>.CreateEmitObjectBuilder();
            List<Dictionary<string, object?>> rows = [];
            return BenchmarkScenario.Of("materialise 1000",
                prepare: () => rows = [.. Enumerable.Range(0, 1000).Select(i => new Dictionary<string, object?>
                {
                    ["ID"] = Guid.NewGuid(),
                    ["Sequence"] = i,
                    ["Quantity"] = i,
                })],
                run: () =>
                {
                    foreach (var row in rows)
                        Assert.False(build(row).IsNew);
                });
        }

        /// <summary>A five-term predicate with a parent term and a closure list, translated 200 times.</summary>
        private static BenchmarkScenario ParsePredicate()
        {
            var table = Table<BenchChild>();
            var parentId = Guid.NewGuid();
            var floor = 3;
            var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
            Expression<Func<BenchChild, bool>> predicate = c =>
                c.Parent.ID == parentId && c.Sequence > floor && c.Quantity <= 50 && ids.Contains(c.ID) && !(c.Sequence == 7);
            return BenchmarkScenario.Of("parse predicate x200", run: () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var query = new SelectQuery(Renderer, table, AnyTable);
                    ExpressionParser.ParseExpressionToQuery(query, predicate.Body);
                    Assert.Equal(5, query.Where.AllParameters().Count);
                }
            });
        }
    }

    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class InProcessBenchmarkCollection
    {
        public const string Name = "InProcessBenchmarks";
    }

    /// <summary>Three levels, deletable through the root, with a rule on the middle level.</summary>
    [Deletable]
    public class BenchParent : CoreObject<BenchParent>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<BenchChild, BenchParent> Children => GetChildCollection<BenchChild, BenchParent>();
    }

    public class BenchChild : CoreChildObject<BenchChild, BenchParent>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }

        public NucleusChildCollection<BenchLeaf, BenchChild> Leaves => GetChildCollection<BenchLeaf, BenchChild>();

        public override void Validate()
        {
            if (Quantity < 0)
                throw new InvalidOperationException("A quantity cannot be negative.");
            base.Validate();
        }
    }

    public class BenchLeaf : CoreChildObject<BenchLeaf, BenchChild>
    {
        [DatabaseColumn]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Two levels, for binding rows: one child query, so one linked result set.</summary>
    public class BenchFlatParent : CoreObject<BenchFlatParent>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<BenchFlatChild, BenchFlatParent> Children => GetChildCollection<BenchFlatChild, BenchFlatParent>();
    }

    public class BenchFlatChild : CoreChildObject<BenchFlatChild, BenchFlatParent>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }
    }
}
