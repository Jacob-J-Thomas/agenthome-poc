using System.Diagnostics;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal static class CancellationHostProcess
{
    internal static Process Start(params string[] arguments)
        => Process.Start(CreateStartInfo(arguments))
            ?? throw new InvalidOperationException("The cancellation host process could not be started.");

    internal static CrossProcessProcess StartOwned(params string[] arguments)
        => CrossProcessProcessOwnership.Start(CreateStartInfo(arguments));

    private static ProcessStartInfo CreateStartInfo(string[] arguments)
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "CancellationHost");
        var executableName = OperatingSystem.IsWindows() ? "EmbodySense.CancellationHost.exe" : "EmbodySense.CancellationHost";
        var hostExecutable = Path.Combine(fixtureDirectory, executableName);
        foreach (var requiredFileName in new[]
        {
            executableName,
            "EmbodySense.CancellationHost.dll",
            "EmbodySense.CancellationHost.deps.json",
            "EmbodySense.CancellationHost.runtimeconfig.json"
        })
        {
            var requiredPath = Path.Combine(fixtureDirectory, requiredFileName);
            if (!File.Exists(requiredPath))
            {
                throw new FileNotFoundException("The authenticated cancellation-host fixture bundle is incomplete.", requiredPath);
            }
        }

        var startInfo = new ProcessStartInfo(hostExecutable)
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return startInfo;
    }
}
