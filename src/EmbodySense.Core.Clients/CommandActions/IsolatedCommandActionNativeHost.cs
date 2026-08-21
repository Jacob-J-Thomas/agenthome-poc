using System.Diagnostics;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Clients.Capabilities;
using EmbodySense.Core.Clients.CommandActions.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Secrets;
using EmbodySense.Core.Common.Secrets.Redaction;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Clients.CommandActions;

/// <summary>Executes exact registered command templates through immutable artifact leases and a pre-launch isolation boundary.</summary>
public sealed class IsolatedCommandActionNativeHost : ICommandActionNativeHost
{
    private readonly ICapabilityExecutableArtifactResolver _artifactResolver;
    private readonly ICommandActionConcurrencyGate _concurrencyGate;
    private readonly ICommandActionEvidenceStore _evidenceStore;
    private readonly ICommandActionProcessIsolationBoundary _isolationBoundary;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a host that remains unavailable unless both artifact resolution and native isolation are configured.</summary>
    public IsolatedCommandActionNativeHost(
        ICommandActionEvidenceStore evidenceStore,
        ICapabilityExecutableArtifactResolver? artifactResolver = null,
        ICommandActionProcessIsolationBoundary? isolationBoundary = null,
        ICommandActionConcurrencyGate? concurrencyGate = null,
        TimeProvider? timeProvider = null)
    {
        _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        _artifactResolver = artifactResolver ?? DenyingCapabilityExecutableArtifactResolver.Instance;
        _isolationBoundary = isolationBoundary ?? DenyingCommandActionProcessIsolationBoundary.Instance;
        _concurrencyGate = concurrencyGate ?? DenyingCommandActionConcurrencyGate.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public CapabilityExecutableAvailability CheckAvailability(CommandActionRegistration registration)
    {
        if (CommandActionRegistrationContract.Validate(registration) is not null)
        {
            return Availability(CapabilityExecutableAvailabilityStatus.Incompatible, "The command registration is invalid.");
        }
        if (registration.Template.RequiresCredentialChannel)
        {
            return Availability(CapabilityExecutableAvailabilityStatus.Unavailable, "The shared one-shot credential host channel is not available.");
        }
        if (registration.Template.Slots.Any(slot => slot.Kind == CommandActionSlotKind.WorkspaceRelativeTarget))
        {
            return Availability(CapabilityExecutableAvailabilityStatus.Unavailable, "No retained workspace-target launch boundary is configured.");
        }
        if (!_concurrencyGate.IsAvailable)
        {
            return Availability(CapabilityExecutableAvailabilityStatus.Unavailable, "Durable cross-process command concurrency admission is unavailable.");
        }
        try
        {
            var availability = _isolationBoundary.CheckAvailability(registration);
            return availability.Status is CapabilityExecutableAvailabilityStatus.Available or CapabilityExecutableAvailabilityStatus.Incompatible or CapabilityExecutableAvailabilityStatus.Unavailable
                ? Availability(availability.Status, availability.Detail)
                : Availability(CapabilityExecutableAvailabilityStatus.Unavailable, "The isolation adapter returned an unsupported availability posture.");
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return Availability(CapabilityExecutableAvailabilityStatus.Unavailable, "The registered isolation adapter is unavailable.");
        }
    }

    /// <inheritdoc />
    public async Task<CapabilityExecutableAvailability> CheckExecutableAvailabilityAsync(CommandActionRegistration registration, CancellationToken cancellationToken = default)
    {
        var platform = CheckAvailability(registration);
        if (platform.Status != CapabilityExecutableAvailabilityStatus.Available)
        {
            return platform;
        }

        try
        {
            var operationId = "command-catalog-" + registration.Template.ContentHash[..32];
            var resolution = await _artifactResolver.ResolveAsync(ResolutionInvocation(registration, "{}", operationId), cancellationToken).ConfigureAwait(false);
            await using var lease = resolution.Lease;
            if (resolution.Status != CapabilityExecutableAvailabilityStatus.Available
                || lease is null
                || !TryValidateLease(registration, lease, out _, out _))
            {
                return Availability(CapabilityExecutableAvailabilityStatus.Unavailable, "The exact activated command artifact is unavailable.");
            }
            await using var launchFence = await lease.AcquireLaunchFenceAsync(cancellationToken).ConfigureAwait(false);
            return launchFence is null
                ? Availability(CapabilityExecutableAvailabilityStatus.Unavailable, "The exact activated command artifact is no longer current.")
                : Availability(CapabilityExecutableAvailabilityStatus.Available, "The exact command artifact and isolation controls are available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsPrelaunchFailure(exception))
        {
            return Availability(CapabilityExecutableAvailabilityStatus.Unavailable, "The exact activated command artifact could not be proved.");
        }
    }

    /// <inheritdoc />
    public async Task<CommandActionNativePreparation?> PrepareAsync(
        CommandActionRegistration registration,
        GovernedActuatorInputEvidence input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(input);
        if (CheckAvailability(registration).Status != CapabilityExecutableAvailabilityStatus.Available
            || !CommandActionInputContract.TryMaterialize(input.CanonicalJson, registration.Template, out var materialized, out _))
        {
            return null;
        }
        try
        {
            var invocation = ResolutionInvocation(registration, input.CanonicalJson, "command-prepare-" + materialized!.InputFingerprint[..32]);
            var resolution = await _artifactResolver.ResolveAsync(invocation, cancellationToken).ConfigureAwait(false);
            await using var lease = resolution.Lease;
            if (resolution.Status != CapabilityExecutableAvailabilityStatus.Available
                || lease is null
                || !TryValidateLease(registration, lease, out _, out _))
            {
                return null;
            }
            var target = CommandActionFingerprint.Compute(
                "embodysense.command-action-target.v1",
                registration.Template.TemplateId,
                registration.Template.ContentHash,
                registration.Template.ArtifactDigest.Value,
                registration.Template.ActivationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                materialized.InputFingerprint);
            var precondition = CommandActionFingerprint.Compute(
                "embodysense.command-action-precondition.v1",
                target,
                registration.Manifest.Checksum.Value,
                registration.Template.ContentHash);
            var evidence = CommandActionEvidenceContract.CreatePreparation(
                registration.Template,
                materialized.InputFingerprint,
                target,
                precondition,
                UtcNow());
            await _evidenceStore.RetainPreparationAsync(evidence, cancellationToken).ConfigureAwait(false);
            return new CommandActionNativePreparation(evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsPrelaunchFailure(exception))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsPreparationCurrentAsync(
        CommandActionRegistration registration,
        GovernedActuatorInputEvidence input,
        string targetFingerprint,
        string preconditionEvidenceHash,
        string beforeEvidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(input);
        if (CommandActionRegistrationContract.Validate(registration) is not null
            || !CommandActionFingerprint.IsCanonicalSha256(targetFingerprint)
            || !CommandActionFingerprint.IsCanonicalSha256(preconditionEvidenceHash)
            || !CommandActionFingerprint.IsEvidenceIdentifier(beforeEvidenceId)
            || !CommandActionInputContract.TryMaterialize(input.CanonicalJson, registration.Template, out var materialized, out _))
        {
            return false;
        }
        var before = await _evidenceStore.ReadPreparationAsync(beforeEvidenceId, cancellationToken).ConfigureAwait(false);
        if (CommandActionEvidenceContract.ValidatePreparation(before) is not null
            || !string.Equals(before!.TargetFingerprint, targetFingerprint, StringComparison.Ordinal)
            || !string.Equals(before.PreconditionEvidenceHash, preconditionEvidenceHash, StringComparison.Ordinal)
            || !string.Equals(before.InputFingerprint, materialized!.InputFingerprint, StringComparison.Ordinal)
            || !string.Equals(before.TemplateHash, registration.Template.ContentHash, StringComparison.Ordinal)
            || !before.ArtifactDigest.FixedTimeEquals(registration.Template.ArtifactDigest)
            || before.ActivationRevision != registration.Template.ActivationRevision
            || CheckAvailability(registration).Status != CapabilityExecutableAvailabilityStatus.Available)
        {
            return false;
        }
        var resolution = await _artifactResolver.ResolveAsync(
            ResolutionInvocation(registration, input.CanonicalJson, "command-prepare-check-" + materialized.InputFingerprint[..32]),
            cancellationToken).ConfigureAwait(false);
        await using var lease = resolution.Lease;
        return resolution.Status == CapabilityExecutableAvailabilityStatus.Available
            && lease is not null
            && TryValidateLease(registration, lease, out _, out _);
    }

    /// <inheritdoc />
    public async Task<CommandActionNativeExecutionResult> ExecuteAsync(
        CommandActionNativeExecutionRequest request,
        ICommandActionNativeLaunchBoundary launchBoundary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(launchBoundary);
        if (!TryValidateExecutionRequest(request, out var materialized)
            || CheckAvailability(request.Registration).Status != CapabilityExecutableAvailabilityStatus.Available)
        {
            return NotStarted(CommandActionDispatchNotStartedReason.InvalidRequest);
        }

        CommandActionPreparationEvidence? before;
        try
        {
            before = await _evidenceStore.ReadPreparationAsync(request.BeforeEvidenceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsPrelaunchFailure(exception))
        {
            return NotStarted(CommandActionDispatchNotStartedReason.PreparationUnavailable);
        }
        if (!MatchesPreparation(before, request, materialized!))
        {
            return NotStarted(CommandActionDispatchNotStartedReason.PreparationUnavailable);
        }

        ICapabilityExecutableArtifactLease? lease = null;
        IAsyncDisposable? concurrencyLease = null;
        var crossingBoundary = false;
        var pendingReason = CommandActionDispatchNotStartedReason.ArtifactUnavailable;
        try
        {
            var resolution = await _artifactResolver.ResolveAsync(
                ResolutionInvocation(request.Registration, request.Input.CanonicalJson, request.IdempotencyOperationId),
                cancellationToken).ConfigureAwait(false);
            lease = resolution.Lease;
            if (resolution.Status != CapabilityExecutableAvailabilityStatus.Available
                || lease is null
                || !TryValidateLease(request.Registration, lease, out _, out _))
            {
                return NotStarted(CommandActionDispatchNotStartedReason.ArtifactUnavailable);
            }

            pendingReason = CommandActionDispatchNotStartedReason.ConcurrencyUnavailable;
            concurrencyLease = await _concurrencyGate.TryAcquireAsync(
                request.Registration.Template.ContentHash,
                request.Registration.Template.Isolation.MaxConcurrency,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            if (concurrencyLease is null)
            {
                return NotStarted(CommandActionDispatchNotStartedReason.ConcurrencyUnavailable);
            }

            var exactLease = lease;
            pendingReason = CommandActionDispatchNotStartedReason.LaunchAuthorityUnavailable;
            var result = await lease.ExecuteWithLaunchFenceAsync(
                async boundaryToken =>
                {
                    if (!TryValidateLease(request.Registration, exactLease, out var launchRoot, out var launchExecutablePath))
                    {
                        return NotStarted(CommandActionDispatchNotStartedReason.ArtifactUnavailable);
                    }
                    crossingBoundary = true;
                    var outcome = await launchBoundary.CrossAsync(
                        token => LaunchAndObserveAsync(request, materialized!, exactLease, launchRoot, launchExecutablePath, token),
                        boundaryToken).ConfigureAwait(false);
                    return new CommandActionNativeExecutionResult(CommandActionNativeExecutionStatus.OutcomeObserved, outcome);
                },
                cancellationToken).ConfigureAwait(false);
            return result ?? NotStarted(CommandActionDispatchNotStartedReason.LaunchAuthorityUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!crossingBoundary && IsPrelaunchFailure(exception))
        {
            return NotStarted(pendingReason);
        }
        finally
        {
            if (concurrencyLease is not null)
            {
                await concurrencyLease.DisposeAsync().ConfigureAwait(false);
            }
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<CommandActionReconciliationProbeResult> ProbeAsync(
        CommandActionNativeExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryValidateExecutionRequest(request, out var materialized))
        {
            return new CommandActionReconciliationProbeResult(CommandActionReconciliationPosture.Indeterminate, null);
        }
        var before = await _evidenceStore.ReadPreparationAsync(request.BeforeEvidenceId, cancellationToken).ConfigureAwait(false);
        if (!MatchesPreparation(before, request, materialized!))
        {
            return new CommandActionReconciliationProbeResult(CommandActionReconciliationPosture.Indeterminate, null);
        }
        var outcome = await _evidenceStore.ReadOutcomeByOperationAsync(
            request.IdempotencyOperationId,
            request.EffectGeneration,
            cancellationToken).ConfigureAwait(false);
        if (!MatchesOutcome(outcome, request, materialized!))
        {
            return new CommandActionReconciliationProbeResult(CommandActionReconciliationPosture.Indeterminate, null);
        }
        return new CommandActionReconciliationProbeResult(
            CommandActionReconciliationPosture.OutcomeObserved,
            new CommandActionNativeOutcome(
                outcome!.Outcome == CommandActionOutcomeKind.Succeeded ? CommandActionNativeOutcomeKind.Succeeded : CommandActionNativeOutcomeKind.Failed,
                outcome.EvidenceId));
    }

    private async Task<CommandActionNativeOutcome> LaunchAndObserveAsync(
        CommandActionNativeExecutionRequest request,
        CommandActionMaterialization materialized,
        ICapabilityExecutableArtifactLease lease,
        string root,
        string executablePath,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        startInfo.Environment.Clear();
        foreach (var entry in materialized.Environment)
        {
            startInfo.Environment.Add(entry.Key, entry.Value);
        }
        foreach (var argument in materialized.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var launch = _isolationBoundary.StartIsolated(startInfo, request.Registration, lease);
        if (launch.Status == CommandActionIsolatedLaunchStatus.RejectedBeforeStart && launch.Process is null)
        {
            return await RetainOutcomeAsync(
                request, materialized, CommandActionOutcomeKind.IsolationRejected, CommandActionTerminationPosture.NotStarted,
                null, null, null, 0, 0, startedAt).ConfigureAwait(false);
        }
        if (launch.Status != CommandActionIsolatedLaunchStatus.Started || launch.Process is null)
        {
            throw new InvalidOperationException("The isolation adapter returned an incoherent launch result.");
        }

        using var process = launch.Process;
        var budget = new CommandActionOutputBudget(request.Registration.Template.Isolation.MaxOutputBytes);
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, budget, standardOutput: true);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, budget, standardOutput: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.Registration.Template.Isolation.MaxExecutionMilliseconds));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            if (materialized.StandardInputUtf8 is { } standardInput)
            {
                var bytes = Encoding.UTF8.GetBytes(standardInput);
                await process.StandardInput.BaseStream.WriteAsync(bytes, lifetime.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
            process.StandardInput.Close();

            var exit = process.WaitForExitAsync(CancellationToken.None);
            var cancellationSignal = WaitForCancellationAsync(lifetime.Token);
            while (!exit.IsCompleted)
            {
                var candidates = new List<Task> { exit, cancellationSignal };
                if (!stdout.IsCompleted)
                {
                    candidates.Add(stdout);
                }
                if (!stderr.IsCompleted)
                {
                    candidates.Add(stderr);
                }
                var signal = await Task.WhenAny(candidates).ConfigureAwait(false);
                if (signal == stdout && stdout.IsFaulted || signal == stderr && stderr.IsFaulted)
                {
                    await signal.ConfigureAwait(false);
                }
                if (signal == cancellationSignal)
                {
                    throw new OperationCanceledException(lifetime.Token);
                }
            }
            await exit.ConfigureAwait(false);
            var streams = Task.WhenAll(stdout, stderr);
            if (await Task.WhenAny(streams, cancellationSignal).ConfigureAwait(false) != streams)
            {
                throw new OperationCanceledException(lifetime.Token);
            }
            var captured = await streams.ConfigureAwait(false);
            var stdoutBytes = captured[0];
            var stderrBytes = captured[1];
            var proofTimeout = TimeSpan.FromMilliseconds(request.Registration.Template.Isolation.MaxTerminationMilliseconds);
            if (!await _isolationBoundary.ProveProcessTreeTerminalAsync(process, CancellationToken.None).WaitAsync(proofTimeout).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The isolation adapter could not prove the admitted process tree terminal.");
            }
            return await RetainExitedOutcomeAsync(request, materialized, process.ExitCode, stdoutBytes, stderrBytes, budget, startedAt).ConfigureAwait(false);
        }
        catch (CommandActionOutputLimitException)
        {
            return await TerminateAndRetainAsync(request, materialized, process, stdout, stderr, budget, CommandActionOutcomeKind.OutputLimitExceeded, startedAt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            var kind = cancellationToken.IsCancellationRequested ? CommandActionOutcomeKind.Cancelled : CommandActionOutcomeKind.TimedOut;
            return await TerminateAndRetainAsync(request, materialized, process, stdout, stderr, budget, kind, startedAt).ConfigureAwait(false);
        }
    }

    private async Task<CommandActionNativeOutcome> RetainExitedOutcomeAsync(
        CommandActionNativeExecutionRequest request,
        CommandActionMaterialization materialized,
        int exitCode,
        byte[] stdoutBytes,
        byte[] stderrBytes,
        CommandActionOutputBudget budget,
        long startedAt)
    {
        string? stdout;
        string? stderr;
        try
        {
            var strict = new UTF8Encoding(false, true);
            stdout = strict.GetString(stdoutBytes);
            stderr = strict.GetString(stderrBytes);
        }
        catch (DecoderFallbackException)
        {
            return await RetainOutcomeAsync(
                request, materialized, CommandActionOutcomeKind.InvalidEncoding, CommandActionTerminationPosture.Exited,
                exitCode, null, null, budget.StandardOutputBytes, budget.StandardErrorBytes, startedAt).ConfigureAwait(false);
        }

        using var redactionScope = CreateRedactionScope(request, out var scopeSummary);
        var redactedStderr = RedactOutput(stderr, redactionScope, scopeSummary);
        if (exitCode != 0)
        {
            var redactedStdout = RedactOutput(stdout, redactionScope, scopeSummary);
            return await RetainOutcomeAsync(
                request, materialized, CommandActionOutcomeKind.NonZeroExit, CommandActionTerminationPosture.Exited,
                exitCode, redactedStdout.Value, redactedStderr.Value, budget.StandardOutputBytes, budget.StandardErrorBytes, startedAt, Combine(redactedStdout.Summary, redactedStderr.Summary)).ConfigureAwait(false);
        }
        if (!GovernedActuatorInputContract.TryCanonicalize(stdout, out var unredactedCanonical, out _))
        {
            var redactedStdout = RedactOutput(stdout, redactionScope, scopeSummary);
            return await RetainOutcomeAsync(
                request, materialized, CommandActionOutcomeKind.MalformedResult, CommandActionTerminationPosture.Exited,
                exitCode, redactedStdout.Value, redactedStderr.Value, budget.StandardOutputBytes, budget.StandardErrorBytes, startedAt, Combine(redactedStdout.Summary, redactedStderr.Summary)).ConfigureAwait(false);
        }
        var structuredOutput = CommandActionJsonOutputRedactor.Redact(unredactedCanonical!.CanonicalJson, redactionScope, scopeSummary);
        var redactionSummary = Combine(structuredOutput.Summary, redactedStderr.Summary);
        if (!GovernedActuatorInputContract.TryCanonicalize(structuredOutput.Value, out var canonical, out _)
            || canonical!.CanonicalJson.Length > CommandActionContractLimits.MaxRetainedOutputCharacters)
        {
            return await RetainOutcomeAsync(
                request, materialized, CommandActionOutcomeKind.MalformedResult, CommandActionTerminationPosture.Exited,
                exitCode, structuredOutput.Value, redactedStderr.Value, budget.StandardOutputBytes, budget.StandardErrorBytes, startedAt, redactionSummary).ConfigureAwait(false);
        }
        return await RetainOutcomeAsync(
            request, materialized, CommandActionOutcomeKind.Succeeded, CommandActionTerminationPosture.Exited,
            exitCode, canonical.CanonicalJson, redactedStderr.Value, budget.StandardOutputBytes, budget.StandardErrorBytes, startedAt, redactionSummary).ConfigureAwait(false);
    }

    private async Task<CommandActionNativeOutcome> TerminateAndRetainAsync(
        CommandActionNativeExecutionRequest request,
        CommandActionMaterialization materialized,
        Process process,
        Task<byte[]> standardOutput,
        Task<byte[]> standardError,
        CommandActionOutputBudget budget,
        CommandActionOutcomeKind outcome,
        long startedAt)
    {
        var timeout = TimeSpan.FromMilliseconds(request.Registration.Template.Isolation.MaxTerminationMilliseconds);
        if (!await _isolationBoundary.TerminateAndProveProcessTreeAsync(process, timeout, CancellationToken.None).WaitAsync(timeout).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The isolation adapter could not prove the admitted process tree terminal after termination.");
        }
        try
        {
            await Task.WhenAll(standardOutput, standardError).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (CommandActionOutputLimitException) when (outcome == CommandActionOutcomeKind.OutputLimitExceeded)
        {
            // The exact bounded counter is the retained overflow evidence; the peer stream was still drained after termination.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException)
        {
            throw new InvalidOperationException("The terminated process streams could not be closed within the registered bound.", exception);
        }
        return await RetainOutcomeAsync(
            request, materialized, outcome, CommandActionTerminationPosture.ProcessTreeTerminated,
            process.HasExited ? process.ExitCode : null, null, null, budget.StandardOutputBytes, budget.StandardErrorBytes, startedAt).ConfigureAwait(false);
    }

    private async Task<CommandActionNativeOutcome> RetainOutcomeAsync(
        CommandActionNativeExecutionRequest request,
        CommandActionMaterialization materialized,
        CommandActionOutcomeKind outcome,
        CommandActionTerminationPosture termination,
        int? exitCode,
        string? stdout,
        string? stderr,
        int stdoutBytes,
        int stderrBytes,
        long startedAt,
        RedactionSummary? redactionSummary = null)
    {
        var evidence = CommandActionEvidenceContract.CreateOutcome(
            request.EffectId,
            request.IdempotencyOperationId,
            request.EffectGeneration,
            request.Registration.Template,
            materialized.InputFingerprint,
            request.TargetFingerprint,
            request.PreconditionEvidenceHash,
            request.BeforeEvidenceId,
            outcome,
            termination,
            exitCode,
            stdout,
            stderr,
            stdoutBytes,
            stderrBytes,
            Math.Min(
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                CommandActionContractLimits.MaxExecutionMilliseconds + (long)CommandActionContractLimits.MaxTerminationMilliseconds),
            UtcNow(),
            redactionSummary);
        await _evidenceStore.RetainOutcomeAsync(evidence, CancellationToken.None).ConfigureAwait(false);
        return new CommandActionNativeOutcome(
            outcome == CommandActionOutcomeKind.Succeeded ? CommandActionNativeOutcomeKind.Succeeded : CommandActionNativeOutcomeKind.Failed,
            evidence.EvidenceId);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CommandActionOutputBudget budget, bool standardOutput)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[4_096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            if (count == 0)
            {
                return destination.ToArray();
            }
            budget.Account(count, standardOutput);
            await destination.WriteAsync(buffer.AsMemory(0, count), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
        => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private static bool TryValidateExecutionRequest(CommandActionNativeExecutionRequest? request, out CommandActionMaterialization? materialized)
    {
        materialized = null;
        return request?.Registration is not null
            && request.Input is not null
            && CommandActionRegistrationContract.Validate(request.Registration) is null
            && CommandActionFingerprint.IsEvidenceIdentifier(request.EffectId)
            && CommandActionFingerprint.IsEvidenceIdentifier(request.IdempotencyOperationId)
            && request.EffectGeneration >= 1
            && CommandActionFingerprint.IsCanonicalSha256(request.TargetFingerprint)
            && CommandActionFingerprint.IsCanonicalSha256(request.PreconditionEvidenceHash)
            && CommandActionFingerprint.IsEvidenceIdentifier(request.BeforeEvidenceId)
            && CommandActionInputContract.TryMaterialize(request.Input.CanonicalJson, request.Registration.Template, out materialized, out _);
    }

    private static bool MatchesPreparation(
        CommandActionPreparationEvidence? evidence,
        CommandActionNativeExecutionRequest request,
        CommandActionMaterialization materialized)
        => CommandActionEvidenceContract.ValidatePreparation(evidence) is null
            && string.Equals(evidence!.EvidenceId, request.BeforeEvidenceId, StringComparison.Ordinal)
            && string.Equals(evidence.TemplateHash, request.Registration.Template.ContentHash, StringComparison.Ordinal)
            && evidence.ArtifactDigest.FixedTimeEquals(request.Registration.Template.ArtifactDigest)
            && evidence.ActivationRevision == request.Registration.Template.ActivationRevision
            && string.Equals(evidence.InputFingerprint, materialized.InputFingerprint, StringComparison.Ordinal)
            && string.Equals(evidence.TargetFingerprint, request.TargetFingerprint, StringComparison.Ordinal)
            && string.Equals(evidence.PreconditionEvidenceHash, request.PreconditionEvidenceHash, StringComparison.Ordinal);

    private static bool MatchesOutcome(
        CommandActionOutcomeEvidence? evidence,
        CommandActionNativeExecutionRequest request,
        CommandActionMaterialization materialized)
        => CommandActionEvidenceContract.ValidateOutcome(evidence) is null
            && string.Equals(evidence!.EffectId, request.EffectId, StringComparison.Ordinal)
            && string.Equals(evidence.IdempotencyOperationId, request.IdempotencyOperationId, StringComparison.Ordinal)
            && evidence.EffectGeneration == request.EffectGeneration
            && string.Equals(evidence.TemplateHash, request.Registration.Template.ContentHash, StringComparison.Ordinal)
            && evidence.ArtifactDigest.FixedTimeEquals(request.Registration.Template.ArtifactDigest)
            && evidence.ActivationRevision == request.Registration.Template.ActivationRevision
            && string.Equals(evidence.InputFingerprint, materialized.InputFingerprint, StringComparison.Ordinal)
            && string.Equals(evidence.TargetFingerprint, request.TargetFingerprint, StringComparison.Ordinal)
            && string.Equals(evidence.PreconditionEvidenceHash, request.PreconditionEvidenceHash, StringComparison.Ordinal)
            && string.Equals(evidence.BeforeEvidenceId, request.BeforeEvidenceId, StringComparison.Ordinal);

    private static CapabilityExecutableInvocation ResolutionInvocation(CommandActionRegistration registration, string inputJson, string operationId)
        => new(registration.Manifest, string.Empty, inputJson, operationId, registration.Template.ActivationRevision);

    private static bool TryValidateLease(
        CommandActionRegistration registration,
        ICapabilityExecutableArtifactLease lease,
        out string root,
        out string executablePath)
    {
        root = string.Empty;
        executablePath = string.Empty;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lease.ArtifactRoot));
            executablePath = Path.GetFullPath(lease.ExecutablePath);
            var expected = Path.GetFullPath(Path.Combine(root, registration.Manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(executablePath, expected, comparison)
                && executablePath.StartsWith(root + Path.DirectorySeparatorChar, comparison)
                && File.Exists(executablePath)
                && !HasLink(root, executablePath)
                && !lease.ExecutableHandle.IsInvalid
                && !lease.ExecutableHandle.IsClosed
                && lease.ArtifactDigest.FixedTimeEquals(registration.Template.ArtifactDigest)
                && lease.ActivationRevision == registration.Template.ActivationRevision;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            return false;
        }
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

    private static CommandActionNativeExecutionResult NotStarted(CommandActionDispatchNotStartedReason reason)
        => new(CommandActionNativeExecutionStatus.DispatchNotStarted, null, reason);

    private static CapabilityExecutableAvailability Availability(CapabilityExecutableAvailabilityStatus status, string? detail)
        => new(status, SafeDetail(detail));

    private static string SafeDetail(string? value)
    {
        var redacted = CapabilityProcessDiagnosticRedactor.Redact(value ?? string.Empty);
        return redacted.Length == 0 ? "The command host is unavailable." : redacted;
    }

    private static TextRedactionResult RedactOutput(string value, SensitiveRedactionScope? scope, RedactionSummary fallback)
    {
        if (scope is null)
        {
            return new TextRedactionResult(SensitiveRedactionScope.ScopeLimitMarker, fallback);
        }
        var redacted = scope.RedactText(value);
        var scrubbed = CapabilityProcessDiagnosticRedactor.Redact(redacted.Value, CommandActionContractLimits.MaxRetainedOutputCharacters);
        return new TextRedactionResult(CommandActionEvidenceContract.SanitizeRetainedText(scrubbed), redacted.Summary);
    }

    private static SensitiveRedactionScope? CreateRedactionScope(CommandActionNativeExecutionRequest request, out RedactionSummary fallback)
    {
        fallback = new RedactionSummary(RedactionStatus.ScopeLimitExceeded, 0, 0, 0, 0, 0);
        if (!CommandActionInputContract.TryParse(request.Input.CanonicalJson, request.Registration.Template, out var input, out _)
            || input!.Values.Any(value => value.Value.Length > EphemeralSecretMaterial.MaxCharacters))
        {
            return null;
        }
        var materials = input.Values.Select(value => EphemeralSecretMaterial.Create(value.Value.AsSpan())).ToArray();
        try
        {
            var scope = SensitiveRedactionScope.Create(
                materials,
                new RedactionLimits(
                    maxSensitiveValues: CommandActionContractLimits.MaxSlots,
                    maxSensitiveValueCharacters: EphemeralSecretMaterial.MaxCharacters,
                    maxInputCharacters: RedactionLimits.AbsoluteMaxProjectionCharacters,
                    maxOutputCharacters: CommandActionContractLimits.MaxRetainedOutputCharacters,
                    maxWorkUnits: RedactionLimits.AbsoluteMaxWorkUnits));
            fallback = new RedactionSummary(RedactionStatus.ScopeLimitExceeded, scope.SensitiveValueCount, scope.IgnoredValueCount, 0, 0, 0);
            return scope;
        }
        finally
        {
            foreach (var material in materials)
            {
                material.Dispose();
            }
        }
    }

    private static RedactionSummary Combine(RedactionSummary first, RedactionSummary second)
        => new(
            first.Status == RedactionStatus.Completed ? second.Status : first.Status,
            Math.Max(first.SensitiveValueCount, second.SensitiveValueCount),
            Math.Max(first.IgnoredValueCount, second.IgnoredValueCount),
            checked(first.ReplacementCount + second.ReplacementCount),
            checked(first.ExaminedCharacterCount + second.ExaminedCharacterCount),
            checked(first.WorkUnitCount + second.WorkUnitCount));

    private DateTimeOffset UtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        if (now == default || now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Trusted UTC time is unavailable.");
        }
        return now;
    }

    private static bool IsBoundaryFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or PlatformNotSupportedException or NotSupportedException;

    private static bool IsPrelaunchFailure(Exception exception)
        => IsBoundaryFailure(exception) || exception is ObjectDisposedException;
}
