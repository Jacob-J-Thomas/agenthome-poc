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
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name
            ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var hostAssembly = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "EmbodySense.CancellationHost",
            "bin",
            configuration,
            targetFramework,
            "EmbodySense.CancellationHost.dll");
        if (!File.Exists(hostAssembly))
        {
            throw new FileNotFoundException(
                $"The cancellation host assembly was not built at `{hostAssembly}`.",
                hostAssembly);
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return startInfo;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The repository root could not be located from the test output directory.");
    }
}
