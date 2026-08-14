namespace EmbodySense.CancellationHost.Persistence;

internal static class CustomLoopRunStagingWriterHost
{
    internal static async Task<int> RunAsync(string lockPath, string stagingPath, string readyMarker, string releaseMarker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath) ?? throw new ArgumentException("The custom-loop mutation lock path has no parent directory.", nameof(lockPath)));
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath) ?? throw new ArgumentException("The custom-loop staging path has no parent directory.", nameof(stagingPath)));
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        await File.WriteAllTextAsync(stagingPath, "active staging content");
        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyMarker, releaseMarker);
        return 0;
    }
}
