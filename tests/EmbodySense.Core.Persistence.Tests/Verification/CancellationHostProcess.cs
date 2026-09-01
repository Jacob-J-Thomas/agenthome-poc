using System.Diagnostics;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal static class CancellationHostProcess
{
    internal static Process Start(params string[] arguments)
        => Process.Start(CreateDotnetStartInfo(arguments))
            ?? throw new InvalidOperationException("The cancellation host process could not be started.");

    internal static Process StartAppHost(params string[] arguments)
        => Process.Start(CreateAppHostStartInfo(arguments))
            ?? throw new InvalidOperationException("The cancellation host apphost process could not be started.");

    internal static CrossProcessProcess StartOwned(params string[] arguments)
        => CrossProcessProcessOwnership.Start(CreateDotnetStartInfo(arguments));

    internal static CrossProcessProcess StartAppHostOwned(params string[] arguments)
        => CrossProcessProcessOwnership.Start(CreateAppHostStartInfo(arguments));

    private static ProcessStartInfo CreateDotnetStartInfo(string[] arguments)
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

        var startInfo = CreateStartInfo("dotnet");
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        AddArguments(startInfo, arguments);
        return startInfo;
    }

    private static ProcessStartInfo CreateAppHostStartInfo(string[] arguments)
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

        var startInfo = CreateStartInfo(hostExecutable);
        AddArguments(startInfo, arguments);
        return startInfo;
    }

    private static ProcessStartInfo CreateStartInfo(string executable)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return startInfo;
    }

    private static void AddArguments(ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
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
