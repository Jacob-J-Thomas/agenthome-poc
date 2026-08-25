using System.Diagnostics;
using System.Globalization;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal static class CrossProcessReadinessDiagnostics
{
    private static readonly TimeSpan _childEvidenceReadTimeout = TimeSpan.FromSeconds(5);

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
        TimeSpan timeout)
    {
        Validate(operation, children, timeout);
        var wait = Stopwatch.StartNew();
        while (true)
        {
            var exited = children.Where(child => child.Process.HasExited).ToArray();
            var unsuccessful = exited.Where(child => child.Process.ExitCode != 0 || !File.Exists(child.ResultPath)).ToArray();
            if (unsuccessful.Length > 0)
            {
                var evidence = await StopAndReadChildEvidenceAsync(operation, stage + "-exit", children);
                Assert.Fail($"Cross-process {operation} child failed during {stage}. {DescribeMarkers(children)}{Environment.NewLine}{evidence}");
            }

            if (exited.Length == children.Count)
            {
                return;
            }

            if (wait.Elapsed >= timeout)
            {
                var evidence = await StopAndReadChildEvidenceAsync(operation, stage + "-timeout", children);
                Assert.Fail($"Cross-process {operation} children did not complete {stage} within {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds. {DescribeMarkers(children)}{Environment.NewLine}{evidence}");
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

        var outputReader = child.Process.StandardOutput;
        var errorReader = child.Process.StandardError;
        using var cancellation = new CancellationTokenSource();
        var outputTask = ReadChildStreamAsync(outputReader, cancellation.Token);
        var errorTask = ReadChildStreamAsync(errorReader, cancellation.Token);
        var drainTask = Task.WhenAll(outputTask, errorTask);
        try
        {
            await drainTask.WaitAsync(_childEvidenceReadTimeout);
        }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            outputReader.Dispose();
            errorReader.Dispose();
        }

        return $"{operation}/{stage}/{child.Label}: pid={child.Process.Id} state=exited exit={child.Process.ExitCode} ready={File.Exists(child.ReadyPath)} result={File.Exists(child.ResultPath)} stdout={GetChildStreamEvidence(outputTask)} stderr={GetChildStreamEvidence(errorTask)}";
    }

    private static async Task<string> ReadChildStreamAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadToEndAsync(cancellationToken);
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
