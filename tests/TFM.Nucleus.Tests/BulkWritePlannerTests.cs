using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Tests
{
    /// <summary>The bulk planner's grouping of a linked write chain into per-table writes.</summary>
    public class BulkWritePlannerTests
    {
        private static readonly TableSchema Widgets = QueryGoldenCorpus.TableOf(typeof(TestWidget));

        private static TestWidget Loaded(string name)
        {
            var widget = ObjectSchema<TestWidget>.BuildObjectViaReflection(new Dictionary<string, object?> { ["ID"] = Guid.NewGuid() });
            widget.Name = name;
            return widget;
        }

        [Fact]
        public void AChainOfInsertsAndUpdatesOfOneTable_GivesOneWriteOfEach()
        {
            var inserts = new[] { new TestWidget { Name = "new 1" }, new TestWidget { Name = "new 2" } };
            var updates = new[] { Loaded("old 1"), Loaded("old 2"), Loaded("old 3") };
            Query root = new InsertQuery(SqlServerQueryRenderer.Instance, Widgets, inserts[0]);
            root.LinkedQueries.Add(new UpdateQuery(SqlServerQueryRenderer.Instance, Widgets, updates[0]));
            root.LinkedQueries.Add(new InsertQuery(SqlServerQueryRenderer.Instance, Widgets, inserts[1]));
            root.LinkedQueries.Add(new UpdateQuery(SqlServerQueryRenderer.Instance, Widgets, updates[1]));
            root.LinkedQueries.Add(new UpdateQuery(SqlServerQueryRenderer.Instance, Widgets, updates[2]));

            var writes = BulkWritePlanner.GroupByTable(root);

            Assert.Collection(writes,
                insert =>
                {
                    Assert.False(insert.IsUpdate);
                    Assert.Equal(inserts.Length, insert.Queries.Count);
                    Assert.All(insert.Queries, q => Assert.Equal(QueryKind.Insert, q.Type));
                },
                update =>
                {
                    Assert.True(update.IsUpdate);
                    Assert.Equal(updates.Length, update.Queries.Count);
                    Assert.All(update.Queries, q => Assert.Equal(QueryKind.Update, q.Type));
                });
        }
    }
}
