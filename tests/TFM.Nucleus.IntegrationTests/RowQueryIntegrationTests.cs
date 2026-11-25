namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// End-to-end cover for row reads (<see cref="RowQuery{T}"/>): chosen columns, an order and a
    /// page with its total, groups with every measure, and the values read back as their types on
    /// each engine. The unit tests pin the SQL; these prove the engine runs it and the reader
    /// labels and converts what comes back. Every read is narrowed to a batch unique to the test,
    /// so the shared database needs no cleanup.
    /// </summary>
    public abstract class RowQueryIntegrationTests(DatabaseFixture fixture)
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Six rows: "a" three times (two sessions, two Standard and one Premium, one with an
        /// optional kind), "b" twice (one session), and one "c" that is soft deleted.
        /// </summary>
        private static async Task<(string Batch, Guid S1, Guid S2)> SeedAsync(ObjectDataManager manager)
        {
            var batch = $"rq-{Guid.NewGuid():N}";
            var s1 = Guid.NewGuid();
            var s2 = Guid.NewGuid();
            IntRowItem Row(string name, IntWidgetKind kind, Guid session, int minutes, int amount, IntWidgetKind? optional = null) => new()
            {
                Batch = batch,
                Name = name,
                Kind = kind,
                OptionalKind = optional,
                Session = session,
                At = Start.AddMinutes(minutes),
                Amount = amount,
                Notes = new string('x', 5000),
            };
            await manager.SaveAsync(Row("a", IntWidgetKind.Standard, s1, 1, 10, IntWidgetKind.Premium));
            await manager.SaveAsync(Row("a", IntWidgetKind.Standard, s1, 5, 20));
            await manager.SaveAsync(Row("a", IntWidgetKind.Premium, s2, 3, 30));
            await manager.SaveAsync(Row("b", IntWidgetKind.Standard, s2, 2, 1));
            await manager.SaveAsync(Row("b", IntWidgetKind.Standard, s2, 4, 2));
            var gone = Row("c", IntWidgetKind.Standard, s1, 6, 100);
            await manager.SaveAsync(gone);
            await manager.DeleteAsync(gone);
            return (batch, s1, s2);
        }

        [Fact]
        public async Task Select_ReturnsOnlyTheColumnsAskedFor_InTheirTypes()
        {
            var manager = fixture.RequireManager();
            var (batch, s1, _) = await SeedAsync(manager);

            var rows = await manager.Rows<IntRowItem>(r => r.Batch == batch && r.Name == "a" && r.At == Start.AddMinutes(1))
                .Select(r => r.Name, r => r.Kind, r => r.OptionalKind, r => r.Session, r => r.At, r => r.Amount)
                .ToListAsync();

            var row = Assert.Single(rows);
            Assert.Equal(["Name", "Kind", "OptionalKind", "Session", "At", "Amount"], row.Names);
            Assert.Equal("a", row.Get<string>("Name"));
            Assert.Equal(IntWidgetKind.Standard, row.Get<IntWidgetKind>("Kind"));
            Assert.Equal(IntWidgetKind.Premium, row.Get<IntWidgetKind?>("OptionalKind"));
            Assert.Equal(s1, row.Get<Guid>("Session"));
            Assert.Equal(Start.AddMinutes(1), row.Get<DateTimeOffset>("At"));
            Assert.Equal(10, row.Get<int>("Amount"));
            Assert.Throws<ArgumentException>(() => row["Notes"]);
        }

        [Fact]
        public async Task NullValue_ReadsAsNullOrTheDefault()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            var row = Assert.Single(await manager.Rows<IntRowItem>(r => r.Batch == batch && r.Name == "b" && r.Amount == 1)
                .Select(r => r.OptionalKind)
                .ToListAsync());

            Assert.Null(row["OptionalKind"]);
            Assert.Null(row.Get<IntWidgetKind?>("OptionalKind"));
            Assert.Equal(IntWidgetKind.Unknown, row.Get<IntWidgetKind>("OptionalKind"));
        }

        [Fact]
        public async Task OrderedPage_ReturnsThePageAndTheTotal_LeavingOutDeletedRows()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            var first = await manager.Rows<IntRowItem>(r => r.Batch == batch)
                .Select(r => r.Amount)
                .OrderByDescending(r => r.At)
                .ToPageAsync(0, 2);
            var second = await manager.Rows<IntRowItem>(r => r.Batch == batch)
                .Select(r => r.Amount)
                .OrderByDescending(r => r.At)
                .ToPageAsync(2, 2);

            Assert.Equal(5, first.TotalCount);
            Assert.Equal([20, 2], first.Rows.Select(r => r.Get<int>("Amount")));
            Assert.Equal(5, second.TotalCount);
            Assert.Equal([30, 1], second.Rows.Select(r => r.Get<int>("Amount")));
        }

        [Fact]
        public async Task GroupBy_WorksOutEveryMeasurePerGroup()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            var groups = (await manager.Rows<IntRowItem>(r => r.Batch == batch)
                    .GroupBy(r => r.Name)
                    .Count("Rows")
                    .CountDistinct(r => r.Session, "Sessions")
                    .Min(r => r.At, "First")
                    .Max(r => r.At, "Last")
                    .Sum(r => r.Amount, "Total")
                    .ToListAsync())
                .ToDictionary(g => g.Get<string>("Name")!);

            Assert.Equal(["a", "b"], groups.Keys.Order());
            var a = groups["a"];
            Assert.Equal(3, a.Get<long>("Rows"));
            Assert.Equal(2, a.Get<int>("Sessions"));
            Assert.Equal(Start.AddMinutes(1), a.Get<DateTimeOffset>("First"));
            Assert.Equal(Start.AddMinutes(5), a.Get<DateTimeOffset>("Last"));
            Assert.Equal(60, a.Get<long>("Total"));
            var b = groups["b"];
            Assert.Equal(2, b.Get<long>("Rows"));
            Assert.Equal(1, b.Get<long>("Sessions"));
            Assert.Equal(3, b.Get<int>("Total"));
        }

        /// <summary>
        /// A grouped read could not be ordered or limited, so "the top groups" meant reading every
        /// group and sorting in memory. The database orders by a measure or a key and cuts.
        /// </summary>
        [Fact]
        public async Task GroupBy_OrderedByAMeasureOrAKey_ReturnsTheGroupsInThatOrder()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);
            RowQuery<IntRowItem> Totals() => manager.Rows<IntRowItem>(r => r.Batch == batch).GroupBy(r => r.Name).Sum(r => r.Amount, "Total");

            var smallestFirst = await Totals().OrderBy("Total").ToListAsync();
            var largestFirst = await Totals().OrderByDescending("total").ToListAsync();
            var byKey = await Totals().OrderByDescending(r => r.Name).ToListAsync();
            var top = await Totals().OrderByDescending("Total").Take(1).ToListAsync();

            Assert.Equal(["b", "a"], smallestFirst.Select(g => g.Get<string>("Name")));
            Assert.Equal(["a", "b"], largestFirst.Select(g => g.Get<string>("Name")));
            Assert.Equal(["b", "a"], byKey.Select(g => g.Get<string>("Name")));
            Assert.Equal(60, Assert.Single(top).Get<long>("Total"));
        }

        /// <summary>Groups that tie on the order are cut by their keys, so the same groups come back every time.</summary>
        [Fact]
        public async Task GroupBy_Limited_BreaksTiesByTheGroupKeys()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            // Five amounts, one row each: every group ties on its count.
            var first = await manager.Rows<IntRowItem>(r => r.Batch == batch)
                .GroupBy(r => r.Amount)
                .Count("Rows")
                .OrderByDescending("Rows")
                .Take(2)
                .ToListAsync();
            var named = await manager.Rows<IntRowItem>(r => r.Batch == batch)
                .GroupBy(r => r.Amount)
                .Count("Rows")
                .OrderByDescending("Rows")
                .ThenByDescending(r => r.Amount)
                .Take(2)
                .ToListAsync();

            Assert.Equal([1, 2], first.Select(g => g.Get<int>("Amount")));
            Assert.Equal([30, 20], named.Select(g => g.Get<int>("Amount")));
        }

        [Fact]
        public async Task Take_OnARowRead_ReadsTheFirstRowsOfItsOrder_OrAsWritten()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            var smallest = await manager.Rows<IntRowItem>(r => r.Batch == batch).Select(r => r.Amount).OrderBy(r => r.Amount).Take(2).ToListAsync();
            var asWritten = await manager.Rows<IntRowItem>(r => r.Batch == batch).Select(r => r.Amount).Take(2).ToListAsync();

            Assert.Equal([1, 2], smallest.Select(r => r.Get<int>("Amount")));
            Assert.Equal([10, 20], asWritten.Select(r => r.Get<int>("Amount")));
        }

        [Fact]
        public async Task AnOrderOrALimitThatMeansNothing_IsRefused()
        {
            var manager = fixture.RequireManager();
            RowQuery<IntRowItem> Counts() => manager.Rows<IntRowItem>().GroupBy(r => r.Name).Count("Rows");

            // Which groups are first is not said.
            await Assert.ThrowsAsync<InvalidOperationException>(() => Counts().Take(3).ToListAsync());
            // Not a key of the read, and not a measure it has.
            await Assert.ThrowsAsync<InvalidOperationException>(() => Counts().OrderBy(r => r.Amount).ToListAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => Counts().OrderBy("Total").ToListAsync());
            // A read with no measures has none to order by.
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Rows<IntRowItem>().Select(r => r.Name).OrderBy("Rows").ToListAsync());
            // A page cuts for itself.
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Rows<IntRowItem>().Select(r => r.Name).Take(3).ToPageAsync(0, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => manager.Rows<IntRowItem>().Take(0));
        }

        [Fact]
        public async Task GroupBy_SeveralKeys_KeepsAnEnumAndANullKeyApart()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            var groups = await manager.Rows<IntRowItem>(r => r.Batch == batch && r.Name == "a")
                .GroupBy(r => r.Kind, r => r.OptionalKind)
                .Count("Rows")
                .ToListAsync();

            Assert.Equal(3, groups.Count);
            Assert.Equal(1, groups.Single(g => g.Get<IntWidgetKind>("Kind") == IntWidgetKind.Premium).Get<long>("Rows"));
            Assert.Equal(1, groups.Single(g => g.Get<IntWidgetKind>("Kind") == IntWidgetKind.Standard && g.Get<IntWidgetKind?>("OptionalKind") == null).Get<long>("Rows"));
            Assert.Equal(1, groups.Single(g => g.Get<IntWidgetKind?>("OptionalKind") == IntWidgetKind.Premium).Get<long>("Rows"));
        }

        [Fact]
        public async Task MeasuresWithoutKeys_AreOneRowOverEveryMatch()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            var row = Assert.Single(await manager.Rows<IntRowItem>(r => r.Batch == batch)
                .Count("Rows")
                .Max(r => r.Amount, "Most")
                .ToListAsync());

            Assert.Equal(5, row.Get<long>("Rows"));
            Assert.Equal(30, row.Get<int>("Most"));
        }

        [Fact]
        public async Task CountAsync_CountsInTheDatabase()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            Assert.Equal(3, await manager.Rows<IntRowItem>(r => r.Batch == batch && r.Name == "a").CountAsync());
            Assert.Equal(0, await manager.Rows<IntRowItem>(r => r.Batch == batch && r.Name == "none").CountAsync());
        }

        [Fact]
        public async Task StreamAsync_YieldsRowsThatCanBeKept()
        {
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            var kept = new List<ResultRow>();
            await foreach (var row in manager.Rows<IntRowItem>(r => r.Batch == batch).Select(r => r.Amount).OrderBy(r => r.Amount).StreamAsync())
                kept.Add(row);

            Assert.Equal([1, 2, 10, 20, 30], kept.Select(r => r.Get<int>("Amount")));
        }

        [Fact]
        public async Task PredicateMatchingNothing_ReturnsNoRowsWithoutARoundTrip()
        {
            var manager = fixture.RequireManager();

            Assert.Empty(await manager.Rows<IntRowItem>(r => false).Select(r => r.Name).ToListAsync());
            Assert.Equal(0, (await manager.Rows<IntRowItem>(r => false).Select(r => r.Name).OrderBy(r => r.Name).ToPageAsync(0, 10)).TotalCount);
        }

        [Fact]
        public async Task AnUnorderedPage_FollowsTheOrderTheRowsWereWritten()
        {
            // A page with no order of its own is ordered by insertion, so pages never overlap.
            var manager = fixture.RequireManager();
            var (batch, _, _) = await SeedAsync(manager);

            var page = await manager.Rows<IntRowItem>(r => r.Batch == batch).Select(r => r.At).ToPageAsync(1, 2);

            Assert.Equal([Start.AddMinutes(5), Start.AddMinutes(3)], page.Rows.Select(r => r.Get<DateTimeOffset>("At")));
            Assert.Equal(5, page.TotalCount);
        }

        [Fact]
        public async Task BadShapes_AreRefusedBeforeAnySql()
        {
            var manager = fixture.RequireManager();

            Assert.Throws<ArgumentException>(() => manager.Rows<IntRowItem>().Count("not a name"));
            Assert.Throws<ArgumentException>(() => manager.Rows<IntRowItem>().Select(r => r.Name!.Length));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Rows<IntRowItem>().ToListAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Rows<IntRowItem>().GroupBy(r => r.Name).Count("Rows").ToPageAsync(0, 5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Rows<IntRowItem>().GroupBy(r => r.Name).Count("Rows").Select(r => r.Kind).ToListAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Rows<IntRowItem>().GroupBy(r => r.Name).Count("Name").ToListAsync());
        }
    }
}
