using System.Diagnostics;
using EmbodySense.Tests.Support;
using Xunit.Sdk;

namespace EmbodySense.Core.Persistence.Tests.Verification;

public sealed class CrossProcessReadinessDiagnosticsTests
{
    [Fact]
    public async Task Windows_owned_wait_retains_its_native_completion_handle_after_the_caller_times_out()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var process = CancellationHostProcess.StartOwned("pipe-holder-child", "30000");
        try
        {
            var completion = process.WaitForExitAsync();
            await Assert.ThrowsAsync<TimeoutException>(() => completion.WaitAsync(TimeSpan.FromMilliseconds(100)));

            process.Dispose();
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            process.Dispose();
        }
    }

    [Fact]
    public async Task Windows_completion_timeout_after_durable_result_terminates_the_owned_child_tree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var process = CancellationHostProcess.StartOwned("pipe-holder-child", "30000");
        var readyPath = workspace.File("ready");
        var resultPath = workspace.File("result");
        await File.WriteAllTextAsync(readyPath, "ready");
        await File.WriteAllTextAsync(resultPath, "result");
        var child = new CrossProcessReadinessChild("completed", process, readyPath, resultPath);

        var wait = CrossProcessReadinessDiagnostics.WaitForChildrenCompletedAsync(
            "verification/completion",
            "post-gate decision",
            [child],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
        var failure = await Assert.ThrowsAsync<FailException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("verification/completion children did not finish post-gate decision teardown", failure.Message, StringComparison.Ordinal);
        Assert.Contains("completed(ready=True,result=True)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("verification/completion/post-gate decision-teardown-timeout/completed", failure.Message, StringComparison.Ordinal);
        Assert.True(process.HasExited, "The completion diagnostic did not terminate the retained child tree.");
    }

    [Fact]
    public async Task Readiness_failure_does_not_hang_when_descendant_holds_redirected_pipes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var childProcessIdPath = workspace.File("pipe-holder-child.pid");
        using var process = CancellationHostProcess.StartOwned("pipe-holder", childProcessIdPath, "30000");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var child = new CrossProcessReadinessChild(
            "pipe-holder",
            process,
            workspace.File("missing-ready"),
            workspace.File("missing-result"));

        var wait = CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync(
            "verification/pipe-holder",
            [child],
            TimeSpan.FromMilliseconds(100));
        var failure = await Assert.ThrowsAsync<FailException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("verification/pipe-holder/readiness-exit/pipe-holder", failure.Message, StringComparison.Ordinal);
        Assert.Contains("stdout=", failure.Message, StringComparison.Ordinal);
        Assert.Contains("stderr=", failure.Message, StringComparison.Ordinal);
        Assert.True(await WaitForProcessExitAsync(childProcessIdPath), "The diagnostic helper did not terminate the retained descendant.");
    }

    private static async Task<bool> WaitForProcessExitAsync(string childProcessIdPath)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(childProcessIdPath) && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        if (!File.Exists(childProcessIdPath)
            || !int.TryParse(await File.ReadAllTextAsync(childProcessIdPath), out var processId))
        {
            return false;
        }

        while (wait.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                using var child = Process.GetProcessById(processId);
                if (child.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }
}
