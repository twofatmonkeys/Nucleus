using System.Linq.Expressions;
using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// OrderBy, ThenBy, Skip and Take on a load: how the parser takes them apart from the filter,
    /// what it refuses, and what the manager builds from them. The SQL each engine writes is
    /// pinned by the goldens; the engines are covered by the integration suite.
    /// </summary>
    public class OrderAndPageTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static IQueryable<TestWidget> Source => Array.Empty<TestWidget>().AsQueryable();

        private static QueryShape Split(IQueryable<TestWidget> query) => ExpressionParser.Split(query.Expression);

        // ---- The parser

        [Fact]
        public void AFilter_IsCarriedAsItCame_WithNoOperators()
        {
            var query = Source.Where(w => w.Name == "a").Where(w => w.Quantity > 1);

            var shape = Split(query);

            Assert.False(shape.HasOperators);
            Assert.Same(query.Expression, shape.Predicate);
        }

        [Fact]
        public void TheWheres_AreAndedIntoTheFilter_AroundAnOrder()
        {
            var shape = Split(Source.Where(w => w.Name == "a").OrderBy(w => w.Quantity).Where(w => w.Quantity > 1));

            var and = Assert.IsAssignableFrom<BinaryExpression>(shape.Predicate);
            Assert.Equal(ExpressionType.AndAlso, and.NodeType);
            Assert.Equal([("Quantity", false)], shape.Order!);
        }

        [Fact]
        public void ThenBy_AddsAKey_AfterTheFirst()
        {
            var shape = Split(Source.OrderByDescending(w => w.Quantity).ThenBy(w => w.Name).ThenByDescending(w => w.ID));

            Assert.Equal([("Quantity", true), ("Name", false), ("ID", true)], shape.Order!);
        }

        [Fact]
        public void ALaterOrderBy_SortsFirst_AndTheEarlierKeysStillBreakItsTies()
        {
            // As LINQ's stable sort leaves them: no key is dropped.
            var shape = Split(Source.OrderBy(w => w.Name).OrderByDescending(w => w.Quantity));

            Assert.Equal([("Quantity", true), ("Name", false)], shape.Order!);
        }

        [Fact]
        public void ARepeatedKey_AddsNothingAfterItsFirstUse()
        {
            Assert.Equal([("Name", true)], Split(Source.OrderBy(w => w.Name).OrderByDescending(w => w.Name)).Order!);
            Assert.Equal([("Name", false)], Split(Source.OrderBy(w => w.Name).ThenByDescending(w => w.Name)).Order!);
        }

        [Fact]
        public void AKeyNeedingAConversion_StillNamesItsProperty()
        {
            Expression<Func<TestWidget, object>> boxed = w => w.Quantity;

            Assert.Equal([("Quantity", false)], Split(Source.OrderBy(boxed)).Order!);
        }

        [Theory]
        [InlineData(3, 10, 3, 10)]
        [InlineData(0, 10, 0, 10)]
        [InlineData(-4, 10, 0, 10)]
        public void SkipThenTake_SkipsThenLimits(int skip, int take, int expectedSkip, int expectedTake)
        {
            var shape = Split(Source.Skip(skip).Take(take));

            Assert.Equal(expectedSkip, shape.Skip);
            Assert.Equal(expectedTake, shape.Take);
        }

        [Fact]
        public void SkipAndTake_ComposeAsLinqDoes()
        {
            Assert.Equal((3, (int?)7), Cut(Source.Take(10).Skip(3)));
            Assert.Equal((5, (int?)null), Cut(Source.Skip(2).Skip(3)));
            Assert.Equal((0, (int?)3), Cut(Source.Take(5).Take(3)));
            Assert.Equal((0, (int?)3), Cut(Source.Take(3).Take(5)));
            Assert.Equal((5, (int?)8), Cut(Source.Skip(3).Take(10).Skip(2)));

            static (int, int?) Cut(IQueryable<TestWidget> query)
            {
                var shape = Split(query);
                return (shape.Skip, shape.Take);
            }
        }

        [Fact]
        public void ACutToNoRows_TakesNothing()
        {
            Assert.True(Split(Source.Take(0)).TakesNothing);
            Assert.True(Split(Source.Take(-1)).TakesNothing);
            Assert.True(Split(Source.Take(3).Skip(5)).TakesNothing);
            Assert.False(Split(Source.Take(1)).TakesNothing);
        }

        [Fact]
        public void ACountInAVariable_IsReadNow()
        {
            var pageSize = 4;

            Assert.Equal(4, Split(Source.Take(pageSize)).Take);
        }

        [Fact]
        public void AWhereAfterACut_IsRefused()
        {
            // It would filter the page, not the rows.
            Assert.Throws<NotSupportedException>(() => Split(Source.Take(5).Where(w => w.Quantity > 1)));
            Assert.Throws<NotSupportedException>(() => Split(Source.Skip(5).Where(w => w.Quantity > 1)));
        }

        [Fact]
        public void AnOrderAfterACut_IsRefused()
        {
            Assert.Throws<NotSupportedException>(() => Split(Source.Take(5).OrderBy(w => w.Name)));
            Assert.Throws<NotSupportedException>(() => Split(Source.Skip(5).OrderByDescending(w => w.Quantity)));
        }

        [Fact]
        public void AnyOtherOperator_IsRefused()
        {
            Assert.Throws<NotSupportedException>(() => Split(Source.Select(w => w).Where(w => w.Quantity > 1)));
            Assert.Throws<NotSupportedException>(() => Split(Source.Distinct()));
            Assert.Throws<NotSupportedException>(() => Split(Source.Reverse()));
            Assert.Throws<NotSupportedException>(() => Split(Source.Where((w, i) => i > 1)));
        }

        [Fact]
        public void AKeyThatIsNotAProperty_IsRefused()
        {
            Assert.Throws<NotSupportedException>(() => Split(Source.OrderBy(w => w.Name!.Length)));
            Assert.Throws<NotSupportedException>(() => Split(Source.OrderBy(w => w.Quantity + 1)));
        }

        [Fact]
        public void ADelete_StillRefusesAnOrderOrACut()
        {
            // A delete parses its expression whole: a Take it ignored would delete every match.
            Assert.Throws<NotSupportedException>(() => ExpressionParser.CheckDeletePredicate(Source.Where(w => w.Quantity > 1).Take(5).Expression));
            Assert.Throws<NotSupportedException>(() => ExpressionParser.CheckDeletePredicate(Source.OrderBy(w => w.Name).Where(w => w.Quantity > 1).Expression));
        }

        // ---- The evaluator

        [Fact]
        public void TheEvaluator_LeavesAQueryOperatorForTheParser()
        {
            // source.Take(5) has no parameter, so it was run and replaced by the queryable it
            // returned, and the Take vanished: a load read every row.
            var evaluated = Evaluator.PartialEval(Source.Take(5).Expression);

            var call = Assert.IsAssignableFrom<MethodCallExpression>(evaluated);
            Assert.Equal("Take", call.Method.Name);
        }

        [Fact]
        public void TheEvaluator_StillFoldsAQueryableBuiltFromAValue()
        {
            int[] ids = [1, 2];
            Expression<Func<IQueryable<int>>> built = () => ids.AsQueryable();

            Assert.IsAssignableFrom<ConstantExpression>(Evaluator.PartialEval(built.Body));
        }

        // ---- The shape

        [Theory]
        [InlineData(10, 0, null, 10)]
        [InlineData(10, 3, null, 7)]
        [InlineData(10, 3, 5, 5)]
        [InlineData(10, 8, 5, 2)]
        [InlineData(10, 12, 5, 0)]
        public void CountWithin_IsWhatTheSkipLeaves_CappedByTheLimit(long matching, int skip, int? take, long expected)
        {
            var shape = QueryShape.WithOperators(Expression.Constant(true), null, skip, take);

            Assert.Equal(expected, shape.CountWithin(matching));
        }

        [Fact]
        public void ApplyTo_RefusesAPropertyThatIsNotAStoredColumn()
        {
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, QueryGoldenCorpus.TableOf(typeof(TestWidget)));
            var shape = QueryShape.WithOperators(Expression.Constant(true), [("NotAColumn", false)], 0, 5);

            var ex = Assert.Throws<NotSupportedException>(() => shape.ApplyTo(query));
            Assert.Contains("NotAColumn is not a stored column", ex.Message);
        }

        // ---- The manager

        private static (IObjectDataAdaptor<TestWidget> Widgets, FakeDataProvider Provider) Widgets()
        {
            var provider = new FakeDataProvider();
            provider.SetupRows<TestWidget>(new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "a", ["Quantity"] = 1 });
            return (new ObjectDataManager(provider, new ObjectSchemaProvider(), null).Get<TestWidget>(), provider);
        }

        private static FakeQuery OnlySelect(FakeDataProvider provider) =>
            Assert.Single(provider.CreatedQueries.OfType<FakeQuery>(), q => q.Kind == FakeQueryKind.Select);

        [Fact]
        public async Task AnOrderedPage_ReachesTheQuery()
        {
            var (widgets, provider) = Widgets();

            await widgets.Where(w => w.Quantity > 0).OrderByDescending(w => w.Quantity).ThenBy(w => w.Name).Skip(2).Take(3).ToListAsync(Ct);

            var select = OnlySelect(provider);
            Assert.Equal([("Quantity", true), ("Name", false)], select.Orderings);
            Assert.Equal(2, select.Skip);
            Assert.Equal(3, select.Take);
            Assert.Single(select.WhereParameters);
        }

        [Fact]
        public void TakeStraightOnTheQuery_IsKept()
        {
            // Get<T>().Take(5) used to read every row.
            var (widgets, provider) = Widgets();

            _ = widgets.Take(5).ToList();

            Assert.Equal(5, OnlySelect(provider).Take);
        }

        [Fact]
        public async Task ACutToNoRows_ReadsNothing()
        {
            var (widgets, provider) = Widgets();

            Assert.Empty(await widgets.Take(0).ToListAsync(Ct));
            Assert.Empty(widgets.Take(0).ToList());
            Assert.Null(await widgets.Take(0).FirstOrDefaultAsync(Ct));

            Assert.DoesNotContain(provider.CreatedQueries.OfType<FakeQuery>(), q => q.Kind == FakeQueryKind.Select);
        }

        [Fact]
        public async Task First_ReadsTheFirstRowOfTheQueryAsOrderedAndSkipped()
        {
            var (widgets, provider) = Widgets();

            await widgets.OrderBy(w => w.Name).Skip(4).FirstOrDefaultAsync(Ct);

            var select = OnlySelect(provider);
            Assert.Equal([("Name", false)], select.Orderings);
            Assert.Equal(4, select.Skip);
            Assert.Equal(1, select.Take);
        }

        [Fact]
        public async Task Single_ReadsNoMoreThanTheQueryTakes()
        {
            var (widgets, provider) = Widgets();

            await widgets.OrderBy(w => w.Name).Take(1).SingleOrDefaultAsync(Ct);

            Assert.Equal(1, OnlySelect(provider).Take);
        }

        [Fact]
        public async Task APagedResult_KeepsTheOrder()
        {
            var (widgets, provider) = Widgets();

            await widgets.OrderByDescending(w => w.Quantity).ToPagedResultAsync(10, 5, Ct);

            var select = OnlySelect(provider);
            Assert.Equal([("Quantity", true)], select.Orderings);
            Assert.Equal(10, select.Skip);
            Assert.Equal(5, select.Take);
            Assert.True(select.WithTotalCount);
        }

        [Fact]
        public async Task APagedResult_OfACutQuery_IsRefused()
        {
            var (widgets, _) = Widgets();

            await Assert.ThrowsAsync<InvalidOperationException>(() => widgets.Take(5).ToPagedResultAsync(0, 5, Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => widgets.OrderBy(w => w.Name).Skip(5).ToPagedResultAsync(0, 5, Ct));
        }

        [Fact]
        public async Task AWhereAfterATake_IsRefusedOnTheLoad()
        {
            var (widgets, _) = Widgets();

            await Assert.ThrowsAsync<NotSupportedException>(() => widgets.Take(5).Where(w => w.Quantity > 0).ToListAsync(Ct));
        }

        [Fact]
        public async Task CountAndAny_OfACutToNoRows_AreZeroAndFalse_WithoutARead()
        {
            var (widgets, provider) = Widgets();

            Assert.Equal(0, await widgets.Take(0).CountAsync(Ct));
            Assert.False(await widgets.Take(0).AnyAsync(Ct));

            Assert.Empty(provider.CreatedQueries);
        }

        [Fact]
        public async Task AnOrderedQuery_OfACachedType_IsReadFromTheDatabase()
        {
            // The cache holds every row in no order, so an order or a cut cannot be answered from it.
            var provider = new FakeDataProvider();
            provider.SetupRows<CachedWidget>(new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "a" });
            var cached = new ObjectDataManager(new DataProviderRegistry([provider]), new ObjectSchemaProvider()).Get<CachedWidget>();

            await cached.OrderBy(w => w.Name).Take(1).ToListAsync(Ct);

            var select = Assert.Single(provider.CreatedQueries.OfType<FakeQuery>(), q => q.Kind == FakeQueryKind.Select);
            Assert.Equal(1, select.Take);
            Assert.Equal([("Name", false)], select.Orderings);
        }
    }
}
