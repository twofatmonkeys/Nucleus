namespace TFM.Nucleus.TestSupport
{
    /// <summary>
    /// Where a test project's source lives, for the accept switches that write goldens, snapshots
    /// and benchmark baselines back into the repo. Shared by every test project (linked from
    /// tests\Shared), so each finds its own folder rather than a hard-coded project name.
    /// </summary>
    public static class TestSourcePaths
    {
        /// <summary>
        /// The running test project's source folder: the first folder above the build output that
        /// holds a .csproj. The output is bin\&lt;configuration&gt;\&lt;framework&gt; under it.
        /// </summary>
        public static string ProjectDirectory()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (dir.EnumerateFiles("*.csproj").Any())
                    return dir.FullName;
            throw new DirectoryNotFoundException($"No .csproj was found above the test output folder {AppContext.BaseDirectory}.");
        }
    }
}
