using System.Diagnostics;
using EmbodySense.Tests.Support;
using Xunit.Sdk;

namespace EmbodySense.Core.Persistence.Tests.Verification;

public sealed class CrossProcessReadinessDiagnosticsTests
{
    [Fact]
    public async Task Readiness_failure_does_not_hang_when_descendant_holds_redirected_pipes()
    {
        using var workspace = new TestWorkspace();
        var childProcessIdPath = workspace.File("pipe-holder-child.pid");
        using var process = CancellationHostProcess.Start("pipe-holder", childProcessIdPath, "30000");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var child = new CrossProcessReadinessChild(
            "pipe-holder",
            process,
            workspace.File("missing-ready"),
            workspace.File("missing-result"));

        try
        {
            var wait = CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync(
                "verification/pipe-holder",
                [child],
                TimeSpan.FromMilliseconds(100));
            var failure = await Assert.ThrowsAsync<FailException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Contains("verification/pipe-holder/readiness-exit/pipe-holder", failure.Message, StringComparison.Ordinal);
            Assert.Contains("stdout=", failure.Message, StringComparison.Ordinal);
            Assert.Contains("stderr=", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await TerminateProcessAsync(childProcessIdPath);
        }
    }

    private static async Task TerminateProcessAsync(string childProcessIdPath)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(childProcessIdPath) && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        if (!File.Exists(childProcessIdPath)
            || !int.TryParse(await File.ReadAllTextAsync(childProcessIdPath), out var processId))
        {
            return;
        }

        try
        {
            using var child = Process.GetProcessById(processId);
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (ArgumentException)
        {
        }
    }
}
