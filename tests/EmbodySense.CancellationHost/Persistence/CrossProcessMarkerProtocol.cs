using System.Diagnostics;

namespace EmbodySense.CancellationHost.Persistence;

internal static class CrossProcessMarkerProtocol
{
    private static readonly TimeSpan _releaseTimeout = TimeSpan.FromSeconds(15);

    internal static async Task SignalReadyAndWaitForReleaseAsync(string readyMarker, string releaseMarker)
    {
        await File.WriteAllTextAsync(readyMarker, "ready");
        var startedAt = TimeProvider.System.GetTimestamp();
        while (!File.Exists(releaseMarker))
        {
            if (TimeProvider.System.GetElapsedTime(startedAt) >= _releaseTimeout)
            {
                throw new TimeoutException($"The cross-process release marker was not published within {_releaseTimeout}.");
            }

            await Task.Delay(10);
        }
    }

    internal static void SignalReadyAndWaitForRelease(string readyMarker, string releaseMarker)
    {
        File.WriteAllText(readyMarker, "ready");
        var startedAt = TimeProvider.System.GetTimestamp();
        while (!File.Exists(releaseMarker))
        {
            if (TimeProvider.System.GetElapsedTime(startedAt) >= _releaseTimeout)
            {
                throw new TimeoutException($"The cross-process release marker was not published within {_releaseTimeout}.");
            }

            Thread.Sleep(10);
        }
    }

    internal static Task WriteResultAsync(string resultMarker, string result)
        => File.WriteAllTextAsync(resultMarker, result);

    internal static void TerminateAbruptly()
    {
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }
}
