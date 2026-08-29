using System.Diagnostics;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal static class HumanInputContinuationHostProcess
{
    internal static Process Start(params string[] arguments)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name
            ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var assembly = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "EmbodySense.HumanInputContinuationHost",
            "bin",
            configuration,
            targetFramework,
            "EmbodySense.HumanInputContinuationHost.dll");
        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException("The Human Input continuation host assembly was not built.", assembly);
        }

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(start) ?? throw new InvalidOperationException("The Human Input continuation host process could not be started.");
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
