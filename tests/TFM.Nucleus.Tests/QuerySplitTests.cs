namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The parameter limit is the most a command may carry: a chain at the limit runs as one
    /// command, and a chain over it splits into chunks that each fill up to the limit.
    /// </summary>
    public class QuerySplitTests
    {
        private const int ParametersPerRow = 2;

        /// <summary>A linked chain of inserts, two parameters a row.</summary>
        private static InsertQuery Chain(int rows)
        {
            InsertQuery? root = null;
            for (var i = 0; i < rows; i++)
            {
                var row = new InsertQuery(SqlServerQueryRenderer.Instance, "[dbo].[tbl_TestWidget]");
                row.SetParameters.Add(new Parameter("Name", $"w{i}"));
                row.SetParameters.Add(new Parameter("Quantity", i));
                if (root == null)
                    root = row;
                else
                    root.LinkedQueries.Add(row);
            }
            return root!;
        }

        [Fact]
        public void AChainAtTheLimit_IsOneCommand()
        {
            var query = Chain(10);

            Assert.False(query.NeedsSplitting(10 * ParametersPerRow, out var chunks));
            Assert.Null(chunks);
            Assert.Single(query.Split(10 * ParametersPerRow));
        }

        [Fact]
        public void AChainOverTheLimit_Splits()
        {
            var query = Chain(10);

            Assert.True(query.NeedsSplitting(10 * ParametersPerRow - 1, out var chunks));
            Assert.Equal(2, chunks!.Count);
        }

        [Fact]
        public void EveryChunk_FillsUpToTheLimit()
        {
            const int limit = 4 * ParametersPerRow;

            var chunks = Chain(10).Split(limit);

            Assert.Equal([limit, limit, 2 * ParametersPerRow], chunks.Select(c => c.GetParameterCount()));
        }

        /// <summary>
        /// Inserts with an update chain linked from them, as a mixed save builds it: the update
        /// chain hangs off the insert root with its own linked updates.
        /// </summary>
        private static InsertQuery MixedChain(int inserts, int updates)
        {
            var root = Chain(inserts);
            UpdateQuery? updateRoot = null;
            for (var i = 0; i < updates; i++)
            {
                var row = new UpdateQuery(SqlServerQueryRenderer.Instance, "[dbo].[tbl_TestWidget]", "ID", Guid.NewGuid());
                row.SetParameters.Add(new Parameter("Quantity", i));
                if (updateRoot == null)
                    updateRoot = row;
                else
                    updateRoot.LinkedQueries.Add(row);
            }
            root.LinkedQueries.Add(updateRoot!);
            return root;
        }

        private static string Normalised(IEnumerable<Query> chunks) =>
            string.Join(";", chunks.Select(c => SqlSnapshot.Normalize(c.BuildQuery()))
                .SelectMany(sql => sql.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)));

        [Fact]
        public void ANestedChainOverTheLimit_IsSplitToo()
        {
            // 10 inserts, then a chain of 50 updates linked from them. The update chain used to
            // move as one unit, so its chunk carried every update's parameters, over the limit.
            const int limit = 20;
            var query = MixedChain(10, 50);
            var total = query.GetParameterCount();

            var chunks = query.Split(limit);

            Assert.All(chunks, c => Assert.True(c.GetParameterCount() <= limit, $"A chunk carries {c.GetParameterCount()} parameters, over {limit}."));
            Assert.Equal(total, chunks.Sum(c => c.GetParameterCount()));
        }

        [Fact]
        public void SplittingKeepsTheStatementsInTheOrderTheUnsplitCommandRunsThem()
        {
            var unsplit = Normalised([MixedChain(6, 9)]);
            var split = Normalised(MixedChain(6, 9).Split(7));

            // The parameter names differ per instance, so compare the statement shapes in order.
            static string Shape(string sql) => string.Join(";", sql.Split(';').Select(s => s.Split(' ')[0]));
            Assert.Equal(Shape(unsplit), Shape(split));
            Assert.StartsWith("INSERT", split);
        }
    }
}
