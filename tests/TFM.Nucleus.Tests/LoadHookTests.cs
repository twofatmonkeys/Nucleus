using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// <c>OnAfterLoadFromDB</c> runs once per loaded object, after the object is finished: its
    /// parent bound, its children attached and finished first, and its excluded collections
    /// unloaded. It used to run only from <c>BindDataSet</c>, which no load path calls.
    /// </summary>
    public class LoadHookTests
    {
        private static TableSchema Table<T>() where T : ICoreObject => new(ObjectSchema.Get<T>());
        private static TableSchema? AnyTable(Type type) => new(ObjectSchema.Get(type));

        private static object?[] Row(IReadOnlyList<string> columns, Dictionary<string, object?> values) =>
            [.. columns.Select(c => values.TryGetValue(c, out var v) ? v : null)];

        /// <summary>Two parents, two lines each, read as a load reads them: one data set with the lines linked.</summary>
        private static (SelectQuery Query, DataSet Rows) TwoParents(bool excludeNotes)
        {
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, Table<HookParent>(), AnyTable);
            if (excludeNotes)
                query.ExcludeChildCollection(nameof(HookParent.Notes));
            var rows = DataSet.Initialize(Table<HookParent>());
            var lines = DataSet.Initialize(Table<HookLine>());
            rows.LinkedDataSets.Add(lines);
            if (!excludeNotes)
                rows.LinkedDataSets.Add(DataSet.Initialize(Table<HookNote>()));
            for (var p = 0; p < 2; p++)
            {
                var id = Guid.NewGuid();
                rows.FillNext(Row(rows.ColumnNames, new() { ["ID"] = id, ["Name"] = $"p{p}", ["Timestamp"] = DateTimeOffset.UtcNow }));
                for (var l = 0; l < 2; l++)
                    lines.FillNext(Row(lines.ColumnNames, new() { ["ID"] = Guid.NewGuid(), ["Parent"] = id, ["Sequence"] = l, ["Timestamp"] = DateTimeOffset.UtcNow }));
            }
            return (query, rows);
        }

        [Fact]
        public void BuildCollection_RunsEachHookOnce_ChildrenFirst_WithTheParentBound()
        {
            HookLog.Clear();
            var (query, rows) = TwoParents(excludeNotes: false);

            var parents = ChildGraphBinder.BuildCollection<HookParent>(query, rows, null);

            Assert.Equal(2, parents.Count);
            Assert.All(parents, p => Assert.Equal(1, p.HookRuns));
            Assert.All(parents.SelectMany(p => p.Lines), l => Assert.Equal(1, l.HookRuns));
            // A child's hook sees its parent; a parent's hook sees its finished children.
            Assert.All(parents.SelectMany(p => p.Lines), l => Assert.True(l.SawParent));
            Assert.All(parents, p => Assert.Equal(2, p.FinishedLinesSeen));
            // Children first: each parent's two lines come before it in the log.
            foreach (var parent in parents)
            {
                var at = HookLog.IndexOf(parent);
                Assert.All(parent.Lines, l => Assert.True(HookLog.IndexOf(l) < at));
            }
        }

        [Fact]
        public void BuildCollection_MarksExcludedCollectionsUnloaded_BeforeTheHookRuns()
        {
            var (query, rows) = TwoParents(excludeNotes: true);

            var parents = ChildGraphBinder.BuildCollection<HookParent>(query, rows, null, [ObjectSchema.Get<HookParent>().GetProperty(nameof(HookParent.Notes))!]);

            Assert.All(parents, p => Assert.False(p.NotesWereLoaded));
            Assert.All(parents, p => Assert.False(p.Notes.IsLoaded));
        }

        [Fact]
        public void Complete_OnALazyCollectionThatIsNotLoaded_DoesNotLoadItOrRunItsChildrensHooks()
        {
            var parent = HookParent.Stored();
            ((TFM.Nucleus.Abstractions.Interfaces.INucleusChildCollection)parent.Lines).MarkUnloaded(lazy: true);

            ChildGraphBinder.Complete(parent, []);

            Assert.Equal(1, parent.HookRuns);
            Assert.False(parent.Lines.IsLoaded);
        }
    }

    /// <summary>The order hooks ran in, across a test.</summary>
    internal static class HookLog
    {
        private static readonly List<object> _entries = [];
        public static void Add(object obj) { lock (_entries) _entries.Add(obj); }
        public static int IndexOf(object obj) { lock (_entries) return _entries.IndexOf(obj); }
        public static void Clear() { lock (_entries) _entries.Clear(); }
    }

    public class HookParent : CoreObject<HookParent>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<HookLine, HookParent> Lines => GetChildCollection<HookLine, HookParent>();

        public NucleusChildCollection<HookNote, HookParent> Notes => GetChildCollection<HookNote, HookParent>();

        public int HookRuns;
        public int FinishedLinesSeen;
        public bool NotesWereLoaded;

        public static HookParent Stored()
        {
            var parent = new HookParent { Name = "stored" };
            ((ICoreObject)parent).CommitChanges();
            return parent;
        }

        protected internal override void OnAfterLoadFromDB()
        {
            HookRuns++;
            NotesWereLoaded = Notes.IsLoaded;
            if (Lines.IsLoaded)
                FinishedLinesSeen = Lines.Count(l => l.HookRuns == 1);
            HookLog.Add(this);
        }
    }

    public class HookLine : CoreChildObject<HookLine, HookParent>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }

        public int HookRuns;
        public bool SawParent;

        protected internal override void OnAfterLoadFromDB()
        {
            HookRuns++;
            SawParent = GetRawParent() != null;
            HookLog.Add(this);
        }

        private object? GetRawParent()
        {
            try { return Parent; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public class HookNote : CoreChildObject<HookNote, HookParent>
    {
        [DatabaseColumn]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }
    }
}
