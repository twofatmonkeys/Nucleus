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
    }
}
