using TFM.Nucleus.Data.SQL;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Exercises the ref-counting contract of the internal QueryNotificationHost by swapping its
    /// listener delegates (InternalsVisibleTo), so no SQL Server is needed. The host's state is
    /// process-wide, so every test uses its own unique fake connection strings; tests in this
    /// class run serially (one xunit collection), and the constructor/Dispose pair restores the
    /// default delegates around each test.
    /// </summary>
    public sealed class QueryNotificationHostTests : IDisposable
    {
        private readonly Action<string> _originalStart;
        private readonly Action<string> _originalStop;
        private readonly List<string> _starts = [];
        private readonly List<string> _stops = [];

        public QueryNotificationHostTests()
        {
            _originalStart = QueryNotificationHost.StartListener;
            _originalStop = QueryNotificationHost.StopListener;
            QueryNotificationHost.StartListener = cs => { lock (_starts) _starts.Add(cs); };
            QueryNotificationHost.StopListener = cs => { lock (_stops) _stops.Add(cs); };
        }

        public void Dispose()
        {
            QueryNotificationHost.StartListener = _originalStart;
            QueryNotificationHost.StopListener = _originalStop;
        }

        private static string NewConnectionString() =>
            $"Server=fake;Database=db_{Guid.NewGuid():N};Integrated Security=true";

        [Fact]
        public void FirstAcquire_StartsListener_SecondAcquireDoesNot()
        {
            var cs = NewConnectionString();
            try
            {
                QueryNotificationHost.Acquire(cs);
                Assert.Equal([cs], _starts);

                QueryNotificationHost.Acquire(cs);
                Assert.Single(_starts);
            }
            finally
            {
                QueryNotificationHost.Release(cs);
                QueryNotificationHost.Release(cs);
            }
        }

        [Fact]
        public void Release_Decrements_AndOnlyLastReleaseStopsListener()
        {
            var cs = NewConnectionString();
            QueryNotificationHost.Acquire(cs);
            QueryNotificationHost.Acquire(cs);

            QueryNotificationHost.Release(cs);
            Assert.Empty(_stops);

            QueryNotificationHost.Release(cs);
            Assert.Equal([cs], _stops);

            // The count is gone: releasing again is a no-op, not a second Stop.
            QueryNotificationHost.Release(cs);
            Assert.Single(_stops);
        }

        [Fact]
        public void DifferentConnectionStrings_AreRefCountedIndependently()
        {
            var csA = NewConnectionString();
            var csB = NewConnectionString();

            QueryNotificationHost.Acquire(csA);
            QueryNotificationHost.Acquire(csB);
            Assert.Equal([csA, csB], _starts);

            QueryNotificationHost.Release(csA);
            Assert.Equal([csA], _stops);

            QueryNotificationHost.Release(csB);
            Assert.Equal([csA, csB], _stops);
        }

        [Fact]
        public void StartListenerFailure_ThrowsWithPrerequisites_AndDoesNotLeakRefCount()
        {
            var cs = NewConnectionString();
            QueryNotificationHost.StartListener = _ => throw new InvalidOperationException("no broker");

            var ex = Assert.Throws<Exception>(() => QueryNotificationHost.Acquire(cs));
            Assert.Contains("ENABLE_BROKER", ex.Message);
            Assert.Contains("SUBSCRIBE QUERY NOTIFICATIONS", ex.Message);
            Assert.IsType<InvalidOperationException>(ex.InnerException);

            // The failed Acquire must not have counted: the next Acquire tries the listener again.
            QueryNotificationHost.StartListener = c => { lock (_starts) _starts.Add(c); };
            try
            {
                QueryNotificationHost.Acquire(cs);
                Assert.Equal([cs], _starts);
            }
            finally
            {
                QueryNotificationHost.Release(cs);
            }
            Assert.Equal([cs], _stops);
        }
    }
}
