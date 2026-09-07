using System.Diagnostics;
using System.Reflection;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
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
        Assert.Contains("pre-termination-state=running", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pre-termination-result=result", failure.Message, StringComparison.Ordinal);
        Assert.True(process.HasExited, "The completion diagnostic did not terminate the retained child tree.");
    }

    [Fact]
    public async Task Readiness_failure_retains_a_genuine_early_exit_distinct_from_cleanup_induced_exit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var process = CancellationHostProcess.StartOwned("pipe-holder-child", "1");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var child = new CrossProcessReadinessChild("early-exit", process, workspace.File("missing-ready"), workspace.File("missing-result"));

        var wait = CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync(
            "verification/early-exit",
            [child],
            TimeSpan.FromMilliseconds(100));
        var failure = await Assert.ThrowsAsync<FailException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("verification/early-exit/readiness-exit/early-exit", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pre-termination-state=exited pre-termination-exit=0", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pre-termination-result=<missing>", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordered_release_diagnostic_reader_preserves_delegate_outcomes_when_reporting_fails()
    {
        var unavailable = new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Unavailable);
        var unavailableReader = CreateDiagnosticReader((_, _, _, _) => Task.FromResult(unavailable), ThrowingDiagnosticWriter);
        Assert.Same(unavailable, await ReadDiagnosticAsync(unavailableReader));

        var current = new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current);
        var currentReader = CreateDiagnosticReader((_, _, _, _) => Task.FromResult(current), ThrowingDiagnosticWriter);
        Assert.Same(current, await ReadDiagnosticAsync(currentReader));

        var expected = new IOException("canonical read failure");
        var throwingReader = CreateDiagnosticReader((_, _, _, _) => Task.FromException<GovernedLoopEffectAttemptReadResult>(expected), ThrowingDiagnosticWriter);
        var exception = await Assert.ThrowsAsync<IOException>(() => ReadDiagnosticAsync(throwingReader));
        Assert.Same(expected, exception);
    }

    [Theory]
    [InlineData(false, "<unavailable>")]
    [InlineData(true, "<timed-out>")]
    public async Task Readiness_failure_terminates_owned_children_when_result_evidence_faults_or_stalls(bool stall, string expectedEvidence)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var process = CancellationHostProcess.StartOwned("pipe-holder-child", "30000");
        var child = new CrossProcessReadinessChild("result-evidence", process, workspace.File("missing-ready"), workspace.File("missing-result"));
        var stalled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync(
            "verification/result-evidence",
            [child],
            TimeSpan.FromMilliseconds(100),
            _ => stall ? stalled.Task : Task.FromException<string>(new IOException("result evidence failure")));
        var failure = await Assert.ThrowsAsync<FailException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains($"pre-termination-result={expectedEvidence}", failure.Message, StringComparison.Ordinal);
        Assert.True(process.HasExited, "The diagnostic helper did not terminate the owned child after result-evidence capture failed.");
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

    private static object CreateDiagnosticReader(Func<string, string, long, CancellationToken, Task<GovernedLoopEffectAttemptReadResult>> read, Action<string> report)
    {
        var hostAssemblyPath = Path.Combine(AppContext.BaseDirectory, "CancellationHost", "EmbodySense.CancellationHost.dll");
        var readerType = Assembly.LoadFrom(hostAssemblyPath).GetType("EmbodySense.CancellationHost.Persistence.HumanReviewOrderedReleaseProcessDiagnosticReadStore", throwOnError: true)!;
        return Activator.CreateInstance(readerType, BindingFlags.Instance | BindingFlags.NonPublic, null, [read, report], null)!;
    }

    private static Task<GovernedLoopEffectAttemptReadResult> ReadDiagnosticAsync(object reader)
        => (Task<GovernedLoopEffectAttemptReadResult>)reader.GetType().GetMethod("ReadAsync")!.Invoke(reader, ["workspace", "operation", 1L, CancellationToken.None])!;

    private static void ThrowingDiagnosticWriter(string _) => throw new IOException("diagnostic writer failure");

}
