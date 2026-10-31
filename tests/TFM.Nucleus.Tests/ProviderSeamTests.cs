using Microsoft.Extensions.Logging.Abstractions;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Runs the neutral stack (ObjectDataManager + ObjectDataAdaptor LINQ) end-to-end against the
    /// SQL-free FakeDataProvider. If any neutral code needs SQL, these tests fail structurally.
    /// </summary>
    public class ProviderSeamTests
    {
        private static (ObjectDataManager Manager, FakeDataProvider Provider) NewManager()
        {
            var provider = new FakeDataProvider();
            var manager = new ObjectDataManager(provider, new ObjectSchemaProvider(), null);
            return (manager, provider);
        }

        private static Dictionary<string, object?> WidgetRow(Guid id, string name, int quantity) => new()
        {
            ["ID"] = id,
            ["Name"] = name,
            ["Quantity"] = quantity,
        };

        [Fact]
        public void Load_WithPredicate_MaterializesThroughFakeProvider()
        {
            var (manager, provider) = NewManager();
            var id = Guid.NewGuid();
            provider.SetupRows<TestWidget>(WidgetRow(id, "Alpha", 1), WidgetRow(Guid.NewGuid(), "Beta", 2));

            var adaptor = manager.Get<TestWidget>();
            var results = adaptor.Where(w => w.Quantity > 0).ToList();

            Assert.Equal(2, results.Count);
            Assert.Contains(results, w => w.ID == id && w.Name == "Alpha");
            var parsed = Assert.IsType<FakeQuery>(provider.CreatedQueries.Single());
            Assert.Contains(parsed.WhereParameters, p => p.Name == "Quantity" && p.Operator == Operator.GreaterThan);
        }

        [Fact]
        public async Task SaveAsync_ExecutesQueryTransactionallyAndCommits()
        {
            var (manager, provider) = NewManager();
            var widget = new TestWidget { Name = "New", Quantity = 3 };

            await manager.SaveAsync(widget);

            var context = Assert.Single(provider.Contexts);
            Assert.True(context.HasTransaction);
            var executed = Assert.IsType<FakeQuery>(Assert.Single(context.ExecutedQueries));
            Assert.Equal(FakeQueryKind.InsertAndUpdate, executed.Kind);
            Assert.Same(widget, Assert.Single(executed.Objects));
            Assert.False(context.RolledBack);
            Assert.False(widget.HasChanges);
        }

        [Fact]
        public void NeutralAssemblies_ContainNoSqlText()
        {
            // Tripwire: string literals are compiled into the #US heap as UTF-16, so a raw byte
            // scan of the neutral assemblies finds any SQL-flavoured literal. Rendering belongs
            // exclusively to provider assemblies. The query model lives in TFM.Nucleus.Data, so
            // the keywords a renderer writes around it (joiners, ordering, paging, locking hints
            // and catalog names of either dialect) are markers too.
            string[] sqlMarkers =
            [
                "SELECT ", "INSERT INTO", "UPDATE ", "DELETE FROM", "[dbo]", "IS NULL", "WITH(NOLOCK)", "SCOPE_IDENTITY",
                " AND ", " OR ", "WHERE ", " FROM ", "ORDER BY", " ASC", " DESC", "LIKE", "ROWLOCK", "NOLOCK",
                "@@ROWCOUNT", "COUNT_BIG", "ROW_NUMBER", "COLLATE", "CONTAINS(", "OFFSET ", "LIMIT ", "RETURNING",
                "CREATE ", "DROP ", "ALTER ", "VALUES", "pg_", "information_schema", "INFORMATION_SCHEMA", "sys.", "::",
            ];
            Type[] anchors = [typeof(Parameter), typeof(IQueryParameter), typeof(CoreObject)]; // TFM.Nucleus.Data, TFM.Nucleus.Abstractions, TFM.Nucleus

            var offenders = new List<string>();
            foreach (var assembly in anchors.Select(t => t.Assembly).Distinct())
            {
                var bytes = System.IO.File.ReadAllBytes(assembly.Location);
                foreach (var marker in sqlMarkers)
                {
                    var pattern = System.Text.Encoding.Unicode.GetBytes(marker);
                    if (bytes.AsSpan().IndexOf(pattern) >= 0)
                        offenders.Add($"{assembly.GetName().Name}: \"{marker}\"");
                }
            }

            Assert.True(offenders.Count == 0,
                $"SQL text found in neutral assemblies:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
        }
    }
}
