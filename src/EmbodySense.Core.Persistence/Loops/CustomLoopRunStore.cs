using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Persists canonical version-1 run traces, lifecycle updates, discovery metadata, deletion receipts, and tombstones.
/// </summary>
/// <remarks>
/// Run mutation uses optimistic lifecycle versions plus process-local and cross-process serialization. Artifacts are bounded,
/// written through sibling temporary files, flushed, and renamed at the single-file commit boundary. Canonical lifecycle,
/// append-only run evidence, and the hash-bound execution frontier therefore become visible atomically in the same artifact;
/// a frontier cannot advance before its referenced outcome event is retained. The discovery index is
/// derived acceleration data and is repaired from canonical artifacts when safely possible; unsupported index versions remain
/// explicit failures. Duplicate identities, corrupt JSON, unknown fields, unsupported run shapes, broken evidence ordering, or
/// ambiguous recovery state throw <see cref="FormatException"/>. No legacy run reader or automatic schema migration is provided.
/// </remarks>
public sealed class CustomLoopRunStore :
    ICustomLoopRunStore,
    IGovernedLoopSequentialOrderedNodeEvidenceRecorder,
    IGovernedLoopSequentialRunEvidenceSource,
    IDisposable
{
    private const string MutationLockFileName = ".custom-loop-runs.lock";
    private const string DiscoveryIndexFileName = ".custom-loop-run-index.json";
    private const string DiscoveryIndexPendingFileName = ".custom-loop-run-index.pending";
    private const string ScheduleAdmissionRetirementFileName = ".schedule-admission-retirements.json";
    private const int MaximumScheduleAdmissionInterruptedWriteArtifacts = 32;
    private const int MaximumAtomicMoveAttempts = 41;
    private static readonly byte[] _discoveryIndexPendingContent = "pending\n"u8.ToArray();
    private static readonly TimeSpan _atomicMoveRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan _discoveryIndexMaintenanceTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _processMutationGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = CustomLoopJsonDepthPolicy.CanonicalRunArtifactMaximumDepth,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly WorkspacePaths _paths;
    private readonly string _workspaceRoot;
    private readonly string _runsRoot;
    private readonly string _traceDeletionOperationsRoot;
    private readonly string _scheduleAdmissionsRoot;
    private readonly string _scheduleAdmissionRetirementPath;
    private readonly string _mutationLockPath;
    private readonly string _discoveryIndexPath;
    private readonly string _discoveryIndexPendingPath;
    private readonly SemaphoreSlim _processMutationGate;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, FileSystemWatcher> _monitorWatcherFactory;
    private readonly object _monitorCacheGate = new();
    private readonly Dictionary<string, long> _monitorArtifactChangeVersions;
    private readonly HashSet<string> _monitorArtifactPaths;
    private readonly HashSet<string> _verifiedMonitorSummaryBindings = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, MonitorRunOwnership>? _monitorRunOwnerships;
    private DiscoveryIndexFileFingerprint? _monitorCacheFingerprint;
    private IReadOnlyDictionary<string, CustomLoopRunDiscoveryIndexEntry>? _monitorCache;
    private FileSystemWatcher? _monitorWatcher;
    private bool _monitorWatcherUncertain;
    private long _monitorArtifactChangeVersion;
    private long _monitorArtifactTopologyVersion;
    private long _monitorRunOwnershipVersion = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopRunStore"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="timeProvider">The time provider.</param>
    public CustomLoopRunStore(WorkspacePaths paths, TimeProvider? timeProvider = null) : this(paths, timeProvider, static path => new FileSystemWatcher(path))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopRunStore"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="monitorWatcherFactory">The monitor watcher factory.</param>
    public CustomLoopRunStore(WorkspacePaths paths, Func<string, FileSystemWatcher> monitorWatcherFactory) : this(paths, null, monitorWatcherFactory)
    {
    }

    private CustomLoopRunStore(WorkspacePaths paths, TimeProvider? timeProvider, Func<string, FileSystemWatcher> monitorWatcherFactory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(monitorWatcherFactory);

        _paths = paths;
        _workspaceRoot = Path.GetFullPath(paths.RootPath);
        _runsRoot = Path.GetFullPath(paths.CustomLoopRunsPath);
        _traceDeletionOperationsRoot = Path.GetFullPath(paths.CustomLoopTraceDeletionOperationsPath);
        _scheduleAdmissionsRoot = Path.GetFullPath(paths.CustomLoopScheduleAdmissionsPath);
        EnsureContained(_workspaceRoot, _runsRoot);
        EnsureContained(_workspaceRoot, _traceDeletionOperationsRoot);
        EnsureContained(_workspaceRoot, _scheduleAdmissionsRoot);
        _scheduleAdmissionRetirementPath = Path.Combine(_scheduleAdmissionsRoot, ScheduleAdmissionRetirementFileName);
        EnsureContained(_scheduleAdmissionsRoot, _scheduleAdmissionRetirementPath);
        _mutationLockPath = Path.Combine(_runsRoot, MutationLockFileName);
        _discoveryIndexPath = Path.Combine(_runsRoot, DiscoveryIndexFileName);
        _discoveryIndexPendingPath = Path.Combine(_runsRoot, DiscoveryIndexPendingFileName);
        _processMutationGate = _processMutationGates.GetOrAdd(_runsRoot, _ => new SemaphoreSlim(1, 1));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _monitorWatcherFactory = monitorWatcherFactory;
        _monitorArtifactChangeVersions = new Dictionary<string, long>(PathComparer);
        _monitorArtifactPaths = new HashSet<string>(PathComparer);
    }

    /// <summary>
    /// Admits and persists a new version-1 run while enforcing unique identities, one active run per loop, and trace capacity.
    /// </summary>
    /// <param name="run">The complete admitted run with lifecycle version one.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// A result describing creation, idempotent admission replay, identity/operation conflict, active-loop conflict, deletion
    /// conflict, or insufficient trace capacity.
    /// </returns>
    public async Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
    {
        ValidateCanonicalRun(run);
        if (run.LifecycleVersion != 1)
        {
            throw new ArgumentException("New custom loop runs must have lifecycle version 1.", nameof(run));
        }

        if (run.Status != CustomLoopRunStatus.Admitted)
        {
            throw new ArgumentException("New custom loop runs must begin in the Admitted lifecycle state.", nameof(run));
        }

        var serialized = SerializeBounded(run);
        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        // Scan canonical traces and tombstones while mutation ownership is held. Tombstoned identities remain reserved,
        // and any ambiguous active-run state fails closed before a new artifact becomes visible.
        CustomLoopRunRecord? operationMatch = null;
        CustomLoopRunRecord? runIdMatch = null;
        CustomLoopRunRecord? activeLoopRun = null;
        var multipleActiveLoopRuns = false;
        var deletedOperation = false;
        var deletedRunId = false;
        var scan = await ScanArtifactsAsync(artifact =>
        {
            if (artifact.Tombstone is { } tombstone)
            {
                deletedOperation |= string.Equals(tombstone.AdmissionOperationId, run.AdmissionOperationId, StringComparison.Ordinal);
                deletedRunId |= string.Equals(tombstone.RunId, run.Id, StringComparison.Ordinal);
                return;
            }

            var persisted = artifact.Run!;
            if (string.Equals(persisted.AdmissionOperationId, run.AdmissionOperationId, StringComparison.Ordinal))
            {
                operationMatch = persisted;
            }

            if (string.Equals(persisted.Id, run.Id, StringComparison.Ordinal))
            {
                runIdMatch = persisted;
            }

            if (string.Equals(persisted.LoopId, run.LoopId, StringComparison.Ordinal) && !persisted.IsTerminal)
            {
                multipleActiveLoopRuns |= activeLoopRun is not null;
                activeLoopRun ??= persisted;
            }
        }, cancellationToken);
        if (deletedOperation)
        {
            return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.DeletedIdentityConflict, null, null);
        }

        if (operationMatch is not null)
        {
            return SameAdmissionRequest(operationMatch, run)
                ? CustomLoopRunStoreResult.AlreadyCreated(operationMatch)
                : CustomLoopRunStoreResult.OperationConflict(operationMatch);
        }

        if (deletedRunId)
        {
            return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.DeletedIdentityConflict, null, null);
        }

        if (runIdMatch is not null)
        {
            return CustomLoopRunStoreResult.VersionConflict(runIdMatch, expectedLifecycleVersion: 0);
        }

        if (multipleActiveLoopRuns)
        {
            throw new FormatException($"Custom loop `{run.LoopId}` has more than one nonterminal run. The persisted state requires review.");
        }

        if (activeLoopRun is not null)
        {
            return CustomLoopRunStoreResult.NonterminalRunExists(activeLoopRun);
        }

        if (CalculateRequiredTraceCapacity(run, serialized.LongLength) > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            return CustomLoopRunStoreResult.LimitExceeded();
        }

        var quota = scan.Quota;
        if (quota.RetainedTraceCount >= quota.MaximumTraceCount
            || quota.AccountedTraceUtf8Bytes > quota.MaximumWorkspaceUtf8Bytes - quota.MaximumPerTraceUtf8Bytes)
        {
            return CustomLoopRunStoreResult.LimitExceeded();
        }

        var path = GetRunPath(run.LoopId, run.Id);
        EnsureSafeDirectory(Path.GetDirectoryName(path)!, create: true);
        EnsureSafeArtifactPath(path, mustExist: false);
        await WriteArtifactAsync(path, serialized, ToSummary(run), overwrite: false, cancellationToken);
        return CustomLoopRunStoreResult.Created(run);
    }

    /// <inheritdoc />
    public async Task<ScheduleRunAdmissionStoreResult> CreateScheduledAsync(
        CustomLoopRunRecord run,
        TriggerDeliveryEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ValidateCanonicalRun(run);
        if (run.LifecycleVersion != 1 || run.Status != CustomLoopRunStatus.Admitted)
        {
            throw new ArgumentException("New scheduled custom-loop runs must begin at admitted lifecycle version 1.", nameof(run));
        }

        if (!TryValidateScheduledRun(run, envelope, out var canonicalEnvelope, out var canonicalEnvelopeHash))
        {
            throw new ArgumentException("The scheduled run does not retain the exact authenticated schedule delivery and directive.", nameof(envelope));
        }

        var directive = envelope.ScheduleExecutionDirective!;
        var serialized = SerializeBounded(run);
        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var admissions = await ReadAllScheduleAdmissionsAsync(cancellationToken);
        var retirements = await ReadScheduleAdmissionRetirementsAsync(cancellationToken);
        (admissions, retirements) = await CompactScheduleAdmissionsAsync(admissions, retirements, cancellationToken);
        var existingEvidence = admissions.SingleOrDefault(item => string.Equals(item.CanonicalEnvelopeHash, canonicalEnvelopeHash, StringComparison.Ordinal));
        var deliveryEvidence = admissions.SingleOrDefault(item =>
            string.Equals(
                TriggerDelivery(item.CanonicalEnvelope).Value,
                envelope.DeliveryId.Value,
                StringComparison.Ordinal));

        if (deliveryEvidence is not null
            && !string.Equals(deliveryEvidence.CanonicalEnvelopeHash, canonicalEnvelopeHash, StringComparison.Ordinal))
        {
            return ScheduleResult(ScheduleRunAdmissionStoreStatus.Conflict, null, deliveryEvidence);
        }

        if (ConflictsWithScheduleDefinition(admissions, retirements, directive, out var definitionConflict))
        {
            return ScheduleResult(ScheduleRunAdmissionStoreStatus.Conflict, null, definitionConflict);
        }

        CustomLoopRunRecord? operationMatch = null;
        CustomLoopRunRecord? runIdMatch = null;
        CustomLoopRunRecord? activeLoopRun = null;
        CustomLoopRunRecord? exactScheduleRun = null;
        var multipleActiveLoopRuns = false;
        var multipleExactScheduleRuns = false;
        var deletedOperation = false;
        var deletedRunId = false;
        var scan = await ScanArtifactsAsync(artifact =>
        {
            if (artifact.Tombstone is { } tombstone)
            {
                deletedOperation |= string.Equals(tombstone.AdmissionOperationId, run.AdmissionOperationId, StringComparison.Ordinal);
                deletedRunId |= string.Equals(tombstone.RunId, run.Id, StringComparison.Ordinal);
                return;
            }

            var persisted = artifact.Run!;
            if (string.Equals(persisted.AdmissionOperationId, run.AdmissionOperationId, StringComparison.Ordinal))
            {
                operationMatch = persisted;
            }

            if (string.Equals(persisted.Id, run.Id, StringComparison.Ordinal))
            {
                runIdMatch = persisted;
            }

            if (string.Equals(persisted.LoopId, run.LoopId, StringComparison.Ordinal) && !persisted.IsTerminal)
            {
                multipleActiveLoopRuns |= activeLoopRun is not null;
                activeLoopRun ??= persisted;
            }

            if (MatchesScheduledEnvelope(persisted, canonicalEnvelope!, canonicalEnvelopeHash!))
            {
                multipleExactScheduleRuns |= exactScheduleRun is not null;
                exactScheduleRun ??= persisted;
            }
        }, cancellationToken);

        if (multipleActiveLoopRuns || multipleExactScheduleRuns)
        {
            throw new FormatException($"Custom loop `{run.LoopId}` has ambiguous nonterminal or schedule-delivery ownership. The persisted state requires review.");
        }

        if (existingEvidence is not null && !MatchesEvidenceEnvelope(existingEvidence, run.LoopId, canonicalEnvelope!, canonicalEnvelopeHash!))
        {
            return ScheduleResult(ScheduleRunAdmissionStoreStatus.Conflict, null, existingEvidence);
        }

        var currentDisposition = existingEvidence?.Attempts[^1].Disposition;
        if (currentDisposition == ScheduleRunAdmissionDisposition.RunCreated)
        {
            return exactScheduleRun is not null
                ? ScheduleResult(ScheduleRunAdmissionStoreStatus.Replayed, exactScheduleRun, existingEvidence)
                : throw new FormatException("Schedule run-admission evidence claims a materialized run that is not present in canonical run storage.");
        }

        if (currentDisposition is ScheduleRunAdmissionDisposition.OverlapSkipped or ScheduleRunAdmissionDisposition.DeferredOneSuppressed)
        {
            return ScheduleResult(
                currentDisposition == ScheduleRunAdmissionDisposition.OverlapSkipped
                    ? ScheduleRunAdmissionStoreStatus.OverlapSkipped
                    : ScheduleRunAdmissionStoreStatus.DeferredOneSuppressed,
                FindBlockingRun(existingEvidence!, activeLoopRun),
                existingEvidence);
        }

        if (existingEvidence?.Attempts.Count >= ScheduleRunAdmissionEvidenceLimits.MaxAttempts)
        {
            return ScheduleResult(ScheduleRunAdmissionStoreStatus.LimitExceeded, activeLoopRun, existingEvidence);
        }

        if (exactScheduleRun is not null)
        {
            if (existingEvidence is null && !HasScheduleAdmissionCapacity(admissions))
            {
                return ScheduleResult(ScheduleRunAdmissionStoreStatus.LimitExceeded, exactScheduleRun, null);
            }

            var recovered = await AppendScheduleAdmissionAsync(
                existingEvidence,
                canonicalEnvelope!,
                canonicalEnvelopeHash!,
                run.LoopId,
                exactScheduleRun.AdmissionOperationId,
                exactScheduleRun.Id,
                ScheduleRunAdmissionDisposition.RunCreated,
                null,
                cancellationToken);
            await CompactScheduleAdmissionsAsync(ReplaceScheduleAdmission(admissions, recovered), retirements, cancellationToken);
            return ScheduleResult(ScheduleRunAdmissionStoreStatus.Replayed, exactScheduleRun, recovered);
        }

        if (existingEvidence is null
            && retirements.Entries.SingleOrDefault(item => string.Equals(item.ScheduleId, directive.ScheduleId.Value, StringComparison.Ordinal)) is { } retirement
            && ScheduleRunAdmissionRetirementCodec.Covers(retirement, directive))
        {
            return ScheduleResult(ScheduleRunAdmissionStoreStatus.Retired, null, null);
        }

        if (deletedOperation || deletedRunId)
        {
            return ScheduleResult(ScheduleRunAdmissionStoreStatus.Conflict, null, existingEvidence);
        }

        if (operationMatch is not null && !SameAdmissionRequest(operationMatch, run)
            || runIdMatch is not null)
        {
            return ScheduleResult(ScheduleRunAdmissionStoreStatus.Conflict, operationMatch ?? runIdMatch, existingEvidence);
        }

        if (activeLoopRun is not null)
        {
            var disposition = directive.Overlap switch
            {
                ScheduleOverlapPolicy.Skip => ScheduleRunAdmissionDisposition.OverlapSkipped,
                ScheduleOverlapPolicy.DeferOne when HasOtherDeferredOccurrence(admissions, canonicalEnvelopeHash!, run.LoopId) => ScheduleRunAdmissionDisposition.DeferredOneSuppressed,
                ScheduleOverlapPolicy.DeferOne => ScheduleRunAdmissionDisposition.OverlapDeferred,
                ScheduleOverlapPolicy.Allow => ScheduleRunAdmissionDisposition.OverlapSerialized,
                _ => ScheduleRunAdmissionDisposition.Unknown,
            };
            if (disposition == ScheduleRunAdmissionDisposition.Unknown)
            {
                return ScheduleResult(ScheduleRunAdmissionStoreStatus.Conflict, activeLoopRun, existingEvidence);
            }

            if (existingEvidence is not null
                && currentDisposition == disposition
                && string.Equals(existingEvidence.Attempts[^1].BlockingRunId, activeLoopRun.Id, StringComparison.Ordinal))
            {
                return ScheduleResult(MapScheduleDisposition(disposition), activeLoopRun, existingEvidence);
            }

            if (existingEvidence is null && !HasScheduleAdmissionCapacity(admissions))
            {
                return ScheduleResult(ScheduleRunAdmissionStoreStatus.LimitExceeded, activeLoopRun, null);
            }

            var retained = await AppendScheduleAdmissionAsync(
                existingEvidence,
                canonicalEnvelope!,
                canonicalEnvelopeHash!,
                run.LoopId,
                run.AdmissionOperationId,
                run.Id,
                disposition,
                activeLoopRun.Id,
                cancellationToken);
            if (IsRetirableScheduleAdmission(retained))
            {
                await CompactScheduleAdmissionsAsync(ReplaceScheduleAdmission(admissions, retained), retirements, cancellationToken);
            }

            return ScheduleResult(MapScheduleDisposition(disposition), activeLoopRun, retained);
        }

        if (CalculateRequiredTraceCapacity(run, serialized.LongLength) > CustomLoopLimits.MaxRunTraceUtf8Bytes
            || scan.Quota.RetainedTraceCount >= scan.Quota.MaximumTraceCount
            || scan.Quota.AccountedTraceUtf8Bytes > scan.Quota.MaximumWorkspaceUtf8Bytes - scan.Quota.MaximumPerTraceUtf8Bytes
            || existingEvidence is null && !HasScheduleAdmissionCapacity(admissions))
        {
            return ScheduleResult(ScheduleRunAdmissionStoreStatus.LimitExceeded, null, existingEvidence);
        }

        var path = GetRunPath(run.LoopId, run.Id);
        EnsureSafeDirectory(Path.GetDirectoryName(path)!, create: true);
        EnsureSafeArtifactPath(path, mustExist: false);
        await WriteArtifactAsync(path, serialized, ToSummary(run), overwrite: false, cancellationToken);
        var evidence = await AppendScheduleAdmissionAsync(
            existingEvidence,
            canonicalEnvelope!,
            canonicalEnvelopeHash!,
            run.LoopId,
            run.AdmissionOperationId,
            run.Id,
            ScheduleRunAdmissionDisposition.RunCreated,
            null,
            cancellationToken);
        await CompactScheduleAdmissionsAsync(ReplaceScheduleAdmission(admissions, evidence), retirements, cancellationToken);
        return ScheduleResult(ScheduleRunAdmissionStoreStatus.Created, run, evidence);
    }

    /// <inheritdoc />
    public async Task<ScheduleRunAdmissionEvidence?> GetScheduleAdmissionAsync(
        TriggerDeliveryId deliveryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);
        var path = GetScheduleAdmissionPath(deliveryId);
        if (!File.Exists(path))
        {
            return null;
        }

        var content = await ReadBoundedJsonArtifactAsync(
            _scheduleAdmissionsRoot,
            path,
            ScheduleRunAdmissionEvidenceLimits.MaxArtifactUtf8Bytes,
            "Schedule run-admission evidence",
            cancellationToken);
        return ScheduleRunAdmissionEvidenceCodec.Deserialize(content);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScheduleRunAdmissionEvidence>> ListPendingScheduleAdmissionsAsync(
        TriggerDeliveryId? afterDeliveryId,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (afterDeliveryId is not null
            && (!TriggerDeliveryId.TryParse(afterDeliveryId.Value, out var parsedAfter)
                || !Equals(afterDeliveryId, parsedAfter)))
        {
            throw new ArgumentException("The schedule-admission cursor is malformed.", nameof(afterDeliveryId));
        }

        if (maximumCount is < 1 or > GovernedLoopBackgroundWorkContractLimits.MaxCandidatesPerFamily)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var admissions = await ReadAllScheduleAdmissionsAsync(cancellationToken);
        var candidates = admissions
            .Select(evidence => (Evidence: evidence, DeliveryId: TriggerDelivery(evidence.CanonicalEnvelope)))
            .Where(candidate => candidate.Evidence.Attempts[^1].Disposition is
                ScheduleRunAdmissionDisposition.OverlapDeferred or ScheduleRunAdmissionDisposition.OverlapSerialized)
            .Where(candidate => afterDeliveryId is null
                || string.Compare(candidate.DeliveryId.Value, afterDeliveryId.Value, StringComparison.Ordinal) > 0)
            .OrderBy(candidate => candidate.DeliveryId.Value, StringComparer.Ordinal)
            .Take(maximumCount)
            .Select(candidate => candidate.Evidence)
            .ToArray();
        return Array.AsReadOnly(candidates);
    }

    private async Task<ScheduleRunAdmissionEvidence> AppendScheduleAdmissionAsync(
        ScheduleRunAdmissionEvidence? existing,
        string canonicalEnvelope,
        string canonicalEnvelopeHash,
        string loopId,
        string admissionOperationId,
        string candidateRunId,
        ScheduleRunAdmissionDisposition disposition,
        string? blockingRunId,
        CancellationToken cancellationToken)
    {
        var priorAttempts = existing?.Attempts ?? [];
        if (priorAttempts.Count >= ScheduleRunAdmissionEvidenceLimits.MaxAttempts)
        {
            throw new InvalidOperationException("The bounded schedule run-admission attempt limit has been reached.");
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (priorAttempts.LastOrDefault()?.RecordedAtUtc is { } prior && now < prior)
        {
            now = prior;
        }

        var attempt = new ScheduleRunAdmissionAttempt(
            ScheduleRunAdmissionAttempt.CurrentSchemaVersion,
            priorAttempts.Count + 1,
            disposition,
            admissionOperationId,
            candidateRunId,
            blockingRunId,
            now);
        var evidence = ScheduleRunAdmissionEvidenceHash.Apply(new ScheduleRunAdmissionEvidence(
            ScheduleRunAdmissionEvidence.CurrentSchemaVersion,
            canonicalEnvelope,
            canonicalEnvelopeHash,
            loopId,
            [.. priorAttempts, attempt],
            string.Empty));
        var path = GetScheduleAdmissionPath(TriggerDelivery(envelope: canonicalEnvelope));
        await WriteBoundedJsonArtifactAsync(
            _scheduleAdmissionsRoot,
            path,
            ScheduleRunAdmissionEvidenceCodec.Serialize(evidence),
            overwrite: existing is not null,
            cancellationToken);
        return evidence;
    }

    private async Task<IReadOnlyList<ScheduleRunAdmissionEvidence>> ReadAllScheduleAdmissionsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_scheduleAdmissionsRoot))
        {
            return [];
        }

        EnsureSafeDirectory(_scheduleAdmissionsRoot, create: false);
        if (Directory.EnumerateDirectories(_scheduleAdmissionsRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Schedule run-admission evidence storage cannot contain subdirectories.");
        }

        var maximumArtifacts = MaximumScheduleAdmissionArtifacts;
        var maximumInventory = maximumArtifacts + 1 + MaximumScheduleAdmissionInterruptedWriteArtifacts;
        var paths = Directory
            .EnumerateFiles(_scheduleAdmissionsRoot, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, PathComparer)
            .Take(maximumInventory + 1)
            .ToArray();
        if (paths.Length > maximumInventory)
        {
            throw new FormatException("Schedule run-admission storage exceeds its bounded evidence, retirement, and interrupted-write inventory.");
        }

        var evidence = new List<ScheduleRunAdmissionEvidence>(paths.Length);
        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            if (string.Equals(fileName, ScheduleAdmissionRetirementFileName, StringComparison.Ordinal))
            {
                continue;
            }

            if (fileName.StartsWith($".{ScheduleAdmissionRetirementFileName}.", StringComparison.Ordinal) && fileName.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsTemporaryArtifactPath(path, TriggerDeliveryLimits.MaxDeliveryIdCharacters))
            {
                continue;
            }

            EnsureSafeArtifactPath(_scheduleAdmissionsRoot, path, mustExist: true);
            var deliveryText = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
                || !TriggerDeliveryId.TryParse(deliveryText, out var deliveryId))
            {
                throw new FormatException($"Schedule run-admission artifact `{path}` has an unsafe delivery identity.");
            }

            var content = await ReadBoundedJsonArtifactAsync(
                _scheduleAdmissionsRoot,
                path,
                ScheduleRunAdmissionEvidenceLimits.MaxArtifactUtf8Bytes,
                "Schedule run-admission evidence",
                cancellationToken);
            var item = ScheduleRunAdmissionEvidenceCodec.Deserialize(content);
            if (!Equals(TriggerDelivery(envelope: item.CanonicalEnvelope), deliveryId))
            {
                throw new FormatException("Schedule run-admission evidence is stored beneath a substituted delivery identity.");
            }

            evidence.Add(item);
        }

        if (evidence.Count > maximumArtifacts)
        {
            throw new FormatException("Schedule run-admission storage exceeds its explicit bounded artifact count.");
        }

        return evidence;
    }

    private async Task<ScheduleRunAdmissionRetirementLedger> ReadScheduleAdmissionRetirementsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_scheduleAdmissionRetirementPath))
        {
            return ScheduleRunAdmissionRetirementCodec.Empty();
        }

        var content = await ReadBoundedJsonArtifactAsync(
            _scheduleAdmissionsRoot,
            _scheduleAdmissionRetirementPath,
            ScheduleRunAdmissionRetirementCodec.MaximumArtifactUtf8Bytes,
            "Schedule run-admission retirement evidence",
            cancellationToken);
        return ScheduleRunAdmissionRetirementCodec.Deserialize(content);
    }

    private async Task<(IReadOnlyList<ScheduleRunAdmissionEvidence> Admissions, ScheduleRunAdmissionRetirementLedger Retirements)> CompactScheduleAdmissionsAsync(
        IReadOnlyList<ScheduleRunAdmissionEvidence> admissions,
        ScheduleRunAdmissionRetirementLedger retirements,
        CancellationToken cancellationToken)
    {
        var retirementBySchedule = retirements.Entries.ToDictionary(item => item.ScheduleId, StringComparer.Ordinal);
        var removals = new List<ScheduleRunAdmissionEvidence>();
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        foreach (var group in admissions
            .Where(IsRetirableScheduleAdmission)
            .Select(item => (Evidence: item, Directive: ScheduleDirective(item)))
            .GroupBy(item => item.Directive.ScheduleId.Value, StringComparer.Ordinal))
        {
            if (group.GroupBy(item => item.Directive.DefinitionRevision).Any(revision => revision.Select(item => item.Directive.DefinitionHash).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            {
                throw new FormatException($"Schedule `{group.Key}` has terminal run-admission evidence with a substituted definition hash at one immutable revision.");
            }

            retirementBySchedule.TryGetValue(group.Key, out var currentRetirement);
            if (currentRetirement is not null && group.Any(item =>
                item.Directive.DefinitionRevision == currentRetirement.ScheduleRevision
                && !string.Equals(item.Directive.DefinitionHash, currentRetirement.DefinitionHash, StringComparison.Ordinal)))
            {
                throw new FormatException($"Schedule `{group.Key}` has detailed and compacted evidence bound to different definition hashes at one immutable revision.");
            }

            var ordered = group
                .OrderByDescending(item => item.Directive.DefinitionRevision)
                .ThenByDescending(item => item.Directive.Occurrence.Ordinal)
                .ThenByDescending(item => TriggerDelivery(item.Evidence.CanonicalEnvelope).Value, StringComparer.Ordinal)
                .ToArray();
            var retained = 0;
            foreach (var item in ordered)
            {
                if (currentRetirement is not null && ScheduleRunAdmissionRetirementCodec.Covers(currentRetirement, item.Directive))
                {
                    removals.Add(item.Evidence);
                    continue;
                }

                if (retained++ < ScheduleRunAdmissionRetirementCodec.RetainedTerminalEvidencePerSchedule)
                {
                    continue;
                }

                if (currentRetirement is null && retirementBySchedule.Count >= ScheduleRunAdmissionRetirementCodec.MaximumSchedules)
                {
                    continue;
                }

                var retiredAtUtc = new[] { now, item.Directive.Occurrence.ScheduledAtUtc, currentRetirement?.RetiredAtUtc ?? DateTimeOffset.MinValue }.Max();
                var candidate = new ScheduleRunAdmissionRetirement(
                    ScheduleRunAdmissionRetirementCodec.CurrentSchemaVersion,
                    group.Key,
                    item.Directive.DefinitionRevision,
                    item.Directive.DefinitionHash,
                    item.Directive.Occurrence.Ordinal,
                    item.Directive.Occurrence.ScheduledAtUtc,
                    retiredAtUtc);
                if (currentRetirement is not null
                    && currentRetirement.ScheduleRevision == candidate.ScheduleRevision
                    && !string.Equals(currentRetirement.DefinitionHash, candidate.DefinitionHash, StringComparison.Ordinal))
                {
                    throw new FormatException($"Schedule `{group.Key}` retirement evidence is bound to a substituted definition hash at one immutable revision.");
                }

                if (currentRetirement is null || ScheduleRunAdmissionRetirementCodec.Compare(candidate, currentRetirement) > 0)
                {
                    currentRetirement = candidate;
                    retirementBySchedule[group.Key] = candidate;
                }

                removals.Add(item.Evidence);
            }
        }

        if (removals.Count == 0)
        {
            return (admissions, retirements);
        }

        var replacement = ScheduleRunAdmissionRetirementCodec.Apply(new ScheduleRunAdmissionRetirementLedger(
            ScheduleRunAdmissionRetirementCodec.CurrentSchemaVersion,
            retirementBySchedule.Values.ToArray(),
            string.Empty));
        await WriteBoundedJsonArtifactAsync(
            _scheduleAdmissionsRoot,
            _scheduleAdmissionRetirementPath,
            ScheduleRunAdmissionRetirementCodec.Serialize(replacement),
            overwrite: File.Exists(_scheduleAdmissionRetirementPath),
            cancellationToken);

        var removed = removals.Select(item => item.CanonicalEnvelopeHash).ToHashSet(StringComparer.Ordinal);
        foreach (var item in removals)
        {
            var path = GetScheduleAdmissionPath(TriggerDelivery(item.CanonicalEnvelope));
            EnsureSafeArtifactPath(_scheduleAdmissionsRoot, path, mustExist: true);
            File.Delete(path);
        }

        return (admissions.Where(item => !removed.Contains(item.CanonicalEnvelopeHash)).ToArray(), replacement);
    }

    private static IReadOnlyList<ScheduleRunAdmissionEvidence> ReplaceScheduleAdmission(
        IReadOnlyList<ScheduleRunAdmissionEvidence> admissions,
        ScheduleRunAdmissionEvidence evidence)
        => [
            .. admissions.Where(item => !string.Equals(item.CanonicalEnvelopeHash, evidence.CanonicalEnvelopeHash, StringComparison.Ordinal)),
            evidence,
        ];

    private static bool HasScheduleAdmissionCapacity(IReadOnlyList<ScheduleRunAdmissionEvidence> admissions)
        => admissions.Count < MaximumScheduleAdmissionArtifacts;

    private static bool ConflictsWithScheduleDefinition(
        IReadOnlyList<ScheduleRunAdmissionEvidence> admissions,
        ScheduleRunAdmissionRetirementLedger retirements,
        ScheduleExecutionDirective directive,
        out ScheduleRunAdmissionEvidence? conflict)
    {
        conflict = admissions.FirstOrDefault(item =>
        {
            var retained = ScheduleDirective(item);
            return string.Equals(retained.ScheduleId.Value, directive.ScheduleId.Value, StringComparison.Ordinal)
                && retained.DefinitionRevision == directive.DefinitionRevision
                && !string.Equals(retained.DefinitionHash, directive.DefinitionHash, StringComparison.Ordinal);
        });
        if (conflict is not null)
        {
            return true;
        }

        return retirements.Entries.Any(item =>
            string.Equals(item.ScheduleId, directive.ScheduleId.Value, StringComparison.Ordinal)
            && item.ScheduleRevision == directive.DefinitionRevision
            && !string.Equals(item.DefinitionHash, directive.DefinitionHash, StringComparison.Ordinal));
    }

    private static int MaximumScheduleAdmissionArtifacts
        => CustomLoopLimits.MaxRunTracesPerWorkspace + CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace;

    private static bool IsRetirableScheduleAdmission(ScheduleRunAdmissionEvidence evidence)
        => evidence.Attempts[^1].Disposition is ScheduleRunAdmissionDisposition.OverlapSkipped
            or ScheduleRunAdmissionDisposition.DeferredOneSuppressed;

    private static ScheduleExecutionDirective ScheduleDirective(ScheduleRunAdmissionEvidence evidence)
    {
        if (!TriggerDeliveryJson.TryDeserialize(evidence.CanonicalEnvelope, out var envelope, out _)
            || envelope?.ScheduleExecutionDirective is not { } directive)
        {
            throw new FormatException("Schedule run-admission evidence does not retain a canonical schedule directive.");
        }

        return directive;
    }

    private string GetScheduleAdmissionPath(TriggerDeliveryId deliveryId)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);
        var path = Path.Combine(_scheduleAdmissionsRoot, deliveryId.Value + ".json");
        EnsureContained(_scheduleAdmissionsRoot, path);
        return path;
    }

    private static TriggerDeliveryId TriggerDelivery(string envelope)
    {
        if (!TriggerDeliveryJson.TryDeserialize(envelope, out var parsed, out _) || parsed is null)
        {
            throw new FormatException("Schedule run-admission evidence does not retain a canonical trigger envelope.");
        }

        return parsed.DeliveryId;
    }

    private static bool TryValidateScheduledRun(
        CustomLoopRunRecord run,
        TriggerDeliveryEnvelope? envelope,
        out string? canonicalEnvelope,
        out string? canonicalEnvelopeHash)
    {
        canonicalEnvelope = null;
        canonicalEnvelopeHash = null;
        var origin = run.SequentialInvocationSnapshot?.TriggerOrigin;
        return envelope is not null
            && envelope.Kind == TriggerKind.Time
            && envelope.ScheduleExecutionDirective is not null
            && TriggerDeliveryValidator.Validate(envelope).IsValid
            && string.Equals(run.LoopId, envelope.Loop.LoopId, StringComparison.Ordinal)
            && origin is not null
            && GovernedLoopSequentialTriggerOriginFactory.MatchesPersistedOrigin(envelope, origin)
            && TriggerDeliveryJson.TrySerialize(envelope, out canonicalEnvelope, out _)
            && TriggerDeliveryHash.TryCompute(envelope, out canonicalEnvelopeHash, out _)
            && string.Equals(origin.CanonicalEnvelope, canonicalEnvelope, StringComparison.Ordinal)
            && string.Equals(origin.CanonicalEnvelopeHash, canonicalEnvelopeHash, StringComparison.Ordinal);
    }

    private static bool MatchesScheduledEnvelope(CustomLoopRunRecord run, string canonicalEnvelope, string canonicalEnvelopeHash)
        => run.SequentialInvocationSnapshot?.TriggerOrigin is { } origin
            && string.Equals(origin.CanonicalEnvelope, canonicalEnvelope, StringComparison.Ordinal)
            && string.Equals(origin.CanonicalEnvelopeHash, canonicalEnvelopeHash, StringComparison.Ordinal);

    private static bool MatchesEvidenceEnvelope(
        ScheduleRunAdmissionEvidence evidence,
        string loopId,
        string canonicalEnvelope,
        string canonicalEnvelopeHash)
        => ScheduleRunAdmissionEvidenceValidator.IsValid(evidence)
            && string.Equals(evidence.LoopId, loopId, StringComparison.Ordinal)
            && string.Equals(evidence.CanonicalEnvelope, canonicalEnvelope, StringComparison.Ordinal)
            && string.Equals(evidence.CanonicalEnvelopeHash, canonicalEnvelopeHash, StringComparison.Ordinal);

    private static bool HasOtherDeferredOccurrence(
        IReadOnlyList<ScheduleRunAdmissionEvidence> evidence,
        string currentEnvelopeHash,
        string loopId)
        => evidence.Any(item => !string.Equals(item.CanonicalEnvelopeHash, currentEnvelopeHash, StringComparison.Ordinal)
            && string.Equals(item.LoopId, loopId, StringComparison.Ordinal)
            && item.Attempts[^1].Disposition == ScheduleRunAdmissionDisposition.OverlapDeferred);

    private static CustomLoopRunRecord? FindBlockingRun(
        ScheduleRunAdmissionEvidence evidence,
        CustomLoopRunRecord? activeLoopRun)
        => activeLoopRun is not null
            && string.Equals(evidence.Attempts[^1].BlockingRunId, activeLoopRun.Id, StringComparison.Ordinal)
                ? activeLoopRun
                : null;

    private static ScheduleRunAdmissionStoreStatus MapScheduleDisposition(ScheduleRunAdmissionDisposition disposition)
        => disposition switch
        {
            ScheduleRunAdmissionDisposition.OverlapSkipped => ScheduleRunAdmissionStoreStatus.OverlapSkipped,
            ScheduleRunAdmissionDisposition.OverlapDeferred => ScheduleRunAdmissionStoreStatus.OverlapDeferred,
            ScheduleRunAdmissionDisposition.OverlapSerialized => ScheduleRunAdmissionStoreStatus.OverlapSerialized,
            ScheduleRunAdmissionDisposition.DeferredOneSuppressed => ScheduleRunAdmissionStoreStatus.DeferredOneSuppressed,
            _ => ScheduleRunAdmissionStoreStatus.Conflict,
        };

    private static ScheduleRunAdmissionStoreResult ScheduleResult(
        ScheduleRunAdmissionStoreStatus status,
        CustomLoopRunRecord? run,
        ScheduleRunAdmissionEvidence? evidence)
        => new(status, run, evidence);

    /// <summary>
    /// Loads the unique canonical run artifact for a run identifier.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The validated run, or <see langword="null"/> when no artifact exists.</returns>
    public async Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        var safeRunId = CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        var locations = EnumerateArtifactLocations();
        var matches = locations.Where(location => string.Equals(location.RunId, safeRunId, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1)
        {
            throw new FormatException($"Custom loop run id `{safeRunId}` exists in more than one loop directory. The persisted state requires review.");
        }

        if (matches.Length == 0)
        {
            return null;
        }

        var artifact = await ReadArtifactAsync(matches[0], cancellationToken);
        return artifact.Run;
    }

    /// <summary>Resolves one exact retained terminal sequential-node receipt from the authoritative run artifacts.</summary>
    public async Task<GovernedLoopSequentialNodeEvidenceReceipt?> ResolveAsync(string evidenceHash, CancellationToken cancellationToken = default)
    {
        if (!IsHash(evidenceHash))
        {
            throw new ArgumentException("Sequential node evidence hash must be lowercase SHA-256 hexadecimal.", nameof(evidenceHash));
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var matches = new List<GovernedLoopSequentialNodeEvidenceReceipt>(1);
        await ScanArtifactsAsync(artifact =>
        {
            if (artifact.Run is not { } run)
            {
                return;
            }

            foreach (var runEvent in run.Events)
            {
                if (runEvent.SequentialNodeEvidence is not { } evidence
                    || evidence.Kind is CustomLoopSequentialNodeEvidenceKind.DispatchStarted or CustomLoopSequentialNodeEvidenceKind.TopologySkipped
                    || !string.Equals(evidence.EvidenceHash, evidenceHash, StringComparison.Ordinal))
                {
                    continue;
                }

                var receipt = ToApplicationReceipt(evidence);
                if (!GovernedLoopSequentialNodeEvidenceHash.Matches(receipt)
                    || !string.Equals(receipt.EvidenceHash, evidenceHash, StringComparison.Ordinal))
                {
                    throw new FormatException("Durable sequential node evidence does not map to its exact Application receipt hash.");
                }

                matches.Add(receipt);
            }
        }, cancellationToken);

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new FormatException($"Sequential node evidence hash `{evidenceHash}` exists more than once. The persisted state requires review."),
        };
    }

    /// <summary>
    /// Authenticates one exact terminal sequential-node event that was already retained atomically in the canonical run artifact.
    /// </summary>
    /// <remarks>
    /// This operation never appends or rewrites evidence. The ordered-runtime coordinates are untrusted lookup hints; the current
    /// run, immutable admission binding, builder-issued plan node, event identity, outcome digest, and terminal receipt must all
    /// agree before the existing evidence identity is returned. Repeating the exact request is therefore naturally idempotent.
    /// </remarks>
    public async Task<GovernedLoopSequentialNodeHandlerResult> RetainAsync(
        GovernedLoopSequentialOrderedNodeEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateOrderedEvidenceRequest(request);

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var dispatch = request.Dispatch;
        var binding = dispatch.Anchor.AdapterBinding;
        var artifact = await ReadArtifactByRunIdAsync(binding.ExecutionBinding.RunId, cancellationToken);
        if (artifact?.Run is not { } run)
        {
            throw new FormatException("The ordered sequential-node outcome does not belong to a live canonical run artifact.");
        }

        if (run.LifecycleVersion < request.OrderedLifecycleVersion
            || run.SequentialAdapterBinding is not { } durableBinding
            || run.SequentialInvocationSnapshot is not { } durableSnapshot
            || !string.Equals(durableBinding.ContentHash, binding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(durableSnapshot.ContentHash, dispatch.Anchor.InvocationSnapshot.ContentHash, StringComparison.Ordinal))
        {
            throw new FormatException("The ordered sequential-node outcome does not match the current durable lifecycle or immutable sequential admission binding.");
        }

        var eventIndex = checked((int)request.OrderedEventSequence - 1);
        if (eventIndex < 0
            || eventIndex >= run.Events.Length
            || run.Events[eventIndex] is not { } orderedEvent
            || orderedEvent.Sequence != request.OrderedEventSequence
            || !string.Equals(orderedEvent.EventId, request.OrderedEventId, StringComparison.Ordinal)
            || orderedEvent.SequentialNodeEvidence is not { } evidence
            || evidence.Kind is CustomLoopSequentialNodeEvidenceKind.DispatchStarted or CustomLoopSequentialNodeEvidenceKind.TopologySkipped)
        {
            throw new FormatException("The exact ordered sequential-node terminal event is not present in the current durable run artifact.");
        }

        var receipt = ToApplicationReceipt(evidence);
        var execution = binding.ExecutionBinding;
        if (receipt.Disposition != request.Disposition
            || receipt.Kind != ExpectedEvidenceKind(request.Disposition)
            || !string.Equals(receipt.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(receipt.RunId, execution.RunId, StringComparison.Ordinal)
            || !Equals(receipt.Revision, execution.Revision)
            || receipt.ExecutionGeneration != execution.ExecutionGeneration
            || receipt.ActivationOrdinal != dispatch.Activation.ActivationOrdinal
            || receipt.VisitOrdinal != dispatch.Activation.VisitOrdinal
            || !string.Equals(receipt.NodeId, dispatch.Node.NodeId, StringComparison.Ordinal)
            || receipt.Attempt != dispatch.Attempt
            || !string.Equals(receipt.CycleId, dispatch.Activation.CycleId, StringComparison.Ordinal)
            || receipt.CycleIteration != dispatch.Activation.CycleIteration
            || !CustomLoopSequentialOutcomeArtifactHash.Matches(orderedEvent)
            || !GovernedLoopSequentialNodeEvidenceHash.Matches(receipt))
        {
            throw new FormatException("The ordered sequential-node terminal event does not authenticate the exact dispatch coordinates and disposition.");
        }

        return new GovernedLoopSequentialNodeHandlerResult(receipt.Disposition, receipt.EvidenceHash);
    }

    async Task<GovernedLoopSequentialRunEvidence?> IGovernedLoopSequentialRunEvidenceSource.ResolveAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var safeRunId = CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var artifact = await ReadArtifactByRunIdAsync(safeRunId, cancellationToken);
        if (artifact?.Run is not { } run)
        {
            return null;
        }

        if (run.SequentialAdapterBinding is null && run.SequentialInvocationSnapshot is null)
        {
            return null;
        }

        if (run.SequentialAdapterBinding is not { } binding || run.SequentialInvocationSnapshot is not { } snapshot)
        {
            throw new FormatException("The durable run contains incomplete canonical sequential admission evidence.");
        }

        var bindingCopy = new EmbodySense.Core.Common.Loops.Sequential.Models.GovernedLoopSequentialAdapterBinding(
            binding.SchemaVersion,
            binding.WorkspaceId,
            binding.ExecutionBinding,
            binding.AdmissionOperationId,
            binding.AdmissionReceipt,
            binding.AdmissionReceiptHash,
            binding.AdmissionRequestHash,
            binding.InvocationPayloadHash,
            binding.GraphArtifactHash,
            binding.GraphLayoutHash,
            binding.ContentHash);
        var snapshotCopy = new EmbodySense.Core.Common.Loops.Sequential.Models.GovernedLoopSequentialInvocationSnapshot(
            snapshot.SchemaVersion,
            snapshot.TriggerPrompt,
            snapshot.ModelSnapshot,
            snapshot.InvokingConversation,
            snapshot.ContextCapturedAtUtc,
            snapshot.ContextManifest,
            snapshot.ContentHash)
        {
            TriggerOrigin = snapshot.TriggerOrigin is null
                ? null
                : snapshot.TriggerOrigin with
                {
                    Occurrence = EmbodySense.Core.Common.Triggers.Schedules.ScheduleContractCopy.Copy(snapshot.TriggerOrigin.Occurrence)!,
                },
        };
        if (!EmbodySense.Core.Common.Loops.Sequential.GovernedLoopSequentialContractValidator.Validate(bindingCopy).IsValid
            || !EmbodySense.Core.Common.Loops.Sequential.GovernedLoopSequentialContractValidator.Validate(snapshotCopy).IsValid
            || !string.Equals(bindingCopy.ExecutionBinding.RunId, safeRunId, StringComparison.Ordinal)
            || !string.Equals(bindingCopy.InvocationPayloadHash, snapshotCopy.ContentHash, StringComparison.Ordinal))
        {
            throw new FormatException("The durable run's canonical sequential admission evidence failed exact defensive projection validation.");
        }

        return new GovernedLoopSequentialRunEvidence(bindingCopy, snapshotCopy);
    }

    /// <summary>
    /// Loads the lightweight monitor projection for one live run.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The validated monitor projection, or <see langword="null"/> when no live artifact exists.</returns>
    public async Task<CustomLoopRunMonitor?> GetMonitorAsync(string runId, CancellationToken cancellationToken = default)
    {
        var safeRunId = CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        if (!Directory.Exists(_runsRoot))
        {
            return null;
        }

        EnsureMonitorWatcher();
        if (!File.Exists(_discoveryIndexPendingPath))
        {
            try
            {
                var fingerprint = GetDiscoveryIndexFingerprint();
                var cached = fingerprint is null ? null : await TryGetCachedMonitorAsync(fingerprint, safeRunId, cancellationToken);
                if (cached is not null)
                {
                    return new CustomLoopRunMonitor(cached.Summary, cached.ArtifactHash);
                }

                var candidateIndex = fingerprint is null ? null : await ReadDiscoveryIndexAsync(cancellationToken);
                var confirmedFingerprint = GetDiscoveryIndexFingerprint();
                if (candidateIndex is not null
                    && confirmedFingerprint is not null
                    && fingerprint == confirmedFingerprint
                    && !File.Exists(_discoveryIndexPendingPath))
                {
                    CacheDiscoveryIndex(candidateIndex, confirmedFingerprint);
                    cached = await TryGetCachedMonitorAsync(confirmedFingerprint, safeRunId, cancellationToken);
                    if (cached is not null)
                    {
                        return new CustomLoopRunMonitor(cached.Summary, cached.ArtifactHash);
                    }
                }
            }
            catch (FormatException exception) when (exception is not UnsupportedCustomLoopRunDiscoveryIndexSchemaException)
            {
                // Fall through to locked repair from canonical artifacts.
            }
        }

        CustomLoopRunDiscoveryIndex index;
        var rebuiltFromCanonicalArtifacts = false;
        await using (var mutation = await AcquireMutationLockAsync(cancellationToken))
        {
            var loaded = await LoadDiscoveryIndexWithSourceAsync(cancellationToken);
            index = loaded.Index;
            rebuiltFromCanonicalArtifacts = loaded.RebuiltFromCanonicalArtifacts;
            CacheDiscoveryIndex(index, GetDiscoveryIndexFingerprint());
        }

        var entry = index.Entries.SingleOrDefault(candidate => string.Equals(candidate.Summary.Id, safeRunId, StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        var verified = rebuiltFromCanonicalArtifacts ? await ValidateRebuiltMonitorEntryAsync(entry, cancellationToken) : await ValidateMonitorEntryAsync(entry, cancellationToken);
        if (verified is not null)
        {
            return new CustomLoopRunMonitor(verified.Summary, verified.ArtifactHash);
        }

        await using (var mutation = await AcquireMutationLockAsync(cancellationToken))
        {
            index = await RebuildDiscoveryIndexAsync(index.Revision, cancellationToken);
            CacheDiscoveryIndex(index, GetDiscoveryIndexFingerprint());
            entry = index.Entries.SingleOrDefault(candidate => string.Equals(candidate.Summary.Id, safeRunId, StringComparison.Ordinal));
            if (entry is null)
            {
                return null;
            }

            verified = await ValidateRebuiltMonitorEntryAsync(entry, cancellationToken);
            return verified is null
                ? throw new FormatException($"Custom loop run `{safeRunId}` changed independently of its discovery metadata and could not be repaired.")
                : new CustomLoopRunMonitor(verified.Summary, verified.ArtifactHash);
        }
    }

    /// <summary>
    /// Loads the unique live run bound to an admission operation identifier.
    /// </summary>
    /// <param name="admissionOperationId">The admission operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The validated run, or <see langword="null"/> when no live artifact has that admission operation.</returns>
    public async Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(admissionOperationId, nameof(admissionOperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        CustomLoopRunRecord? match = null;
        foreach (var location in EnumerateArtifactLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = await ReadArtifactAsync(location, cancellationToken);
            if (artifact.Tombstone is not null && string.Equals(artifact.Tombstone.AdmissionOperationId, safeOperationId, StringComparison.Ordinal))
            {
                throw new FormatException($"Admission operation id `{safeOperationId}` belongs to a deleted terminal trace and cannot be reused or replayed.");
            }

            var run = artifact.Run;
            if (run is null || !string.Equals(run.AdmissionOperationId, safeOperationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                throw new FormatException($"Admission operation id `{safeOperationId}` is bound to more than one custom loop run. The persisted state requires review.");
            }

            match = run;
        }

        return match;
    }

    /// <summary>
    /// Loads the unique nonterminal run for a loop identifier.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The validated nonterminal run, or <see langword="null"/> when the loop has none.</returns>
    public async Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
    {
        var safeLoopId = CustomLoopArtifactIdentifier.Require(loopId, nameof(loopId));
        CustomLoopRunRecord? match = null;
        var multipleMatches = false;
        await ScanArtifactsAsync(artifact =>
        {
            var run = artifact.Run;
            if (run is null || run.IsTerminal || !string.Equals(run.LoopId, safeLoopId, StringComparison.Ordinal))
            {
                return;
            }

            multipleMatches |= match is not null;
            match ??= run;
        }, cancellationToken);
        if (multipleMatches)
        {
            throw new FormatException($"Custom loop `{safeLoopId}` has more than one nonterminal run. The persisted state requires review.");
        }

        return match;
    }

    /// <summary>
    /// Lists the most recent live run summaries across the workspace.
    /// </summary>
    /// <param name="maximumCount">The positive maximum number of summaries to return.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>Summaries ordered from newest to oldest by the canonical discovery ordering.</returns>
    public async Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        return (await ListPageAsync(new CustomLoopRunPageRequest(maximumCount), cancellationToken)).Items;
    }

    /// <summary>
    /// Lists one validated cursor page of live run summaries, optionally restricted to a loop.
    /// </summary>
    /// <param name="request">The page size, optional loop filter, and opaque continuation cursor.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The ordered page and an opaque next cursor when more matching runs remain.</returns>
    public async Task<CustomLoopRunPage> ListPageAsync(CustomLoopRunPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumCount < 1 || request.MaximumCount > CustomLoopLimits.MaxRecentRunsPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumCount), request.MaximumCount, $"Run page size must be between 1 and {CustomLoopLimits.MaxRecentRunsPageSize}.");
        }

        var safeLoopId = request.LoopId is null ? null : CustomLoopArtifactIdentifier.Require(request.LoopId, nameof(request.LoopId));
        var after = CustomLoopRunPageCursorCodec.Decode(request.Cursor, safeLoopId);
        if (!Directory.Exists(_runsRoot))
        {
            return new CustomLoopRunPage([], null);
        }

        MutationLease mutation;
        try
        {
            mutation = await AcquireMutationLockAsync(cancellationToken);
        }
        catch (Exception exception) when (IsReadOnlyLockAccessFailure(exception))
        {
            return await ListCanonicalPageWithoutMutationLockAsync(request.MaximumCount, safeLoopId, after, cancellationToken);
        }

        await using (mutation)
        {
            var repairIndex = false;
            long previousRevision = 0;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var index = repairIndex
                    ? await RebuildDiscoveryIndexAsync(previousRevision, cancellationToken)
                    : await LoadDiscoveryIndexAsync(cancellationToken);
                previousRevision = index.Revision;
                var pageEntries = index.Entries
                    .Where(entry => safeLoopId is null || string.Equals(entry.Summary.LoopId, safeLoopId, StringComparison.Ordinal))
                    .Where(entry => after is null || CompareSummaryToCursor(entry.Summary, after) > 0)
                    .Take(request.MaximumCount + 1)
                    .ToArray();

                var canonicalSummaries = await ReadCanonicalDiscoveryPageAsync(pageEntries, cancellationToken);
                if (canonicalSummaries is not null)
                {
                    return CreateDiscoveryPage(canonicalSummaries, request.MaximumCount, safeLoopId);
                }

                repairIndex = true;
            }
        }

        throw new FormatException("The custom loop run discovery index changed independently of its canonical artifacts and could not be repaired.");
    }

    /// <summary>
    /// Scans canonical artifacts and returns every live nonterminal run.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The validated nonterminal runs in deterministic loop and run identity order.</returns>
    public async Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
    {
        var runs = new List<CustomLoopRunRecord>();
        await ScanArtifactsAsync(artifact =>
        {
            if (artifact.Run is { IsTerminal: false } run)
            {
                runs.Add(run);
            }
        }, cancellationToken);
        return runs
            .OrderBy(run => run.CreatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Calculates used and available trace storage from canonical run, tombstone, and deletion-operation artifacts.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The workspace trace quota snapshot after validating all accounted artifacts.</returns>
    public async Task<CustomLoopTraceQuota> GetTraceQuotaAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_runsRoot) && !Directory.Exists(_traceDeletionOperationsRoot))
        {
            return CustomLoopTraceQuota.Empty();
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var quota = (await ScanArtifactsAsync(null, cancellationToken)).Quota;
        return quota with { DeletionOperationCount = EnumerateTraceDeletionOperationPaths().Count };
    }

    /// <summary>
    /// Inspects one run trace without mutating it, including its canonical persisted size and hash.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The validated inspection, or <see langword="null"/> when no live trace exists for the run.</returns>
    public async Task<CustomLoopTraceInspection?> InspectTraceAsync(string runId, CancellationToken cancellationToken = default)
    {
        var safeRunId = CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        if (!Directory.Exists(_runsRoot))
        {
            return null;
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var artifact = await ReadArtifactByRunIdAsync(safeRunId, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        if (artifact.Run is not null)
        {
            var run = artifact.Run;
            return new CustomLoopTraceInspection(
                CustomLoopTraceArtifactKind.LiveTrace,
                run.Id,
                run.LoopId,
                run.Status,
                run.AdmittedDefinition.DefinitionVersion,
                run.AdmittedDefinition.ContentHash,
                artifact.PersistedHash,
                artifact.PersistedUtf8Bytes,
                artifact.PersistedHash,
                artifact.PersistedUtf8Bytes,
                run.CreatedAtUtc,
                run.CompletedAtUtc,
                null);
        }

        var tombstone = artifact.Tombstone ?? throw new FormatException($"Custom loop trace `{safeRunId}` contains an unsupported artifact.");
        return new CustomLoopTraceInspection(
            CustomLoopTraceArtifactKind.Tombstone,
            tombstone.RunId,
            tombstone.LoopId,
            tombstone.TerminalStatus,
            tombstone.DefinitionVersion,
            tombstone.DefinitionHash,
            artifact.PersistedHash,
            artifact.PersistedUtf8Bytes,
            tombstone.OriginalTraceHash,
            tombstone.OriginalTraceUtf8Bytes,
            tombstone.CreatedAtUtc,
            tombstone.CompletedAtUtc,
            tombstone);
    }

    /// <summary>
    /// Loads and validates one durable trace-deletion receipt.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The receipt and its persisted transition state, or a not-found result.</returns>
    public async Task<CustomLoopTraceDeletionLookupResult> GetTraceDeletionOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        if (!Directory.Exists(_traceDeletionOperationsRoot))
        {
            return CustomLoopTraceDeletionLookupResult.NotFound();
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var operation = await ReadTraceDeletionOperationAsync(safeOperationId, cancellationToken);
        return operation is null ? CustomLoopTraceDeletionLookupResult.NotFound() : CustomLoopTraceDeletionLookupResult.Found(operation);
    }

    /// <summary>
    /// Reserves or idempotently replays the intent receipt for deleting one terminal run trace.
    /// </summary>
    /// <param name="mutation">The deletion request, expected trace binding, actor, surface, and operation identity.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result describing reservation, replay, request conflict, trace conflict, absence, or nonterminal refusal.</returns>
    public async Task<CustomLoopTraceDeletionReservationResult> ReserveTraceDeletionOperationAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default)
    {
        ValidateDeletionMutation(mutation);
        await using var lease = await AcquireMutationLockAsync(cancellationToken);
        var existingOperation = await ReadTraceDeletionOperationAsync(mutation.Request.OperationId, cancellationToken);
        if (existingOperation is not null)
        {
            if (!DeletionRequestMatches(existingOperation, mutation))
            {
                return new CustomLoopTraceDeletionReservationResult(CustomLoopTraceDeletionReservationStatus.OperationConflict, existingOperation);
            }

            var existingStatus = existingOperation.State == CustomLoopTraceDeletionOperationState.PendingMutation
                ? CustomLoopTraceDeletionReservationStatus.Pending
                : CustomLoopTraceDeletionReservationStatus.OutcomeCommitted;
            return new CustomLoopTraceDeletionReservationResult(existingStatus, existingOperation);
        }

        _ = await ReadCleanDiscoveryIndexAsync(cancellationToken);
        var artifact = await ReadArtifactByRunIdAsync(mutation.Request.RunId, cancellationToken);
        var deletionOperationCount = EnumerateTraceDeletionOperationPaths().Count;
        var generalOperationCapacity = CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace - CustomLoopLimits.ReservedRunTraceDeletionOperationsForTombstones;
        if (deletionOperationCount >= CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace
            || (deletionOperationCount >= generalOperationCapacity && !CanUseTombstoneDeletionOperationReservation(artifact, mutation.Request)))
        {
            return new CustomLoopTraceDeletionReservationResult(CustomLoopTraceDeletionReservationStatus.DeletionOperationLimitExceeded, null);
        }

        var reservedAtUtc = Max(mutation.RequestedAtUtc, _timeProvider.GetUtcNow().ToUniversalTime());
        var operation = new CustomLoopTraceDeletionOperation(
            CustomLoopTraceDeletionOperation.CurrentSchemaVersion,
            mutation.Request.OperationId,
            mutation.RequestHash,
            mutation.Request,
            mutation.RequestedAtUtc,
            reservedAtUtc,
            CustomLoopTraceDeletionOperationState.PendingMutation,
            CustomLoopTraceDeletionStoreStatus.Unknown,
            null,
            CustomLoopTraceDeletionIntegrity.Unknown);
        await WriteTraceDeletionOperationAsync(operation, overwrite: false, cancellationToken);
        return new CustomLoopTraceDeletionReservationResult(CustomLoopTraceDeletionReservationStatus.Reserved, operation);
    }

    /// <summary>
    /// Records that the deletion intent audit failed before any terminal trace was removed.
    /// </summary>
    /// <param name="mutation">The exact mutation previously used to reserve the deletion operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The durable audit-failure outcome, replay, conflict, or not-found result.</returns>
    public async Task<CustomLoopTraceDeletionStoreResult> CommitTraceDeletionAuditFailureAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default)
    {
        ValidateDeletionMutation(mutation);
        await using var lease = await AcquireMutationLockAsync(cancellationToken);
        var operation = await ReadTraceDeletionOperationAsync(mutation.Request.OperationId, cancellationToken);
        if (operation is null)
        {
            return new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Unknown, null, CustomLoopTraceDeletionIntegrity.Unknown);
        }

        if (!DeletionRequestMatches(operation, mutation))
        {
            return new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.OperationConflict, operation.Tombstone, operation.Integrity);
        }

        if (operation.State == CustomLoopTraceDeletionOperationState.OutcomeCommitted)
        {
            return operation.ToStoreResult() with { Status = operation.Outcome == CustomLoopTraceDeletionStoreStatus.Deleted ? CustomLoopTraceDeletionStoreStatus.AlreadyDeleted : operation.Outcome };
        }

        return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.AuditUnavailable, null, cancellationToken);
    }

    /// <summary>
    /// Revalidates and replaces one terminal trace with its tombstone after the intent audit is durable.
    /// </summary>
    /// <param name="mutation">The exact reserved deletion request and expected trace hash.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result describing deletion, replay, request/trace conflict, absence, or nonterminal refusal.</returns>
    public async Task<CustomLoopTraceDeletionStoreResult> DeleteTerminalTraceAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default)
    {
        ValidateDeletionMutation(mutation);
        await using var lease = await AcquireMutationLockAsync(cancellationToken);
        var existingOperation = await ReadTraceDeletionOperationAsync(mutation.Request.OperationId, cancellationToken);
        if (existingOperation is not null && !DeletionRequestMatches(existingOperation, mutation))
        {
            return new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.OperationConflict, existingOperation.Tombstone, existingOperation.Integrity);
        }

        var operation = existingOperation ?? new CustomLoopTraceDeletionOperation(
            CustomLoopTraceDeletionOperation.CurrentSchemaVersion,
            mutation.Request.OperationId,
            mutation.RequestHash,
            mutation.Request,
            mutation.RequestedAtUtc,
            mutation.RequestedAtUtc,
            CustomLoopTraceDeletionOperationState.PendingMutation,
            CustomLoopTraceDeletionStoreStatus.Unknown,
            null,
            CustomLoopTraceDeletionIntegrity.Unknown);
        if (existingOperation?.State == CustomLoopTraceDeletionOperationState.OutcomeCommitted)
        {
            return existingOperation.ToStoreResult() with { Status = existingOperation.Outcome == CustomLoopTraceDeletionStoreStatus.Deleted ? CustomLoopTraceDeletionStoreStatus.AlreadyDeleted : existingOperation.Outcome };
        }

        RunArtifact? artifact = null;
        var scan = await ScanArtifactsAsync(candidate =>
        {
            if (string.Equals(candidate.Location.RunId, mutation.Request.RunId, StringComparison.Ordinal))
            {
                artifact = candidate;
            }
        }, cancellationToken);
        if (existingOperation is null)
        {
            var deletionOperationCount = EnumerateTraceDeletionOperationPaths().Count;
            var generalOperationCapacity = CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace - CustomLoopLimits.ReservedRunTraceDeletionOperationsForTombstones;
            if (deletionOperationCount >= CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace
                || (deletionOperationCount >= generalOperationCapacity && !CanUseTombstoneDeletionOperationReservation(artifact, mutation.Request)))
            {
                return new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.DeletionOperationLimitExceeded, null, CustomLoopTraceDeletionIntegrity.Complete);
            }

            await WriteTraceDeletionOperationAsync(operation, overwrite: false, cancellationToken);
        }

        if (artifact is null)
        {
            return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.NotFound, null, cancellationToken);
        }

        if (artifact.Tombstone is not null)
        {
            if (string.Equals(artifact.Tombstone.DeletionOperationId, operation.OperationId, StringComparison.Ordinal)
                && string.Equals(artifact.Tombstone.DeletionRequestHash, operation.RequestHash, StringComparison.Ordinal))
            {
                return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.Deleted, artifact.Tombstone, cancellationToken);
            }

            return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.OperationConflict, artifact.Tombstone, cancellationToken);
        }

        var run = artifact.Run ?? throw new FormatException($"Custom loop trace `{mutation.Request.RunId}` contains an unsupported artifact.");
        if (!run.IsTerminal)
        {
            return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.Nonterminal, null, cancellationToken);
        }

        if (!string.Equals(artifact.PersistedHash, mutation.Request.ExpectedTraceHash, StringComparison.Ordinal))
        {
            return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.HashMismatch, null, cancellationToken);
        }

        if (scan.Quota.TombstoneCount >= CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
        {
            return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.TombstoneLimitExceeded, null, cancellationToken);
        }

        var completedAtUtc = run.CompletedAtUtc ?? throw new FormatException("A terminal custom-loop run must have a completion timestamp before trace deletion.");
        var mutationAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var deletedAtUtc = mutationAtUtc < completedAtUtc ? completedAtUtc : mutationAtUtc;
        var tombstone = new CustomLoopTraceTombstone(
            CustomLoopTraceTombstone.CurrentSchemaVersion,
            CustomLoopTraceTombstone.CurrentArtifactKind,
            run.Id,
            run.LoopId,
            run.AdmissionOperationId,
            run.AdmissionRequestHash,
            run.Status,
            run.AdmittedDefinition.DefinitionVersion,
            run.AdmittedDefinition.ContentHash,
            artifact.PersistedHash,
            artifact.PersistedUtf8Bytes,
            run.CreatedAtUtc,
            completedAtUtc,
            deletedAtUtc,
            mutation.Request.Actor,
            mutation.Request.Surface,
            mutation.Request.OperationId,
            mutation.RequestHash,
            mutation.Request.OperationId,
            mutation.Request.OperationId,
            CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        ValidateTombstone(tombstone);
        await WriteArtifactAsync(artifact.Location.Path, SerializeTombstoneBounded(tombstone), ToSummary(tombstone), overwrite: true, cancellationToken);
        return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, cancellationToken);
    }

    /// <summary>
    /// Marks the terminal audit-integrity outcome of a committed trace-deletion receipt.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="integrity">The terminal recorded or warning integrity state.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A status reporting a new mark, idempotent prior mark, conflict, or missing operation.</returns>
    public async Task<CustomLoopTraceDeletionAuditMarkStatus> MarkTraceDeletionOutcomeAsync(string operationId, CustomLoopTraceDeletionIntegrity integrity, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        if (integrity is not CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted and not CustomLoopTraceDeletionIntegrity.Complete and not CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning)
        {
            throw new ArgumentOutOfRangeException(nameof(integrity), integrity, "Trace-deletion outcome integrity must start the outcome audit, complete it, or mark a committed audit warning.");
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var operation = await ReadTraceDeletionOperationAsync(safeOperationId, cancellationToken);
        if (operation is null)
        {
            return CustomLoopTraceDeletionAuditMarkStatus.NotFound;
        }

        if (operation.State != CustomLoopTraceDeletionOperationState.OutcomeCommitted)
        {
            throw new InvalidOperationException("A trace-deletion outcome cannot be marked before its mutation outcome is committed.");
        }

        if (operation.Integrity is CustomLoopTraceDeletionIntegrity.Complete or CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning
            || operation.Integrity == CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted && integrity == CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted)
        {
            return CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked;
        }

        if (operation.Integrity == CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit && integrity is CustomLoopTraceDeletionIntegrity.Complete or CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning)
        {
            throw new InvalidOperationException("Trace-deletion outcome audit must be durably started before it can be completed or marked with a warning.");
        }

        var tombstone = operation.Tombstone;
        if (tombstone is not null && operation.Outcome is CustomLoopTraceDeletionStoreStatus.Deleted or CustomLoopTraceDeletionStoreStatus.AlreadyDeleted)
        {
            tombstone = tombstone with { OutcomeIntegrity = integrity };
            ValidateTombstone(tombstone);
            var path = GetRunPath(tombstone.LoopId, tombstone.RunId);
            var persisted = await ReadArtifactAsync(new RunArtifactLocation(path, tombstone.LoopId, tombstone.RunId), cancellationToken);
            if (persisted.Tombstone is null
                || !string.Equals(persisted.Tombstone.DeletionOperationId, operation.OperationId, StringComparison.Ordinal)
                || !string.Equals(persisted.Tombstone.DeletionRequestHash, operation.RequestHash, StringComparison.Ordinal))
            {
                throw new FormatException("The trace-deletion tombstone no longer matches its durable operation ledger.");
            }

            await WriteArtifactAsync(path, SerializeTombstoneBounded(tombstone), ToSummary(tombstone), overwrite: true, cancellationToken);
        }

        var updated = operation with { UpdatedAtUtc = Max(operation.UpdatedAtUtc, DateTimeOffset.UtcNow), Tombstone = tombstone, Integrity = integrity };
        await WriteTraceDeletionOperationAsync(updated, overwrite: true, cancellationToken);
        return CustomLoopTraceDeletionAuditMarkStatus.Marked;
    }

    /// <summary>
    /// Appends one idempotent integrity-warning event to an otherwise immutable terminal run.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="expectedLifecycleVersion">The currently expected durable lifecycle version.</param>
    /// <param name="warning">The validated terminal integrity-warning event to append.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result describing update, idempotent replay, absence, version conflict, or deletion conflict.</returns>
    public async Task<CustomLoopRunStoreResult> AppendTerminalIntegrityWarningAsync(string runId, int expectedLifecycleVersion, CustomLoopRunEvent warning, CancellationToken cancellationToken = default)
    {
        var safeRunId = CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        ArgumentNullException.ThrowIfNull(warning);
        if (expectedLifecycleVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLifecycleVersion), expectedLifecycleVersion, "Expected lifecycle version must be at least 1.");
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var locations = EnumerateArtifactLocations();
        var matches = locations.Where(location => string.Equals(location.RunId, safeRunId, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
        {
            return CustomLoopRunStoreResult.NotFound();
        }

        if (matches.Length > 1)
        {
            throw new FormatException($"Custom loop run id `{safeRunId}` exists in more than one loop directory. The persisted state requires review.");
        }

        var artifact = await ReadArtifactAsync(matches[0], cancellationToken);
        if (artifact.Tombstone is not null)
        {
            return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.DeletedIdentityConflict, null, null);
        }

        var current = artifact.Run ?? throw new FormatException($"Custom loop run `{safeRunId}` contains an unsupported artifact.");
        if (current.LifecycleVersion == checked(expectedLifecycleVersion + 1)
            && current.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.IntegrityWarning } existingWarning
            && TerminalWarningsEqual(existingWarning, warning))
        {
            return CustomLoopRunStoreResult.Updated(current);
        }

        if (current.LifecycleVersion != expectedLifecycleVersion)
        {
            return CustomLoopRunStoreResult.VersionConflict(current, expectedLifecycleVersion);
        }

        var validation = CustomLoopRunValidator.ValidateTerminalIntegrityWarningAppend(current, warning);
        if (!validation.IsValid)
        {
            var details = string.Join(" ", validation.Errors.Select(error => $"{error.Field}: {error.Message}"));
            throw new FormatException($"Custom loop terminal integrity-warning append is invalid. {details}");
        }

        var candidate = current with
        {
            LifecycleVersion = checked(current.LifecycleVersion + 1),
            UpdatedAtUtc = warning.TimestampUtc,
            Events = [.. current.Events, warning]
        };
        var serialized = SerializeBounded(candidate);
        ValidateReservedTraceCapacity(current, candidate, artifact.PersistedUtf8Bytes, serialized.LongLength);
        await WriteArtifactAsync(matches[0].Path, serialized, ToSummary(candidate), overwrite: true, cancellationToken);
        return CustomLoopRunStoreResult.Updated(candidate);
    }

    /// <summary>
    /// Replaces one nonterminal run using optimistic lifecycle-version concurrency.
    /// </summary>
    /// <param name="run">The complete replacement whose lifecycle version is exactly one greater than the expected version.</param>
    /// <param name="expectedLifecycleVersion">The currently expected durable lifecycle version.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// A result describing update, absence, version conflict, terminal immutability, deletion conflict, or trace-capacity refusal.
    /// </returns>
    public async Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        ValidateCanonicalRun(run);
        if (expectedLifecycleVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLifecycleVersion), expectedLifecycleVersion, "Expected lifecycle version must be at least 1.");
        }

        if (run.LifecycleVersion != checked(expectedLifecycleVersion + 1))
        {
            throw new ArgumentException("Updated custom loop run lifecycle version must be exactly one greater than the expected version.", nameof(run));
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var locations = EnumerateArtifactLocations();
        var matches = locations.Where(location => string.Equals(location.RunId, run.Id, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
        {
            return CustomLoopRunStoreResult.NotFound();
        }

        if (matches.Length > 1)
        {
            throw new FormatException($"Custom loop run id `{run.Id}` exists in more than one loop directory. The persisted state requires review.");
        }

        var artifact = await ReadArtifactAsync(matches[0], cancellationToken);
        if (artifact.Tombstone is not null)
        {
            return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.DeletedIdentityConflict, null, null);
        }

        var current = artifact.Run ?? throw new FormatException($"Custom loop run `{run.Id}` contains an unsupported artifact.");
        if (current.LifecycleVersion != expectedLifecycleVersion)
        {
            return CustomLoopRunStoreResult.VersionConflict(current, expectedLifecycleVersion);
        }

        if (current.IsTerminal)
        {
            return CustomLoopRunStoreResult.TerminalImmutable(current, expectedLifecycleVersion);
        }

        var validation = CustomLoopRunValidator.ValidateUpdate(current, run);
        if (!validation.IsValid)
        {
            var details = string.Join(" ", validation.Errors.Select(error => $"{error.Field}: {error.Message}"));
            throw new FormatException($"Custom loop run update is invalid. {details}");
        }

        var serialized = SerializeBounded(run);
        ValidateReservedTraceCapacity(current, run, artifact.PersistedUtf8Bytes, serialized.LongLength);

        await WriteArtifactAsync(matches[0].Path, serialized, ToSummary(run), overwrite: true, cancellationToken);
        return CustomLoopRunStoreResult.Updated(run);
    }

    /// <summary>
    /// Determines whether a matching live run can reserve enough trace capacity for the candidate dispatch.
    /// </summary>
    /// <param name="candidate">The canonical candidate state whose encoded size and remaining lifecycle reserve are measured.</param>
    /// <param name="expectedLifecycleVersion">The live lifecycle version the caller expects to dispatch from.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// <see langword="false"/> only when the unique live, nonterminal run at the expected version lacks capacity.
    /// Missing, stale, terminal, deleted, or ambiguous state returns <see langword="true"/> so the authoritative mutation path
    /// can report that separate conflict.
    /// </returns>
    public async Task<bool> HasSufficientTraceCapacityForDispatchAsync(CustomLoopRunRecord candidate, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        ValidateCanonicalRun(candidate);
        if (expectedLifecycleVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLifecycleVersion), expectedLifecycleVersion, "Expected lifecycle version must be at least 1.");
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var matches = EnumerateArtifactLocations().Where(location => string.Equals(location.RunId, candidate.Id, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            return true;
        }

        var artifact = await ReadArtifactAsync(matches[0], cancellationToken);
        if (artifact.Tombstone is not null || artifact.Run is null || artifact.Run.LifecycleVersion != expectedLifecycleVersion || artifact.Run.IsTerminal)
        {
            return true;
        }

        return HasSufficientTraceCapacityForDispatch(candidate);
    }

    private static bool HasSufficientTraceCapacityForDispatch(CustomLoopRunRecord candidate)
    {
        var serialized = CustomLoopRunArtifactCodec.Encode(candidate);
        return serialized.LongLength <= CustomLoopLimits.MaxRunTraceUtf8Bytes
            && CalculateRequiredTraceCapacity(candidate, serialized.LongLength) <= CustomLoopLimits.MaxRunTraceUtf8Bytes;
    }

    private static bool TerminalWarningsEqual(CustomLoopRunEvent left, CustomLoopRunEvent right)
    {
        return left.Sequence == right.Sequence
            && string.Equals(left.EventId, right.EventId, StringComparison.Ordinal)
            && left.TimestampUtc == right.TimestampUtc
            && left.Kind == right.Kind
            && string.Equals(left.Detail, right.Detail, StringComparison.Ordinal)
            && left.ContextBlocks is { Length: 0 }
            && right.ContextBlocks is { Length: 0 }
            && left.Iteration is null
            && right.Iteration is null
            && left.StepId is null
            && right.StepId is null
            && left.Attempt is null
            && right.Attempt is null
            && left.CanonicalOutput is null
            && right.CanonicalOutput is null
            && left.OriginalOutputCharacterCount is null
            && right.OriginalOutputCharacterCount is null
            && left.CanonicalOutputTruncated is null
            && right.CanonicalOutputTruncated is null
            && left.RetainedForLoopReasoning is null
            && right.RetainedForLoopReasoning is null
            && left.PublishedToInvokingConversation is null
            && right.PublishedToInvokingConversation is null
            && left.ConversationPublicationId is null
            && right.ConversationPublicationId is null
            && left.Provider is null
            && right.Provider is null
            && left.Model is null
            && right.Model is null
            && left.ProviderResponseId is null
            && right.ProviderResponseId is null
            && left.ExitDecision is null
            && right.ExitDecision is null
            && left.ToolAuthority is null
            && right.ToolAuthority is null
            && left.ToolEvidence is null
            && right.ToolEvidence is null
            && left.TraceReservationUtf8Bytes is null
            && right.TraceReservationUtf8Bytes is null
            && left.ControlExpectedLifecycleVersion is null
            && right.ControlExpectedLifecycleVersion is null
            && left.SequentialNodeEvidence is null
            && right.SequentialNodeEvidence is null
            && left.PureNodeOutcomeJson is null
            && right.PureNodeOutcomeJson is null;
    }

    private async Task<ArtifactScanResult> ScanArtifactsAsync(Action<RunArtifact>? visitor, CancellationToken cancellationToken)
    {
        var locations = EnumerateArtifactLocations();
        var accumulator = new ArtifactScanAccumulator();
        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunArtifact? artifact = await ReadArtifactAsync(location, cancellationToken);
            accumulator.Add(artifact);
            visitor?.Invoke(artifact);
            artifact = null;
        }

        return accumulator.Complete();
    }

    private async Task<CustomLoopRunDiscoveryIndex> LoadDiscoveryIndexAsync(CancellationToken cancellationToken)
    {
        return (await LoadDiscoveryIndexWithSourceAsync(cancellationToken)).Index;
    }

    private async Task<(CustomLoopRunDiscoveryIndex Index, bool RebuiltFromCanonicalArtifacts)> LoadDiscoveryIndexWithSourceAsync(CancellationToken cancellationToken)
    {
        DeleteOrphanedDiscoveryIndexTemporaryArtifacts();
        var hasPendingMutation = File.Exists(_discoveryIndexPendingPath);
        if (hasPendingMutation)
        {
            EnsureSafeArtifactPath(_discoveryIndexPendingPath, mustExist: true);
        }

        try
        {
            var index = await ReadDiscoveryIndexAsync(cancellationToken);
            if (!hasPendingMutation && index is not null && DiscoveryIndexMatchesArtifacts(index))
            {
                return (index, false);
            }
        }
        catch (FormatException exception) when (exception is not UnsupportedCustomLoopRunDiscoveryIndexSchemaException)
        {
            // The index is derived evidence. Rebuild it from canonical run artifacts below.
        }

        return (await RebuildDiscoveryIndexAsync(previousRevision: 0, cancellationToken), true);
    }

    private async Task<CustomLoopRunDiscoveryIndex?> ReadCleanDiscoveryIndexAsync(CancellationToken cancellationToken)
    {
        DeleteOrphanedDiscoveryIndexTemporaryArtifacts();
        var hasPendingMutation = File.Exists(_discoveryIndexPendingPath);
        if (hasPendingMutation)
        {
            EnsureSafeArtifactPath(_discoveryIndexPendingPath, mustExist: true);
        }

        try
        {
            var index = await ReadDiscoveryIndexAsync(cancellationToken);
            return !hasPendingMutation && index is not null && DiscoveryIndexMatchesArtifacts(index) ? index : null;
        }
        catch (FormatException exception) when (exception is not UnsupportedCustomLoopRunDiscoveryIndexSchemaException)
        {
            return null;
        }
    }

    private async Task<CustomLoopRunDiscoveryIndex?> ReadDiscoveryIndexAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_discoveryIndexPath))
        {
            return null;
        }

        var content = await ReadBoundedJsonArtifactAsync(_runsRoot, _discoveryIndexPath, CustomLoopLimits.MaxRunDiscoveryIndexUtf8Bytes, "Custom loop run discovery index", cancellationToken);
        CustomLoopJsonDepthPolicy.ValidatePersistedJsonDepth(content, _jsonOptions.MaxDepth, "Custom loop run discovery index", _discoveryIndexPath);
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = _jsonOptions.MaxDepth });
            RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            ThrowIfDiscoveryIndexSchemaVersionIsUnsupported(document.RootElement);
            RequireCompleteContract(document.RootElement, typeof(CustomLoopRunDiscoveryIndex), "$");
            var index = JsonSerializer.Deserialize<CustomLoopRunDiscoveryIndex>(content, _jsonOptions) ?? throw new FormatException("The custom loop run discovery index was empty.");
            ValidateDiscoveryIndex(index);
            return index;
        }
        catch (JsonException exception)
        {
            throw new FormatException("The custom loop run discovery index contains invalid JSON, unknown fields, missing fields, or unsupported enum values.", exception);
        }
    }

    private static void ThrowIfDiscoveryIndexSchemaVersionIsUnsupported(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number
            || !schemaVersion.TryGetInt32(out var value)
            || value == CustomLoopRunDiscoveryIndex.CurrentSchemaVersion)
        {
            return;
        }

        throw new UnsupportedCustomLoopRunDiscoveryIndexSchemaException(value);
    }

    private async Task<CustomLoopRunDiscoveryIndex> RebuildDiscoveryIndexAsync(long previousRevision, CancellationToken cancellationToken)
    {
        var index = await BuildDiscoveryIndexAsync(previousRevision, cancellationToken);
        try
        {
            await WriteDiscoveryIndexAsync(index, cancellationToken);
            DeleteDiscoveryIndexPendingMarker();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The discovery index is derived. Canonical readable evidence remains available when cache persistence is not writable.
        }
        return index;
    }

    private async Task<CustomLoopRunDiscoveryIndex> BuildDiscoveryIndexAsync(long previousRevision, CancellationToken cancellationToken)
    {
        var entries = new List<CustomLoopRunDiscoveryIndexEntry>();
        await ScanArtifactsAsync(artifact =>
        {
            var summary = artifact.Run is not null ? ToSummary(artifact.Run) : ToSummary(artifact.Tombstone!);
            entries.Add(CreateDiscoveryIndexEntry(artifact.Location.Path, summary, artifact.PersistedHash));
        }, cancellationToken);
        entries.Sort(CompareDiscoveryIndexEntries);
        var index = new CustomLoopRunDiscoveryIndex(CustomLoopRunDiscoveryIndex.CurrentSchemaVersion, checked(previousRevision + 1), entries.ToArray());
        ValidateDiscoveryIndex(index);
        return index;
    }

    private async Task UpdateDiscoveryIndexAsync(CustomLoopRunDiscoveryIndex? current, string artifactPath, CustomLoopRunSummary summary, string artifactHash, CancellationToken cancellationToken)
    {
        if (current is null)
        {
            await RebuildDiscoveryIndexAsync(previousRevision: 0, cancellationToken);
            return;
        }

        var entries = current.Entries
            .Where(entry => !string.Equals(entry.Summary.Id, summary.Id, StringComparison.Ordinal))
            .Append(CreateDiscoveryIndexEntry(artifactPath, summary, artifactHash))
            .OrderBy(entry => entry, Comparer<CustomLoopRunDiscoveryIndexEntry>.Create(CompareDiscoveryIndexEntries))
            .ToArray();
        var updated = new CustomLoopRunDiscoveryIndex(CustomLoopRunDiscoveryIndex.CurrentSchemaVersion, checked(current.Revision + 1), entries);
        ValidateDiscoveryIndex(updated);
        if (!DiscoveryIndexMatchesArtifacts(updated))
        {
            await RebuildDiscoveryIndexAsync(current.Revision, cancellationToken);
            return;
        }

        await WriteDiscoveryIndexAsync(updated, cancellationToken);
        DeleteDiscoveryIndexPendingMarker();
    }

    private async Task WriteDiscoveryIndexAsync(CustomLoopRunDiscoveryIndex index, CancellationToken cancellationToken)
    {
        var content = CustomLoopJsonDepthPolicy.SerializeToUtf8Bytes(index, _jsonOptions, "Custom loop run discovery index", _discoveryIndexPath);
        if (content.Length + 1 > CustomLoopLimits.MaxRunDiscoveryIndexUtf8Bytes)
        {
            throw new FormatException($"The custom loop run discovery index exceeds {CustomLoopLimits.MaxRunDiscoveryIndexUtf8Bytes} UTF-8 bytes.");
        }

        var terminated = new byte[content.Length + 1];
        content.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        await WriteBoundedJsonArtifactAsync(_runsRoot, _discoveryIndexPath, terminated, overwrite: File.Exists(_discoveryIndexPath), cancellationToken);
        CacheDiscoveryIndex(index, GetDiscoveryIndexFingerprint());
    }

    private DiscoveryIndexFileFingerprint? GetDiscoveryIndexFingerprint()
    {
        if (!File.Exists(_discoveryIndexPath))
        {
            return null;
        }

        EnsureSafeArtifactPath(_runsRoot, _discoveryIndexPath, mustExist: true);
        var info = new FileInfo(_discoveryIndexPath);
        info.Refresh();
        return info.Exists ? new DiscoveryIndexFileFingerprint(info.Length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks) : null;
    }

    private async Task<CachedMonitorResult?> TryGetCachedMonitorAsync(DiscoveryIndexFileFingerprint fingerprint, string runId, CancellationToken cancellationToken)
    {
        CustomLoopRunDiscoveryIndexEntry? entry;
        lock (_monitorCacheGate)
        {
            if (_monitorCacheFingerprint != fingerprint || _monitorCache is null)
            {
                return null;
            }

            if (!_monitorCache.TryGetValue(runId, out entry))
            {
                return null;
            }
        }

        return await ValidateMonitorEntryAsync(entry, cancellationToken);
    }

    private async Task<CachedMonitorResult?> ValidateMonitorEntryAsync(CustomLoopRunDiscoveryIndexEntry entry, CancellationToken cancellationToken)
    {
        ThrowIfRunIdentityIsAmbiguous(entry.Summary.Id, entry.Summary.LoopId);
        var path = GetRunPath(entry.Summary.LoopId, entry.Summary.Id);
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.Length != entry.ArtifactUtf8Bytes || info.LastWriteTimeUtc.Ticks != entry.ArtifactLastWriteUtcTicks)
        {
            return null;
        }

        EnsureSafeArtifactPath(path, mustExist: true);
        bool summaryWasVerified;
        long observedChangeVersion;
        lock (_monitorCacheGate)
        {
            _monitorArtifactChangeVersions.TryGetValue(path, out observedChangeVersion);
            summaryWasVerified = !_monitorWatcherUncertain
                && observedChangeVersion == 0
                && _verifiedMonitorSummaryBindings.Contains(entry.SummaryBindingHash);
        }

        try
        {
            if (summaryWasVerified)
            {
                try
                {
                    var observedHash = await ComputeBoundedArtifactHashAsync(path, cancellationToken);
                    if (!string.Equals(observedHash, entry.ArtifactHash, StringComparison.Ordinal))
                    {
                        return null;
                    }
                }
                catch (IOException exception) when (IsLockContention(exception))
                {
                    // A previously verified cache remains the only readable snapshot while another reader owns an
                    // exclusive lease. Watcher uncertainty and all accessible-file paths still require current bytes.
                }

                ThrowIfRunIdentityIsAmbiguous(entry.Summary.Id, entry.Summary.LoopId);
                return new CachedMonitorResult(entry.Summary, entry.ArtifactHash);
            }

            var artifact = await ReadArtifactAsync(new RunArtifactLocation(path, entry.Summary.LoopId, entry.Summary.Id), cancellationToken);
            var canonicalSummary = artifact.Run is not null ? ToSummary(artifact.Run) : ToSummary(artifact.Tombstone!);
            if (!string.Equals(artifact.PersistedHash, entry.ArtifactHash, StringComparison.Ordinal) || canonicalSummary != entry.Summary)
            {
                return null;
            }

            lock (_monitorCacheGate)
            {
                if (!_monitorWatcherUncertain && _monitorWatcher is not null)
                {
                    _verifiedMonitorSummaryBindings.Add(entry.SummaryBindingHash);
                }
                if (observedChangeVersion != 0
                    && _monitorArtifactChangeVersions.TryGetValue(path, out var currentChangeVersion)
                    && currentChangeVersion == observedChangeVersion)
                {
                    _monitorArtifactChangeVersions.Remove(path);
                }
            }
            ThrowIfRunIdentityIsAmbiguous(entry.Summary.Id, entry.Summary.LoopId);
            return new CachedMonitorResult(canonicalSummary, artifact.PersistedHash);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async Task<CachedMonitorResult?> ValidateRebuiltMonitorEntryAsync(CustomLoopRunDiscoveryIndexEntry entry, CancellationToken cancellationToken)
    {
        ThrowIfRunIdentityIsAmbiguous(entry.Summary.Id, entry.Summary.LoopId);
        var path = GetRunPath(entry.Summary.LoopId, entry.Summary.Id);
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.Length != entry.ArtifactUtf8Bytes || info.LastWriteTimeUtc.Ticks != entry.ArtifactLastWriteUtcTicks)
        {
            return null;
        }

        EnsureSafeArtifactPath(path, mustExist: true);
        var content = await ReadBoundedArtifactAsync(path, cancellationToken);
        if (!string.Equals(ComputeHash(content), entry.ArtifactHash, StringComparison.Ordinal))
        {
            return null;
        }

        lock (_monitorCacheGate)
        {
            if (!_monitorWatcherUncertain && _monitorWatcher is not null && !_monitorArtifactChangeVersions.ContainsKey(path))
            {
                _verifiedMonitorSummaryBindings.Add(entry.SummaryBindingHash);
            }
        }

        ThrowIfRunIdentityIsAmbiguous(entry.Summary.Id, entry.Summary.LoopId);
        return new CachedMonitorResult(entry.Summary, entry.ArtifactHash);
    }

    private void ThrowIfRunIdentityIsAmbiguous(string runId, string expectedLoopId)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            long observedTopologyVersion;
            MonitorRunOwnership? ownership;
            lock (_monitorCacheGate)
            {
                observedTopologyVersion = _monitorArtifactTopologyVersion;
                if (_monitorRunOwnerships is not null && _monitorRunOwnershipVersion == observedTopologyVersion)
                {
                    _monitorRunOwnerships.TryGetValue(runId, out ownership);
                    ThrowIfRunOwnershipIsAmbiguous(runId, expectedLoopId, ownership);
                    return;
                }
            }

            var ownerships = ReadMonitorRunOwnerships();
            lock (_monitorCacheGate)
            {
                if (_monitorArtifactTopologyVersion != observedTopologyVersion)
                {
                    continue;
                }

                _monitorRunOwnerships = ownerships;
                _monitorRunOwnershipVersion = observedTopologyVersion;
                ownerships.TryGetValue(runId, out ownership);
            }

            ThrowIfRunOwnershipIsAmbiguous(runId, expectedLoopId, ownership);
            return;
        }

        throw new FormatException($"Custom loop run identity ownership changed repeatedly while `{runId}` was being monitored. The persisted state requires review.");
    }

    private IReadOnlyDictionary<string, MonitorRunOwnership> ReadMonitorRunOwnerships()
    {
        var ownerships = new Dictionary<string, MonitorRunOwnership>(StringComparer.Ordinal);
        foreach (var location in EnumerateArtifactLocations())
        {
            ownerships[location.RunId] = ownerships.TryGetValue(location.RunId, out var existing)
                ? new MonitorRunOwnership(existing.LoopId, checked(existing.ArtifactCount + 1))
                : new MonitorRunOwnership(location.LoopId, 1);
        }
        return ownerships;
    }

    private static void ThrowIfRunOwnershipIsAmbiguous(string runId, string expectedLoopId, MonitorRunOwnership? ownership)
    {
        if (ownership is not null && (ownership.ArtifactCount != 1 || !string.Equals(ownership.LoopId, expectedLoopId, StringComparison.Ordinal)))
        {
            throw new FormatException($"Custom loop run id `{runId}` exists in more than one canonical artifact location. The persisted state requires review.");
        }
    }

    private void EnsureMonitorWatcher()
    {
        lock (_monitorCacheGate)
        {
            if (_monitorWatcher is not null)
            {
                return;
            }

            var watcher = _monitorWatcherFactory(_runsRoot);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.Security;
            watcher.Changed += MonitorArtifactChanged;
            watcher.Created += MonitorArtifactTopologyChanged;
            watcher.Deleted += MonitorArtifactTopologyChanged;
            watcher.Renamed += MonitorArtifactRenamed;
            watcher.Error += MonitorWatcherError;
            watcher.EnableRaisingEvents = true;
            _monitorWatcher = watcher;
            _monitorWatcherUncertain = false;
        }
    }

    private void MonitorArtifactChanged(object sender, FileSystemEventArgs eventArgs)
    {
        RecordMonitorArtifactChange(eventArgs.FullPath);
    }

    private void MonitorArtifactTopologyChanged(object sender, FileSystemEventArgs eventArgs)
    {
        RecordMonitorArtifactChange(eventArgs.FullPath);
        RecordMonitorArtifactTopologyChange(eventArgs.FullPath);
    }

    private void MonitorArtifactRenamed(object sender, RenamedEventArgs eventArgs)
    {
        RecordMonitorArtifactChange(eventArgs.OldFullPath);
        RecordMonitorArtifactChange(eventArgs.FullPath);
        RecordMonitorArtifactTopologyChange(eventArgs.OldFullPath);
        RecordMonitorArtifactTopologyChange(eventArgs.FullPath);
    }

    private void MonitorWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        FileSystemWatcher? watcher;
        lock (_monitorCacheGate)
        {
            if (!ReferenceEquals(sender, _monitorWatcher))
            {
                return;
            }

            watcher = _monitorWatcher;
            _monitorWatcher = null;
            _monitorWatcherUncertain = true;
            _monitorArtifactTopologyVersion++;
            _monitorRunOwnerships = null;
            _monitorRunOwnershipVersion = -1;
            _verifiedMonitorSummaryBindings.Clear();
        }
        watcher.Dispose();
    }

    private void RecordMonitorArtifactChange(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            lock (_monitorCacheGate)
            {
                _monitorWatcherUncertain = true;
            }
            return;
        }

        lock (_monitorCacheGate)
        {
            if (!_monitorArtifactPaths.Contains(fullPath))
            {
                return;
            }

            _monitorArtifactChangeVersions[fullPath] = ++_monitorArtifactChangeVersion;
        }
    }

    private void RecordMonitorArtifactTopologyChange(string path)
    {
        if (!IsPotentialMonitorArtifactTopologyPath(path))
        {
            return;
        }

        lock (_monitorCacheGate)
        {
            _monitorArtifactTopologyVersion++;
        }
    }

    private bool IsPotentialMonitorArtifactTopologyPath(string path)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(_runsRoot, Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison))
        {
            return true;
        }

        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return CustomLoopArtifactIdentifier.IsValid(parts[0]);
        }

        return parts.Length == 2
            && CustomLoopArtifactIdentifier.IsValid(parts[0])
            && string.Equals(Path.GetExtension(parts[1]), ".json", StringComparison.OrdinalIgnoreCase)
            && CustomLoopArtifactIdentifier.IsValid(Path.GetFileNameWithoutExtension(parts[1]));
    }

    /// <summary>
    /// Executes the dispose operation.
    /// </summary>
    /// <returns>The operation.</returns>
    public void Dispose()
    {
        lock (_monitorCacheGate)
        {
            _monitorWatcher?.Dispose();
            _monitorWatcher = null;
        }
    }

    private void CacheDiscoveryIndex(CustomLoopRunDiscoveryIndex index, DiscoveryIndexFileFingerprint? fingerprint)
    {
        if (fingerprint is null)
        {
            return;
        }

        var summaries = index.Entries.ToDictionary(entry => entry.Summary.Id, entry => entry, StringComparer.Ordinal);
        var artifactPaths = index.Entries.Select(entry => GetRunPath(entry.Summary.LoopId, entry.Summary.Id)).ToHashSet(PathComparer);
        lock (_monitorCacheGate)
        {
            _monitorCache = summaries;
            _monitorCacheFingerprint = fingerprint;
            _monitorArtifactPaths.Clear();
            _monitorArtifactPaths.UnionWith(artifactPaths);
            foreach (var stalePath in _monitorArtifactChangeVersions.Keys.Where(path => !_monitorArtifactPaths.Contains(path)).ToArray())
            {
                _monitorArtifactChangeVersions.Remove(stalePath);
            }
            _verifiedMonitorSummaryBindings.IntersectWith(index.Entries.Select(entry => entry.SummaryBindingHash));
        }
    }

    private async Task MarkDiscoveryIndexPendingAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_discoveryIndexPendingPath))
        {
            EnsureSafeArtifactPath(_discoveryIndexPendingPath, mustExist: true);
            return;
        }

        await WriteBoundedJsonArtifactAsync(_runsRoot, _discoveryIndexPendingPath, _discoveryIndexPendingContent, overwrite: false, cancellationToken);
    }

    private void DeleteDiscoveryIndexPendingMarker()
    {
        if (!File.Exists(_discoveryIndexPendingPath))
        {
            return;
        }

        EnsureSafeArtifactPath(_discoveryIndexPendingPath, mustExist: true);
        File.Delete(_discoveryIndexPendingPath);
    }

    private void DeleteOrphanedDiscoveryIndexTemporaryArtifacts()
    {
        if (!Directory.Exists(_runsRoot))
        {
            return;
        }

        EnsureSafeDirectory(_runsRoot, create: false);
        foreach (var path in Directory.EnumerateFiles(_runsRoot, "*", SearchOption.TopDirectoryOnly).Where(IsDiscoveryIndexTemporaryArtifactPath))
        {
            EnsureSafeArtifactPath(_runsRoot, path, mustExist: true);
            File.Delete(path);
        }
    }

    private bool DiscoveryIndexMatchesArtifacts(CustomLoopRunDiscoveryIndex index)
    {
        var locations = EnumerateArtifactLocations();
        if (locations.Count != index.Entries.Length)
        {
            return false;
        }

        var locationsByIdentity = locations.ToDictionary(location => (location.LoopId, location.RunId));
        foreach (var entry in index.Entries)
        {
            var summary = entry.Summary;
            if (!locationsByIdentity.TryGetValue((summary.LoopId, summary.Id), out var location))
            {
                return false;
            }

            var info = new FileInfo(location.Path);
            info.Refresh();
            if (!info.Exists || info.Length != entry.ArtifactUtf8Bytes || info.LastWriteTimeUtc.Ticks != entry.ArtifactLastWriteUtcTicks)
            {
                return false;
            }

            if (!string.Equals(entry.SummaryBindingHash, ComputeSummaryBindingHash(summary, entry.ArtifactHash), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<CustomLoopRunSummary[]?> ReadCanonicalDiscoveryPageAsync(IEnumerable<CustomLoopRunDiscoveryIndexEntry> entries, CancellationToken cancellationToken)
    {
        var summaries = new List<CustomLoopRunSummary>();
        foreach (var entry in entries)
        {
            var verified = await ValidateMonitorEntryAsync(entry, cancellationToken);
            if (verified is null)
            {
                return null;
            }

            summaries.Add(verified.Summary);
        }

        return summaries.ToArray();
    }

    private async Task<CustomLoopRunPage> ListCanonicalPageWithoutMutationLockAsync(int maximumCount, string? loopId, CustomLoopRunPageCursor? after, CancellationToken cancellationToken)
    {
        var index = await BuildDiscoveryIndexAsync(previousRevision: 0, cancellationToken);
        var summaries = index.Entries
            .Select(entry => entry.Summary)
            .Where(summary => loopId is null || string.Equals(summary.LoopId, loopId, StringComparison.Ordinal))
            .Where(summary => after is null || CompareSummaryToCursor(summary, after) > 0)
            .Take(maximumCount + 1)
            .ToArray();
        return CreateDiscoveryPage(summaries, maximumCount, loopId);
    }

    private static CustomLoopRunPage CreateDiscoveryPage(CustomLoopRunSummary[] summaries, int maximumCount, string? loopId)
    {
        var hasMore = summaries.Length > maximumCount;
        var items = summaries.Take(maximumCount).ToArray();
        var continuationCursor = hasMore
            ? CustomLoopRunPageCursorCodec.Encode(ToCursor(items[^1], loopId))
            : null;
        return new CustomLoopRunPage(items, continuationCursor);
    }

    private static CustomLoopRunDiscoveryIndexEntry CreateDiscoveryIndexEntry(string path, CustomLoopRunSummary summary, string artifactHash)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileNotFoundException("Custom loop run artifact does not exist.", path);
        }

        RequireHash(artifactHash, nameof(artifactHash));
        return new CustomLoopRunDiscoveryIndexEntry(summary, artifactHash, ComputeSummaryBindingHash(summary, artifactHash), info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static void ValidateDiscoveryIndex(CustomLoopRunDiscoveryIndex index)
    {
        if (index.Revision < 1 || index.Entries is null)
        {
            throw new FormatException("The custom loop run discovery index uses an unsupported schema or revision.");
        }

        if (index.Entries.Length > CustomLoopLimits.MaxRunTracesPerWorkspace + CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
        {
            throw new FormatException("The custom loop run discovery index exceeds its explicit entry limit.");
        }

        var runIds = new HashSet<string>(StringComparer.Ordinal);
        var admissionOperationIds = new HashSet<string>(StringComparer.Ordinal);
        for (var indexPosition = 0; indexPosition < index.Entries.Length; indexPosition++)
        {
            var entry = index.Entries[indexPosition] ?? throw new FormatException("The custom loop run discovery index contains a null entry.");
            var summary = entry.Summary ?? throw new FormatException("The custom loop run discovery index contains a null summary.");
            if (!CustomLoopArtifactIdentifier.IsValid(summary.Id)
                || !CustomLoopArtifactIdentifier.IsValid(summary.LoopId)
                || !CustomLoopArtifactIdentifier.IsValid(summary.AdmissionOperationId, CustomLoopLimits.MaxMutationOperationIdCharacters))
            {
                throw new FormatException("The custom loop run discovery index contains an invalid run, loop, or admission-operation identifier.");
            }

            RequireHash(entry.ArtifactHash, nameof(entry.ArtifactHash));
            RequireHash(entry.SummaryBindingHash, nameof(entry.SummaryBindingHash));
            if (!string.Equals(entry.SummaryBindingHash, ComputeSummaryBindingHash(summary, entry.ArtifactHash), StringComparison.Ordinal))
            {
                throw new FormatException("The custom loop run discovery index contains a summary that is not bound to its canonical artifact.");
            }

            if (!runIds.Add(summary.Id) || !admissionOperationIds.Add(summary.AdmissionOperationId))
            {
                throw new FormatException("The custom loop run discovery index contains duplicate run or admission-operation identities.");
            }

            var maximumArtifactBytes = summary.IsDeleted ? CustomLoopLimits.MaxRunTraceTombstoneUtf8Bytes : CustomLoopLimits.MaxRunTraceUtf8Bytes;
            if (entry.ArtifactUtf8Bytes is < 1 || entry.ArtifactUtf8Bytes > maximumArtifactBytes
                || entry.ArtifactLastWriteUtcTicks < DateTimeOffset.UnixEpoch.UtcTicks
                || summary.DefinitionVersion < 1
                || summary.IsDeleted && summary.LifecycleVersion != 0
                || !summary.IsDeleted && summary.LifecycleVersion < 1
                || summary.CreatedAtUtc < DateTimeOffset.UnixEpoch
                || summary.UpdatedAtUtc < summary.CreatedAtUtc
                || summary.CompletedAtUtc < summary.CreatedAtUtc
                || summary.Iteration < 0
                || summary.NextStepIndex < 0
                || summary.IsDeleted && !IsTerminalStatus(summary.Status))
            {
                throw new FormatException("The custom loop run discovery index contains invalid bounded metadata.");
            }

            if (indexPosition > 0 && CompareDiscoveryIndexEntries(index.Entries[indexPosition - 1], entry) >= 0)
            {
                throw new FormatException("The custom loop run discovery index is not in canonical immutable order.");
            }
        }
    }

    private static int CompareDiscoveryIndexEntries(CustomLoopRunDiscoveryIndexEntry left, CustomLoopRunDiscoveryIndexEntry right) => CompareSummaries(left.Summary, right.Summary);

    private static bool IsTerminalStatus(CustomLoopRunStatus status) => status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;

    private static int CompareSummaries(CustomLoopRunSummary left, CustomLoopRunSummary right)
    {
        var comparison = right.CreatedAtUtc.CompareTo(left.CreatedAtUtc);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private static int CompareSummaryToCursor(CustomLoopRunSummary summary, CustomLoopRunPageCursor cursor)
    {
        var comparison = cursor.CreatedAtUtc.CompareTo(summary.CreatedAtUtc);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(summary.Id, cursor.RunId);
    }

    private static CustomLoopRunPageCursor ToCursor(CustomLoopRunSummary summary, string? loopId) => new(summary.CreatedAtUtc, summary.Id, loopId);

    private IReadOnlyList<RunArtifactLocation> EnumerateArtifactLocations()
    {
        if (!Directory.Exists(_runsRoot))
        {
            return [];
        }

        EnsureSafeDirectory(_runsRoot, create: false);
        var rootFiles = Directory.EnumerateFiles(_runsRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (rootFiles.Any(path => !IsAllowedRootArtifact(path)))
        {
            throw new FormatException("Custom loop run storage contains an unexpected root-level artifact; traces must be stored beneath their loop-id directory.");
        }

        var locations = new List<RunArtifactLocation>();
        foreach (var directory in Directory.EnumerateDirectories(_runsRoot, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, PathComparer))
        {
            EnsureSafeDirectory(directory, create: false);
            var loopId = Path.GetFileName(directory);
            if (!CustomLoopArtifactIdentifier.IsValid(loopId))
            {
                throw new FormatException($"Custom loop run directory `{directory}` has an unsafe loop id.");
            }

            if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new FormatException($"Custom loop run directory `{directory}` cannot contain nested directories.");
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, PathComparer))
            {
                if (IsTemporaryArtifactPath(path, CustomLoopLimits.MaxArtifactIdCharacters))
                {
                    continue;
                }

                EnsureSafeArtifactPath(path, mustExist: true);
                var runId = Path.GetFileNameWithoutExtension(path);
                if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase) || !CustomLoopArtifactIdentifier.IsValid(runId))
                {
                    throw new FormatException($"Custom loop run artifact `{path}` has an unsafe run id.");
                }

                locations.Add(new RunArtifactLocation(path, loopId, runId));
            }
        }

        if (locations.Count > CustomLoopLimits.MaxRunTracesPerWorkspace + CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
        {
            throw new FormatException("Custom loop run storage contains more live traces and tombstones than its explicit bounded enumeration limit.");
        }

        return locations;
    }

    private static bool IsAllowedRootArtifact(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.Equals(fileName, MutationLockFileName, StringComparison.Ordinal)
            || string.Equals(fileName, DiscoveryIndexFileName, StringComparison.Ordinal)
            || string.Equals(fileName, DiscoveryIndexPendingFileName, StringComparison.Ordinal)
            || IsDiscoveryIndexTemporaryArtifactPath(path);
    }

    private async Task<RunArtifact> ReadArtifactAsync(RunArtifactLocation location, CancellationToken cancellationToken)
    {
        EnsureSafeArtifactPath(location.Path, mustExist: true);
        // Restart readers must share the destination with the fenced writer. The writer publishes a sibling staging file
        // with an atomic replacement, so sharing write access does not permit in-place mutation or expose partial JSON.
        await using var stream = OpenSharedArtifactReadStream(location.Path);
        if (stream.Length <= 0 || stream.Length > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException($"Custom loop run `{location.Path}` must contain between 1 and {CustomLoopLimits.MaxRunTraceUtf8Bytes} UTF-8 bytes.");
        }

        var length = checked((int)stream.Length);
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await stream.ReadExactlyAsync(rented.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            return ReadArtifact(location, rented.AsMemory(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private RunArtifact ReadArtifact(RunArtifactLocation location, ReadOnlyMemory<byte> utf8Json)
    {
        CustomLoopJsonDepthPolicy.ValidatePersistedJsonDepth(utf8Json.Span, _jsonOptions.MaxDepth, "Custom loop run artifact", location.Path);
        try
        {
            var persistedHash = ComputeHash(utf8Json.Span);
            if (CustomLoopRunArtifactCodec.IsEnvelope(utf8Json.Span))
            {
                var run = CustomLoopRunArtifactCodec.DecodeDepthValidated(utf8Json, location.Path);
                if (!string.Equals(run.Id, location.RunId, StringComparison.Ordinal) || !string.Equals(run.LoopId, location.LoopId, StringComparison.Ordinal))
                {
                    throw new FormatException($"Custom loop run `{location.Path}` identity does not match its containing directory and filename.");
                }

                return new RunArtifact(location, run, null, persistedHash, utf8Json.Length);
            }

            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = _jsonOptions.MaxDepth });
            RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            if (document.RootElement.TryGetProperty("artifactKind", out var artifactKind))
            {
                if (artifactKind.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException($"Custom loop trace `{location.Path}` has an unsupported artifact kind.");
                }

                if (string.Equals(artifactKind.GetString(), CustomLoopTraceTombstone.CurrentArtifactKind, StringComparison.Ordinal))
                {
                    RequireCompleteContract(document.RootElement, typeof(CustomLoopTraceTombstone), "$");
                    var tombstone = JsonSerializer.Deserialize<CustomLoopTraceTombstone>(utf8Json.Span, _jsonOptions) ?? throw new FormatException($"Custom loop trace tombstone `{location.Path}` was empty.");
                    ValidateTombstone(tombstone);
                    if (!string.Equals(tombstone.RunId, location.RunId, StringComparison.Ordinal) || !string.Equals(tombstone.LoopId, location.LoopId, StringComparison.Ordinal))
                    {
                        throw new FormatException($"Custom loop trace tombstone `{location.Path}` identity does not match its containing directory and filename.");
                    }

                    if (utf8Json.Length > CustomLoopLimits.MaxRunTraceTombstoneUtf8Bytes)
                    {
                        throw new FormatException($"Custom loop trace tombstone `{location.Path}` exceeds {CustomLoopLimits.MaxRunTraceTombstoneUtf8Bytes} UTF-8 bytes.");
                    }

                    return new RunArtifact(location, null, tombstone, persistedHash, utf8Json.Length);
                }

                throw new FormatException($"Custom loop trace `{location.Path}` has an unsupported artifact kind.");
            }

            throw new FormatException($"Custom loop run `{location.Path}` uses the unsupported legacy direct-run JSON shape. Live runs require the versioned compact envelope.");
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Custom loop run `{location.Path}` contains invalid JSON, unknown fields, missing fields, or unsupported enum values.", exception);
        }
    }

    private async Task<RunArtifact?> ReadArtifactByRunIdAsync(string runId, CancellationToken cancellationToken)
    {
        var matches = EnumerateArtifactLocations().Where(location => string.Equals(location.RunId, runId, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1)
        {
            throw new FormatException($"Custom loop run id `{runId}` exists in more than one loop directory. The persisted state requires review.");
        }

        return matches.Length == 0 ? null : await ReadArtifactAsync(matches[0], cancellationToken);
    }

    private async Task<byte[]> ReadBoundedArtifactAsync(string path, CancellationToken cancellationToken)
    {
        EnsureSafeArtifactPath(path, mustExist: true);
        await using var stream = OpenSharedArtifactReadStream(path);
        if (stream.Length <= 0 || stream.Length > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException($"Custom loop run `{path}` must contain between 1 and {CustomLoopLimits.MaxRunTraceUtf8Bytes} UTF-8 bytes.");
        }

        var content = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        return content;
    }

    private async Task<string> ComputeBoundedArtifactHashAsync(string path, CancellationToken cancellationToken)
    {
        EnsureSafeArtifactPath(path, mustExist: true);
        await using var stream = OpenSharedArtifactReadStream(path);
        if (stream.Length <= 0 || stream.Length > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException($"Custom loop run `{path}` must contain between 1 and {CustomLoopLimits.MaxRunTraceUtf8Bytes} UTF-8 bytes.");
        }

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] SerializeBounded(CustomLoopRunRecord run)
    {
        var content = CustomLoopRunArtifactCodec.Encode(run);
        if (content.Length > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException($"Custom loop run `{run.Id}` exceeds the {CustomLoopLimits.MaxRunTraceUtf8Bytes}-byte trace limit.");
        }

        return content;
    }

    private static FileStream OpenSharedArtifactReadStream(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static void ValidateReservedTraceCapacity(CustomLoopRunRecord current, CustomLoopRunRecord candidate, long currentUtf8Bytes, long candidateUtf8Bytes)
    {
        var appended = candidate.Events.Skip(current.Events.Length).ToArray();
        var delta = Math.Max(0, candidateUtf8Bytes - currentUtf8Bytes);
        var toolEvidenceBudget = appended.Where(item => item.ToolEvidence is not null).Sum(item => (long)GetToolEvidencePhaseUtf8Bytes(item.ToolEvidence!));
        var appendedAttemptStarts = appended.Where(item => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted).ToArray();
        var priorAttemptStarts = current.Events.Where(item => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted).ToArray();
        var attemptClosures = appended.Where(IsAttemptClosure).ToArray();
        var pureCompletions = attemptClosures.Count(item => IsExactPureCompletion(candidate, item));
        var lifecycleEvents = appended.Count(IsLifecycleControlEvent);
        if (toolEvidenceBudget > 0 && delta > toolEvidenceBudget)
        {
            throw new FormatException("A governed tool-evidence phase exceeded its reserved maximum serialized footprint.");
        }

        var materializedAttemptShapes = priorAttemptStarts.Select(AttemptStartIdentity).ToHashSet();
        var hasStartedAttempt = priorAttemptStarts.Length > 0;
        long attemptStartBudget = 0;
        foreach (var start in appendedAttemptStarts)
        {
            if (!hasStartedAttempt)
            {
                attemptStartBudget = checked(attemptStartBudget + CustomLoopLimits.MaxFirstAttemptStartEvidenceUtf8Bytes);
                hasStartedAttempt = true;
                materializedAttemptShapes.Add(AttemptStartIdentity(start));
            }
            else if (materializedAttemptShapes.Add(AttemptStartIdentity(start)))
            {
                attemptStartBudget = checked(attemptStartBudget + CustomLoopLimits.MaxFirstDistinctNodeAttemptStartEvidenceUtf8Bytes);
            }
            else
            {
                attemptStartBudget = checked(attemptStartBudget + CustomLoopLimits.MaxAttemptStartEvidenceUtf8Bytes);
            }
        }

        if (appendedAttemptStarts.Length > 0 && delta > attemptStartBudget)
        {
            throw new FormatException($"A node-attempt start exceeded its reserved maximum serialized footprint ({delta} > {attemptStartBudget}).");
        }

        var attemptClosureBudget = checked(
            (long)pureCompletions * CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes
            + (long)(attemptClosures.Length - pureCompletions) * CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        if (attemptClosures.Length > 0 && delta > attemptClosureBudget)
        {
            throw new FormatException(pureCompletions == attemptClosures.Length
                ? "A pure-node outcome exceeded its reserved maximum serialized footprint."
                : pureCompletions == 0
                    ? "A provider-attempt outcome exceeded its reserved maximum serialized footprint."
                    : "A mixed node-attempt outcome append exceeded its reserved maximum serialized footprint.");
        }

        var controlEventCount = candidate.Events.Count(IsLifecycleControlEvent);
        if (controlEventCount > MaximumLifecycleControlEvents(candidate))
        {
            throw new FormatException("The run consumed lifecycle/control slots reserved for terminalization or its one optional post-terminal integrity warning.");
        }

        var terminalDataBudget = !current.IsTerminal && candidate.IsTerminal ? CustomLoopLimits.MaxPermanentTerminalIntegrityReserveUtf8Bytes : 0;
        if (lifecycleEvents > 0 && delta > checked((long)lifecycleEvents * CustomLoopLimits.MaxTraceControlEventUtf8Bytes + terminalDataBudget))
        {
            throw new FormatException("A lifecycle control event exceeded its permanent reserved serialized footprint.");
        }

        var committedAndReserved = CalculateRequiredTraceCapacity(candidate, candidateUtf8Bytes);
        if (committedAndReserved > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException("The run trace lacks atomically reserved capacity for all mandatory pure-node/provider/tool evidence, remaining lifecycle/control events, and terminal/integrity evidence.");
        }
    }

    /// <summary>
    /// Calculates the reserved trace capacity required for the current persisted run state.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="persistedUtf8Bytes">The persisted UTF-8 bytes.</param>
    /// <returns>The persisted bytes plus any lifecycle-dependent capacity reservation.</returns>
    internal static long CalculateRequiredTraceCapacity(CustomLoopRunRecord run, long persistedUtf8Bytes)
    {
        if (persistedUtf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(persistedUtf8Bytes));
        }

        ValidateRecordedToolEvidenceBound(run);
        if (run.IsTerminal)
        {
            return checked(persistedUtf8Bytes + (HasTerminalIntegrityWarning(run) ? 0 : CustomLoopLimits.MaxTraceControlEventUtf8Bytes));
        }

        var maximumAttempts = CustomLoopLimits.GetMaximumModelAttempts(run.AdmittedDefinition.InferenceSteps.Length, run.AdmittedDefinition.ExitPolicy.MaxAdditionalIterations);
        var canonicalExitStarts = run.Events.Count(IsCanonicalDeterministicExitStart);
        if (canonicalExitStarts > 1)
        {
            throw new FormatException("A schema-1 sequential run can retain only one deterministic canonical Exit dispatch marker.");
        }

        var startedModelAttempts = run.Events.Count(item => (item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted)
                && !IsCanonicalNonProviderNodeEvent(run, item))
            - canonicalExitStarts;
        if (startedModelAttempts > maximumAttempts)
        {
            throw new FormatException("The run contains more provider-attempt starts than its admitted traversal can execute.");
        }

        var controlEventCount = run.Events.Count(IsLifecycleControlEvent);
        if (controlEventCount > MaximumLifecycleControlEvents(run))
        {
            throw new FormatException("The run consumed lifecycle/control slots reserved for terminalization or its one optional post-terminal integrity warning.");
        }

        var outstanding = CalculateOutstandingReservation(run);
        var remainingControlReserve = CustomLoopLimits.MaxTraceControlReserveUtf8Bytes - checked(controlEventCount * CustomLoopLimits.MaxTraceControlEventUtf8Bytes);
        // Reserve evidence only for effects already open. Future pure-node, provider, and tool effects are
        // independently capacity-gated before dispatch; workspace quota reserves the full per-run ceiling.
        return checked(
            persistedUtf8Bytes
            + outstanding.Utf8Bytes
            + remainingControlReserve
            + CustomLoopLimits.MaxPermanentTerminalIntegrityReserveUtf8Bytes);
    }

    private static bool IsCanonicalDeterministicExitStart(CustomLoopRunEvent item)
        => item is
        {
            Kind: CustomLoopRunEventKind.ExitDecisionStarted,
            StepId: "exit",
            Attempt: 1,
            SequentialNodeEvidence:
            {
                SchemaVersion: CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                Disposition: CustomLoopSequentialNodeDisposition.Unknown,
            },
        };

    private static int GetToolEvidencePhaseUtf8Bytes(CustomLoopToolTraceEvidence evidence)
    {
        return evidence.Phase switch
        {
            CustomLoopToolEvidencePhase.RequestReserved => CustomLoopLimits.MaxGovernedToolRequestEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.GovernanceDecided => CustomLoopLimits.MaxGovernedToolGovernanceEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.OutcomeObserved when !evidence.ReturnedToModel => CustomLoopLimits.MaxGovernedToolOutcomeEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.OutcomeObserved => CustomLoopLimits.MaxGovernedToolReturnEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.IntegrityFailed when evidence.ReservedUtf8Bytes == CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes => CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.IntegrityFailed => CustomLoopLimits.MaxGovernedToolReturnEvidenceUtf8Bytes,
            _ => 0
        };
    }

    private static void ValidateRecordedToolEvidenceBound(CustomLoopRunRecord run)
    {
        var groups = run.Events
            .Where(item => item.ToolEvidence is not null)
            .GroupBy(item => (item.ToolEvidence!.RequestOrdinal, item.ToolEvidence.RequestCorrelationId))
            .ToArray();
        var reservationCount = groups.Count(group => group.Any(item => item.ToolEvidence!.Phase == CustomLoopToolEvidencePhase.RequestReserved));
        var integrityOnly = groups.Where(group => group.All(item => item.ToolEvidence!.Phase != CustomLoopToolEvidencePhase.RequestReserved)).ToArray();
        var maximumVisibleRequests = run.AdmittedDefinition.ToolAssignments.Length == 0 ? 0 : CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun;
        var maximumRecordedRequests = run.AdmittedDefinition.ToolAssignments.Length == 0 ? 0 : CustomLoopLimits.MaxRecordedGovernedToolRequestsPerRun;
        if (reservationCount > maximumVisibleRequests
            || groups.Length > maximumRecordedRequests
            || integrityOnly.Length > 1
            || integrityOnly.Any(group => group.Count() != 1 || group.Single().ToolEvidence!.Phase != CustomLoopToolEvidencePhase.IntegrityFailed))
        {
            throw new FormatException("The run contains governed tool evidence outside the finite model-visible and one repeated-request integrity bounds.");
        }
    }

    private static bool IsLifecycleControlEvent(CustomLoopRunEvent item)
    {
        return item.Kind is CustomLoopRunEventKind.LifecycleChanged or CustomLoopRunEventKind.IntegrityWarning;
    }

    private static bool IsAttemptClosure(CustomLoopRunEvent item)
        => item.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.ExitDecisionCompleted or CustomLoopRunEventKind.NodeAttemptFailed;

    private static bool IsExactPureCompletion(CustomLoopRunRecord run, CustomLoopRunEvent item)
        => item is
        {
            Kind: CustomLoopRunEventKind.NodeAttemptCompleted,
            PureNodeOutcomeJson: not null,
            SequentialNodeEvidence:
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            },
        }
        && IsPureNodeEvent(run, item);

    private static bool IsPureNodeEvent(CustomLoopRunRecord run, CustomLoopRunEvent item)
    {
        if (item.SequentialNodeEvidence is not { ActivationOrdinal: var activationOrdinal })
        {
            return false;
        }

        var node = run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal);
        return node?.Descriptor.Kind is GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate;
    }

    private static bool IsCanonicalNonProviderNodeEvent(CustomLoopRunRecord run, CustomLoopRunEvent item)
    {
        if (item.SequentialNodeEvidence is not { ActivationOrdinal: var activationOrdinal })
        {
            return false;
        }

        return run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal)?.Descriptor.Kind is
            GovernedLoopNodeKind.Transform
            or GovernedLoopNodeKind.Validate
            or GovernedLoopNodeKind.Condition
            or GovernedLoopNodeKind.Join
            or GovernedLoopNodeKind.Wait;
    }

    /// <summary>
    /// Determines whether the run has terminal integrity warning.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns><see langword="true"/> when has terminal integrity warning; otherwise, <see langword="false"/>.</returns>
    internal static bool HasTerminalIntegrityWarning(CustomLoopRunRecord run)
    {
        return run.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.IntegrityWarning };
    }

    private static int MaximumLifecycleControlEvents(CustomLoopRunRecord run)
    {
        if (!run.IsTerminal)
        {
            return CustomLoopLimits.MaxNonterminalLifecycleControlEventsPerRun;
        }

        return HasTerminalIntegrityWarning(run)
            ? CustomLoopLimits.MaxLifecycleControlEventsPerRun
            : CustomLoopLimits.MaxTerminalLifecycleControlEventsBeforeIntegrityWarning;
    }

    private static AttemptStartShape AttemptStartIdentity(CustomLoopRunEvent item)
    {
        return new AttemptStartShape(item.Kind == CustomLoopRunEventKind.ExitDecisionStarted, item.StepId ?? string.Empty);
    }

    private static TraceReservation CalculateOutstandingReservation(CustomLoopRunRecord run)
    {
        if (run.IsTerminal)
        {
            return new TraceReservation(0, null);
        }

        long total = 0;
        long? earliest = null;
        var openAttempts = 0;
        foreach (var started in run.Events.Where(item => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted))
        {
            var closed = run.Events.Any(item => item.Sequence > started.Sequence
                && item.Iteration == started.Iteration
                && string.Equals(item.StepId, started.StepId, StringComparison.Ordinal)
                && item.Attempt == started.Attempt
                && item.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.ExitDecisionCompleted or CustomLoopRunEventKind.NodeAttemptFailed);
            if (closed)
            {
                continue;
            }

            openAttempts++;
            total = checked(total + (started.TraceReservationUtf8Bytes ?? 0));
            earliest = earliest is null ? started.Sequence : Math.Min(earliest.Value, started.Sequence);
        }

        if (openAttempts > 1)
        {
            throw new FormatException("A custom-loop run cannot hold more than one node-attempt trace reservation.");
        }

        foreach (var group in run.Events.Where(item => item.ToolEvidence is not null).GroupBy(item => (item.ToolEvidence!.RequestOrdinal, item.ToolEvidence.RequestCorrelationId)))
        {
            var reservation = group.SingleOrDefault(item => item.ToolEvidence!.Phase == CustomLoopToolEvidencePhase.RequestReserved);
            if (reservation is null)
            {
                if (group.Count() == 1 && group.Single().ToolEvidence!.Phase == CustomLoopToolEvidencePhase.IntegrityFailed)
                {
                    continue;
                }

                throw new FormatException("Governed tool evidence exists without one exact request reservation or the one bounded non-actuating repeated-request integrity record.");
            }

            var finalized = group.Any(item => item.ToolEvidence!.Phase == CustomLoopToolEvidencePhase.IntegrityFailed
                || item.ToolEvidence!.Phase == CustomLoopToolEvidencePhase.OutcomeObserved && item.ToolEvidence.ReturnedToModel);
            if (finalized)
            {
                continue;
            }

            var latest = group.OrderBy(item => item.Sequence).Last().ToolEvidence!;
            var remaining = latest.Phase switch
            {
                CustomLoopToolEvidencePhase.RequestReserved => CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes - CustomLoopLimits.MaxGovernedToolRequestEvidenceUtf8Bytes,
                CustomLoopToolEvidencePhase.GovernanceDecided => CustomLoopLimits.MaxGovernedToolOutcomeEvidenceUtf8Bytes + CustomLoopLimits.MaxGovernedToolReturnEvidenceUtf8Bytes,
                CustomLoopToolEvidencePhase.OutcomeObserved when !latest.ReturnedToModel => CustomLoopLimits.MaxGovernedToolReturnEvidenceUtf8Bytes,
                _ => 0
            };
            total = checked(total + remaining);
            earliest = earliest is null ? reservation.Sequence : Math.Min(earliest.Value, reservation.Sequence);
        }

        return new TraceReservation(total, earliest);
    }

    private static byte[] SerializeTombstoneBounded(CustomLoopTraceTombstone tombstone)
    {
        var content = CustomLoopJsonDepthPolicy.SerializeToUtf8Bytes(tombstone, _jsonOptions, $"Custom loop trace tombstone `{tombstone.RunId}`");
        if (content.Length + 1 > CustomLoopLimits.MaxRunTraceTombstoneUtf8Bytes)
        {
            throw new FormatException($"Custom loop trace tombstone `{tombstone.RunId}` exceeds the {CustomLoopLimits.MaxRunTraceTombstoneUtf8Bytes}-byte limit.");
        }

        var terminated = new byte[content.Length + 1];
        content.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        return terminated;
    }

    private async Task<CustomLoopTraceDeletionStoreResult> CommitDeletionOutcomeAsync(CustomLoopTraceDeletionOperation operation, CustomLoopTraceDeletionStoreStatus status, CustomLoopTraceTombstone? tombstone, CancellationToken cancellationToken)
    {
        var integrity = tombstone is not null && status == CustomLoopTraceDeletionStoreStatus.Deleted
            ? tombstone.OutcomeIntegrity
            : CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit;
        var updatedAtUtc = tombstone is null ? operation.UpdatedAtUtc : Max(operation.UpdatedAtUtc, tombstone.DeletedAtUtc);
        var completed = operation with
        {
            UpdatedAtUtc = updatedAtUtc,
            State = CustomLoopTraceDeletionOperationState.OutcomeCommitted,
            Outcome = status,
            Tombstone = tombstone,
            Integrity = integrity
        };
        ValidateDeletionOperation(completed);
        await WriteTraceDeletionOperationAsync(completed, overwrite: true, cancellationToken);
        return new CustomLoopTraceDeletionStoreResult(status, tombstone, integrity);
    }

    private async Task<CustomLoopTraceDeletionOperation?> ReadTraceDeletionOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        var paths = EnumerateTraceDeletionOperationPaths();
        var path = paths.SingleOrDefault(candidate => string.Equals(Path.GetFileNameWithoutExtension(candidate), operationId, StringComparison.Ordinal));
        if (path is null)
        {
            return null;
        }

        var utf8Json = await ReadBoundedJsonArtifactAsync(_traceDeletionOperationsRoot, path, CustomLoopLimits.MaxRunTraceDeletionOperationUtf8Bytes, "Custom loop trace-deletion operation", cancellationToken);
        CustomLoopJsonDepthPolicy.ValidatePersistedJsonDepth(utf8Json, _jsonOptions.MaxDepth, "Custom loop trace-deletion operation", path);
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = _jsonOptions.MaxDepth });
            RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            RequireCompleteContract(document.RootElement, typeof(CustomLoopTraceDeletionOperation), "$");
            var operation = JsonSerializer.Deserialize<CustomLoopTraceDeletionOperation>(utf8Json, _jsonOptions) ?? throw new FormatException($"Custom loop trace-deletion operation `{path}` was empty.");
            ValidateDeletionOperation(operation);
            if (!string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
            {
                throw new FormatException($"Custom loop trace-deletion operation `{path}` identity does not match its filename.");
            }

            return operation;
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Custom loop trace-deletion operation `{path}` contains invalid JSON, unknown fields, missing fields, or unsupported enum values.", exception);
        }
    }

    private IReadOnlyList<string> EnumerateTraceDeletionOperationPaths()
    {
        if (!Directory.Exists(_traceDeletionOperationsRoot))
        {
            return [];
        }

        EnsureSafeDirectory(_traceDeletionOperationsRoot, create: false);
        if (Directory.EnumerateDirectories(_traceDeletionOperationsRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Custom loop trace-deletion operation storage cannot contain subdirectories.");
        }

        var paths = Directory.EnumerateFiles(_traceDeletionOperationsRoot, "*", SearchOption.TopDirectoryOnly).Where(path => !IsTemporaryArtifactPath(path, CustomLoopLimits.MaxMutationOperationIdCharacters)).OrderBy(path => path, PathComparer).ToArray();
        if (paths.Length > CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace)
        {
            throw new FormatException($"Custom loop trace-deletion operation storage exceeds its explicit {CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace}-artifact limit.");
        }

        foreach (var path in paths)
        {
            EnsureSafeArtifactPath(_traceDeletionOperationsRoot, path, mustExist: true);
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
                || !CustomLoopArtifactIdentifier.IsValid(Path.GetFileNameWithoutExtension(path), CustomLoopLimits.MaxMutationOperationIdCharacters))
            {
                throw new FormatException($"Custom loop trace-deletion operation artifact `{path}` has an unsafe filename.");
            }
        }

        return paths;
    }

    private static bool CanUseTombstoneDeletionOperationReservation(RunArtifact? artifact, CustomLoopTraceDeletionRequest request)
    {
        return artifact?.Run is { IsTerminal: true }
            && string.Equals(artifact.PersistedHash, request.ExpectedTraceHash, StringComparison.Ordinal);
    }

    private void RecoverOrphanedTemporaryArtifacts()
    {
        RecoverRunTemporaryArtifacts();
        RecoverTraceDeletionOperationTemporaryArtifacts();
        RecoverScheduleAdmissionTemporaryArtifacts();
    }

    private void RecoverRunTemporaryArtifacts()
    {
        EnsureSafeDirectory(_runsRoot, create: false);
        var rootFiles = Directory.EnumerateFiles(_runsRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (rootFiles.Any(path => !IsAllowedRootArtifact(path)))
        {
            throw new FormatException("Custom loop run storage contains an unexpected root-level artifact; traces must be stored beneath their loop-id directory.");
        }

        foreach (var directory in Directory.EnumerateDirectories(_runsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            EnsureSafeDirectory(directory, create: false);
            if (!CustomLoopArtifactIdentifier.IsValid(Path.GetFileName(directory)))
            {
                throw new FormatException($"Custom loop run directory `{directory}` has an unsafe loop id.");
            }

            if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new FormatException($"Custom loop run directory `{directory}` cannot contain nested directories.");
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsTemporaryArtifactPath(path, CustomLoopLimits.MaxArtifactIdCharacters))
                {
                    DeleteOrphanedTemporaryArtifact(_runsRoot, path);
                }
                else if (LooksLikeTemporaryArtifactPath(path))
                {
                    throw new FormatException($"Custom loop run storage contains an unrecognized temporary-looking artifact `{path}`.");
                }
            }
        }
    }

    private void RecoverTraceDeletionOperationTemporaryArtifacts()
    {
        if (!Directory.Exists(_traceDeletionOperationsRoot))
        {
            return;
        }

        EnsureSafeDirectory(_traceDeletionOperationsRoot, create: false);
        if (Directory.EnumerateDirectories(_traceDeletionOperationsRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Custom loop trace-deletion operation storage cannot contain subdirectories.");
        }

        foreach (var path in Directory.EnumerateFiles(_traceDeletionOperationsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsTemporaryArtifactPath(path, CustomLoopLimits.MaxMutationOperationIdCharacters))
            {
                DeleteOrphanedTemporaryArtifact(_traceDeletionOperationsRoot, path);
            }
            else if (LooksLikeTemporaryArtifactPath(path))
            {
                throw new FormatException($"Custom loop trace-deletion operation storage contains an unrecognized temporary-looking artifact `{path}`.");
            }
        }
    }

    private void RecoverScheduleAdmissionTemporaryArtifacts()
    {
        if (!Directory.Exists(_scheduleAdmissionsRoot))
        {
            return;
        }

        EnsureSafeDirectory(_scheduleAdmissionsRoot, create: false);
        if (Directory.EnumerateDirectories(_scheduleAdmissionsRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Schedule run-admission evidence storage cannot contain subdirectories.");
        }

        foreach (var path in Directory.EnumerateFiles(_scheduleAdmissionsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (string.Equals(fileName, ScheduleAdmissionRetirementFileName, StringComparison.Ordinal))
            {
                continue;
            }

            if (fileName.StartsWith($".{ScheduleAdmissionRetirementFileName}.", StringComparison.Ordinal) && fileName.EndsWith(".tmp", StringComparison.Ordinal))
            {
                DeleteOrphanedTemporaryArtifact(_scheduleAdmissionsRoot, path);
            }
            else if (IsTemporaryArtifactPath(path, TriggerDeliveryLimits.MaxDeliveryIdCharacters))
            {
                DeleteOrphanedTemporaryArtifact(_scheduleAdmissionsRoot, path);
            }
            else if (LooksLikeTemporaryArtifactPath(path))
            {
                throw new FormatException($"Schedule run-admission evidence storage contains an unrecognized temporary-looking artifact `{path}`.");
            }
        }
    }

    private void DeleteOrphanedTemporaryArtifact(string root, string path)
    {
        EnsureSafeArtifactPath(root, path, mustExist: true);
        File.Delete(path);
        if (File.Exists(path))
        {
            throw new IOException($"Orphaned custom loop staging artifact `{path}` could not be removed under the mutation lease.");
        }
    }

    private static bool LooksLikeTemporaryArtifactPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith(".", StringComparison.Ordinal) || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTemporaryArtifactPath(string path, int maximumIdentifierCharacters)
    {
        var fileName = Path.GetFileName(path);
        if (!fileName.StartsWith(".", StringComparison.Ordinal) || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        var nonceSeparator = fileName.LastIndexOf('.', fileName.Length - ".tmp".Length - 1);
        var nonceStart = nonceSeparator + 1;
        var nonceLength = fileName.Length - nonceStart - ".tmp".Length;
        var targetFileName = nonceSeparator > 1 ? fileName[1..nonceSeparator] : "";
        var identifier = targetFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? targetFileName[..^".json".Length] : "";
        if (!CustomLoopArtifactIdentifier.IsValid(identifier, maximumIdentifierCharacters) || nonceLength != 32)
        {
            return false;
        }

        for (var index = nonceStart; index < nonceStart + nonceLength; index++)
        {
            if (fileName[index] is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDiscoveryIndexTemporaryArtifactPath(string path)
    {
        var fileName = Path.GetFileName(path);
        var indexPrefix = "." + DiscoveryIndexFileName + ".";
        var pendingPrefix = "." + DiscoveryIndexPendingFileName + ".";
        var prefix = fileName.StartsWith(indexPrefix, StringComparison.Ordinal)
            ? indexPrefix
            : fileName.StartsWith(pendingPrefix, StringComparison.Ordinal)
                ? pendingPrefix
                : null;
        if (prefix is null || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        var nonceStart = prefix.Length;
        var nonceLength = fileName.Length - nonceStart - ".tmp".Length;
        if (nonceLength != 32)
        {
            return false;
        }

        for (var index = nonceStart; index < nonceStart + nonceLength; index++)
        {
            if (fileName[index] is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private async Task WriteTraceDeletionOperationAsync(CustomLoopTraceDeletionOperation operation, bool overwrite, CancellationToken cancellationToken)
    {
        ValidateDeletionOperation(operation);
        var path = GetTraceDeletionOperationPath(operation.OperationId);
        var content = CustomLoopJsonDepthPolicy.SerializeToUtf8Bytes(operation, _jsonOptions, "Custom loop trace-deletion operation", path);
        if (content.Length + 1 > CustomLoopLimits.MaxRunTraceDeletionOperationUtf8Bytes)
        {
            throw new FormatException($"Custom loop trace-deletion operation `{operation.OperationId}` exceeds the {CustomLoopLimits.MaxRunTraceDeletionOperationUtf8Bytes}-byte limit.");
        }

        var terminated = new byte[content.Length + 1];
        content.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        await WriteBoundedJsonArtifactAsync(_traceDeletionOperationsRoot, path, terminated, overwrite, cancellationToken);
    }

    private string GetTraceDeletionOperationPath(string operationId)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        var path = Path.Combine(_traceDeletionOperationsRoot, safeOperationId + ".json");
        EnsureContained(_traceDeletionOperationsRoot, path);
        return path;
    }

    private async Task<byte[]> ReadBoundedJsonArtifactAsync(string root, string path, int maximumBytes, string label, CancellationToken cancellationToken)
    {
        EnsureSafeArtifactPath(root, path, mustExist: true);
        await using var stream = OpenSharedArtifactReadStream(path);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw new FormatException($"{label} `{path}` must contain between 1 and {maximumBytes} UTF-8 bytes.");
        }

        var content = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        return content;
    }

    private async Task WriteBoundedJsonArtifactAsync(string root, string path, byte[] content, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureSafeArtifactPath(root, path, mustExist: overwrite);
        var directory = Path.GetDirectoryName(path)!;
        EnsureSafeDirectory(directory, create: true);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        EnsureContained(root, temporaryPath);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            EnsureSafeDirectory(directory, create: false);
            EnsureSafeArtifactPath(root, temporaryPath, mustExist: true);
            await MoveAtomicallyWithRetryAsync(temporaryPath, path, overwrite, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task WriteArtifactAsync(string path, byte[] content, CustomLoopRunSummary summary, bool overwrite, CancellationToken cancellationToken)
    {
        var index = await ReadCleanDiscoveryIndexAsync(cancellationToken);
        await MarkDiscoveryIndexPendingAsync(cancellationToken);
        await WriteArtifactContentAsync(path, content, overwrite, cancellationToken);
        using var maintenanceCancellation = new CancellationTokenSource(_discoveryIndexMaintenanceTimeout);
        try
        {
            await UpdateDiscoveryIndexAsync(index, path, summary, ComputeHash(content), maintenanceCancellation.Token);
        }
        catch (Exception exception) when (IsRecoverableDiscoveryIndexMaintenanceFailure(exception))
        {
            // Canonical mutation committed. Leave the pending marker so the next indexed read repairs derived evidence.
        }
    }

    private async Task WriteArtifactContentAsync(string path, byte[] content, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureSafeArtifactPath(path, mustExist: overwrite);
        var directory = Path.GetDirectoryName(path)!;
        EnsureSafeDirectory(directory, create: true);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        EnsureContained(_runsRoot, temporaryPath);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            EnsureSafeDirectory(directory, create: false);
            EnsureSafeArtifactPath(temporaryPath, mustExist: true);
            await MoveAtomicallyWithRetryAsync(temporaryPath, path, overwrite, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task MoveAtomicallyWithRetryAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                if (overwrite && File.Exists(destinationPath))
                {
                    // #475: bounded readers share the destination with the fenced writer; replacement stays atomic because the writer publishes a sibling staging file.
                    File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(sourcePath, destinationPath, overwrite);
                }
                return;
            }
            catch (Exception exception) when (attempt < MaximumAtomicMoveAttempts && IsTransientWindowsFileAccess(exception))
            {
                await Task.Delay(_atomicMoveRetryDelay, cancellationToken);
            }
        }
    }

    private static bool IsTransientWindowsFileAccess(Exception exception)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (exception is UnauthorizedAccessException)
        {
            return true;
        }

        if (exception is not IOException ioException)
        {
            return false;
        }

        var errorCode = ioException.HResult & 0xFFFF;
        return errorCode is 5 or 32 or 33;
    }

    private string GetRunPath(string loopId, string runId)
    {
        var safeLoopId = CustomLoopArtifactIdentifier.Require(loopId, nameof(loopId));
        var safeRunId = CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        var path = Path.Combine(_runsRoot, safeLoopId, safeRunId + ".json");
        EnsureContained(_runsRoot, path);
        return path;
    }

    private async Task<MutationLease> AcquireMutationLockAsync(CancellationToken cancellationToken)
    {
        await _processMutationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureSafeDirectory(_runsRoot, create: true);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream stream;
                try
                {
                    RejectReparsePointIfPresent(_mutationLockPath);
                    stream = new FileStream(_mutationLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                }
                catch (IOException exception) when (IsLockContention(exception))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
                    continue;
                }

                try
                {
                    RejectReparsePoint(_mutationLockPath);
                    RecoverOrphanedTemporaryArtifacts();
                    return new MutationLease(stream, _processMutationGate);
                }
                catch
                {
                    await stream.DisposeAsync();
                    throw;
                }
            }
        }
        catch
        {
            _processMutationGate.Release();
            throw;
        }
    }

    private void EnsureSafeDirectory(string path, bool create)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureContained(_workspaceRoot, fullPath);
        var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
        var current = _workspaceRoot;
        RejectReparsePoint(current);
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) && !Directory.Exists(current))
            {
                throw new IOException($"Custom loop artifact directory `{current}` is occupied by a file.");
            }

            if (!Directory.Exists(current))
            {
                if (!create)
                {
                    throw new DirectoryNotFoundException($"Custom loop artifact directory `{current}` does not exist.");
                }

                Directory.CreateDirectory(current);
            }

            RejectReparsePoint(current);
        }
    }

    private void EnsureSafeArtifactPath(string path, bool mustExist)
    {
        EnsureSafeArtifactPath(_runsRoot, path, mustExist);
    }

    private void EnsureSafeArtifactPath(string root, string path, bool mustExist)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureContained(root, fullPath);
        EnsureSafeDirectory(Path.GetDirectoryName(fullPath)!, create: !mustExist);
        if (File.Exists(fullPath))
        {
            RejectReparsePoint(fullPath);
        }
        else if (mustExist)
        {
            throw new FileNotFoundException("Custom loop run artifact does not exist.", fullPath);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Custom loop artifact path `{path}` cannot traverse a reparse point.");
        }
    }

    private static void RejectReparsePointIfPresent(string path)
    {
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
            {
                throw new IOException($"Custom loop artifact path `{path}` cannot traverse a reparse point.");
            }
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            RejectReparsePoint(path);
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        const int ResourceTemporarilyUnavailable = 11;
        const int SharingViolation = 32;
        const int LockViolation = 33;
        const int ResourceDeadlockAvoided = 35;
        var errorCode = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows()
            ? errorCode is SharingViolation or LockViolation
            : errorCode is ResourceTemporarilyUnavailable or ResourceDeadlockAvoided;
    }

    private static bool IsReadOnlyLockAccessFailure(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return true;
        }

        const int AccessDenied = 5;
        const int PermissionDenied = 13;
        const int WriteProtected = 19;
        const int ReadOnlyFileSystem = 30;
        return exception is IOException ioException && (ioException.HResult & 0xFFFF) is AccessDenied or PermissionDenied or WriteProtected or ReadOnlyFileSystem;
    }

    private static void EnsureContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison))
        {
            throw new IOException($"Custom loop artifact path `{candidate}` escapes its expected root.");
        }
    }

    private static void ValidateCanonicalRun(CustomLoopRunRecord? run)
    {
        var validation = CustomLoopRunValidator.Validate(run);
        if (!validation.IsValid)
        {
            var details = string.Join(" ", validation.Errors.Select(error => $"{error.Field}: {error.Message}"));
            throw new FormatException($"Custom loop run is invalid. {details}");
        }
    }

    private static GovernedLoopSequentialNodeEvidenceReceipt ToApplicationReceipt(CustomLoopSequentialNodeEvidence evidence)
    {
        var kind = evidence.Kind switch
        {
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome => GovernedLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection => GovernedLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention => GovernedLoopSequentialNodeEvidenceKind.AmbiguityAttention,
            _ => throw new FormatException("Dispatch-start evidence cannot satisfy a terminal sequential-node receipt lookup."),
        };
        var disposition = evidence.Disposition switch
        {
            CustomLoopSequentialNodeDisposition.Completed => GovernedLoopSequentialNodeHandlerResultStatus.Completed,
            CustomLoopSequentialNodeDisposition.Rejected => GovernedLoopSequentialNodeHandlerResultStatus.Rejected,
            CustomLoopSequentialNodeDisposition.NeedsReview => GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview,
            _ => throw new FormatException("Terminal sequential-node evidence has no terminal disposition."),
        };
        return new GovernedLoopSequentialNodeEvidenceReceipt(
            GovernedLoopSequentialNodeEvidenceReceipt.CurrentSchemaVersion,
            kind,
            evidence.WorkspaceId,
            evidence.RunId,
            EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopRevisionReference.Create(
                evidence.Revision.SchemaVersion,
                evidence.Revision.GraphId,
                evidence.Revision.RevisionId,
                evidence.Revision.ExecutableHash),
            evidence.ExecutionGeneration,
            evidence.ActivationOrdinal,
            evidence.VisitOrdinal,
            evidence.NodeId,
            evidence.Attempt!.Value,
            evidence.CycleId,
            evidence.CycleIteration,
            evidence.ControlOutcome,
            evidence.SelectedControlEdgeIds.ToArray(),
            evidence.SkippedControlEdgeIds.ToArray(),
            disposition,
            evidence.OutcomeArtifactHash,
            evidence.EvidenceHash);
    }

    private static void ValidateOrderedEvidenceRequest(GovernedLoopSequentialOrderedNodeEvidenceRequest? request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var dispatch = request.Dispatch;
        if (request.SchemaVersion != GovernedLoopSequentialOrderedNodeEvidenceRequest.CurrentSchemaVersion
            || dispatch is null
            || dispatch.SchemaVersion != GovernedLoopSequentialNodeDispatchRequest.CurrentSchemaVersion
            || dispatch.Anchor is null
            || dispatch.Plan is null
            || dispatch.Node is null
            || dispatch.Activation is null
            || dispatch.Attempt is < 1 or > EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionLimits.MaxNodeAttempt
            || request.Disposition == GovernedLoopSequentialNodeHandlerResultStatus.Unknown
            || !Enum.IsDefined(request.Disposition)
            || request.OrderedLifecycleVersion < 1
            || request.OrderedEventSequence is < 1 or > int.MaxValue
            || string.IsNullOrEmpty(request.OrderedEventId))
        {
            throw new ArgumentException("Ordered sequential-node evidence coordinates are invalid or unsupported.", nameof(request));
        }

        var binding = dispatch.Anchor.AdapterBinding;
        var snapshot = dispatch.Anchor.InvocationSnapshot;
        var plan = dispatch.Plan;
        var node = dispatch.Node;
        if (!EmbodySense.Core.Common.Loops.Sequential.GovernedLoopSequentialContractValidator.Validate(binding).IsValid
            || !EmbodySense.Core.Common.Loops.Sequential.GovernedLoopSequentialContractValidator.Validate(snapshot).IsValid
            || plan.SchemaVersion != 1
            || plan.Revision is null
            || plan.Nodes is null
            || node.Ordinal < 0
            || node.Ordinal >= plan.Nodes.Count
            || !ReferenceEquals(plan.Nodes[node.Ordinal], node)
            || dispatch.Activation.Status != GovernedLoopNodeExecutionStatus.Running
            || dispatch.Activation.PlanOrdinal != node.Ordinal
            || dispatch.Activation.Attempt != dispatch.Attempt
            || !string.Equals(dispatch.Activation.NodeId, node.NodeId, StringComparison.Ordinal)
            || !EmbodySense.Core.Application.Loops.Sequential.GovernedLoopSequentialNodeDescriptors.IsSupported(node.Descriptor)
            || !Equals(plan.Revision, binding.ExecutionBinding.Revision)
            || !string.Equals(plan.GraphArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(plan.GraphLayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal)
            || !string.Equals(snapshot.ContentHash, binding.InvocationPayloadHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("Ordered sequential-node evidence does not carry an exact guard-issued anchor and builder-issued plan node.", nameof(request));
        }
    }

    private static GovernedLoopSequentialNodeEvidenceKind ExpectedEvidenceKind(GovernedLoopSequentialNodeHandlerResultStatus disposition)
        => disposition switch
        {
            GovernedLoopSequentialNodeHandlerResultStatus.Completed => GovernedLoopSequentialNodeEvidenceKind.CompletedOutcome,
            GovernedLoopSequentialNodeHandlerResultStatus.Rejected => GovernedLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview => GovernedLoopSequentialNodeEvidenceKind.AmbiguityAttention,
            _ => GovernedLoopSequentialNodeEvidenceKind.Unknown,
        };

    private static void ValidateDeletionMutation(CustomLoopTraceDeletionMutation? mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateDeletionRequest(mutation.Request);
        RequireHash(mutation.RequestHash, nameof(mutation.RequestHash));
        if (!string.Equals(mutation.RequestHash, CustomLoopTraceDeletionRequestHash.Compute(mutation.Request), StringComparison.Ordinal))
        {
            throw new ArgumentException("Trace-deletion request hash does not match its canonical authenticated request.", nameof(mutation));
        }

        RequireUtc(mutation.RequestedAtUtc, nameof(mutation.RequestedAtUtc));
    }

    private static void ValidateDeletionOperation(CustomLoopTraceDeletionOperation? operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.SchemaVersion != CustomLoopTraceDeletionOperation.CurrentSchemaVersion)
        {
            throw new FormatException($"Unsupported custom loop trace-deletion operation schema version `{operation.SchemaVersion}`.");
        }

        CustomLoopArtifactIdentifier.Require(operation.OperationId, nameof(operation.OperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        ValidateDeletionRequest(operation.Request);
        RequireHash(operation.RequestHash, nameof(operation.RequestHash));
        if (!string.Equals(operation.OperationId, operation.Request.OperationId, StringComparison.Ordinal)
            || !string.Equals(operation.RequestHash, CustomLoopTraceDeletionRequestHash.Compute(operation.Request), StringComparison.Ordinal))
        {
            throw new FormatException("Custom loop trace-deletion operation identity or canonical request hash is inconsistent.");
        }

        RequireUtc(operation.RequestedAtUtc, nameof(operation.RequestedAtUtc));
        RequireUtc(operation.UpdatedAtUtc, nameof(operation.UpdatedAtUtc));
        if (operation.UpdatedAtUtc < operation.RequestedAtUtc)
        {
            throw new FormatException("Custom loop trace-deletion operation update timestamp cannot precede its request timestamp.");
        }

        if (operation.State == CustomLoopTraceDeletionOperationState.PendingMutation)
        {
            if (operation.Outcome != CustomLoopTraceDeletionStoreStatus.Unknown || operation.Tombstone is not null || operation.Integrity != CustomLoopTraceDeletionIntegrity.Unknown)
            {
                throw new FormatException("A pending trace-deletion operation cannot contain a committed outcome, tombstone, or outcome-integrity state.");
            }

            return;
        }

        if (operation.State != CustomLoopTraceDeletionOperationState.OutcomeCommitted || operation.Outcome == CustomLoopTraceDeletionStoreStatus.Unknown)
        {
            throw new FormatException("Custom loop trace-deletion operation state or outcome is unsupported.");
        }

        if (operation.Outcome is CustomLoopTraceDeletionStoreStatus.Deleted or CustomLoopTraceDeletionStoreStatus.AlreadyDeleted)
        {
            ValidateTombstone(operation.Tombstone);
            if (!string.Equals(operation.Tombstone!.DeletionOperationId, operation.OperationId, StringComparison.Ordinal)
                || !string.Equals(operation.Tombstone.DeletionRequestHash, operation.RequestHash, StringComparison.Ordinal)
                || !string.Equals(operation.Tombstone.RunId, operation.Request.RunId, StringComparison.Ordinal)
                || !string.Equals(operation.Tombstone.OriginalTraceHash, operation.Request.ExpectedTraceHash, StringComparison.Ordinal)
                || !string.Equals(operation.Tombstone.DeletionActor, operation.Request.Actor, StringComparison.Ordinal)
                || !string.Equals(operation.Tombstone.DeletionSurface, operation.Request.Surface, StringComparison.Ordinal)
                || operation.Tombstone.OutcomeIntegrity != operation.Integrity)
            {
                throw new FormatException("Trace-deletion operation outcome does not match its tombstone identity or integrity state.");
            }
        }
        else if (operation.Tombstone is not null)
        {
            ValidateTombstone(operation.Tombstone);
            if (operation.Outcome != CustomLoopTraceDeletionStoreStatus.OperationConflict)
            {
                throw new FormatException("A rejected trace-deletion operation cannot retain an unrelated tombstone.");
            }

            if (!string.Equals(operation.Tombstone.RunId, operation.Request.RunId, StringComparison.Ordinal)
                || string.Equals(operation.Tombstone.DeletionOperationId, operation.OperationId, StringComparison.Ordinal)
                || string.Equals(operation.Tombstone.DeletionRequestHash, operation.RequestHash, StringComparison.Ordinal))
            {
                throw new FormatException("A conflicting trace-deletion operation must retain a tombstone for the requested run and a distinct deletion identity.");
            }
        }

        if (operation.Integrity is CustomLoopTraceDeletionIntegrity.Unknown)
        {
            throw new FormatException("A committed trace-deletion outcome must retain its outcome-audit integrity state.");
        }
    }

    private static void ValidateTombstone(CustomLoopTraceTombstone? tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        if (tombstone.SchemaVersion != CustomLoopTraceTombstone.CurrentSchemaVersion
            || !string.Equals(tombstone.ArtifactKind, CustomLoopTraceTombstone.CurrentArtifactKind, StringComparison.Ordinal))
        {
            throw new FormatException("Custom loop terminal-trace tombstone schema or artifact kind is unsupported.");
        }

        CustomLoopArtifactIdentifier.Require(tombstone.RunId, nameof(tombstone.RunId));
        CustomLoopArtifactIdentifier.Require(tombstone.LoopId, nameof(tombstone.LoopId));
        CustomLoopArtifactIdentifier.Require(tombstone.AdmissionOperationId, nameof(tombstone.AdmissionOperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        CustomLoopArtifactIdentifier.Require(tombstone.DeletionOperationId, nameof(tombstone.DeletionOperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        RequireHash(tombstone.AdmissionRequestHash, nameof(tombstone.AdmissionRequestHash));
        RequireHash(tombstone.DefinitionHash, nameof(tombstone.DefinitionHash));
        RequireHash(tombstone.OriginalTraceHash, nameof(tombstone.OriginalTraceHash));
        RequireHash(tombstone.DeletionRequestHash, nameof(tombstone.DeletionRequestHash));
        if (tombstone.TerminalStatus is not CustomLoopRunStatus.Completed and not CustomLoopRunStatus.Failed and not CustomLoopRunStatus.Cancelled and not CustomLoopRunStatus.NeedsReview)
        {
            throw new FormatException("Custom loop trace tombstone must retain a terminal run status.");
        }

        if (tombstone.DefinitionVersion < 1 || tombstone.OriginalTraceUtf8Bytes < 1 || tombstone.OriginalTraceUtf8Bytes > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException("Custom loop trace tombstone definition version or original trace size is invalid.");
        }

        RequireUtc(tombstone.CreatedAtUtc, nameof(tombstone.CreatedAtUtc));
        RequireUtc(tombstone.CompletedAtUtc, nameof(tombstone.CompletedAtUtc));
        RequireUtc(tombstone.DeletedAtUtc, nameof(tombstone.DeletedAtUtc));
        if (tombstone.CompletedAtUtc < tombstone.CreatedAtUtc || tombstone.DeletedAtUtc < tombstone.CompletedAtUtc)
        {
            throw new FormatException("Custom loop trace tombstone timestamps are not monotonic.");
        }

        if (!IsActor(tombstone.DeletionActor) || !IsSurface(tombstone.DeletionSurface))
        {
            throw new FormatException("Custom loop trace tombstone actor or surface is invalid.");
        }

        var deletionRequest = new CustomLoopTraceDeletionRequest(tombstone.RunId, tombstone.OriginalTraceHash, tombstone.DeletionOperationId, tombstone.DeletionActor, tombstone.DeletionSurface);
        if (!string.Equals(tombstone.DeletionRequestHash, CustomLoopTraceDeletionRequestHash.Compute(deletionRequest), StringComparison.Ordinal))
        {
            throw new FormatException("Custom loop trace tombstone deletion metadata does not match its canonical request hash.");
        }

        if (!string.Equals(tombstone.IntentAuditCorrelationId, tombstone.DeletionOperationId, StringComparison.Ordinal)
            || !string.Equals(tombstone.OutcomeAuditCorrelationId, tombstone.DeletionOperationId, StringComparison.Ordinal)
            || tombstone.OutcomeIntegrity is not CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit and not CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted and not CustomLoopTraceDeletionIntegrity.Complete and not CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning)
        {
            throw new FormatException("Custom loop trace tombstone audit correlation or integrity state is invalid.");
        }
    }

    private static void ValidateDeletionRequest(CustomLoopTraceDeletionRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CustomLoopArtifactIdentifier.Require(request.RunId, nameof(request.RunId));
        CustomLoopArtifactIdentifier.Require(request.OperationId, nameof(request.OperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        RequireHash(request.ExpectedTraceHash, nameof(request.ExpectedTraceHash));
        if (!IsActor(request.Actor) || !IsSurface(request.Surface))
        {
            throw new ArgumentException("Trace-deletion actor or surface is invalid.", nameof(request));
        }
    }

    private static bool DeletionRequestMatches(CustomLoopTraceDeletionOperation operation, CustomLoopTraceDeletionMutation mutation)
    {
        return string.Equals(operation.RequestHash, mutation.RequestHash, StringComparison.Ordinal) && operation.Request == mutation.Request;
    }

    private static void RequireHash(string? value, string parameterName)
    {
        if (value is not { Length: CustomLoopLimits.Sha256HexCharacters } || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new FormatException($"`{parameterName}` must be lowercase SHA-256 hexadecimal.");
        }
    }

    private static bool IsHash(string? value)
        => value is { Length: CustomLoopLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new FormatException($"`{parameterName}` must be a non-default timestamp with UTC offset zero.");
        }
    }

    private static bool IsActor(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CustomLoopLimits.MaxArtifactIdCharacters && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '@' or ':');

    private static bool IsSurface(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CustomLoopLimits.MaxArtifactIdCharacters && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string ComputeHash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string ComputeSummaryBindingHash(CustomLoopRunSummary summary, string artifactHash)
    {
        var summaryContent = JsonSerializer.SerializeToUtf8Bytes(summary, _jsonOptions);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Convert.FromHexString(artifactHash));
        hash.AppendData(summaryContent);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsRecoverableDiscoveryIndexMaintenanceFailure(Exception exception)
    {
        return exception is OperationCanceledException
            or IOException
            or UnauthorizedAccessException
            or FormatException
            or JsonException
            or InvalidOperationException
            or OverflowException;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static bool SameAdmissionRequest(CustomLoopRunRecord existing, CustomLoopRunRecord candidate)
    {
        return string.Equals(existing.AdmissionOperationId, candidate.AdmissionOperationId, StringComparison.Ordinal)
            && string.Equals(existing.AdmissionRequestHash, candidate.AdmissionRequestHash, StringComparison.Ordinal);
    }

    private static CustomLoopRunSummary ToSummary(CustomLoopRunRecord run)
    {
        return new CustomLoopRunSummary(run.Id, run.LoopId, run.AdmissionOperationId, run.AdmittedDefinition.DefinitionVersion, run.LifecycleVersion, run.Status, run.CreatedAtUtc, run.UpdatedAtUtc, run.CompletedAtUtc, run.Checkpoint.Iteration, run.Checkpoint.NextStepIndex, run.FailureCode, IsDeleted: false);
    }

    private static CustomLoopRunSummary ToSummary(CustomLoopTraceTombstone tombstone)
    {
        return new CustomLoopRunSummary(tombstone.RunId, tombstone.LoopId, tombstone.AdmissionOperationId, tombstone.DefinitionVersion, 0, tombstone.TerminalStatus, tombstone.CreatedAtUtc, tombstone.DeletedAtUtc, tombstone.CompletedAtUtc, 0, 0, null, IsDeleted: true);
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            names.Clear();
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException($"JSON object `{path}` contains duplicate property `{property.Name}`.");
                }

                RejectDuplicateProperties(property.Value, path + "." + property.Name, new HashSet<string>(StringComparer.Ordinal));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]", new HashSet<string>(StringComparer.Ordinal));
                index++;
            }
        }
    }

    private static void RequireCompleteContract(JsonElement element, Type type, string path)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (type.IsEnum)
        {
            if (element.ValueKind != JsonValueKind.String || !GetCanonicalEnumNames(type).Contains(element.GetString(), StringComparer.Ordinal))
            {
                throw new FormatException($"JSON value `{path}` must be an exact supported camel-case enum name.");
            }

            return;
        }

        if (type.IsArray)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"JSON value `{path}` must be an array.");
            }

            var itemType = type.GetElementType()!;
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RequireCompleteContract(item, itemType, $"{path}[{index}]");
                index++;
            }

            return;
        }

        if (IsScalar(type))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"JSON value `{path}` must be an object.");
        }

        foreach (var property in GetPersistedProperties(type))
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? _jsonOptions.PropertyNamingPolicy!.ConvertName(property.Name);
            if (!element.TryGetProperty(name, out var value))
            {
                throw new FormatException($"JSON object `{path}` is missing required property `{name}`.");
            }

            RequireCompleteContract(value, property.PropertyType, path + "." + name);
        }
    }

    private static IEnumerable<PropertyInfo> GetPersistedProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetMethod is not null && property.GetIndexParameters().Length == 0)
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always);
    }

    private static IReadOnlyList<string> GetCanonicalEnumNames(Type type)
    {
        return Enum.GetNames(type).Select(name => JsonNamingPolicy.CamelCase.ConvertName(name)).ToArray();
    }

    private static bool IsScalar(Type type)
    {
        return type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTimeOffset) || type == typeof(DateTime) || type == typeof(Guid) || type == typeof(TimeSpan);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record DiscoveryIndexFileFingerprint(long Utf8Bytes, long CreationTimeUtcTicks, long LastWriteTimeUtcTicks);

    private sealed record CachedMonitorResult(CustomLoopRunSummary Summary, string ArtifactHash);

    private sealed record MonitorRunOwnership(string LoopId, int ArtifactCount);

    private sealed record TraceReservation(long Utf8Bytes, long? EarliestSequence);

    private readonly record struct AttemptStartShape(bool IsExit, string StepId);

}
