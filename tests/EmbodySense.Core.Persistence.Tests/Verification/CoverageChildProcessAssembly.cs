using System.Diagnostics;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal static class CoverageChildProcessAssembly
{
    internal const string IsolatedAssemblyDirectoryVariable = "EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY";

    internal static void AddUninstrumentedVstestArguments(ProcessStartInfo startInfo, string currentAssemblyPath, string fullyQualifiedTestName)
        => AddUninstrumentedVstestArguments(
            startInfo,
            currentAssemblyPath,
            fullyQualifiedTestName,
            Environment.GetEnvironmentVariable(IsolatedAssemblyDirectoryVariable));

    internal static void AddUninstrumentedVstestArguments(
        ProcessStartInfo startInfo,
        string currentAssemblyPath,
        string fullyQualifiedTestName,
        string? isolatedAssemblyDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedTestName);

        startInfo.ArgumentList.Add("vstest");
        if (string.IsNullOrWhiteSpace(isolatedAssemblyDirectory))
        {
            startInfo.ArgumentList.Add(currentAssemblyPath);
        }
        else
        {
            // This child executes the exact existing xUnit identity, while the parent lane owns
            // production hit evidence. Reading the immutable verifier copy avoids collector work
            // whose Windows baseline contributed no production line absent from the parent lane.
            if (!Directory.Exists(isolatedAssemblyDirectory))
            {
                throw new DirectoryNotFoundException($"The immutable coverage child-process directory is unavailable: `{isolatedAssemblyDirectory}`.");
            }

            var isolatedPath = Path.Combine(isolatedAssemblyDirectory, Path.GetFileName(currentAssemblyPath));
            if (!File.Exists(isolatedPath))
            {
                throw new FileNotFoundException("The immutable coverage child-process assembly is unavailable.", isolatedPath);
            }

            startInfo.ArgumentList.Add(isolatedPath);
        }

        startInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + fullyQualifiedTestName);
    }

    internal static void AddExpectedTerminationVstestArguments(ProcessStartInfo startInfo, string currentAssemblyPath, string fullyQualifiedTestName)
        => AddExpectedTerminationVstestArguments(
            startInfo,
            currentAssemblyPath,
            fullyQualifiedTestName,
            Environment.GetEnvironmentVariable(IsolatedAssemblyDirectoryVariable));

    internal static void AddExpectedTerminationVstestArguments(
        ProcessStartInfo startInfo,
        string currentAssemblyPath,
        string fullyQualifiedTestName,
        string? isolatedAssemblyDirectory)
        => AddUninstrumentedVstestArguments(startInfo, currentAssemblyPath, fullyQualifiedTestName, isolatedAssemblyDirectory);
}
