namespace EmbodySense.CancellationHost.Persistence;

/// <summary>
/// Holds an existing file open with the Windows restrictive-reader sharing mode for a bounded
/// cross-process contention test. The operation has no persistence authority; it only owns the
/// requested operating-system handle until the parent publishes the release marker.
/// </summary>
internal static class WindowsRestrictiveReaderCrossProcessHost
{
    private static readonly TimeSpan _releaseTimeout = TimeSpan.FromSeconds(60);

    internal static async Task<int> RunAsync(string path, string readyPath, string releasePath, string resultPath)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(readyPath)
            || string.IsNullOrWhiteSpace(releasePath)
            || string.IsNullOrWhiteSpace(resultPath)
            || string.Equals(readyPath, releasePath, StringComparison.Ordinal)
            || string.Equals(readyPath, resultPath, StringComparison.Ordinal)
            || string.Equals(releasePath, resultPath, StringComparison.Ordinal))
        {
            return 2;
        }

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await File.WriteAllTextAsync(readyPath, "ready");
        await WaitForReleaseAsync(releasePath);
        await File.WriteAllTextAsync(resultPath, "released");
        return 0;
    }

    private static async Task WaitForReleaseAsync(string releasePath)
    {
        using var cancellation = new CancellationTokenSource(_releaseTimeout);
        try
        {
            while (!File.Exists(releasePath))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"The restrictive-reader release marker `{releasePath}` was not published within {_releaseTimeout}.");
        }
    }
}
