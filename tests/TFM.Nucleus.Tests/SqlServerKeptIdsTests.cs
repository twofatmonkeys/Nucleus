using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// SQL Server reads and deletes a graph one table per statement. A linked query that reached
    /// its parent's table through its own (child-table scan, parent probe) held a child row of
    /// some other parent while it waited on that parent's row: the opposite order to a save of
    /// the parent, and the two deadlocked, at every level of a graph. Each level's keys are kept
    /// in a table variable read from that level's table alone, and the next level selects by them.
    /// </summary>
    public class SqlServerKeptIdsTests
    {
        private const string Owner = "tbl_TFM_Nucleus_Tests_CmSoftOwner";
        private const string Line = "tbl_TFM_Nucleus_Tests_CmSoftLine";
        private const string Leaf = "tbl_TFM_Nucleus_Tests_CmSoftLeaf";

        private static Func<Type, IDataTableSchema?> Tables()
        {
            var tables = new[] { typeof(CmSoftOwner), typeof(CmSoftLine), typeof(CmSoftLeaf) }
                .ToDictionary(t => t, t => (IDataTableSchema)new TableSchema(ObjectSchema.Get(t)));
            return t => tables.GetValueOrDefault(t);
        }

        private static SelectQuery OwnerSelect(Expression<Func<CmSoftOwner, bool>> predicate)
        {
            var tables = Tables();
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, tables(typeof(CmSoftOwner))!, tables);
            ExpressionParser.ParseExpressionToQuery(query, predicate.Body);
            return query;
        }

        private static string[] Statements(string sql) =>
            [.. SqlSnapshot.Normalize(sql).Split(';').Select(s => s.Trim()).Where(s => s.Length > 0)];

        private static void AssertEveryStatementNamesOneTable(string sql)
        {
            foreach (var statement in Statements(sql))
                Assert.True(Regex.Matches(statement, @"tbl_\w+").Select(m => m.Value).Distinct().Count() <= 1,
                    $"Reads more than one table: {statement}");
        }

        [Fact]
        public void ABatchWithGrandchildren_ReadsOneTablePerStatement()
        {
            var sql = OwnerSelect(o => o.Name == "a").BuildQuery();

            AssertEveryStatementNamesOneTable(sql);
            var statements = Statements(sql);
            Assert.Contains($"INSERT INTO @__parents ([ID]) SELECT [ID] FROM {Owner} WHERE [Name]=@p0 AND [IsDeleted] = 0", statements);
            Assert.Contains($"INSERT INTO @__parents1 ([ID]) SELECT [ID] FROM {Line} WHERE [Parent] IN (SELECT [ID] FROM @__parents) AND [IsDeleted] = 0", statements);
            Assert.EndsWith($"FROM {Leaf} WHERE [Parent] IN (SELECT [ID] FROM @__parents1) AND [IsDeleted] = 0", statements[^1]);
        }

        [Fact]
        public void AGrandchildQueryWrittenOnItsOwn_KeepsEachLevelAboveIt()
        {
            // A streamed load sends each linked query as a command of its own.
            var owner = OwnerSelect(o => o.Name == "a");
            var leaves = owner.LinkedQueries.OfType<SelectQuery>().Single().LinkedQueries.OfType<SelectQuery>().Single();

            var sql = leaves.BuildQuery(includeChildQueries: false);

            AssertEveryStatementNamesOneTable(sql);
            var statements = Statements(sql);
            Assert.Equal(5, statements.Length);
            Assert.StartsWith($"SELECT [Sequence] ,[Parent] ,[ID] ,[Timestamp] FROM {Leaf} WHERE [Parent] IN (SELECT [ID] FROM @__parents1)", statements[^1]);
        }

        [Fact]
        public void ADeleteOfThreeLevels_KeepsEachLevelsIds_AndDeletesEachByItsParents()
        {
            var tables = Tables();
            var query = new DeleteWhereQuery(SqlServerQueryRenderer.Instance, tables(typeof(CmSoftOwner))!, t => (TableSchema?)tables(t));
            ExpressionParser.ParseExpressionToQuery(query, ((Expression<Func<CmSoftOwner, bool>>)(o => o.Name == "a")).Body);
            var sql = query.BuildQuery();

            AssertEveryStatementNamesOneTable(sql);
            var statements = Statements(sql);
            Assert.Equal(
            [
                "DECLARE @__targets TABLE ([ID] uniqueidentifier PRIMARY KEY)",
                $"INSERT INTO @__targets ([ID]) SELECT [ID] FROM {Owner} WHERE ([Name]=@p0) AND [IsDeleted] = 0",
                "DECLARE @__targets1 TABLE ([ID] uniqueidentifier PRIMARY KEY)",
                $"INSERT INTO @__targets1 ([ID]) SELECT [ID] FROM {Line} WHERE [Parent] IN (SELECT [ID] FROM @__targets) AND [IsDeleted] = 0",
            ], statements[..4]);
            Assert.EndsWith("WHERE [Parent] IN (SELECT [ID] FROM @__targets1) AND [IsDeleted] = 0", statements[4]);
            Assert.StartsWith($"UPDATE {Leaf}", statements[4]);
            Assert.StartsWith($"UPDATE {Line}", statements[5]);
            Assert.EndsWith("WHERE [Parent] IN (SELECT [ID] FROM @__targets) AND [IsDeleted] = 0", statements[5]);
            Assert.StartsWith($"UPDATE {Owner}", statements[6]);
            Assert.EndsWith("WHERE [ID] IN (SELECT [ID] FROM @__targets) AND [IsDeleted] = 0", statements[6]);
            Assert.Equal("SELECT @@ROWCOUNT", statements[7]);
        }
    }
}
