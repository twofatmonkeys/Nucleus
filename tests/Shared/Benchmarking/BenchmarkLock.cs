namespace TFM.Nucleus.TestSupport.Benchmarking
{
    /// <summary>
    /// A machine-wide lock that one benchmark holds while it measures. dotnet test runs each target
    /// framework in its own process and each provider's collection in parallel, and two
    /// benchmarks measuring at once slow each other down and count each other's allocations.
    /// An exclusively opened file in the temp folder is the lock: Windows refuses the second
    /// open, and on Linux and macOS .NET backs FileShare.None with an advisory lock.
    /// </summary>
    public sealed class BenchmarkLock : IDisposable
    {
        private static readonly string LockPath = Path.Combine(Path.GetTempPath(), "tfm-nucleus-benchmarks.lock");
        private readonly FileStream _stream;

        private BenchmarkLock(FileStream stream) => _stream = stream;

        /// <summary>Waits for the lock, giving up after <paramref name="timeout"/> (an hour by default).</summary>
        public static async Task<BenchmarkLock> AcquireAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromHours(1));
            while (true)
            {
                try
                {
                    return new BenchmarkLock(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    // Held by another benchmark, in this process or another one.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
                catch (IOException ex)
                {
                    throw new TimeoutException($"Another benchmark held {LockPath} for longer than the timeout.", ex);
                }
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
