using System.Diagnostics;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal static class CoverageChildProcessAssembly
{
    internal const string IsolatedAssemblyDirectoryVariable = "EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY";

    internal static void AddVstestArguments(ProcessStartInfo startInfo, string currentAssemblyPath, string fullyQualifiedTestName)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedTestName);

        startInfo.ArgumentList.Add("vstest");
        var isolatedDirectory = Environment.GetEnvironmentVariable(IsolatedAssemblyDirectoryVariable);
        if (string.IsNullOrWhiteSpace(isolatedDirectory))
        {
            startInfo.ArgumentList.Add(currentAssemblyPath);
            startInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + fullyQualifiedTestName);
            return;
        }

        var isolationRoot = Directory.GetParent(isolatedDirectory)?.FullName
            ?? throw new DirectoryNotFoundException("The isolated coverage child-process root is unavailable.");
        var collectorDirectory = Path.Combine(isolationRoot, "Collector");
        var runSettingsPath = Path.Combine(isolationRoot, "verification-pull-request.runsettings");
        if (!Directory.Exists(collectorDirectory))
        {
            throw new DirectoryNotFoundException($"The isolated coverage collector is unavailable: `{collectorDirectory}`.");
        }
        if (!File.Exists(runSettingsPath))
        {
            throw new FileNotFoundException("The isolated coverage runsettings file is unavailable.", runSettingsPath);
        }

        var invocationId = Guid.NewGuid().ToString("N");
        var executionDirectory = Path.Combine(isolationRoot, "Invocations", invocationId);
        // The parent verifier discovers every report below Results. A distinct child directory
        // prevents concurrent XPlat collectors from sharing a testhost flush destination while
        // retaining report provenance and manifest validation for each invocation.
        var resultsDirectory = Path.Combine(isolationRoot, "Results", invocationId);
        CopyDirectory(isolatedDirectory, executionDirectory);
        var isolatedPath = Path.Combine(executionDirectory, Path.GetFileName(currentAssemblyPath));
        if (!File.Exists(isolatedPath))
        {
            throw new FileNotFoundException("The isolated coverage child-process assembly is unavailable.", isolatedPath);
        }

        Directory.CreateDirectory(resultsDirectory);
        startInfo.ArgumentList.Add(isolatedPath);
        startInfo.ArgumentList.Add("--TestAdapterPath:" + collectorDirectory);
        startInfo.ArgumentList.Add("--Settings:" + runSettingsPath);
        startInfo.ArgumentList.Add("--Collect:XPlat Code Coverage");
        startInfo.ArgumentList.Add("--ResultsDirectory:" + resultsDirectory);
        startInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + fullyQualifiedTestName);
    }

    internal static void AddExpectedTerminationVstestArguments(ProcessStartInfo startInfo, string currentAssemblyPath, string fullyQualifiedTestName)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedTestName);

        startInfo.ArgumentList.Add("vstest");
        var isolatedDirectory = Environment.GetEnvironmentVariable(IsolatedAssemblyDirectoryVariable);
        if (string.IsNullOrWhiteSpace(isolatedDirectory))
        {
            startInfo.ArgumentList.Add(currentAssemblyPath);
        }
        else
        {
            // An intentionally terminated testhost cannot flush useful hit data. Read the immutable
            // verifier copy directly; the outer verifier re-hashes it after every child has exited.
            if (!Directory.Exists(isolatedDirectory))
            {
                throw new DirectoryNotFoundException($"The immutable coverage child-process directory is unavailable: `{isolatedDirectory}`.");
            }

            var isolatedPath = Path.Combine(isolatedDirectory, Path.GetFileName(currentAssemblyPath));
            if (!File.Exists(isolatedPath))
            {
                throw new FileNotFoundException("The immutable coverage child-process assembly is unavailable.", isolatedPath);
            }

            startInfo.ArgumentList.Add(isolatedPath);
        }

        startInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + fullyQualifiedTestName);
    }

    internal static void AddCoordinationOnlyVstestArguments(ProcessStartInfo startInfo, string currentAssemblyPath, string fullyQualifiedTestName)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedTestName);

        startInfo.ArgumentList.Add("vstest");
        var isolatedDirectory = Environment.GetEnvironmentVariable(IsolatedAssemblyDirectoryVariable);
        if (string.IsNullOrWhiteSpace(isolatedDirectory))
        {
            startInfo.ArgumentList.Add(currentAssemblyPath);
        }
        else
        {
            // This child preserves real cross-process coordination while its production paths are
            // already covered by the parent lane. Keep the immutable verifier assembly intact and
            // omit duplicate child collection; see https://github.com/Jacob-J-Thomas/agenthome-poc/issues/422.
            if (!Directory.Exists(isolatedDirectory))
            {
                throw new DirectoryNotFoundException($"The immutable coverage child-process directory is unavailable: `{isolatedDirectory}`.");
            }

            var isolatedPath = Path.Combine(isolatedDirectory, Path.GetFileName(currentAssemblyPath));
            if (!File.Exists(isolatedPath))
            {
                throw new FileNotFoundException("The immutable coverage child-process assembly is unavailable.", isolatedPath);
            }

            startInfo.ArgumentList.Add(isolatedPath);
        }

        startInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + fullyQualifiedTestName);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file)));
        }
    }
}
