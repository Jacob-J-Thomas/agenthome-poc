using System.Diagnostics;
using System.Globalization;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal static class CrossProcessReadinessDiagnostics
{
    private static readonly TimeSpan _childEvidenceReadTimeout = TimeSpan.FromSeconds(5);

    // Covered VSTest children publish their operation marker before the XPlat collector flushes and
    // the testhost exits. Keep that teardown allowance local to the two process-race callers rather
    // than changing the repository-wide verifier or the nested operation decision bound.
    internal static readonly TimeSpan CoverageChildTeardownTimeout = TimeSpan.FromSeconds(90);

    private const int MaximumChildEvidenceCharacters = 8_192;

    internal static async Task WaitForChildrenReadyAsync(
        string operation,
        IReadOnlyList<CrossProcessReadinessChild> children,
        TimeSpan timeout)
    {
        Validate(operation, children, timeout);
        var wait = Stopwatch.StartNew();
        while (!children.All(child => File.Exists(child.ReadyPath)))
        {
            if (children.Any(child => child.Process.HasExited))
            {
                var evidence = await StopAndReadChildEvidenceAsync(operation, "readiness-exit", children);
                Assert.Fail($"Cross-process {operation} child exited before readiness. {DescribeMarkers(children)}{Environment.NewLine}{evidence}");
            }

            if (wait.Elapsed >= timeout)
            {
                var evidence = await StopAndReadChildEvidenceAsync(operation, "readiness-timeout", children);
                Assert.Fail($"Cross-process {operation} children did not all report ready within {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds. {DescribeMarkers(children)}{Environment.NewLine}{evidence}");
            }

            await Task.Delay(10);
        }
    }

    internal static async Task WaitForChildrenCompletedAsync(
        string operation,
        string stage,
        IReadOnlyList<CrossProcessReadinessChild> children,
        TimeSpan timeout,
        TimeSpan? postResultTeardownTimeout = null)
    {
        Validate(operation, children, timeout);
        if (postResultTeardownTimeout is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(postResultTeardownTimeout.Value, TimeSpan.Zero);
        }

        var resultWait = Stopwatch.StartNew();
        while (true)
        {
            var exited = children.Where(child => child.Process.HasExited).ToArray();
            var unsuccessful = exited.Where(child => child.Process.ExitCode != 0 || !File.Exists(child.ResultPath)).ToArray();
            if (unsuccessful.Length > 0)
            {
                var evidence = await StopAndReadChildEvidenceAsync(operation, stage + "-exit", children);
                Assert.Fail($"Cross-process {operation} child failed during {stage}. {DescribeMarkers(children)}{Environment.NewLine}{evidence}");
            }

            if (children.All(child => File.Exists(child.ResultPath)))
            {
                break;
            }

            if (resultWait.Elapsed >= timeout)
            {
                var evidence = await StopAndReadChildEvidenceAsync(operation, stage + "-result-timeout", children);
                Assert.Fail($"Cross-process {operation} children did not publish {stage} results within {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds. {DescribeMarkers(children)}{Environment.NewLine}{evidence}");
            }

            await Task.Delay(10);
        }

        // A successful result marker only proves that the nested operation returned. For covered
        // VSTest children, retain the process and its native completion handle until the collector
        // has flushed and the host has exited. This prevents accepting a result while its coverage
        // report is still being published and keeps the teardown bound explicit and fail-closed.
        var teardownTimeout = postResultTeardownTimeout ?? timeout - resultWait.Elapsed;
        var teardownWait = Stopwatch.StartNew();
        while (true)
        {
            var exited = children.Where(child => child.Process.HasExited).ToArray();
            var unsuccessful = exited.Where(child => child.Process.ExitCode != 0).ToArray();
            if (unsuccessful.Length > 0)
            {
                var evidence = await StopAndReadChildEvidenceAsync(operation, stage + "-teardown-exit", children);
                Assert.Fail($"Cross-process {operation} child failed during {stage} teardown. {DescribeMarkers(children)}{Environment.NewLine}{evidence}");
            }

            if (exited.Length == children.Count)
            {
                return;
            }

            if (teardownWait.Elapsed >= teardownTimeout)
            {
                var evidence = await StopAndReadChildEvidenceAsync(operation, stage + "-teardown-timeout", children);
                Assert.Fail($"Cross-process {operation} children did not finish {stage} teardown within {teardownTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds after publishing results. {DescribeMarkers(children)}{Environment.NewLine}{evidence}");
            }

            await Task.Delay(10);
        }
    }

    private static void Validate(string operation, IReadOnlyList<CrossProcessReadinessChild> children, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count == 0)
        {
            throw new ArgumentException("At least one cross-process child is required.", nameof(children));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
    }

    private static string DescribeMarkers(IReadOnlyList<CrossProcessReadinessChild> children)
        => string.Join(
            " ",
            children.Select(child =>
                $"{child.Label}(ready={File.Exists(child.ReadyPath)},result={File.Exists(child.ResultPath)})"));

    private static async Task<string> StopAndReadChildEvidenceAsync(
        string operation,
        string stage,
        IReadOnlyList<CrossProcessReadinessChild> children)
    {
        await Task.WhenAll(children.Select(StopChildProcessAsync));
        var evidence = await Task.WhenAll(children.Select(child => ReadChildEvidenceAsync(operation, stage, child)));
        return string.Join(Environment.NewLine, evidence);
    }

    private static async Task StopChildProcessAsync(CrossProcessReadinessChild child)
    {
        try
        {
            child.Ownership.TerminateProcessTree();
        }
        catch (InvalidOperationException) when (child.Process.HasExited)
        {
        }

        try
        {
            await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task<string> ReadChildEvidenceAsync(
        string operation,
        string stage,
        CrossProcessReadinessChild child)
    {
        if (!child.Process.HasExited)
        {
            return $"{operation}/{stage}/{child.Label}: pid={child.Process.Id} state=still-running exit=<unavailable> ready={File.Exists(child.ReadyPath)} result={File.Exists(child.ResultPath)} stdout=<unavailable> stderr=<unavailable>";
        }

        using var fallbackCancellation = child.EvidenceCancellation is null ? new CancellationTokenSource() : null;
        var cancellation = child.EvidenceCancellation ?? fallbackCancellation!;
        var outputTask = child.StandardOutputTask ?? ReadChildStreamAsync(child.Process.ReadStandardOutputToEndAsync(cancellation.Token));
        var errorTask = child.StandardErrorTask ?? ReadChildStreamAsync(child.Process.ReadStandardErrorToEndAsync(cancellation.Token));
        var drainTask = Task.WhenAll(outputTask, errorTask);
        try
        {
            await drainTask.WaitAsync(_childEvidenceReadTimeout);
        }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            await drainTask.WaitAsync(_childEvidenceReadTimeout);
        }

        return $"{operation}/{stage}/{child.Label}: pid={child.Process.Id} state=exited exit={child.Process.ExitCode} ready={File.Exists(child.ReadyPath)} result={File.Exists(child.ResultPath)} stdout={GetChildStreamEvidence(outputTask)} stderr={GetChildStreamEvidence(errorTask)}";
    }

    private static async Task<string> ReadChildStreamAsync(Task<string> readTask)
    {
        try
        {
            return await readTask;
        }
        catch (OperationCanceledException)
        {
            return "<timed-out>";
        }
        catch (IOException)
        {
            return "<unavailable>";
        }
        catch (ObjectDisposedException)
        {
            return "<unavailable>";
        }
    }

    private static string GetChildStreamEvidence(Task<string> streamTask)
        => streamTask.IsCompletedSuccessfully ? BoundChildEvidence(streamTask.Result) : "<unavailable>";

    private static string BoundChildEvidence(string evidence)
    {
        if (string.IsNullOrEmpty(evidence))
        {
            return "<empty>";
        }

        return evidence.Length <= MaximumChildEvidenceCharacters
            ? evidence
            : "<truncated>" + evidence[^MaximumChildEvidenceCharacters..];
    }
}
