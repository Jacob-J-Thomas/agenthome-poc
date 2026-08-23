using System.Security.Cryptography;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Application.LocalWorkspace.Actions;
using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.WorkspaceActions.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Executes exact governed workspace append, write, and recoverable-delete operations through retained native handles.</summary>
public sealed class WorkspaceActionNativeHost : IWorkspaceActionNativeHost
{
    private const int AutomaticOrphanCleanupLimit = 8;
    private static readonly TimeSpan _deleteRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan _orphanRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan _operationLeaseWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _targetLeaseRetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly IWorkspaceMutationCommitBoundary _commitBoundary;
    private readonly IWorkspaceActionPermissionRevalidator _permissionRevalidator;
    private readonly IWorkspaceActionCommittedAfterEvidenceResolver? _committedAfterEvidence;
    private readonly IWorkspaceActionAttemptPresenceResolver? _attemptPresence;
    private readonly IWorkspaceActionCommitObserver? _commitObserver;
    private readonly IWorkspaceActionDurabilityObserver? _durabilityObserver;
    private readonly IWorkspaceActionNamespaceRaceObserver? _namespaceRaceObserver;
    private readonly WorkspaceActionCleanupCursorStore _cleanupCursors;
    private readonly WorkspaceActionEvidenceStore _evidence;
    private readonly WorkspaceActionPrivateArtifactPathGuard _guard;
    private readonly string _locksRoot;
    private readonly WorkspaceActionStorageLimits _quota;
    private readonly string _quarantineRoot;
    private readonly string _rootPath;
    private readonly WorkspaceActionScopeId _scopeId;
    private readonly string _stagingRoot;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the one native workspace actuator host for a statically admitted root and scope.</summary>
    public WorkspaceActionNativeHost(
        WorkspacePaths paths,
        WorkspaceActionScopeId scopeId,
        IWorkspaceMutationCommitBoundary commitBoundary,
        IWorkspaceActionPermissionRevalidator permissionRevalidator,
        WorkspaceActionEvidenceStore? evidenceStore = null,
        TimeProvider? timeProvider = null,
        IWorkspaceActionDurabilityObserver? durabilityObserver = null,
        IWorkspaceActionCommitObserver? commitObserver = null,
        IWorkspaceActionNamespaceRaceObserver? namespaceRaceObserver = null,
        WorkspaceActionStorageLimits? quota = null,
        IWorkspaceActionCommittedAfterEvidenceResolver? committedAfterEvidence = null,
        IWorkspaceActionAttemptPresenceResolver? attemptPresence = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _scopeId = scopeId ?? throw new ArgumentNullException(nameof(scopeId));
        _commitBoundary = commitBoundary ?? throw new ArgumentNullException(nameof(commitBoundary));
        _permissionRevalidator = permissionRevalidator ?? throw new ArgumentNullException(nameof(permissionRevalidator));
        _committedAfterEvidence = committedAfterEvidence;
        _attemptPresence = attemptPresence;
        _quota = WorkspaceActionStorageLimits.Validate(quota);
        _evidence = evidenceStore ?? new WorkspaceActionEvidenceStore(paths, _quota);
        _cleanupCursors = new WorkspaceActionCleanupCursorStore(paths);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _durabilityObserver = durabilityObserver;
        _commitObserver = commitObserver;
        _namespaceRaceObserver = namespaceRaceObserver;
        _rootPath = Path.TrimEndingDirectorySeparator(paths.RootPath);
        var privateRoot = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions");
        _stagingRoot = Path.Combine(privateRoot, "staging");
        _quarantineRoot = Path.Combine(privateRoot, "quarantine");
        _locksRoot = Path.Combine(privateRoot, "target-locks");
        _guard = new WorkspaceActionPrivateArtifactPathGuard(paths.RootPath);
    }

    /// <inheritdoc />
    public async Task<WorkspaceActionNativePreparation?> PrepareAsync(
        WorkspaceActionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TryCaptureInput(input, out var capturedInput))
        {
            return null;
        }
        input = capturedInput!;
        // Establish the private lease tree before authenticating the workspace root. On Unix,
        // creating the first private directory changes the root directory link count.
        PreparePrivateRoot(_locksRoot);
        using var initial = WorkspaceActionRetainedTargetSession.Open(_rootPath, _scopeId, input.Target, writeTarget: false);
        using var namespaceLease = await TryAcquireOperationLeaseAsync(
            ComputeNamespaceLeaseKey(initial.RootIdentity.Fingerprint, initial.ParentIdentity.Fingerprint),
            cancellationToken).ConfigureAwait(false);
        if (namespaceLease is null)
        {
            return null;
        }
        using var session = WorkspaceActionRetainedTargetSession.Open(_rootPath, _scopeId, input.Target, writeTarget: false);
        if (!string.Equals(session.TargetFingerprint, initial.TargetFingerprint, StringComparison.Ordinal))
        {
            return null;
        }
        if (!await _evidence.IsUniqueTargetReferenceAsync(
                session.TargetFingerprint,
                input.Target.Value,
                session.RootIdentity.Fingerprint,
                session.ParentIdentity.Fingerprint,
                session.TargetIdentity?.Fingerprint,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var preconditionHash = WorkspaceActionInputContract.ComputePreconditionHash(input.Precondition);
        var state = await CapturePreparedStateAsync(input, session, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }
        var permissionOperation = WorkspaceActionPermissionOperation.For(input.Kind, state.Value.EntryKind);
        var permission = await RevalidatePermissionAsync(
            input,
            state.Value.EntryKind,
            permissionOperation,
            session,
            cancellationToken).ConfigureAwait(false);
        if (!permission.IsAllowed || permission.PolicyHash is null)
        {
            return null;
        }
        var retained = await _evidence.FindBeforeStateAsync(
            _scopeId.Value,
            input.Target.Value,
            session.TargetFingerprint,
            preconditionHash,
            state.Value.EntryKind,
            permissionOperation,
            permission.PolicyHash,
            session.RootIdentity.Fingerprint,
            session.ParentIdentity.Fingerprint,
            state.Value.NativeIdentityFingerprint,
            state.Value.ContentHash,
            state.Value.ByteCount,
            state.Value.GovernedVersion,
            cancellationToken).ConfigureAwait(false);
        if (retained is not null)
        {
            return new WorkspaceActionNativePreparation(retained);
        }

        var before = WorkspaceActionEvidenceContract.CreateBefore(
            _scopeId,
            input.Target,
            session.TargetFingerprint,
            preconditionHash,
            state.Value.EntryKind,
            permissionOperation,
            permission.PolicyHash,
            session.RootIdentity.Fingerprint,
            session.ParentIdentity.Fingerprint,
            state.Value.NativeIdentityFingerprint,
            state.Value.ContentHash,
            state.Value.ByteCount,
            state.Value.GovernedVersion,
            UtcNow());
        try
        {
            await _evidence.RetainBeforeAsync(before, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceActionEvidenceCapacityException)
        {
            if (await CleanupPreparationsAsync(AutomaticOrphanCleanupLimit, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw;
            }
            await _evidence.RetainBeforeAsync(before, cancellationToken).ConfigureAwait(false);
        }
        return new WorkspaceActionNativePreparation(before);
    }

    /// <inheritdoc />
    public async Task<bool> IsPreparationCurrentAsync(
        WorkspaceActionInput input,
        string targetFingerprint,
        string beforeEvidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TryCaptureInput(input, out var capturedInput)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(targetFingerprint)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(beforeEvidenceId))
        {
            return false;
        }
        var before = await _evidence.ReadBeforeAsync(beforeEvidenceId, cancellationToken).ConfigureAwait(false);
        if (!MatchesInput(before, capturedInput!)
            || !string.Equals(before!.EvidenceId, beforeEvidenceId, StringComparison.Ordinal)
            || !string.Equals(before.TargetFingerprint, targetFingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        // Native target and permission posture are revalidated immediately before dispatch. This claim only
        // closes the preparation-cleanup race while the canonical attempt-store mutation lock is held.
        return true;
    }

    /// <summary>
    /// Removes only old authenticated before evidence while the canonical attempt store proves that no durable
    /// effect attempt references it. Ambiguous, recent, referenced, or corrupt evidence is preserved.
    /// </summary>
    /// <param name="maximumPreparations">The maximum number of expired records removed in one bounded pass.</param>
    /// <param name="cancellationToken">The token used to cancel the cleanup pass.</param>
    /// <returns>The number of exact authenticated before records removed.</returns>
    public async Task<int> CleanupPreparationsAsync(int maximumPreparations, CancellationToken cancellationToken = default)
    {
        if (maximumPreparations is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPreparations), "Workspace action preparation cleanup is bounded to 1 through 64 records per pass.");
        }
        if (_attemptPresence is null)
        {
            return 0;
        }

        await using var cursor = await _cleanupCursors.AcquirePreparationsAsync(maximumPreparations, cancellationToken).ConfigureAwait(false);
        var candidates = await _evidence.ReadPreparationCleanupCandidatesAsync(
            UtcNow() - _orphanRetention,
            maximumPreparations,
            cursor.Value,
            cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            await cursor.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        var result = await _attemptPresence.TryCleanupPreparationsAsync(
            candidates,
            maximumPreparations,
            cancellationToken).ConfigureAwait(false);
        if (result.EvidenceComplete)
        {
            await cursor.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        return result.RemovedCount;
    }

    /// <summary>
    /// Removes only old private preparation artifacts whose marker, before state, unchanged target, and absence
    /// from the canonical effect-attempt store are all proved. Ambiguous artifacts are preserved.
    /// </summary>
    /// <param name="maximumArtifacts">The maximum number of eligible markers examined in one bounded pass.</param>
    /// <param name="cancellationToken">The token used to cancel the cleanup pass.</param>
    /// <returns>The number of exact marker-owned artifacts removed.</returns>
    public async Task<int> CleanupOrphansAsync(int maximumArtifacts, CancellationToken cancellationToken = default)
    {
        if (maximumArtifacts is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArtifacts), "Workspace action orphan cleanup is bounded to 1 through 64 markers per pass.");
        }
        if (_attemptPresence is null)
        {
            return 0;
        }

        var now = UtcNow();
        var discovered = await ReadCleanupCandidatesAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<WorkspaceActionCleanupCandidate>(discovered.Count);
        foreach (var candidate in discovered)
        {
            if (candidate.Marker.CreatedAtUtc <= now - _orphanRetention
                && await IsCleanupCandidateEligibleAsync(candidate, now, cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(candidate);
            }
        }
        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.Marker.CreatedAtUtc)
            .ThenBy(candidate => candidate.Marker.MarkerHash, StringComparer.Ordinal)
            .ToArray();
        var examinationCount = Math.Min(maximumArtifacts, orderedCandidates.Length);
        if (examinationCount == 0)
        {
            return 0;
        }
        await using var cursor = await _cleanupCursors.AcquireArtifactsAsync(examinationCount, cancellationToken).ConfigureAwait(false);
        var start = (int)(cursor.Value % (ulong)orderedCandidates.Length);
        var removed = 0;
        var windowComplete = true;
        for (var index = 0; index < examinationCount; index++)
        {
            var candidate = orderedCandidates[(start + index) % orderedCandidates.Length];
            cancellationToken.ThrowIfCancellationRequested();
            var presence = await _attemptPresence.ResolveAsync(
                candidate.Marker.EffectId,
                candidate.Marker.IdempotencyOperationId,
                candidate.Marker.EffectGeneration,
                candidate.Marker.BeforeEvidenceId,
                cancellationToken).ConfigureAwait(false);
            if (presence is not (WorkspaceActionAttemptPresence.NotFound or WorkspaceActionAttemptPresence.ArtifactReleased))
            {
                windowComplete &= presence == WorkspaceActionAttemptPresence.Exists;
                continue;
            }
            var before = await _evidence.ReadBeforeAsync(candidate.Marker.BeforeEvidenceId, cancellationToken).ConfigureAwait(false);
            if (before is null
                || !candidate.Marker.MatchesBefore(before)
                || !WorkspaceRelativeFileTarget.TryParse(before.TargetReference, out var target, out _))
            {
                windowComplete = false;
                continue;
            }
            using var namespaceLease = await TryAcquireOperationLeaseAsync(
                ComputeExecutionLeaseKey(before),
                cancellationToken).ConfigureAwait(false);
            if (namespaceLease is null)
            {
                windowComplete = false;
                continue;
            }
            var targetUnchanged = presence == WorkspaceActionAttemptPresence.ArtifactReleased;
            if (!targetUnchanged)
            {
                try
                {
                    using var session = WorkspaceActionRetainedTargetSession.Open(
                        _rootPath,
                        _scopeId,
                        target!,
                        writeTarget: false);
                    targetUnchanged = await session.MatchesBeforeAsync(before, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    targetUnchanged = false;
                    windowComplete = false;
                }
            }
            if (targetUnchanged
                && await TryDeleteOrphanCandidateAsync(
                    candidate,
                    before,
                    presence == WorkspaceActionAttemptPresence.ArtifactReleased,
                    cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }
        if (windowComplete)
        {
            await cursor.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        return removed;
    }

    /// <inheritdoc />
    public async Task<WorkspaceActionNativeCommitResult> ExecuteAsync(
        WorkspaceActionNativeExecutionRequest request,
        IWorkspaceActionNativeDispatchBoundary dispatchBoundary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dispatchBoundary);
        if (!TryCaptureInput(request.Input, out var capturedInput)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(request.TargetFingerprint)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(request.BeforeEvidenceId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(request.EffectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(request.IdempotencyOperationId)
            || request.EffectGeneration < 1)
        {
            return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
        }
        request = request with { Input = capturedInput! };

        var before = await _evidence.ReadBeforeAsync(request.BeforeEvidenceId, cancellationToken).ConfigureAwait(false);
        if (!MatchesInput(before, request.Input)
            || !string.Equals(before!.EvidenceId, request.BeforeEvidenceId, StringComparison.Ordinal)
            || !string.Equals(before.TargetFingerprint, request.TargetFingerprint, StringComparison.Ordinal)
            || before.PermissionOperation != WorkspaceActionPermissionOperation.For(request.Input.Kind, before.EntryKind))
        {
            return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
        }
        using var session = WorkspaceActionRetainedTargetSession.Open(
            _rootPath,
            _scopeId,
            request.Input.Target,
            writeTarget: true,
            fenceTargetNamespace: OperatingSystem.IsWindows() && request.Input.Kind == WorkspaceActionKind.Delete,
            fenceDirectoryNamespace: OperatingSystem.IsWindows() && before.EntryKind == WorkspaceActionEntryKind.RegularFile);
        if (!await session.MatchesBeforeAsync(before, cancellationToken).ConfigureAwait(false))
        {
            return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
        }
        var currentPermission = await RevalidatePermissionAsync(
            request.Input,
            before.EntryKind,
            before.PermissionOperation,
            session,
            cancellationToken).ConfigureAwait(false);
        if (!MatchesRetainedPermission(before, currentPermission))
        {
            return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
        }

        return request.Input.Kind == WorkspaceActionKind.Delete
            ? await ExecuteDeleteAsync(request, before, session, dispatchBoundary, cancellationToken).ConfigureAwait(false)
            : await ExecuteInstallAsync(request, before, session, dispatchBoundary, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkspaceActionReconciliationProbeResult> ProbeAsync(
        WorkspaceActionReconciliationProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryCaptureInput(request.Input, out var input)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(request.TargetFingerprint)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(request.BeforeEvidenceId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(request.EffectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(request.IdempotencyOperationId)
            || request.EffectGeneration < 1)
        {
            return new WorkspaceActionReconciliationProbeResult(WorkspaceActionReconciliationPosture.Unknown, null, null);
        }
        var before = await _evidence.ReadBeforeAsync(request.BeforeEvidenceId, cancellationToken).ConfigureAwait(false);
        if (before is null
            || !string.Equals(before.EvidenceId, request.BeforeEvidenceId, StringComparison.Ordinal)
            || !string.Equals(before.TargetFingerprint, request.TargetFingerprint, StringComparison.Ordinal)
            || !MatchesInput(before, input!)
            || !WorkspaceActionScopeId.TryParse(before.ScopeId, out var scope)
            || !Equals(scope, _scopeId)
            || !WorkspaceRelativeFileTarget.TryParse(before.TargetReference, out var target, out _))
        {
            return new WorkspaceActionReconciliationProbeResult(WorkspaceActionReconciliationPosture.Unknown, null, null);
        }
        var after = await _evidence.FindAfterAsync(
            request.EffectId,
            request.IdempotencyOperationId,
            request.EffectGeneration,
            cancellationToken).ConfigureAwait(false);
        var outcome = await _evidence.FindOutcomeAsync(
            request.EffectId,
            request.IdempotencyOperationId,
            request.EffectGeneration,
            cancellationToken).ConfigureAwait(false);
        if (outcome is not null
            && (after is null || !OutcomeMatchesAfter(outcome, after)))
        {
            return new WorkspaceActionReconciliationProbeResult(WorkspaceActionReconciliationPosture.Unknown, after?.EvidenceId, after?.TombstoneReference);
        }
        if (after is not null)
        {
            if (!string.Equals(after.BeforeEvidenceId, before.EvidenceId, StringComparison.Ordinal)
                || !string.Equals(after.OperationId, WorkspaceActionOperationIds.For(input!.Kind), StringComparison.Ordinal)
                || !string.Equals(after.ScopeId, before.ScopeId, StringComparison.Ordinal)
                || !string.Equals(after.TargetReference, before.TargetReference, StringComparison.Ordinal)
                || !string.Equals(after.TargetFingerprint, before.TargetFingerprint, StringComparison.Ordinal))
            {
                return new WorkspaceActionReconciliationProbeResult(WorkspaceActionReconciliationPosture.Unknown, null, null);
            }
            if (outcome is not null)
            {
                return new WorkspaceActionReconciliationProbeResult(WorkspaceActionReconciliationPosture.ProvedOutcomeObserved, after.EvidenceId, after.TombstoneReference, outcome.EvidenceId);
            }
            // Retained conclusive evidence proves that dispatch completed. A later target or quarantine
            // change cannot safely reclassify the original effect as not started.
            return new WorkspaceActionReconciliationProbeResult(WorkspaceActionReconciliationPosture.Indeterminate, after.EvidenceId, after.TombstoneReference);
        }

        using var current = WorkspaceActionRetainedTargetSession.Open(
            _rootPath,
            _scopeId,
            target!,
            writeTarget: false);
        return await current.MatchesBeforeAsync(before, cancellationToken).ConfigureAwait(false)
            ? new WorkspaceActionReconciliationProbeResult(WorkspaceActionReconciliationPosture.ProvedNotStarted, null, null)
            : new WorkspaceActionReconciliationProbeResult(WorkspaceActionReconciliationPosture.Indeterminate, null, null);
    }

    private async Task<WorkspaceActionNativeCommitResult> ExecuteInstallAsync(
        WorkspaceActionNativeExecutionRequest request,
        WorkspaceActionBeforeEvidence before,
        WorkspaceActionRetainedTargetSession session,
        IWorkspaceActionNativeDispatchBoundary dispatchBoundary,
        CancellationToken cancellationToken)
    {
        var literal = WorkspaceActionInputContract.MaterializeLiteralBytes(request.Input);
        var beforeBytes = session.Exists
            ? await session.ReadTargetBytesAsync(WorkspaceActionContractLimits.MaxBeforeImageBytes, cancellationToken).ConfigureAwait(false)
            : [];
        var afterBytes = request.Input.Kind == WorkspaceActionKind.Append
            ? Concat(beforeBytes, literal)
            : literal;
        if (afterBytes.Length > WorkspaceActionContractLimits.MaxAfterImageBytes)
        {
            return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
        }
        var afterHash = Sha256(afterBytes);
        using var stage = await CreateStageWithPressureCleanupAsync(
            request,
            before,
            afterBytes,
            afterHash,
            session,
            cancellationToken).ConfigureAwait(false);
        SafeFileHandle? windowsDisplaced = null;
        WorkspaceActionNativeFileStamp? windowsDisplacedIdentity = null;
        var retainUnpublishedWindowsStage = false;
        try
        {
            using var operationLease = await TryAcquireOperationLeaseAsync(
                ComputeExecutionLeaseKey(before),
                cancellationToken).ConfigureAwait(false);
            if (operationLease is null)
            {
                return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
            }
            session.RevalidateDirectories();
            if (!await session.MatchesBeforeAsync(before, cancellationToken).ConfigureAwait(false))
            {
                return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
            }
            var affectedPath = AbsoluteTarget(request.Input.Target);
            var outcome = await _commitBoundary.ExecuteAsync(
                [affectedPath],
                token => dispatchBoundary.CrossAsync(
                    async boundaryToken =>
                    {
                        session.RevalidateDirectories();
                        if (!await session.MatchesBeforeAsync(before, boundaryToken).ConfigureAwait(false))
                        {
                            throw new IOException("The exact workspace precondition changed at the native commit boundary.");
                        }
                        if (_commitObserver is not null)
                        {
                            await _commitObserver.ObserveAsync(
                                WorkspaceActionCommitPoint.BeforeInstallTargetMutation,
                                before.EvidenceId,
                                boundaryToken).ConfigureAwait(false);
                        }
                        session.RevalidateDirectoryNamespace();
                        var permission = await RevalidatePermissionAsync(
                            request.Input,
                            before.EntryKind,
                            before.PermissionOperation,
                            session,
                            boundaryToken).ConfigureAwait(false);
                        if (!MatchesRetainedPermission(before, permission))
                        {
                            throw new IOException("The exact workspace mutation permission changed at the native commit boundary.");
                        }
                        if (!await session.MatchesBeforeAsync(before, boundaryToken).ConfigureAwait(false))
                        {
                            throw new IOException("The exact workspace precondition changed immediately before the native system call.");
                        }

                        WorkspaceActionNativeFileStamp observedIdentity;
                        byte[] observedBytes;
                        var windowsAtomicReplacement = OperatingSystem.IsWindows()
                            && before.EntryKind == WorkspaceActionEntryKind.RegularFile;
                        if (before.EntryKind == WorkspaceActionEntryKind.Absent)
                        {
                            if (_namespaceRaceObserver is not null)
                            {
                                await _namespaceRaceObserver.ObserveAsync(
                                    WorkspaceActionNamespaceRacePoint.BeforeInstallSystemCall,
                                    before.EvidenceId,
                                    boundaryToken).ConfigureAwait(false);
                            }
                            WorkspaceActionNativeFileSystem.RenameRelative(
                                stage.File,
                                stage.Directory,
                                stage.Name,
                                session.ParentHandle,
                                session.TerminalName,
                                overwrite: false);
                            stage.Published = true;
                            if (_namespaceRaceObserver is not null)
                            {
                                await _namespaceRaceObserver.ObserveAsync(
                                    WorkspaceActionNamespaceRacePoint.AfterInstallSystemCall,
                                    before.EvidenceId,
                                    boundaryToken).ConfigureAwait(false);
                            }
                            using var observed = WorkspaceActionNativeFileSystem.OpenRelativeFile(session.ParentHandle, session.TerminalName, allowMissing: false, write: false)!;
                            WorkspaceActionNativeFileSystem.RequireExactOpenedName(observed, session.TerminalName);
                            observedIdentity = WorkspaceActionNativeFileSystem.GetIdentity(observed);
                            observedBytes = await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(observed, WorkspaceActionContractLimits.MaxAfterImageBytes, boundaryToken).ConfigureAwait(false);
                        }
                        else if (windowsAtomicReplacement)
                        {
                            var displacedName = stage.Name + ".displaced";
                            using var stageFence = WorkspaceActionRetainedPrivateDirectoryFence.Open(_rootPath, _stagingRoot);
                            stageFence.Revalidate();
                            if (_namespaceRaceObserver is not null)
                            {
                                await _namespaceRaceObserver.ObserveAsync(
                                    WorkspaceActionNamespaceRacePoint.BeforeInstallSystemCall,
                                    before.EvidenceId,
                                    boundaryToken).ConfigureAwait(false);
                            }
                            stageFence.Revalidate();
                            session.RevalidateDirectories();
                            session.RevalidateTerminalName();
                            var replacementPath = WorkspaceActionNativeFileSystem.CaptureWindowsReplacementPath(stage.File, stage.Name);
                            retainUnpublishedWindowsStage = true;
                            stage.ReleaseFileHandle();
                            WorkspaceActionNativeFileSystem.ReplaceWindowsRelativeWithBackup(
                                replacementPath,
                                session.TargetHandle!,
                                session.ParentHandle,
                                session.TerminalName,
                                stageFence.DirectoryHandle,
                                displacedName);
                            stage.Published = true;
                            stageFence.Revalidate();
                            session.RevalidateDirectories();
                            windowsDisplaced = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                                stageFence.DirectoryHandle,
                                displacedName,
                                allowMissing: false,
                                write: true,
                                denyDeleteSharing: true,
                                denyWriteSharing: true)!;
                            WorkspaceActionNativeFileSystem.RequireExactOpenedName(windowsDisplaced, displacedName);
                            windowsDisplacedIdentity = WorkspaceActionNativeFileSystem.GetIdentity(windowsDisplaced);
                            if (_namespaceRaceObserver is not null)
                            {
                                await _namespaceRaceObserver.ObserveAsync(
                                    WorkspaceActionNamespaceRacePoint.AfterInstallSystemCall,
                                    before.EvidenceId,
                                    boundaryToken).ConfigureAwait(false);
                            }
                            var displacedBytes = await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(
                                windowsDisplaced,
                                WorkspaceActionContractLimits.MaxBeforeImageBytes,
                                boundaryToken).ConfigureAwait(false);
                            if (!windowsDisplacedIdentity.Value.SameEntry(session.TargetIdentity!.Value)
                                || displacedBytes.LongLength != before.ByteCount
                                || !string.Equals(Sha256(displacedBytes), before.ContentHash, StringComparison.Ordinal))
                            {
                                throw new IOException("The atomic Windows replacement displaced a target other than the exact retained before image.");
                            }
                            using var observed = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                                session.ParentHandle,
                                session.TerminalName,
                                allowMissing: false,
                                write: true,
                                denyDeleteSharing: true,
                                denyWriteSharing: true)!;
                            WorkspaceActionNativeFileSystem.RequireExactOpenedName(observed, session.TerminalName);
                            observedIdentity = WorkspaceActionNativeFileSystem.GetIdentity(observed);
                            observedBytes = await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(
                                observed,
                                WorkspaceActionContractLimits.MaxAfterImageBytes,
                                boundaryToken).ConfigureAwait(false);
                            if (!observedIdentity.SameEntry(stage.Identity)
                                || !string.Equals(Sha256(observedBytes), afterHash, StringComparison.Ordinal))
                            {
                                throw new IOException("The published Windows workspace after-image does not match the authenticated stage.");
                            }
                            WorkspaceActionNativeFileSystem.RequireReplacementMetadata(session.TargetHandle!, observed);
                            WorkspaceActionNativeFileSystem.FlushFile(observed);
                        }
                        else
                        {
                            WorkspaceActionNativeFileSystem.RequireReplacementMetadata(session.TargetHandle!, stage.File);
                            if (_namespaceRaceObserver is not null)
                            {
                                await _namespaceRaceObserver.ObserveAsync(
                                    WorkspaceActionNamespaceRacePoint.BeforeInstallSystemCall,
                                    before.EvidenceId,
                                    boundaryToken).ConfigureAwait(false);
                            }
                            WorkspaceActionNativeFileSystem.ExchangeRelative(
                                stage.File,
                                stage.Directory,
                                stage.Name,
                                session.TargetHandle!,
                                session.ParentHandle,
                                session.TerminalName);
                            stage.Published = true;
                            if (_namespaceRaceObserver is not null)
                            {
                                await _namespaceRaceObserver.ObserveAsync(
                                    WorkspaceActionNamespaceRacePoint.AfterInstallSystemCall,
                                    before.EvidenceId,
                                    boundaryToken).ConfigureAwait(false);
                            }
                            using var displaced = WorkspaceActionNativeFileSystem.OpenRelativeFile(stage.Directory, stage.Name, allowMissing: false, write: true)!;
                            var displacedBytes = await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(
                                displaced,
                                WorkspaceActionContractLimits.MaxBeforeImageBytes,
                                boundaryToken).ConfigureAwait(false);
                            if (!WorkspaceActionNativeFileSystem.GetIdentity(displaced).SameEntry(session.TargetIdentity!.Value)
                                || displacedBytes.LongLength != before.ByteCount
                                || !string.Equals(Sha256(displacedBytes), before.ContentHash, StringComparison.Ordinal))
                            {
                                throw new IOException("A concurrent target replacement won the exchange; automatic rollback is forbidden because a later external replacement must remain untouched.");
                            }
                            using var observed = WorkspaceActionNativeFileSystem.OpenRelativeFile(session.ParentHandle, session.TerminalName, allowMissing: false, write: false)!;
                            observedIdentity = WorkspaceActionNativeFileSystem.GetIdentity(observed);
                            observedBytes = await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(observed, WorkspaceActionContractLimits.MaxAfterImageBytes, boundaryToken).ConfigureAwait(false);
                        }
                        session.RevalidateDirectories();
                        if (!observedIdentity.SameEntry(stage.Identity)
                            || !string.Equals(Sha256(observedBytes), afterHash, StringComparison.Ordinal))
                        {
                            throw new IOException("The published workspace after-image does not match the authenticated stage.");
                        }
                        if (session.TargetHandle is not null && !windowsAtomicReplacement)
                        {
                            using var observed = WorkspaceActionNativeFileSystem.OpenRelativeFile(session.ParentHandle, session.TerminalName, allowMissing: false, write: false)!;
                            WorkspaceActionNativeFileSystem.RequireReplacementMetadata(session.TargetHandle, observed);
                        }
                        if (stage.HasRetainedFileHandle)
                        {
                            WorkspaceActionNativeFileSystem.FlushFile(stage.File);
                        }
                        WorkspaceActionNativeFileSystem.FlushDirectory(session.ParentHandle);
                        var after = WorkspaceActionEvidenceContract.CreateAfter(
                            before,
                            WorkspaceActionOperationIds.For(request.Input.Kind),
                            request.EffectId,
                            request.IdempotencyOperationId,
                            request.EffectGeneration,
                            WorkspaceActionEntryKind.RegularFile,
                            observedIdentity.Fingerprint,
                            afterHash,
                            observedBytes.LongLength,
                            request.Input.Kind == WorkspaceActionKind.Append ? literal.LongLength : 0,
                            checked(before.GovernedVersion + 1),
                            null,
                            null,
                            UtcNow());
                        if (_durabilityObserver is not null)
                        {
                            await _durabilityObserver.ObserveAsync(
                                WorkspaceActionDurabilityPoint.AfterInstallBeforeEvidence,
                                before.EvidenceId,
                                request.EffectId,
                                boundaryToken).ConfigureAwait(false);
                        }
                        await _evidence.RetainAfterAsync(after, boundaryToken).ConfigureAwait(false);
                        if (_durabilityObserver is not null)
                        {
                            await _durabilityObserver.ObserveAsync(
                                WorkspaceActionDurabilityPoint.AfterEvidenceBeforeOutcome,
                                before.EvidenceId,
                                request.EffectId,
                                boundaryToken).ConfigureAwait(false);
                        }
                        var outcome = WorkspaceActionEvidenceContract.CreateOutcome(after);
                        await _evidence.RetainOutcomeAsync(outcome, boundaryToken).ConfigureAwait(false);

                        if (before.EntryKind == WorkspaceActionEntryKind.RegularFile)
                        {
                            if (windowsAtomicReplacement)
                            {
                                WorkspaceActionNativeFileSystem.DeleteExact(
                                    stage.Directory,
                                    stage.Name + ".displaced",
                                    windowsDisplaced!,
                                    windowsDisplacedIdentity!.Value);
                                (windowsDisplaced ?? throw new IOException("The authenticated Windows workspace replacement backup handle is unavailable for cleanup.")).Dispose();
                                windowsDisplaced = null;
                                windowsDisplacedIdentity = null;
                            }
                            else
                            {
                                WorkspaceActionNativeFileSystem.DeleteExact(
                                    stage.Directory,
                                    stage.Name,
                                    session.TargetHandle!,
                                    session.TargetIdentity!.Value);
                            }
                            WorkspaceActionNativeFileSystem.FlushDirectory(stage.Directory);
                        }
                        DeleteStageMarker(stage);
                        WorkspaceActionNativeFileSystem.FlushDirectory(stage.Directory);
                        return new WorkspaceActionNativeOutcome(outcome.EvidenceId, after.EvidenceId);
                    },
                    token),
                cancellationToken).ConfigureAwait(false);
            return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.OutcomeObserved, outcome);
        }
        finally
        {
            windowsDisplaced?.Dispose();
            if (!stage.Published)
            {
                if (!retainUnpublishedWindowsStage && stage.HasRetainedFileHandle)
                {
                    WorkspaceActionNativeFileSystem.DeleteExact(stage.Directory, stage.Name, stage.File, stage.Identity);
                    DeleteStageMarker(stage);
                    WorkspaceActionNativeFileSystem.FlushDirectory(stage.Directory);
                }
            }
        }
    }

    private async Task<WorkspaceActionNativeCommitResult> ExecuteDeleteAsync(
        WorkspaceActionNativeExecutionRequest request,
        WorkspaceActionBeforeEvidence before,
        WorkspaceActionRetainedTargetSession session,
        IWorkspaceActionNativeDispatchBoundary dispatchBoundary,
        CancellationToken cancellationToken)
    {
        var quarantineReference = "quarantine-" + WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-delete-quarantine.v1",
            before.ContentHashOfRecord,
            request.EffectId,
            request.IdempotencyOperationId,
            request.EffectGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var reservation = WorkspaceActionAttemptArtifactMarker.CreateQuarantine(
            quarantineReference,
            before,
            request,
            UtcNow());
        reservation = await ReserveQuarantineWithPressureCleanupAsync(reservation, cancellationToken).ConfigureAwait(false);
        var namespaceChanged = false;
        try
        {
            using var operationLease = await TryAcquireOperationLeaseAsync(
                ComputeExecutionLeaseKey(before),
                cancellationToken).ConfigureAwait(false);
            if (operationLease is null)
            {
                return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
            }
            using var quarantineDirectory = WorkspaceActionNativeFileSystem.OpenPrivateDirectoryUnderWorkspace(_rootPath, _quarantineRoot);
            var quarantineName = quarantineReference + ".payload";
            using var existing = WorkspaceActionNativeFileSystem.OpenRelativeFile(quarantineDirectory, quarantineName, allowMissing: true, write: true);
            if (!WorkspaceActionNativeFileSystem.GetIdentity(quarantineDirectory).SameMount(session.RootIdentity))
            {
                return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
            }
            if (existing is not null)
            {
                // A retained payload can only be classified through the read-only reconciliation probe.
                // Preserve its authenticated marker instead of treating it as an abandoned reservation.
                namespaceChanged = true;
                return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
            }
            session.RevalidateDirectories();
            if (!await session.MatchesBeforeAsync(before, cancellationToken).ConfigureAwait(false))
            {
                return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.DispatchNotStarted, null);
            }
            var affectedPath = AbsoluteTarget(request.Input.Target);
            var outcome = await _commitBoundary.ExecuteAsync(
                [affectedPath],
                token => dispatchBoundary.CrossAsync(
                    async boundaryToken =>
                    {
                        session.RevalidateDirectories();
                        if (!await session.MatchesBeforeAsync(before, boundaryToken).ConfigureAwait(false))
                        {
                            throw new IOException("The exact workspace delete precondition changed at the native commit boundary.");
                        }
                        if (_commitObserver is not null)
                        {
                            await _commitObserver.ObserveAsync(
                                WorkspaceActionCommitPoint.BeforeDeleteNamespaceMutation,
                                before.EvidenceId,
                                boundaryToken).ConfigureAwait(false);
                        }
                        session.RevalidateDirectoryNamespace();
                        var permission = await RevalidatePermissionAsync(
                            request.Input,
                            before.EntryKind,
                            before.PermissionOperation,
                            session,
                            boundaryToken).ConfigureAwait(false);
                        if (!MatchesRetainedPermission(before, permission))
                        {
                            throw new IOException("The exact workspace delete permission changed at the native commit boundary.");
                        }
                        if (!await session.MatchesBeforeAsync(before, boundaryToken).ConfigureAwait(false))
                        {
                            throw new IOException("The exact workspace delete precondition changed immediately before the native system call.");
                        }
                        if (_namespaceRaceObserver is not null)
                        {
                            await _namespaceRaceObserver.ObserveAsync(
                                WorkspaceActionNamespaceRacePoint.BeforeDeleteSystemCall,
                                before.EvidenceId,
                                boundaryToken).ConfigureAwait(false);
                        }

                        // Windows renames the retained no-delete-sharing handle. Unix uses one atomic
                        // no-replace rename from the target name directly into private quarantine, so
                        // no visible placeholder can transiently occupy the workspace target.
                        WorkspaceActionNativeFileSystem.RenameRelative(
                            session.TargetHandle!,
                            session.ParentHandle,
                            session.TerminalName,
                            quarantineDirectory,
                            quarantineName,
                            overwrite: false);
                        namespaceChanged = true;
                        if (_namespaceRaceObserver is not null)
                        {
                            await _namespaceRaceObserver.ObserveAsync(
                                WorkspaceActionNamespaceRacePoint.AfterDeleteSystemCall,
                                before.EvidenceId,
                                boundaryToken).ConfigureAwait(false);
                        }
                        using (var replacement = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                            session.ParentHandle,
                            session.TerminalName,
                            allowMissing: true,
                            write: false))
                        {
                            if (replacement is not null)
                            {
                                throw new IOException("The workspace delete target name was repopulated after the exact target entered quarantine.");
                            }
                        }
                        session.RevalidateDirectories();

                        using var reopenedQuarantine = OperatingSystem.IsWindows()
                            ? null
                            : WorkspaceActionNativeFileSystem.OpenRelativeFile(
                                quarantineDirectory,
                                quarantineName,
                                allowMissing: false,
                                write: true)!;
                        var quarantined = OperatingSystem.IsWindows() ? session.TargetHandle! : reopenedQuarantine!;
                        var quarantinedIdentity = WorkspaceActionNativeFileSystem.GetIdentity(quarantined);
                        var quarantinedBytes = await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(
                            quarantined,
                            WorkspaceActionContractLimits.MaxBeforeImageBytes,
                            boundaryToken).ConfigureAwait(false);
                        if (!quarantinedIdentity.SameEntry(session.TargetIdentity!.Value)
                            || quarantinedBytes.LongLength != before.ByteCount
                            || !string.Equals(Sha256(quarantinedBytes), before.ContentHash, StringComparison.Ordinal))
                        {
                            throw new IOException("The recoverable workspace delete quarantine payload is not the exact retained target.");
                        }
                        if (!OperatingSystem.IsWindows())
                        {
                            WorkspaceActionNativeFileSystem.FlushFile(quarantined);
                        }
                        WorkspaceActionNativeFileSystem.FlushDirectory(session.ParentHandle);
                        WorkspaceActionNativeFileSystem.FlushDirectory(quarantineDirectory);
                        var now = UtcNow();
                        var governedVersion = checked(before.GovernedVersion + 1);
                        var tombstone = WorkspaceActionEvidenceContract.CreateTombstone(
                            before,
                            quarantineReference,
                            request.EffectId,
                            request.IdempotencyOperationId,
                            request.EffectGeneration,
                            governedVersion,
                            now,
                            now.Add(_deleteRetention));
                        await _evidence.RetainTombstoneAsync(tombstone, boundaryToken).ConfigureAwait(false);
                        var after = WorkspaceActionEvidenceContract.CreateAfter(
                            before,
                            WorkspaceActionOperationIds.Delete,
                            request.EffectId,
                            request.IdempotencyOperationId,
                            request.EffectGeneration,
                            WorkspaceActionEntryKind.Absent,
                            null,
                            null,
                            0,
                            0,
                            governedVersion,
                            quarantineReference,
                            tombstone.TombstoneReference,
                            now);
                        if (_durabilityObserver is not null)
                        {
                            await _durabilityObserver.ObserveAsync(
                                WorkspaceActionDurabilityPoint.AfterDeleteTombstoneBeforeEvidence,
                                before.EvidenceId,
                                request.EffectId,
                                boundaryToken).ConfigureAwait(false);
                        }
                        await _evidence.RetainAfterAsync(after, boundaryToken).ConfigureAwait(false);
                        if (_durabilityObserver is not null)
                        {
                            await _durabilityObserver.ObserveAsync(
                                WorkspaceActionDurabilityPoint.AfterEvidenceBeforeOutcome,
                                before.EvidenceId,
                                request.EffectId,
                                boundaryToken).ConfigureAwait(false);
                        }
                        var outcome = WorkspaceActionEvidenceContract.CreateOutcome(after);
                        await _evidence.RetainOutcomeAsync(outcome, boundaryToken).ConfigureAwait(false);
                        return new WorkspaceActionNativeOutcome(outcome.EvidenceId, after.EvidenceId);
                    },
                    token),
                cancellationToken).ConfigureAwait(false);
            return new WorkspaceActionNativeCommitResult(WorkspaceActionNativeCommitStatus.OutcomeObserved, outcome);
        }
        finally
        {
            if (!namespaceChanged)
            {
                await DeleteAuthenticatedMarkerAsync(_quarantineRoot, quarantineReference + ".reservation", reservation, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<WorkspaceActionStage> CreateStageAsync(
        WorkspaceActionNativeExecutionRequest request,
        WorkspaceActionBeforeEvidence before,
        byte[] afterBytes,
        string afterHash,
        WorkspaceActionRetainedTargetSession session,
        CancellationToken cancellationToken)
    {
        PreparePrivateRoot(_stagingRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_stagingRoot, cancellationToken).ConfigureAwait(false);
        var stageName = "stage-" + WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-action-stage.v1",
            before.ContentHashOfRecord,
            request.EffectId,
            request.IdempotencyOperationId,
            request.EffectGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            afterHash) + ".stage";
        var markerName = stageName + ".marker";
        var maximumEntries = checked(_quota.MaximumStagingEntries * 2 + 1);
        var entries = _guard.EnumerateNames(ownership, maximumEntries + _quota.MaximumStagingEntries + 1).ToArray();
        if (entries.Length > maximumEntries + _quota.MaximumStagingEntries)
        {
            throw new WorkspaceActionArtifactCapacityException("Workspace action staging capacity is exhausted.");
        }
        entries = RemoveAtomicMarkerTemporariesUnderLock(
            ownership,
            entries,
            WorkspaceActionAttemptArtifactKind.Stage,
            _quota.MaximumStagingEntries);
        if (entries.Length > maximumEntries)
        {
            throw new WorkspaceActionArtifactCapacityException("Workspace action staging capacity is exhausted.");
        }
        var stagedReferences = entries
            .Where(name => name.EndsWith(".stage", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var displacedReferences = entries
            .Where(name => name.EndsWith(".stage.displaced", StringComparison.Ordinal))
            .Select(name => name[..^".displaced".Length])
            .ToHashSet(StringComparer.Ordinal);
        var markerReferences = entries
            .Where(name => name.EndsWith(".stage.marker", StringComparison.Ordinal))
            .Select(name => name[..^".marker".Length])
            .ToHashSet(StringComparer.Ordinal);
        var markerAlreadyRetained = markerReferences.Contains(stageName);
        if (entries.Any(name =>
                !string.Equals(name, ".custom-loop-mutations.lock", StringComparison.Ordinal)
                && !name.EndsWith(".stage", StringComparison.Ordinal)
                && !name.EndsWith(".stage.displaced", StringComparison.Ordinal)
                && !name.EndsWith(".stage.marker", StringComparison.Ordinal))
            || stagedReferences.Except(markerReferences, StringComparer.Ordinal).Any()
            || displacedReferences.Except(markerReferences, StringComparer.Ordinal).Any())
        {
            throw new FormatException("Workspace action staging contains an unauthenticated or unsupported artifact.");
        }
        var retainedReferences = new HashSet<string>(stagedReferences, StringComparer.Ordinal);
        retainedReferences.UnionWith(displacedReferences);
        retainedReferences.UnionWith(markerReferences);
        if (!retainedReferences.Contains(stageName) && retainedReferences.Count >= _quota.MaximumStagingEntries)
        {
            throw new WorkspaceActionArtifactCapacityException("Workspace action staging capacity is exhausted.");
        }
        var marker = WorkspaceActionAttemptArtifactMarker.CreateStage(
            stageName,
            before,
            request,
            afterHash,
            afterBytes.LongLength,
            UtcNow());
        if (displacedReferences.Contains(stageName))
        {
            throw new IOException("A retained displaced before image requires reconciliation before this workspace action can continue.");
        }
        var directory = WorkspaceActionNativeFileSystem.Duplicate(ownership.DirectoryHandle);
        SafeFileHandle? file = null;
        var createdFile = false;
        var markerRetained = false;
        try
        {
            _ = await RetainMarkerAsync(ownership, markerName, marker, cancellationToken).ConfigureAwait(false);
            markerRetained = true;
            if (!WorkspaceActionNativeFileSystem.GetIdentity(directory).SameMount(session.RootIdentity))
            {
                throw new IOException("Workspace action staging is not on the exact target filesystem device or volume.");
            }
            file = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                directory,
                stageName,
                allowMissing: true,
                write: true,
                privateSecurityAccess: OperatingSystem.IsWindows());
            if (file is null)
            {
                file = WorkspaceActionNativeFileSystem.CreateRelativeFile(
                    directory,
                    stageName,
                    privateSecurityAccess: OperatingSystem.IsWindows());
                createdFile = true;
                await WorkspaceActionNativeFileSystem.WriteAllBytesAsync(file, afterBytes, cancellationToken).ConfigureAwait(false);
                if (session.TargetHandle is not null && !OperatingSystem.IsWindows())
                {
                    WorkspaceActionNativeFileSystem.PreserveReplacementMetadata(session.TargetHandle, file);
                    WorkspaceActionNativeFileSystem.FlushFile(file);
                }
            }
            else
            {
                var retainedBytes = await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(file, WorkspaceActionContractLimits.MaxAfterImageBytes, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Sha256(retainedBytes), afterHash, StringComparison.Ordinal))
                {
                    file.Dispose();
                    throw new FormatException("Authenticated workspace action staging conflicts with retained content.");
                }
            }
            var markerHandle = WorkspaceActionNativeFileSystem.OpenRelativeFile(directory, markerName, allowMissing: false, write: true)!;
            return new WorkspaceActionStage(
                directory,
                file,
                stageName,
                WorkspaceActionNativeFileSystem.GetIdentity(file),
                markerHandle,
                markerName,
                WorkspaceActionNativeFileSystem.GetIdentity(markerHandle));
        }
        catch (Exception exception)
        {
            try
            {
                if (createdFile && file is not null)
                {
                    var identity = WorkspaceActionNativeFileSystem.GetIdentity(file);
                    WorkspaceActionNativeFileSystem.DeleteExact(directory, stageName, file, identity);
                }
                if (markerRetained && !markerAlreadyRetained)
                {
                    var retainedMarker = await ReadMarkerAsync(ownership, markerName, CancellationToken.None).ConfigureAwait(false);
                    if (!Equals(retainedMarker, marker))
                    {
                        throw new FormatException("Workspace action staging cleanup refused a substituted marker.");
                    }
                    using var markerHandle = WorkspaceActionNativeFileSystem.OpenRelativeFile(directory, markerName, allowMissing: false, write: true)!;
                    var markerIdentity = WorkspaceActionNativeFileSystem.GetIdentity(markerHandle);
                    WorkspaceActionNativeFileSystem.DeleteExact(directory, markerName, markerHandle, markerIdentity);
                }
                WorkspaceActionNativeFileSystem.FlushDirectory(directory);
            }
            catch (Exception cleanupException)
            {
                file?.Dispose();
                directory.Dispose();
                throw new AggregateException("Workspace action staging failed and its exact private preparation could not be cleaned.", exception, cleanupException);
            }
            file?.Dispose();
            directory.Dispose();
            throw;
        }
    }

    private async Task<WorkspaceActionStage> CreateStageWithPressureCleanupAsync(
        WorkspaceActionNativeExecutionRequest request,
        WorkspaceActionBeforeEvidence before,
        byte[] afterBytes,
        string afterHash,
        WorkspaceActionRetainedTargetSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CreateStageAsync(request, before, afterBytes, afterHash, session, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceActionArtifactCapacityException)
        {
            if (await CleanupOrphansAsync(AutomaticOrphanCleanupLimit, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw;
            }
            return await CreateStageAsync(request, before, afterBytes, afterHash, session, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(WorkspaceActionEntryKind EntryKind, string? NativeIdentityFingerprint, string? ContentHash, long ByteCount, long GovernedVersion)?> CapturePreparedStateAsync(
        WorkspaceActionInput input,
        WorkspaceActionRetainedTargetSession session,
        CancellationToken cancellationToken)
    {
        if (input.Precondition.Kind == WorkspaceActionPreconditionKind.ExpectedAbsent)
        {
            return session.Exists
                ? null
                : (WorkspaceActionEntryKind.Absent, null, null, 0, 0);
        }
        if (!session.Exists || session.TargetIdentity is null)
        {
            return null;
        }
        var bytes = await session.ReadTargetBytesAsync(WorkspaceActionContractLimits.MaxBeforeImageBytes, cancellationToken).ConfigureAwait(false);
        var contentHash = Sha256(bytes);
        if (input.Precondition.Kind == WorkspaceActionPreconditionKind.ExpectedContentHash)
        {
            return string.Equals(contentHash, input.Precondition.ExpectedContentHash, StringComparison.Ordinal)
                ? (WorkspaceActionEntryKind.RegularFile, session.TargetIdentity.Value.Fingerprint, contentHash, bytes.LongLength, 0)
                : null;
        }
        var prior = await _evidence.ReadAfterAsync(input.Precondition.PriorAfterEvidenceId!, cancellationToken).ConfigureAwait(false);
        if (WorkspaceActionEvidenceContract.ValidateAfter(prior) is not null
            || !string.Equals(prior!.ContentHashOfRecord, input.Precondition.PriorAfterEvidenceHash, StringComparison.Ordinal)
            || prior.GovernedVersion != input.Precondition.ExpectedGovernedVersion
            || prior.EntryKind != WorkspaceActionEntryKind.RegularFile
            || !string.Equals(prior.ScopeId, _scopeId.Value, StringComparison.Ordinal)
            || !string.Equals(prior.TargetReference, input.Target.Value, StringComparison.Ordinal)
            || !string.Equals(prior.TargetFingerprint, session.TargetFingerprint, StringComparison.Ordinal)
            || !string.Equals(prior.NativeIdentityFingerprint, session.TargetIdentity.Value.Fingerprint, StringComparison.Ordinal)
            || !string.Equals(prior.ContentHash, contentHash, StringComparison.Ordinal)
            || prior.ByteCount != bytes.LongLength)
        {
            return null;
        }
        if (_committedAfterEvidence is null
            || !await _committedAfterEvidence.IsCommittedAsync(
                prior.EffectId,
                prior.IdempotencyOperationId,
                prior.EffectGeneration,
                prior.EvidenceId,
                prior.ContentHashOfRecord,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return (WorkspaceActionEntryKind.RegularFile, session.TargetIdentity.Value.Fingerprint, contentHash, bytes.LongLength, prior.GovernedVersion);
    }

    private string ComputeNamespaceLeaseKey(string rootIdentityFingerprint, string parentIdentityFingerprint)
    {
        if (!WorkspaceActionFingerprint.IsCanonicalSha256(rootIdentityFingerprint)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(parentIdentityFingerprint))
        {
            throw new ArgumentException("Workspace action namespace lease identities are invalid.");
        }
        return WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-parent-namespace-lease.v1",
            rootIdentityFingerprint,
            _scopeId.Value,
            parentIdentityFingerprint);
    }

    private string ComputeExecutionLeaseKey(WorkspaceActionBeforeEvidence before)
        => before.EntryKind == WorkspaceActionEntryKind.Absent
            ? ComputeNamespaceLeaseKey(before.RootIdentityFingerprint, before.ParentIdentityFingerprint)
            : before.TargetFingerprint;

    private async Task<WorkspaceActionTargetLease?> TryAcquireOperationLeaseAsync(string namespaceFingerprint, CancellationToken cancellationToken)
    {
        if (!WorkspaceActionFingerprint.IsCanonicalSha256(namespaceFingerprint))
        {
            return null;
        }
        PreparePrivateRoot(_locksRoot);
        var shard = uint.Parse(namespaceFingerprint.AsSpan(0, 8), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)
            % checked((uint)_quota.MaximumStagingEntries);
        var lockName = $"shard-{shard:D4}.lock";
        var startedAt = _timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                WorkspaceActionPrivateArtifactLockLease? ownership = await _guard.AcquireExclusiveReadLockAsync(
                    _locksRoot,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    var entries = _guard.EnumerateNames(ownership, _quota.MaximumStagingEntries + 2).ToArray();
                    if (entries.Length > _quota.MaximumStagingEntries + 1)
                    {
                        throw new FormatException("Workspace action target-lock storage is malformed or exceeds its fixed shard bound.");
                    }
                    foreach (var name in entries)
                    {
                        if (string.Equals(name, ".custom-loop-mutations.lock", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        if (!IsTargetLockName(name))
                        {
                            throw new FormatException("Workspace action target-lock storage is malformed or exceeds its fixed shard bound.");
                        }
                        using var retained = WorkspaceActionNativeFileSystem.OpenRelativeFileForUpdate(
                            ownership.DirectoryHandle,
                            name,
                            allowMissing: false,
                            create: false,
                            shareForLocking: true,
                            denyDeleteSharing: true,
                            requireDeleteAccess: false)!;
                        if (RandomAccess.GetLength(retained) != 0)
                        {
                            throw new FormatException("Workspace action target-lock storage is malformed or exceeds its fixed shard bound.");
                        }
                    }
                    SafeFileHandle? handle = WorkspaceActionNativeFileSystem.OpenRelativeFileForUpdate(
                        ownership.DirectoryHandle,
                        lockName,
                        allowMissing: false,
                        create: true,
                        shareForLocking: true,
                        denyDeleteSharing: true,
                        requireDeleteAccess: false)!;
                    try
                    {
                        var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
                        handle = null;
                        if (stream.Length != 0)
                        {
                            stream.Dispose();
                            throw new FormatException("Workspace action target ownership evidence must remain value-free.");
                        }
                        if (!CustomLoopCrossProcessFileLock.TryAcquire(stream))
                        {
                            stream.Dispose();
                            throw new IOException("Workspace action target ownership is held by another process.");
                        }
                        var retainedNamespaceOwnership = OperatingSystem.IsWindows() ? null : ownership;
                        if (retainedNamespaceOwnership is not null)
                        {
                            ownership = null;
                        }
                        return new WorkspaceActionTargetLease(stream, retainedNamespaceOwnership);
                    }
                    finally
                    {
                        handle?.Dispose();
                    }
                }
                finally
                {
                    ownership?.Dispose();
                }
            }
            catch (IOException)
            {
                if (_timeProvider.GetElapsedTime(startedAt) >= _operationLeaseWaitLimit)
                {
                    return null;
                }
                await Task.Delay(_targetLeaseRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (exception.InnerException is IOException)
            {
                if (_timeProvider.GetElapsedTime(startedAt) >= _operationLeaseWaitLimit)
                {
                    return null;
                }
                await Task.Delay(_targetLeaseRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool IsTargetLockName(string name)
    {
        if (!name.StartsWith("shard-", StringComparison.Ordinal)
            || !name.EndsWith(".lock", StringComparison.Ordinal)
            || name.Length != "shard-0000.lock".Length
            || !int.TryParse(name.AsSpan("shard-".Length, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var shard))
        {
            return false;
        }
        return shard >= 0 && shard < _quota.MaximumStagingEntries;
    }

    private async Task<WorkspaceActionAttemptArtifactMarker> ReserveQuarantineAsync(WorkspaceActionAttemptArtifactMarker marker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.Kind != WorkspaceActionAttemptArtifactKind.QuarantineReservation
            || marker.ByteCount is < 0 or > WorkspaceActionContractLimits.MaxBeforeImageBytes)
        {
            throw new ArgumentException("Workspace delete quarantine reservation is invalid.", nameof(marker));
        }
        var quarantineReference = marker.ArtifactReference;
        var incomingBytes = marker.ByteCount;
        PreparePrivateRoot(_quarantineRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_quarantineRoot, cancellationToken).ConfigureAwait(false);
        var maximumEntries = checked(_quota.MaximumTombstones * 2 + 1);
        var entries = _guard.EnumerateNames(ownership, maximumEntries + _quota.MaximumTombstones + 1).ToArray();
        if (entries.Length > maximumEntries + _quota.MaximumTombstones)
        {
            throw new WorkspaceActionArtifactCapacityException("Workspace delete quarantine entry capacity is exhausted.");
        }
        entries = RemoveAtomicMarkerTemporariesUnderLock(
            ownership,
            entries,
            WorkspaceActionAttemptArtifactKind.QuarantineReservation,
            _quota.MaximumTombstones);
        if (entries.Length > maximumEntries)
        {
            throw new WorkspaceActionArtifactCapacityException("Workspace delete quarantine entry capacity is exhausted.");
        }
        var payloads = new Dictionary<string, long>(StringComparer.Ordinal);
        var reservations = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = entry;
            if (string.Equals(name, ".custom-loop-mutations.lock", StringComparison.Ordinal))
            {
                continue;
            }
            using var retained = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                ownership.DirectoryHandle,
                name,
                allowMissing: false,
                write: false)!;

            if (name.EndsWith(".payload", StringComparison.Ordinal))
            {
                var reference = name[..^".payload".Length];
                if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(reference)
                    || !payloads.TryAdd(reference, RandomAccess.GetLength(retained)))
                {
                    throw new FormatException("Workspace delete quarantine contains an invalid payload identity.");
                }
                continue;
            }
            if (name.EndsWith(".reservation", StringComparison.Ordinal))
            {
                var reference = name[..^".reservation".Length];
                var retainedMarker = await ReadMarkerAsync(ownership, name, cancellationToken).ConfigureAwait(false);
                if (retainedMarker is null
                    || retainedMarker.Kind != WorkspaceActionAttemptArtifactKind.QuarantineReservation
                    || !string.Equals(retainedMarker.ArtifactReference, reference, StringComparison.Ordinal)
                    || !reservations.TryAdd(reference, retainedMarker.ByteCount))
                {
                    throw new FormatException("Workspace delete quarantine contains an invalid reservation.");
                }
                continue;
            }
            throw new FormatException("Workspace delete quarantine contains an unsupported artifact.");
        }

        var retainedReferences = new HashSet<string>(payloads.Keys, StringComparer.Ordinal);
        retainedReferences.UnionWith(reservations.Keys);
        var retainedBytes = payloads.Values.Aggregate(0L, checked((total, value) => total + value));
        retainedBytes = reservations
            .Where(item => !payloads.ContainsKey(item.Key))
            .Aggregate(retainedBytes, checked((total, item) => total + item.Value));
        if (reservations.TryGetValue(quarantineReference, out var retainedReservationBytes)
            && retainedReservationBytes != incomingBytes)
        {
            throw new FormatException("Workspace delete quarantine reservation conflicts with its retained byte budget.");
        }
        var alreadyRetained = retainedReferences.Contains(quarantineReference);
        if (!alreadyRetained
            && (retainedReferences.Count >= _quota.MaximumTombstones
                || retainedBytes > _quota.MaximumQuarantineBytes - incomingBytes))
        {
            throw new WorkspaceActionArtifactCapacityException("Workspace delete quarantine capacity is exhausted.");
        }
        return await RetainMarkerAsync(
            ownership,
            quarantineReference + ".reservation",
            marker,
            cancellationToken).ConfigureAwait(false);
    }

    private void PreparePrivateRoot(string root)
    {
        _guard.PrepareRoot(root);
        WorkspaceActionPrivatePermissions.RequireDirectory(_rootPath, root);
    }

    private async Task<WorkspaceActionAttemptArtifactMarker> RetainMarkerAsync(
        WorkspaceActionPrivateArtifactLockLease ownership,
        string name,
        WorkspaceActionAttemptArtifactMarker marker,
        CancellationToken cancellationToken)
    {
        var canonical = marker.Encode();
        if (!await _guard.WriteTextAtomicallyIfAbsentAsync(ownership, name, canonical, cancellationToken).ConfigureAwait(false))
        {
            var retained = await ReadMarkerAsync(ownership, name, cancellationToken).ConfigureAwait(false);
            if (retained is null || !retained.HasSameArtifactBinding(marker))
            {
                throw new FormatException("Workspace action attempt artifact marker conflicts with retained evidence.");
            }
            return retained;
        }
        return marker;
    }

    private async Task<WorkspaceActionAttemptArtifactMarker> ReserveQuarantineWithPressureCleanupAsync(
        WorkspaceActionAttemptArtifactMarker marker,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReserveQuarantineAsync(marker, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceActionArtifactCapacityException)
        {
            if (await CleanupOrphansAsync(AutomaticOrphanCleanupLimit, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw;
            }
            return await ReserveQuarantineAsync(marker, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<WorkspaceActionAttemptArtifactMarker?> ReadMarkerAsync(
        WorkspaceActionPrivateArtifactLockLease ownership,
        string name,
        CancellationToken cancellationToken)
    {
        var encoded = await _guard.ReadAllBytesAsync(
            ownership,
            name,
            WorkspaceActionAttemptArtifactMarker.MaximumEncodedBytes,
            "Workspace action attempt artifact marker",
            cancellationToken).ConfigureAwait(false);
        return WorkspaceActionAttemptArtifactMarker.TryDecode(encoded, out var marker) ? marker : null;
    }

    private void DeleteStageMarker(WorkspaceActionStage stage)
    {
        WorkspaceActionNativeFileSystem.DeleteExact(
            stage.Directory,
            stage.MarkerName,
            stage.Marker,
            stage.MarkerIdentity);
    }

    private async Task DeleteAuthenticatedMarkerAsync(
        string root,
        string markerName,
        WorkspaceActionAttemptArtifactMarker expected,
        CancellationToken cancellationToken)
    {
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(root, cancellationToken).ConfigureAwait(false);
        if (!_guard.FileExists(ownership, markerName))
        {
            return;
        }
        var retained = await ReadMarkerAsync(ownership, markerName, cancellationToken).ConfigureAwait(false);
        if (retained is null || !Equals(retained, expected))
        {
            throw new FormatException("Workspace action cleanup refused a marker without its exact authenticated attempt binding.");
        }
        using var marker = WorkspaceActionNativeFileSystem.OpenRelativeFile(ownership.DirectoryHandle, markerName, allowMissing: false, write: true)!;
        var identity = WorkspaceActionNativeFileSystem.GetIdentity(marker);
        WorkspaceActionNativeFileSystem.DeleteExact(ownership.DirectoryHandle, markerName, marker, identity);
        WorkspaceActionNativeFileSystem.FlushDirectory(ownership.DirectoryHandle);
    }

    private async Task<IReadOnlyList<WorkspaceActionCleanupCandidate>> ReadCleanupCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<WorkspaceActionCleanupCandidate>();
        await ReadCleanupRootAsync(
            _stagingRoot,
            WorkspaceActionAttemptArtifactKind.Stage,
            ".stage.marker",
            checked(_quota.MaximumStagingEntries * 2 + 1),
            candidates,
            cancellationToken).ConfigureAwait(false);
        await ReadCleanupRootAsync(
            _quarantineRoot,
            WorkspaceActionAttemptArtifactKind.QuarantineReservation,
            ".reservation",
            checked(_quota.MaximumTombstones * 2 + 1),
            candidates,
            cancellationToken).ConfigureAwait(false);
        return candidates;
    }

    private async Task ReadCleanupRootAsync(
        string root,
        WorkspaceActionAttemptArtifactKind kind,
        string markerSuffix,
        int maximumEntries,
        List<WorkspaceActionCleanupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        PreparePrivateRoot(root);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(root, cancellationToken).ConfigureAwait(false);
        var maximumTemporaries = Math.Max(1, (maximumEntries - 1) / 2);
        var entries = _guard.EnumerateNames(ownership, maximumEntries + maximumTemporaries + 1).ToArray();
        if (entries.Length > maximumEntries + maximumTemporaries)
        {
            throw new FormatException("Private workspace action artifact storage exceeds its closed bounded shape.");
        }
        entries = RemoveAtomicMarkerTemporariesUnderLock(ownership, entries, kind, maximumTemporaries);
        if (entries.Length > maximumEntries)
        {
            throw new FormatException("Private workspace action artifact storage exceeds its closed bounded shape.");
        }
        var names = entries.ToHashSet(StringComparer.Ordinal);
        foreach (var name in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(name, ".custom-loop-mutations.lock", StringComparison.Ordinal))
            {
                continue;
            }
            using var retained = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                ownership.DirectoryHandle,
                name,
                allowMissing: false,
                write: false)!;
            if (!name.EndsWith(markerSuffix, StringComparison.Ordinal))
            {
                var expectedMarkerName = kind == WorkspaceActionAttemptArtifactKind.Stage
                    ? name.EndsWith(".stage.displaced", StringComparison.Ordinal)
                        ? name[..^".displaced".Length] + ".marker"
                        : name + ".marker"
                    : name.EndsWith(".payload", StringComparison.Ordinal)
                        ? name[..^".payload".Length] + ".reservation"
                        : string.Empty;
                if (expectedMarkerName.Length == 0 || !names.Contains(expectedMarkerName))
                {
                    throw new FormatException("Private workspace action storage contains an unauthenticated artifact.");
                }
                continue;
            }
            var marker = await ReadMarkerAsync(ownership, name, cancellationToken).ConfigureAwait(false);
            if (marker is null
                || marker.Kind != kind
                || kind == WorkspaceActionAttemptArtifactKind.Stage
                    && !string.Equals(name, marker.ArtifactReference + ".marker", StringComparison.Ordinal)
                || kind == WorkspaceActionAttemptArtifactKind.QuarantineReservation
                    && !string.Equals(name, marker.ArtifactReference + ".reservation", StringComparison.Ordinal))
            {
                throw new FormatException("Private workspace action storage contains a marker with a mismatched authenticated identity.");
            }
            candidates.Add(new WorkspaceActionCleanupCandidate(root, name, marker));
        }
    }

    private async Task<bool> IsCleanupCandidateEligibleAsync(
        WorkspaceActionCleanupCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (candidate.Marker.Kind != WorkspaceActionAttemptArtifactKind.QuarantineReservation)
        {
            return true;
        }
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(candidate.Root, cancellationToken).ConfigureAwait(false);
        if (!_guard.FileExists(ownership, candidate.Marker.ArtifactReference + ".payload"))
        {
            return true;
        }
        var tombstone = await FindExactTombstoneAsync(candidate.Marker, cancellationToken).ConfigureAwait(false);
        return tombstone is not null && tombstone.RetainUntilUtc <= now;
    }

    private string[] RemoveAtomicMarkerTemporariesUnderLock(
        WorkspaceActionPrivateArtifactLockLease ownership,
        IReadOnlyList<string> entries,
        WorkspaceActionAttemptArtifactKind kind,
        int maximumTemporaries)
    {
        var temporaries = entries
            .Where(name => IsAtomicMarkerTemporaryName(name, kind))
            .ToArray();
        if (temporaries.Length > maximumTemporaries)
        {
            throw new FormatException("Private workspace action crash-recovery artifacts exceed their finite bound.");
        }
        if (temporaries.Length == 0)
        {
            return entries.ToArray();
        }
        foreach (var name in temporaries)
        {
            using var temporary = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                ownership.DirectoryHandle,
                name,
                allowMissing: false,
                write: true)!;
            var identity = WorkspaceActionNativeFileSystem.GetIdentity(temporary);
            WorkspaceActionNativeFileSystem.DeleteExact(ownership.DirectoryHandle, name, temporary, identity);
        }
        WorkspaceActionNativeFileSystem.FlushDirectory(ownership.DirectoryHandle);
        return entries.Except(temporaries, StringComparer.Ordinal).ToArray();
    }

    private static bool IsAtomicMarkerTemporaryName(string name, WorkspaceActionAttemptArtifactKind kind)
    {
        if (name.Length < 1 || name[0] != '.' || !name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }
        var withoutSuffix = name.AsSpan(1, name.Length - ".tmp".Length - 1);
        var separator = withoutSuffix.LastIndexOf('.');
        if (separator < 1)
        {
            return false;
        }
        var destinationName = withoutSuffix[..separator];
        var nonce = withoutSuffix[(separator + 1)..];
        var expectedPrefix = kind == WorkspaceActionAttemptArtifactKind.Stage ? "stage-" : "quarantine-";
        var expectedSuffix = kind == WorkspaceActionAttemptArtifactKind.Stage ? ".stage.marker" : ".reservation";
        if (!destinationName.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || !destinationName.EndsWith(expectedSuffix, StringComparison.Ordinal)
            || destinationName.Length <= expectedPrefix.Length + expectedSuffix.Length)
        {
            return false;
        }
        var hash = destinationName[expectedPrefix.Length..^expectedSuffix.Length];
        return WorkspaceActionFingerprint.IsCanonicalSha256(hash.ToString())
            && nonce.Length == 32
            && IsLowerHex(nonce);
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private async Task<bool> TryDeleteOrphanCandidateAsync(
        WorkspaceActionCleanupCandidate candidate,
        WorkspaceActionBeforeEvidence before,
        bool artifactReleased,
        CancellationToken cancellationToken)
    {
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(candidate.Root, cancellationToken).ConfigureAwait(false);
        if (!_guard.FileExists(ownership, candidate.MarkerName))
        {
            return false;
        }
        var retainedMarker = await ReadMarkerAsync(ownership, candidate.MarkerName, cancellationToken).ConfigureAwait(false);
        if (retainedMarker is null || !Equals(retainedMarker, candidate.Marker))
        {
            return false;
        }
        if (candidate.Marker.Kind == WorkspaceActionAttemptArtifactKind.Stage
            && !_guard.FileExists(ownership, candidate.Marker.ArtifactReference)
            && _guard.FileExists(ownership, candidate.Marker.ArtifactReference + ".displaced"))
        {
            return false;
        }
        var payloadName = candidate.Marker.Kind == WorkspaceActionAttemptArtifactKind.Stage
            ? _guard.FileExists(ownership, candidate.Marker.ArtifactReference)
                ? candidate.Marker.ArtifactReference
                : candidate.Marker.ArtifactReference + ".displaced"
            : candidate.Marker.ArtifactReference + ".payload";
        WorkspaceActionTombstone? expiredTombstone = null;
        if (candidate.Marker.Kind == WorkspaceActionAttemptArtifactKind.QuarantineReservation && artifactReleased)
        {
            expiredTombstone = await FindExactTombstoneAsync(candidate.Marker, cancellationToken).ConfigureAwait(false);
            if (expiredTombstone is not null && expiredTombstone.RetainUntilUtc > UtcNow())
            {
                return false;
            }
        }
        using var payload = WorkspaceActionNativeFileSystem.OpenRelativeFile(ownership.DirectoryHandle, payloadName, allowMissing: true, write: true);
        if (payload is not null)
        {
            var identity = WorkspaceActionNativeFileSystem.GetIdentity(payload);
            var bytes = await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(
                payload,
                WorkspaceActionContractLimits.MaxAfterImageBytes,
                cancellationToken).ConfigureAwait(false);
            var contentHash = Sha256(bytes);
            if (candidate.Marker.Kind == WorkspaceActionAttemptArtifactKind.Stage)
            {
                var exactAfterImage = bytes.LongLength == candidate.Marker.ByteCount
                    && string.Equals(contentHash, candidate.Marker.ContentHash, StringComparison.Ordinal);
                var exactReleasedBeforeImage = artifactReleased
                    && string.Equals(identity.Fingerprint, before.NativeIdentityFingerprint, StringComparison.Ordinal)
                    && bytes.LongLength == before.ByteCount
                    && string.Equals(contentHash, before.ContentHash, StringComparison.Ordinal);
                if (!exactAfterImage && !exactReleasedBeforeImage)
                {
                    throw new FormatException("Authenticated workspace action staging content matches neither its after image nor one released displaced before image.");
                }
            }
            else if (string.Equals(identity.Fingerprint, before.NativeIdentityFingerprint, StringComparison.Ordinal)
                && bytes.LongLength == before.ByteCount
                && string.Equals(contentHash, before.ContentHash, StringComparison.Ordinal)
                && (!artifactReleased || expiredTombstone is null))
            {
                // The exact original target reached quarantine, so the namespace boundary may have crossed.
                return false;
            }
            WorkspaceActionNativeFileSystem.DeleteExact(ownership.DirectoryHandle, payloadName, payload, identity);
        }
        if (expiredTombstone is not null
            && !await _evidence.DeleteExactTombstoneAsync(expiredTombstone, cancellationToken).ConfigureAwait(false))
        {
            throw new FormatException("Expired workspace delete retention could not remove its exact authenticated tombstone.");
        }
        using var marker = WorkspaceActionNativeFileSystem.OpenRelativeFile(
            ownership.DirectoryHandle,
            candidate.MarkerName,
            allowMissing: false,
            write: true)!;
        var markerIdentity = WorkspaceActionNativeFileSystem.GetIdentity(marker);
        WorkspaceActionNativeFileSystem.DeleteExact(ownership.DirectoryHandle, candidate.MarkerName, marker, markerIdentity);
        WorkspaceActionNativeFileSystem.FlushDirectory(ownership.DirectoryHandle);
        return true;
    }

    private async Task<WorkspaceActionTombstone?> FindExactTombstoneAsync(
        WorkspaceActionAttemptArtifactMarker marker,
        CancellationToken cancellationToken)
    {
        var tombstone = await _evidence.FindTombstoneAsync(
            marker.BeforeEvidenceId,
            marker.ArtifactReference,
            marker.EffectId,
            marker.IdempotencyOperationId,
            marker.EffectGeneration,
            cancellationToken).ConfigureAwait(false);
        return tombstone is not null
            && string.Equals(tombstone.TargetFingerprint, marker.TargetFingerprint, StringComparison.Ordinal)
            && string.Equals(tombstone.TargetReference, marker.TargetReference, StringComparison.Ordinal)
            && string.Equals(tombstone.ContentHash, marker.ContentHash, StringComparison.Ordinal)
            && tombstone.ByteCount == marker.ByteCount
                ? tombstone
                : null;
    }

    private async Task<bool> ProveDeleteOutcomeAsync(WorkspaceActionAfterEvidence after, CancellationToken cancellationToken)
    {
        if (after.TombstoneReference is null || after.QuarantineReference is null)
        {
            return false;
        }
        var tombstone = await _evidence.ReadTombstoneAsync(after.TombstoneReference, cancellationToken).ConfigureAwait(false);
        if (WorkspaceActionEvidenceContract.ValidateTombstone(tombstone) is not null
            || !string.Equals(tombstone!.BeforeEvidenceId, after.BeforeEvidenceId, StringComparison.Ordinal)
            || !string.Equals(tombstone.QuarantineReference, after.QuarantineReference, StringComparison.Ordinal))
        {
            return false;
        }
        using var directory = WorkspaceActionNativeFileSystem.OpenPrivateDirectoryUnderWorkspace(_rootPath, _quarantineRoot);
        using var payload = WorkspaceActionNativeFileSystem.OpenRelativeFile(directory, tombstone.QuarantineReference + ".payload", allowMissing: true, write: false);
        if (payload is null
            || !string.Equals(WorkspaceActionNativeFileSystem.GetIdentity(payload).Fingerprint, tombstone.NativeIdentityFingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        var bytes = await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(payload, WorkspaceActionContractLimits.MaxBeforeImageBytes, cancellationToken).ConfigureAwait(false);
        return bytes.LongLength == tombstone.ByteCount && string.Equals(Sha256(bytes), tombstone.ContentHash, StringComparison.Ordinal);
    }

    private static bool OutcomeMatchesAfter(WorkspaceActionOutcomeEvidence outcome, WorkspaceActionAfterEvidence after)
        => WorkspaceActionEvidenceContract.ValidateOutcome(outcome) is null
            && WorkspaceActionEvidenceContract.ValidateAfter(after) is null
            && string.Equals(outcome.BeforeEvidenceId, after.BeforeEvidenceId, StringComparison.Ordinal)
            && string.Equals(outcome.AfterEvidenceId, after.EvidenceId, StringComparison.Ordinal)
            && string.Equals(outcome.AfterEvidenceHash, after.ContentHashOfRecord, StringComparison.Ordinal)
            && string.Equals(outcome.OperationId, after.OperationId, StringComparison.Ordinal)
            && string.Equals(outcome.EffectId, after.EffectId, StringComparison.Ordinal)
            && string.Equals(outcome.IdempotencyOperationId, after.IdempotencyOperationId, StringComparison.Ordinal)
            && outcome.EffectGeneration == after.EffectGeneration
            && string.Equals(outcome.TargetFingerprint, after.TargetFingerprint, StringComparison.Ordinal)
            && outcome.GovernedVersion == after.GovernedVersion
            && string.Equals(outcome.TombstoneReference, after.TombstoneReference, StringComparison.Ordinal)
            && outcome.ObservedAtUtc == after.ObservedAtUtc;

    private bool TryCaptureInput(WorkspaceActionInput input, out WorkspaceActionInput? captured)
    {
        captured = null;
        try
        {
            var canonical = WorkspaceActionInputContract.Encode(input);
            if (!WorkspaceActionInputContract.TryParse(canonical, input.Kind, out var parsed, out _)
                || !Equals(parsed!.ScopeId, _scopeId)
                || WorkspaceActionInputContract.RequiresCredentialBridge(parsed))
            {
                return false;
            }
            captured = parsed;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool MatchesInput(WorkspaceActionBeforeEvidence? before, WorkspaceActionInput input)
        => WorkspaceActionEvidenceContract.ValidateBefore(before) is null
            && string.Equals(before!.ScopeId, input.ScopeId.Value, StringComparison.Ordinal)
            && string.Equals(before.TargetReference, input.Target.Value, StringComparison.Ordinal)
            && string.Equals(before.PreconditionEvidenceHash, WorkspaceActionInputContract.ComputePreconditionHash(input.Precondition), StringComparison.Ordinal);

    private Task<WorkspaceActionPermissionRevalidation> RevalidatePermissionAsync(
        WorkspaceActionInput input,
        WorkspaceActionEntryKind entryKind,
        EmbodySense.Core.Common.Governance.Permissions.Models.FileSystemOperation operation,
        WorkspaceActionRetainedTargetSession session,
        CancellationToken cancellationToken)
        => _permissionRevalidator.RevalidateAsync(
            new WorkspaceActionPermissionRevalidationRequest(
                input,
                entryKind,
                operation,
                session.TargetFingerprint,
                session.RootIdentity.Fingerprint,
                session.ParentIdentity.Fingerprint,
                session.TargetIdentity?.Fingerprint),
            cancellationToken);

    private static bool MatchesRetainedPermission(
        WorkspaceActionBeforeEvidence before,
        WorkspaceActionPermissionRevalidation permission)
        => permission.IsAllowed
            && permission.Operation == before.PermissionOperation
            && string.Equals(permission.PolicyHash, before.PermissionPolicyHash, StringComparison.Ordinal);

    private string AbsoluteTarget(WorkspaceRelativeFileTarget target)
        => Path.Combine(_rootPath, target.Value.Replace('/', Path.DirectorySeparatorChar));

    private DateTimeOffset UtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        if (now == default || now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Trusted UTC time is unavailable for workspace action evidence.");
        }
        return now;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[checked(first.Length + second.Length)];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record WorkspaceActionCleanupCandidate(
        string Root,
        string MarkerName,
        WorkspaceActionAttemptArtifactMarker Marker);
}
