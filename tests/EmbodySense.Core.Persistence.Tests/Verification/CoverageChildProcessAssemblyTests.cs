using System.Diagnostics;
using System.Security.Cryptography;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Verification;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CoverageChildProcessAssemblyTests
{
    private const string TestName = "EmbodySense.Core.Persistence.Tests.Verification.CoverageChildProcessAssemblyTests.Coordination_only_vstest_arguments_use_the_pristine_assembly_without_coverage_outputs";

    [Fact]
    public void Coordination_only_vstest_arguments_use_the_pristine_assembly_without_coverage_outputs()
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
            var coordinationOnly = new ProcessStartInfo("dotnet");
            CoverageChildProcessAssembly.AddCoordinationOnlyVstestArguments(coordinationOnly, currentAssemblyPath, TestName);

            Assert.Equal(
                ["vstest", pristineAssemblyPath, $"--TestCaseFilter:FullyQualifiedName={TestName}"],
                coordinationOnly.ArgumentList);
            Assert.DoesNotContain(coordinationOnly.ArgumentList, argument => argument.StartsWith("--TestAdapterPath:", StringComparison.Ordinal));
            Assert.DoesNotContain(coordinationOnly.ArgumentList, argument => argument.StartsWith("--Settings:", StringComparison.Ordinal));
            Assert.DoesNotContain(coordinationOnly.ArgumentList, argument => argument.StartsWith("--Collect:", StringComparison.Ordinal));
            Assert.DoesNotContain(coordinationOnly.ArgumentList, argument => argument.StartsWith("--ResultsDirectory:", StringComparison.Ordinal));
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
