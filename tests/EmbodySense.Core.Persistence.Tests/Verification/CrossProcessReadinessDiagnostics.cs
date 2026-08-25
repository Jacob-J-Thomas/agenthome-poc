using System.Diagnostics;
using System.Globalization;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal static class CrossProcessReadinessDiagnostics
{
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
        await Task.WhenAll(children.Select(child => StopChildProcessAsync(child.Process)));
        var evidence = await Task.WhenAll(children.Select(child => ReadChildEvidenceAsync(operation, stage, child)));
        return string.Join(Environment.NewLine, evidence);
    }

    private static async Task StopChildProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
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

        var outputTask = child.Process.StandardOutput.ReadToEndAsync();
        var errorTask = child.Process.StandardError.ReadToEndAsync();
        await Task.WhenAll(outputTask, errorTask);
        return $"{operation}/{stage}/{child.Label}: pid={child.Process.Id} state=exited exit={child.Process.ExitCode} ready={File.Exists(child.ReadyPath)} result={File.Exists(child.ResultPath)} stdout={BoundChildEvidence(outputTask.Result)} stderr={BoundChildEvidence(errorTask.Result)}";
    }

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
