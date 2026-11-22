using System.Linq.Expressions;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Every shape of query a provider renders, built through the neutral query model with the
    /// provider's renderer, so each provider's raw goldens cover the same cases. A provider's
    /// corpus adds its own schema scripts. Keep object batches at 100 or fewer: above that the
    /// insert and update builders run in parallel and their order is not stable.
    /// </summary>
    public sealed class QueryGoldenCorpus(IQueryRenderer renderer)
    {
        private static TableSchema Table<T>() where T : ICoreObject => new(ObjectSchema.Get<T>());

        private static TableSchema Table(Type type) => new(ObjectSchema.Get(type));

        private static Func<Type, TableSchema?> Tables(params Type[] types)
        {
            var tables = types.ToDictionary(t => t, Table);
            return t => tables.GetValueOrDefault(t);
        }

        private static T Loaded<T>(Guid id) where T : CoreObject =>
            ObjectSchema<T>.BuildObjectViaReflection(new Dictionary<string, object?> { ["ID"] = id });

        /// <summary>Finds any type's table, for the child collection queries of a select.</summary>
        private static TableSchema? AnyTable(Type type) => Table(type);

        private string Select<T>(Action<SelectQuery>? configure = null) where T : ICoreObject
        {
            var query = new SelectQuery(renderer, Table<T>(), AnyTable);
            configure?.Invoke(query);
            return query.BuildQuery();
        }

        /// <summary>A row read of chosen columns, as RowQuery builds one: no child queries.</summary>
        private string Rows<T>(Action<SelectQuery> configure) where T : ICoreObject
        {
            var query = new SelectQuery(renderer, Table<T>(), AnyTable);
            query.LinkedQueries.Clear();
            configure(query);
            return query.BuildQuery();
        }

        /// <summary>A grouped row read over <paramref name="keys"/> with the measures given.</summary>
        private string Grouped<T>(string[] keys, Action<SelectQuery>? configure, params (MeasureKind Kind, string? Column, string Alias)[] measures) where T : ICoreObject
        {
            var table = Table<T>();
            var query = new SelectQuery(renderer, table, AnyTable);
            query.LinkedQueries.Clear();
            query.Columns = [.. keys];
            query.GroupBy = [.. keys];
            query.Measures = [.. measures.Select(m => new Measure(m.Kind, m.Column == null ? null : table[m.Column], m.Alias))];
            configure?.Invoke(query);
            return query.BuildQuery();
        }

        private string Where<T>(Expression<Func<T, bool>> predicate) where T : ICoreObject =>
            new SelectQuery(renderer, Table<T>(), AnyTable).Parse(predicate.Body).BuildQuery();

        private string Term(Parameter parameter) =>
            Select<TestWidget>(q => q.Where.And(parameter));

        /// <summary>A parent of TParent with a child collection query of TChild linked by Parent.</summary>
        private SelectQuery ParentWithChild<TParent, TChild>(Action<SelectQuery>? configureParent = null, Action<SelectQuery>? configureChild = null)
            where TParent : ICoreObject where TChild : ICoreObject
        {
            var parent = new SelectQuery(renderer, Table<TParent>());
            configureParent?.Invoke(parent);
            var child = new SelectQuery(renderer, Table<TChild>()) { ChildCollectionProperty = "Children" };
            configureChild?.Invoke(child);
            parent.AddChildQuery(child, "Parent");
            return parent;
        }

        /// <summary>
        /// The select a delete of <typeparamref name="TOne"/> runs first, to refuse the delete while
        /// a linked collection has members: the members' IDs whose key is in the rows it matches.
        /// </summary>
        private string LinkedMembersOfDelete<TOne, TMany>(string keyColumn, Action<SelectQuery> condition)
            where TOne : ICoreObject where TMany : ICoreObject
        {
            // As RelationalDataProvider.LinkedMembersOf builds it: both reads wait for writers.
            var ones = new SelectQuery(renderer, Table<TOne>(), AnyTable) { WaitForPendingWrites = true };
            ones.LinkedQueries.Clear();
            condition(ones);
            var members = new SelectQuery(renderer, Table<TMany>(), AnyTable) { WaitForPendingWrites = true };
            members.LinkedQueries.Clear();
            members.Columns = ["ID"];
            ones.AddChildQuery(members, keyColumn);
            return members.BuildQuery(includeChildQueries: false);
        }

        private string DeleteWhere<T>(Expression<Func<T, bool>> predicate, Func<Type, TableSchema?> tables)
        {
            var query = new DeleteWhereQuery(renderer, tables(typeof(T))!, tables);
            ExpressionParser.ParseExpressionToQuery(query, predicate.Body);
            return query.BuildQuery();
        }

        /// <summary>The entity types whose CREATE TABLE every provider's goldens pin.</summary>
        public static IEnumerable<Type> SchemaTypes() =>
            new[] { typeof(TestWidget), typeof(TestUniqueWidget), typeof(TestSoftWidget), typeof(TestDeletableChild), typeof(TestAllTypesWidget), typeof(TestSoftParent), typeof(NullableWidget), typeof(TestRenamedWidget), typeof(TestRenamedChild), typeof(TestServerValueWidget) };

        public static TableSchema TableOf(Type type) => Table(type);

        /// <summary>Every query case, rendered by this corpus's renderer.</summary>
        public Dictionary<string, Func<string>> Build()
        {
            var cases = new Dictionary<string, Func<string>>(StringComparer.Ordinal)
            {
                // ---- Selects
                ["select.plain"] = () => Select<TestWidget>(),
                ["select.where"] = () => Select<TestWidget>(q => q.Where.And("Name", "abc")),
                ["select.top"] = () => Select<TestWidget>(q => q.Take = 5),
                ["select.top.ordered"] = () => Select<TestWidget>(q => { q.Take = 5; q.AddOrderBy("Name"); q.AddOrderBy("Quantity", descending: true); }),
                ["select.ordered"] = () => Select<TestWidget>(q => q.AddOrderBy("Name")),
                ["select.paged.count"] = () => Select<TestWidget>(q => { q.Skip = 10; q.Take = 5; q.WithTotalCount = true; q.Where.And("Name", "abc"); }),
                ["select.paged.ordered.notake"] = () => Select<TestWidget>(q => { q.Skip = 10; q.AddOrderBy("Name", descending: true); }),
                ["select.count"] = () => Select<TestWidget>(q => q.WithTotalCount = true),
                ["select.count.where"] = () => Select<TestWidget>(q => { q.WithTotalCount = true; q.Where.And("Quantity", 3); }),
                ["select.nolockflag"] = () => Select<TestWidget>(q => { q.AllowDirtyRead = true; q.WithTotalCount = true; }),
                ["select.empty"] = () => new SelectQuery(renderer, "[dbo].[tbl_TestWidget]", typeof(TestWidget)).BuildQuery(),
                ["select.tablename"] = () =>
                {
                    var q = new SelectQuery(renderer, "[dbo].[tbl_TestWidget]", ["ID", "Name"], typeof(TestWidget));
                    q.Where.And("Name", "abc");
                    return q.BuildQuery();
                },
                ["select.children"] = () => ParentWithChild<TestDeletableWidget, TestDeletableChild>().BuildQuery(),
                ["select.children.where"] = () => ParentWithChild<TestDeletableWidget, TestDeletableChild>(p => p.Where.And("Name", "a")).BuildQuery(),
                ["select.children.top"] = () => ParentWithChild<TestDeletableWidget, TestDeletableChild>(p => p.Take = 5).BuildQuery(),
                ["select.children.top.ordered"] = () => ParentWithChild<TestDeletableWidget, TestDeletableChild>(p => { p.Take = 5; p.AddOrderBy("Name"); }).BuildQuery(),
                ["select.children.paged"] = () => ParentWithChild<TestDeletableWidget, TestDeletableChild>(p => { p.Skip = 10; p.Take = 5; p.WithTotalCount = true; }).BuildQuery(),
                ["select.children.nobatch"] = () => ParentWithChild<TestDeletableWidget, TestDeletableChild>().BuildQuery(includeChildQueries: false),
                ["select.grandchildren"] = () =>
                {
                    var parent = ParentWithChild<TestDeletableWidget, TestDeletableChild>(p => p.Take = 3);
                    var grandchild = new SelectQuery(renderer, Table<TestDeletableChild>()) { ChildCollectionProperty = "Children" };
                    parent.LinkedQueries[0].AddChildQuery(grandchild, "Parent");
                    return parent.BuildQuery();
                },
                ["select.soft"] = () => Select<TestSoftWidget>(),
                ["select.soft.where"] = () => Select<TestSoftWidget>(q => q.Where.And("Name", "a")),
                ["select.soft.includedeleted"] = () => Select<TestSoftWidget>(q => q.IncludeDeleted = true),
                ["select.soft.paged.count"] = () => Select<TestSoftWidget>(q => { q.Skip = 2; q.Take = 2; q.WithTotalCount = true; }),
                ["select.soft.children"] = () => ParentWithChild<TestSoftWidget, TestSoftChild>(p => p.Where.And("Name", "a")).BuildQuery(),
                ["select.soft.children.includedeleted"] = () => ParentWithChild<TestSoftWidget, TestSoftChild>(p => p.IncludeDeleted = true).BuildQuery(),
                ["select.reference"] = () => Select<TestLinkingWidget>(q => q.IncludeLinkedObject("Widget")),
                ["select.reference.where"] = () => Select<TestLinkingWidget>(q => { q.Where.And("Name", "a"); q.IncludeLinkedObject("Widget"); }),
                ["select.reference.top"] = () => Select<TestLinkingWidget>(q => { q.Take = 5; q.AddOrderBy("Name"); q.IncludeLinkedObject("Widget"); }),
                ["select.reference.paged"] = () => Select<TestLinkingWidget>(q => { q.Skip = 10; q.Take = 5; q.WithTotalCount = true; q.IncludeLinkedObject("Widget"); }),
                ["select.reference.withchildren"] = () => Select<TestLinkingWidget>(q => q.IncludeLinkedObject("Owner")),
                ["select.linked"] = () => Select<LcCampaign>(q => q.IncludeLinkedCollection("Vouchers")),
                ["select.linked.where"] = () => Select<LcCampaign>(q => { q.Where.And("Name", "a"); q.IncludeLinkedCollection("Vouchers"); }),
                ["select.linked.top"] = () => Select<LcCampaign>(q => { q.Take = 5; q.AddOrderBy("Name"); q.IncludeLinkedCollection("Vouchers"); }),
                ["select.linked.paged"] = () => Select<LcCampaign>(q => { q.Skip = 10; q.Take = 5; q.WithTotalCount = true; q.IncludeLinkedCollection("Vouchers"); }),
                ["select.linked.members.ofdelete"] = () => LinkedMembersOfDelete<LcCampaign, LcVoucher>("CampaignId",
                    q => q.Where.And(new Parameter("ID", new List<Guid> { Guid.Empty }, Operator.In))),
                ["select.alltypes"] = () => Select<TestAllTypesWidget>(),

                // ---- Row reads: chosen columns, ordered and paged; groups with measures
                ["rows.columns"] = () => Rows<TestWidget>(q => q.Columns = ["Quantity", "Name"]),
                ["rows.columns.top.ordered"] = () => Rows<TestWidget>(q => { q.Columns = ["Name"]; q.Take = 5; q.AddOrderBy("Quantity", descending: true); q.AddOrderBy("ID"); }),
                ["rows.columns.paged.count"] = () => Rows<TestWidget>(q => { q.Columns = ["Name"]; q.Skip = 10; q.Take = 5; q.WithTotalCount = true; q.AddOrderBy("Name"); q.Where.And("Quantity", 3); }),
                ["rows.columns.soft"] = () => Rows<TestSoftWidget>(q => { q.Columns = ["Name"]; q.Where.And("Name", "a"); }),
                ["rows.grouped"] = () => Grouped<TestWidget>(["Name"], q => q.Where.And(new Parameter("Quantity", 1, Operator.GreaterThan)),
                    (MeasureKind.Count, null, "Rows"),
                    (MeasureKind.CountDistinct, "Quantity", "Kinds"),
                    (MeasureKind.Min, "Quantity", "Least"),
                    (MeasureKind.Max, "Quantity", "Most"),
                    (MeasureKind.Sum, "Quantity", "Total")),
                ["rows.grouped.keys"] = () => Grouped<TestWidget>(["Name", "Quantity"], null, (MeasureKind.Count, null, "Rows")),
                ["rows.grouped.total"] = () => Grouped<TestWidget>([], q => q.Where.And("Name", "a"), (MeasureKind.Count, null, "Rows")),
                ["rows.grouped.soft"] = () => Grouped<TestSoftWidget>(["Name"], null, (MeasureKind.Count, null, "Rows")),

                // ---- Terms
                ["term.equals"] = () => Term(new Parameter("Name", "a")),
                ["term.notequals"] = () => Term(new Parameter("Name", "a", Operator.NotEquals)),
                ["term.greaterthan"] = () => Term(new Parameter("Quantity", 1, Operator.GreaterThan)),
                ["term.lessthan"] = () => Term(new Parameter("Quantity", 1, Operator.LessThan)),
                ["term.greaterthanorequal"] = () => Term(new Parameter("Quantity", 1, Operator.GreaterThanOrEqualTo)),
                ["term.lessthanorequal"] = () => Term(new Parameter("Quantity", 1, Operator.LessThanOrEqualTo)),
                ["term.like"] = () => Term(new Parameter("Name", "a", Operator.Like)),
                ["term.likecasesensitive"] = () => Term(new Parameter("Name", "a", Operator.LikeCaseSensitive)),
                ["term.contains"] = () => Term(new Parameter("Name", "a", Operator.Contains)),
                ["term.contains.fulltext"] = () => Where<TestAllTypesWidget>(w => w.Searchable!.Contains("needle")),
                ["term.containscasesensitive"] = () => Term(new Parameter("Name", "a", Operator.ContainsCaseSensitive)),
                ["term.startswith"] = () => Term(new Parameter("Name", "a", Operator.StartsWith)),
                ["term.endswith"] = () => Term(new Parameter("Name", "a", Operator.EndsWith)),
                ["term.equalsignorecase"] = () => Term(new Parameter("Name", "a", Operator.EqualsIgnoreCase)),
                ["term.in"] = () => Term(new Parameter("Quantity", new List<int> { 1, 2, 3 }, Operator.In)),
                ["term.in.empty"] = () => Term(new Parameter("Quantity", new List<int>(), Operator.In)),
                ["term.equals.null"] = () => Term(new Parameter("Name", null)),
                ["term.notequals.null"] = () => Term(new Parameter("Name", null, Operator.NotEquals)),
                ["term.serverfunction"] = () => Term(new Parameter("Name", ServerFunction.CurrentDateTime, Operator.LessThan)),
                ["where.or.and"] = () => Where<TestWidget>(w => (w.Name == "a" || w.Name == "b") && w.Quantity > 1),
                ["where.and.or"] = () => Where<TestWidget>(w => w.Name == "a" && (w.Quantity == 1 || w.Quantity == 2)),
                ["where.methods"] = () => Where<TestWidget>(w => w.Name!.StartsWith("a") && w.Name.EndsWith("z") && w.Name.Contains("m")),
                ["where.ignorecase"] = () => Where<TestWidget>(w => string.Equals(w.Name, "A", StringComparison.OrdinalIgnoreCase)),
                ["where.parent.id"] = () =>
                {
                    var id = Guid.NewGuid();
                    return Where<TestDeletableChild>(c => c.Parent.ID == id && c.Sequence > 1);
                },
                ["where.parent.in"] = () =>
                {
                    var parents = new List<TestDeletableParent> { Loaded<TestDeletableParent>(Guid.NewGuid()), Loaded<TestDeletableParent>(Guid.NewGuid()) };
                    return Where<TestDeletableChild>(c => parents.Contains(c.Parent));
                },
                ["where.in"] = () =>
                {
                    var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
                    return Where<TestWidget>(w => ids.Contains(w.ID));
                },
                ["where.enum"] = () => Where<NullableWidget>(w => w.Kind == WidgetKind.Premium && w.OptionalKind == null),
                ["where.contains.plain"] = () => Where<TestWidget>(w => w.Name!.Contains("m")),
                ["where.startswith.wildcard"] = () => Where<TestWidget>(w => w.Name!.StartsWith("50%_[x]")),

                // ---- Columns named apart from their properties
                ["renamed.select"] = () => Select<TestRenamedWidget>(),
                ["renamed.where"] = () => Where<TestRenamedWidget>(w => w.Title == "a" && w.Code!.StartsWith("x")),
                ["renamed.ordered"] = () => Select<TestRenamedWidget>(q => { q.AddOrderBy("Title"); q.AddOrderBy("Code", descending: true); }),
                ["renamed.children.top"] = () => Select<TestRenamedWidget>(q => q.Take = 3),
                ["renamed.insert"] = () => new InsertQuery(renderer, Table<TestRenamedWidget>(), new TestRenamedWidget { Title = "a", Code = "c", Quantity = 1 }).BuildQuery(),
                ["renamed.update"] = () =>
                {
                    var w = Loaded<TestRenamedWidget>(Guid.NewGuid());
                    w.Title = "b";
                    w.Code = "d";
                    return new UpdateQuery(renderer, Table<TestRenamedWidget>(), w).BuildQuery();
                },

                // ---- A column the server sets and the entity reads back
                ["servervalue.insert"] = () => new InsertQuery(renderer, Table<TestServerValueWidget>(), new TestServerValueWidget { Name = "a" }).BuildQuery(),
                ["servervalue.update"] = () =>
                {
                    var w = Loaded<TestServerValueWidget>(Guid.NewGuid());
                    w.Name = "b";
                    return new UpdateQuery(renderer, Table<TestServerValueWidget>(), w).BuildQuery();
                },

                // ---- Inserts and updates
                ["insert.one"] = () => new InsertQuery(renderer, Table<TestWidget>(), new TestWidget { Name = "A", Quantity = 5 }).BuildQuery(),
                ["insert.null"] = () => new InsertQuery(renderer, Table<TestWidget>(), new TestWidget { Name = null, Quantity = 1 }).BuildQuery(),
                ["insert.many"] = () => InsertQuery.Create(renderer, Table<TestWidget>(),
                    new[] { new TestWidget { Name = "A" }, new TestWidget { Name = "B" }, new TestWidget { Name = "C" } })!.BuildQuery(),
                ["insert.alltypes"] = () => new InsertQuery(renderer, Table<TestAllTypesWidget>(), new TestAllTypesWidget
                {
                    Ansi = "a", UnicodeSized = "u", UnicodeMax = "m", Count = 1, Big = 2, Ratio = 0.5, Flag = true,
                    When = new DateTime(2026, 1, 2), Day = new DateTime(2026, 1, 3), At = new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.FromHours(10)),
                    Reference = Guid.NewGuid(), Blob = [1, 2], Money = 1.25m, Kind = WidgetKind.Standard, Linked = new TestWidget(), Searchable = "s",
                }).BuildQuery(),
                ["insert.tablename"] = () =>
                {
                    var q = new InsertQuery(renderer, "[dbo].[tbl_TestWidget]");
                    q.SetParameters.Add(new Parameter("Name", "Widget A"));
                    q.SetParameters.Add(new Parameter("Quantity", 5));
                    return q.BuildQuery();
                },
                ["update.one"] = () =>
                {
                    var w = Loaded<TestWidget>(Guid.NewGuid());
                    w.Name = "B";
                    return new UpdateQuery(renderer, Table<TestWidget>(), w).BuildQuery();
                },
                ["update.null"] = () =>
                {
                    var w = Loaded<TestWidget>(Guid.NewGuid());
                    w.Name = null;
                    return new UpdateQuery(renderer, Table<TestWidget>(), w).BuildQuery();
                },
                ["update.many"] = () =>
                {
                    var widgets = Enumerable.Range(0, 3).Select(i =>
                    {
                        var w = Loaded<TestWidget>(Guid.NewGuid());
                        w.Quantity = i;
                        return w;
                    }).ToList();
                    return UpdateQuery.Create(renderer, Table<TestWidget>(), widgets)!.BuildQuery();
                },
                ["update.tablename"] = () =>
                {
                    var q = new UpdateQuery(renderer, "[dbo].[tbl_TestWidget]", "ID", Guid.NewGuid());
                    q.SetParameters.Add(new Parameter("Name", "Widget B"));
                    return q.BuildQuery();
                },

                // ---- Deletes
                ["delete.hard"] = () => DeleteWhere<TestDeletableWidget>(w => w.Name == "old", Tables(typeof(TestDeletableWidget))),
                ["delete.soft"] = () => DeleteWhere<TestSoftWidget>(w => w.Name == "old", Tables(typeof(TestSoftWidget))),
                ["delete.cascade"] = () => DeleteWhere<TestDeletableParent>(p => p.Name == "old" || p.Name == "older",
                    Tables(typeof(TestDeletableParent), typeof(TestDeletableChild))),
                ["delete.soft.cascade"] = () => DeleteWhere<TestSoftParent>(p => p.Name == "old",
                    Tables(typeof(TestSoftParent), typeof(TestSoftChild))),
                ["delete.where.parent"] = () =>
                {
                    var id = Guid.NewGuid();
                    return DeleteWhere<TestDeletableChild>(c => c.Parent.ID == id, Tables(typeof(TestDeletableChild)));
                },
                ["delete.ids"] = () =>
                {
                    var tables = Tables(typeof(TestDeletableParent), typeof(TestDeletableChild));
                    return DeleteWhereQuery.ForIds(renderer, tables(typeof(TestDeletableParent))!, tables, [Guid.NewGuid(), Guid.NewGuid()])!.BuildQuery();
                },
                ["delete.ids.secondchunk"] = () =>
                {
                    var tables = Tables(typeof(TestSoftWidget));
                    var ids = Enumerable.Range(0, DeleteWhereQuery.IdsPerStatement + 1).Select(_ => Guid.NewGuid());
                    return DeleteWhereQuery.ForIds(renderer, tables(typeof(TestSoftWidget))!, tables, ids)!.LinkedQueries.Single().BuildQuery();
                },

            };
            return cases;
        }
    }
}
