using System.Diagnostics;
using System.Globalization;

namespace EmbodySense.CancellationHost.Persistence;

internal static class PipeHoldingChildHost
{
    internal static async Task<int> StartAsync(string childProcessIdPath, string childLifetimeMillisecondsText)
    {
        if (!int.TryParse(childLifetimeMillisecondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var childLifetimeMilliseconds)
            || childLifetimeMilliseconds <= 0)
        {
            return 2;
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(typeof(PipeHoldingChildHost).Assembly.Location);
        startInfo.ArgumentList.Add("pipe-holder-child");
        startInfo.ArgumentList.Add(childLifetimeMillisecondsText);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";

        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The pipe-holding child process could not be started.");
        await File.WriteAllTextAsync(childProcessIdPath, child.Id.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    internal static async Task<int> HoldAsync(string childLifetimeMillisecondsText)
    {
        if (!int.TryParse(childLifetimeMillisecondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var childLifetimeMilliseconds)
            || childLifetimeMilliseconds <= 0)
        {
            return 2;
        }

        await Task.Delay(childLifetimeMilliseconds);
        return 0;
    }
}
