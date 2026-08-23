using System.Diagnostics;
using System.Security.Cryptography;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Verification;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CoverageChildProcessAssemblyTests
{
    private const string ExpectedTerminationTestName = "EmbodySense.Core.Startup.Tests.Verification.CoverageChildProcessAssemblyTests.Expected_termination_vstest_arguments_use_the_pristine_assembly_without_coverage_outputs";
    private const string TestName = "EmbodySense.Core.Startup.Tests.Verification.CoverageChildProcessAssemblyTests.Report_free_vstest_arguments_use_the_pristine_assembly_without_coverage_outputs";

    [Fact]
    public void Expected_termination_vstest_arguments_use_the_pristine_assembly_without_coverage_outputs()
    {
        using var workspace = new TestWorkspace();
        var pristineDirectory = workspace.File("pristine");
        var collectorDirectory = workspace.File("Collector");
        Directory.CreateDirectory(pristineDirectory);
        Directory.CreateDirectory(collectorDirectory);
        File.WriteAllText(workspace.File("verification-pull-request.runsettings"), "<RunSettings />");
        var currentAssemblyPath = typeof(CoverageChildProcessAssemblyTests).Assembly.Location;
        var pristineAssemblyPath = Path.Combine(pristineDirectory, Path.GetFileName(currentAssemblyPath));
        File.WriteAllBytes(pristineAssemblyPath, [0x01, 0x02, 0x03, 0x04]);
        File.WriteAllBytes(Path.Combine(pristineDirectory, "dependency.dll"), [0x05, 0x06, 0x07, 0x08]);
        var expectedHashes = GetDirectoryHashes(pristineDirectory);
        var originalDirectory = Environment.GetEnvironmentVariable(CoverageChildProcessAssembly.IsolatedAssemblyDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(CoverageChildProcessAssembly.IsolatedAssemblyDirectoryVariable, pristineDirectory);
            var expectedTermination = new ProcessStartInfo("dotnet");
            CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(expectedTermination, currentAssemblyPath, ExpectedTerminationTestName);

            Assert.Equal(
                ["vstest", pristineAssemblyPath, $"--TestCaseFilter:FullyQualifiedName={ExpectedTerminationTestName}"],
                expectedTermination.ArgumentList);
            Assert.False(Directory.Exists(workspace.File("Invocations")));
            Assert.False(Directory.Exists(workspace.File("Results")));
            Assert.Equal(expectedHashes, GetDirectoryHashes(pristineDirectory));

            var successful = new ProcessStartInfo("dotnet");
            CoverageChildProcessAssembly.AddVstestArguments(successful, currentAssemblyPath, ExpectedTerminationTestName);
            Assert.Contains("--Collect:XPlat Code Coverage", successful.ArgumentList);
            Assert.Contains($"--ResultsDirectory:{workspace.File("Results")}", successful.ArgumentList);
            Assert.Single(Directory.EnumerateDirectories(workspace.File("Invocations")));
            Assert.True(Directory.Exists(workspace.File("Results")));
            Assert.Equal(expectedHashes, GetDirectoryHashes(pristineDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CoverageChildProcessAssembly.IsolatedAssemblyDirectoryVariable, originalDirectory);
        }
    }

    [Fact]
    public void Report_free_vstest_arguments_use_the_pristine_assembly_without_coverage_outputs()
    {
        using var workspace = new TestWorkspace();
        var pristineDirectory = workspace.File("pristine");
        Directory.CreateDirectory(pristineDirectory);
        var currentAssemblyPath = typeof(CoverageChildProcessAssemblyTests).Assembly.Location;
        var pristineAssemblyPath = Path.Combine(pristineDirectory, Path.GetFileName(currentAssemblyPath));
        File.WriteAllBytes(pristineAssemblyPath, [0x01, 0x02, 0x03, 0x04]);
        File.WriteAllBytes(Path.Combine(pristineDirectory, "dependency.dll"), [0x05, 0x06, 0x07, 0x08]);
        var expectedHashes = GetDirectoryHashes(pristineDirectory);
        var originalDirectory = Environment.GetEnvironmentVariable(CoverageChildProcessAssembly.IsolatedAssemblyDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(CoverageChildProcessAssembly.IsolatedAssemblyDirectoryVariable, pristineDirectory);
            var reportFree = new ProcessStartInfo("dotnet");
            CoverageChildProcessAssembly.AddReportFreeVstestArguments(reportFree, currentAssemblyPath, TestName);

            Assert.Equal(
                ["vstest", pristineAssemblyPath, $"--TestCaseFilter:FullyQualifiedName={TestName}"],
                reportFree.ArgumentList);
            Assert.DoesNotContain(reportFree.ArgumentList, argument => argument.StartsWith("--TestAdapterPath:", StringComparison.Ordinal));
            Assert.DoesNotContain(reportFree.ArgumentList, argument => argument.StartsWith("--Settings:", StringComparison.Ordinal));
            Assert.DoesNotContain(reportFree.ArgumentList, argument => argument.StartsWith("--Collect:", StringComparison.Ordinal));
            Assert.DoesNotContain(reportFree.ArgumentList, argument => argument.StartsWith("--ResultsDirectory:", StringComparison.Ordinal));
            Assert.False(Directory.Exists(workspace.File("Invocations")));
            Assert.False(Directory.Exists(workspace.File("Results")));
            Assert.Equal(expectedHashes, GetDirectoryHashes(pristineDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CoverageChildProcessAssembly.IsolatedAssemblyDirectoryVariable, originalDirectory);
        }
    }

    private static IReadOnlyList<string> GetDirectoryHashes(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => $"{Path.GetRelativePath(directory, path)}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
            .ToArray();
}
