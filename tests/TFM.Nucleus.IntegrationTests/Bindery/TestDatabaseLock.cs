namespace TFM.Nucleus.IntegrationTests.Bindery
{
    /// <summary>
    /// A cross-process lock over the shared test database, held as an exclusively opened file in
    /// the temp folder. A file rather than a named mutex because a mutex must be released on the
    /// thread that took it, which an async test cannot promise.
    /// </summary>
    internal sealed class TestDatabaseLock : IDisposable
    {
        private static readonly string LockPath = Path.Combine(Path.GetTempPath(), "tfm-nucleus-bindery-tests.lock");
        private readonly FileStream _handle;

        private TestDatabaseLock(FileStream handle) => _handle = handle;

        public static async Task<TestDatabaseLock> AcquireAsync()
        {
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (true)
            {
                try
                {
                    return new TestDatabaseLock(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
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
