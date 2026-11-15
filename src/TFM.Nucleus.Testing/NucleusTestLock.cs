namespace TFM.Nucleus.Testing;

/// <summary>
/// A cross-process lock over one suite's tables in a shared test database, held as an exclusively
/// opened file in the temp folder. A file rather than a named mutex because a mutex must be
/// released on the thread that took it, which an async test cannot promise. Each suite names its
/// own lock, so unrelated suites never wait on each other, while the net8.0 and net10.0 runs of
/// one suite, which <c>dotnet test</c> starts together, still cannot reset each other's rows.
/// </summary>
/// <remarks>
/// The lock file name does not depend on the repo, so suites in different repos that share a
/// table (the schema lock, say) also exclude each other on one machine.
/// </remarks>
public sealed class NucleusTestLock : IDisposable
{
    /// <summary>How long <see cref="AcquireAsync"/> waits before it gives up and throws.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly FileStream _handle;

    private NucleusTestLock(FileStream handle) => _handle = handle;

    /// <summary>Waits for the lock called <paramref name="name"/>, retrying every 100 ms for up to <see cref="Timeout"/>.</summary>
    /// <exception cref="IOException">The lock was still held by another process after <see cref="Timeout"/>.</exception>
    public static async Task<NucleusTestLock> AcquireAsync(string name, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var lockPath = Path.Combine(Path.GetTempPath(), $"tfm-nucleus-{name}-tests.lock");
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            try
            {
                return new NucleusTestLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, token);
            }
        }
    }

    /// <summary>Releases the lock.</summary>
    public void Dispose() => _handle.Dispose();
}
