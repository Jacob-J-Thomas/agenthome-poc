using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Clients.Capabilities;

/// <summary>Hosts executable artifacts behind an explicit platform isolation boundary and bounded process protocol.</summary>
public sealed class IsolatedCapabilityExecutableHost : ICapabilityExecutableHost, IDisposable
{
    private const int MaximumInputBytes = 16 * 1024 * 1024;
    private readonly ICapabilityProcessIsolationBoundary _isolationBoundary;
    private readonly ICapabilityExecutableArtifactResolver _artifactResolver;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _concurrencyGates = new(StringComparer.Ordinal);
    private readonly IAuditLog _auditLog;

    /// <summary>Creates a host that fails closed unless a trusted isolation boundary is supplied.</summary>
    public IsolatedCapabilityExecutableHost(IAuditLog auditLog, ICapabilityProcessIsolationBoundary? isolationBoundary = null, ICapabilityExecutableArtifactResolver? artifactResolver = null)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        _auditLog = auditLog;
        _isolationBoundary = isolationBoundary ?? DenyingCapabilityProcessIsolationBoundary.Instance;
        _artifactResolver = artifactResolver ?? DenyingCapabilityExecutableArtifactResolver.Instance;
    }

    /// <inheritdoc />
    public CapabilityExecutableAvailability CheckAvailability(CapabilityArtifactManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!CapabilityArtifactManifestValidator.Validate(manifest).IsValid)
        {
            return new CapabilityExecutableAvailability(CapabilityExecutableAvailabilityStatus.Incompatible, "The executable artifact manifest is invalid.");
        }
        if (manifest.Descriptor.Requirements.Secrets.Count > 0)
        {
            return new CapabilityExecutableAvailability(CapabilityExecutableAvailabilityStatus.Unavailable, "Secret-requiring artifacts remain unavailable until governed secret brokerage exists.");
        }
        if (!OperatingSystem.IsWindows())
        {
            return new CapabilityExecutableAvailability(CapabilityExecutableAvailabilityStatus.Unavailable, "This platform has no configured handle-bound executable launch seam; artifact execution remains unavailable.");
        }
        var availability = _isolationBoundary.CheckAvailability(manifest);
        return availability with { Detail = SafeDetail(availability.Detail) };
    }

    /// <inheritdoc />
    public async Task<CapabilityExecutableInvocationResult> InvokeAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var target = invocation.Manifest?.Descriptor?.Id?.Value ?? "capability-artifact";
        var intentMetadata = Metadata(invocation, result: null);
        await _auditLog.AppendAsync(AuditEvent.Create(AuditSchema.Actors.CapabilityHost, AuditSchema.Actions.CapabilityExecutableInvocation, target, AuditSchema.Outcomes.Requested, "Capability executable invocation requested without persisting input, output, environment, or paths.", intentMetadata), CancellationToken.None);
        CapabilityExecutableInvocationResult result;
        try
        {
            result = await InvokeCoreAsync(invocation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = Result(CapabilityExecutableInvocationStatus.Cancelled, invocation.OperationId, null, "The invocation was cancelled before isolated process execution completed.", null, Stopwatch.GetTimestamp());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            result = Result(CapabilityExecutableInvocationStatus.Unavailable, invocation.OperationId, null, "The isolated process boundary could not produce a safe invocation result.", null, Stopwatch.GetTimestamp());
        }
        var outcome = result.Status == CapabilityExecutableInvocationStatus.Succeeded ? AuditSchema.Outcomes.Succeeded : result.Status == CapabilityExecutableInvocationStatus.Cancelled ? AuditSchema.Outcomes.Rejected : AuditSchema.Outcomes.Failed;
        await _auditLog.AppendAsync(AuditEvent.Create(AuditSchema.Actors.CapabilityHost, AuditSchema.Actions.CapabilityExecutableInvocation, target, outcome, result.Diagnostic.Length == 0 ? "Capability executable invocation completed." : result.Diagnostic, Metadata(invocation, result)), CancellationToken.None);
        return result;
    }

    private async Task<CapabilityExecutableInvocationResult> InvokeCoreAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        if (!CapabilityArtifactManifestValidator.Validate(invocation.Manifest).IsValid || !CapabilityArtifactManifestValidator.IsOperationId(invocation.OperationId) || Encoding.UTF8.GetByteCount(invocation.InputJson) > MaximumInputBytes || !IsJson(invocation.InputJson))
        {
            return Result(CapabilityExecutableInvocationStatus.Invalid, invocation.OperationId, null, "The executable invocation request is invalid.", null, startedAt);
        }

        var availability = CheckAvailability(invocation.Manifest);
        if (availability.Status != CapabilityExecutableAvailabilityStatus.Available)
        {
            return Result(CapabilityExecutableInvocationStatus.Unavailable, invocation.OperationId, null, SafeDetail(availability.Detail), null, startedAt);
        }

        CapabilityExecutableArtifactResolution resolved;
        try
        {
            resolved = await _artifactResolver.ResolveAsync(invocation, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Result(CapabilityExecutableInvocationStatus.Unavailable, invocation.OperationId, null, "The immutable artifact lease cannot be resolved safely.", null, startedAt);
        }
        await using var lease = resolved.Lease;
        if (resolved.Status != CapabilityExecutableAvailabilityStatus.Available || lease is null)
        {
            return Result(CapabilityExecutableInvocationStatus.Unavailable, invocation.OperationId, null, SafeDetail(resolved.Detail), null, startedAt);
        }

        string root;
        string executablePath;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lease.ArtifactRoot));
            executablePath = Path.GetFullPath(lease.ExecutablePath);
            var expectedExecutablePath = Path.GetFullPath(Path.Combine(root, invocation.Manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(executablePath, expectedExecutablePath, comparison) || !executablePath.StartsWith(root + Path.DirectorySeparatorChar, comparison) || !File.Exists(executablePath) || HasLink(root, executablePath) || lease.ExecutableHandle.IsInvalid || lease.ExecutableHandle.IsClosed || !invocation.Manifest.Checksum.FixedTimeEquals(lease.ArtifactDigest) || lease.ActivationRevision != invocation.ExpectedActivationRevision)
            {
                return Result(CapabilityExecutableInvocationStatus.Invalid, invocation.OperationId, null, "The executable lease does not match the requested proved activation.", null, startedAt);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            return Result(CapabilityExecutableInvocationStatus.Invalid, invocation.OperationId, null, "The immutable artifact lease cannot be validated safely.", null, startedAt);
        }

        var gateKey = invocation.Manifest.Descriptor.Id.Value + "@" + invocation.Manifest.Checksum.Value;
        var gate = _concurrencyGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(invocation.Manifest.Descriptor.ResourceLimits.MaxConcurrency, invocation.Manifest.Descriptor.ResourceLimits.MaxConcurrency));
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(CapabilityExecutableInvocationStatus.Cancelled, invocation.OperationId, null, "The invocation was cancelled before process admission.", null, startedAt);
        }
        try
        {
            return await InvokeProcessAsync(invocation, lease, root, executablePath, startedAt, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Releases concurrency gates after all invocations have completed.</summary>
    public void Dispose()
    {
        // Process-wide leases deliberately outlive one surface host so separately constructed projections cannot exceed MaxConcurrency.
    }

    private async Task<CapabilityExecutableInvocationResult> InvokeProcessAsync(CapabilityExecutableInvocation invocation, ICapabilityExecutableArtifactLease artifactLease, string root, string executablePath, long startedAt, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment.Clear();
        startInfo.Environment["EMBODYSENSE_CAPABILITY_ID"] = invocation.Manifest.Descriptor.Id.Value;
        startInfo.Environment["EMBODYSENSE_CAPABILITY_VERSION"] = invocation.Manifest.Descriptor.Version.Value;
        foreach (var argument in invocation.Manifest.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = null;
        try
        {
            process = await artifactLease.ExecuteWithLaunchFenceAsync(
                _ => Task.FromResult(_isolationBoundary.StartIsolated(startInfo, invocation.Manifest, artifactLease)),
                cancellationToken);
            if (process is null)
            {
                return Result(CapabilityExecutableInvocationStatus.Unavailable, invocation.OperationId, null, "The capability lifecycle changed before isolated process launch.", null, startedAt);
            }
            var budget = new CapabilityProcessOutputBudget(invocation.Manifest.Descriptor.ResourceLimits.MaxOutputBytes);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, budget);
            var stderrTask = ReadBoundedAsync(process.StandardError, budget);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(invocation.Manifest.Descriptor.ResourceLimits.MaxExecutionMilliseconds));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                var input = Encoding.UTF8.GetBytes(invocation.InputJson + "\n");
                await process.StandardInput.BaseStream.WriteAsync(input, combined.Token);
                await process.StandardInput.BaseStream.FlushAsync(combined.Token);
                process.StandardInput.Close();
                var exitTask = process.WaitForExitAsync(combined.Token);
                var first = await Task.WhenAny(exitTask, stdoutTask, stderrTask);
                if (first == stdoutTask)
                {
                    _ = await stdoutTask;
                }
                else if (first == stderrTask)
                {
                    _ = await stderrTask;
                }
                await exitTask;
                var stdout = await stdoutTask.WaitAsync(combined.Token);
                var stderr = await stderrTask.WaitAsync(combined.Token);
                if (process.ExitCode != 0)
                {
                    return Result(CapabilityExecutableInvocationStatus.Crashed, invocation.OperationId, null, CapabilityProcessDiagnosticRedactor.Redact(stderr), process.ExitCode, startedAt);
                }
                if (!IsJson(stdout))
                {
                    return Result(CapabilityExecutableInvocationStatus.MalformedResult, invocation.OperationId, null, CapabilityProcessDiagnosticRedactor.Redact(stderr), process.ExitCode, startedAt);
                }
                return Result(CapabilityExecutableInvocationStatus.Succeeded, invocation.OperationId, CapabilityProcessDiagnosticRedactor.Redact(stdout), CapabilityProcessDiagnosticRedactor.Redact(stderr), process.ExitCode, startedAt);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                var status = cancellationToken.IsCancellationRequested ? CapabilityExecutableInvocationStatus.Cancelled : CapabilityExecutableInvocationStatus.TimedOut;
                return Result(status, invocation.OperationId, null, status == CapabilityExecutableInvocationStatus.Cancelled ? "The isolated process tree was cancelled and terminated." : "The isolated process tree exceeded its time bound and was terminated.", process.HasExited ? process.ExitCode : null, startedAt);
            }
            catch (CapabilityProcessOutputLimitException)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                return Result(CapabilityExecutableInvocationStatus.OutputLimitExceeded, invocation.OperationId, null, "Process output exceeded its declared bound and the process tree was terminated.", process.ExitCode, startedAt);
            }
        }
        catch (CapabilityProcessOutputLimitException)
        {
            if (process is not null)
            {
                Kill(process);
            }
            return Result(CapabilityExecutableInvocationStatus.OutputLimitExceeded, invocation.OperationId, null, "Process output exceeded its declared bound and the process tree was terminated.", null, startedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            if (process is not null)
            {
                Kill(process);
            }
            return Result(CapabilityExecutableInvocationStatus.Unavailable, invocation.OperationId, null, "The isolated process boundary could not produce a safe invocation result.", null, startedAt);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CapabilityProcessOutputBudget budget)
    {
        var builder = new StringBuilder();
        var buffer = new char[4_096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0)
            {
                break;
            }
            budget.Account(Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count)));
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }

    private static bool HasLink(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string SafeDetail(string? detail)
    {
        var redacted = CapabilityProcessDiagnosticRedactor.Redact(detail ?? string.Empty);
        return redacted.Length == 0 ? "The platform isolation boundary is unavailable." : redacted;
    }

    private static CapabilityExecutableInvocationResult Result(CapabilityExecutableInvocationStatus status, string operationId, string? outputJson, string diagnostic, int? exitCode, long startedAt) => new(status, operationId, outputJson, diagnostic, exitCode, Stopwatch.GetElapsedTime(startedAt));

    private static IReadOnlyDictionary<string, object?> Metadata(CapabilityExecutableInvocation invocation, CapabilityExecutableInvocationResult? result)
    {
        return new Dictionary<string, object?>
        {
            ["operationId"] = invocation.OperationId,
            ["artifactDigest"] = invocation.Manifest?.Checksum?.Value,
            ["artifactVersion"] = invocation.Manifest?.Descriptor?.Version?.Value,
            ["implementationId"] = invocation.Manifest?.Descriptor?.Implementation?.ImplementationId,
            ["status"] = result?.Status.ToString(),
            ["exitCode"] = result?.ExitCode,
            ["durationMilliseconds"] = result?.Duration.TotalMilliseconds
        };
    }
}
