namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A cross-process lock over one suite's tables in the shared test database, held as an
    /// exclusively opened file in the temp folder. A file rather than a named mutex because a
    /// mutex must be released on the thread that took it, which an async test cannot promise.
    /// Each suite names its own lock, so the Bindery and S3Library runs never wait on each other,
    /// while the net8.0 and net10.0 runs of one suite, which <c>dotnet test</c> starts together,
    /// still cannot reset each other's rows.
    /// </summary>
    internal sealed class TestDatabaseLock : IDisposable
    {
        private readonly FileStream _handle;

        private TestDatabaseLock(FileStream handle) => _handle = handle;

        public static async Task<TestDatabaseLock> AcquireAsync(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var lockPath = Path.Combine(Path.GetTempPath(), $"tfm-nucleus-{name}-tests.lock");
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (true)
            {
                try
                {
                    return new TestDatabaseLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }
            }
        }

        public void Dispose() => _handle.Dispose();
    }
}
