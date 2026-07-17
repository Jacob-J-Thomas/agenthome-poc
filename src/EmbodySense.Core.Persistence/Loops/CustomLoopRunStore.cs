using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

public sealed class CustomLoopRunStore : ICustomLoopRunStore
{
    private const string MutationLockFileName = ".custom-loop-runs.lock";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessMutationGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly WorkspacePaths _paths;
    private readonly string _workspaceRoot;
    private readonly string _runsRoot;
    private readonly string _traceDeletionOperationsRoot;
    private readonly string _mutationLockPath;
    private readonly SemaphoreSlim _processMutationGate;

    public CustomLoopRunStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _workspaceRoot = Path.GetFullPath(paths.RootPath);
        _runsRoot = Path.GetFullPath(paths.CustomLoopRunsPath);
        _traceDeletionOperationsRoot = Path.GetFullPath(paths.CustomLoopTraceDeletionOperationsPath);
        EnsureContained(_workspaceRoot, _runsRoot);
        EnsureContained(_workspaceRoot, _traceDeletionOperationsRoot);
        _mutationLockPath = Path.Combine(_runsRoot, MutationLockFileName);
        _processMutationGate = ProcessMutationGates.GetOrAdd(_runsRoot, _ => new SemaphoreSlim(1, 1));
    }

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
        var artifacts = await ReadAllArtifactsAsync(cancellationToken);
        var persistedRuns = artifacts.Where(item => item.Run is not null).Select(item => item.Run!).ToArray();
        var deletedOperation = artifacts.SingleOrDefault(item => item.Tombstone is not null && string.Equals(item.Tombstone.AdmissionOperationId, run.AdmissionOperationId, StringComparison.Ordinal));
        if (deletedOperation is not null)
        {
            return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.DeletedIdentityConflict, null, null);
        }

        var operationMatch = FindUniqueByOperation(persistedRuns, run.AdmissionOperationId);
        if (operationMatch is not null)
        {
            return SameAdmissionRequest(operationMatch, run)
                ? CustomLoopRunStoreResult.AlreadyCreated(operationMatch)
                : CustomLoopRunStoreResult.OperationConflict(operationMatch);
        }

        var deletedRunId = artifacts.SingleOrDefault(item => item.Tombstone is not null && string.Equals(item.Tombstone.RunId, run.Id, StringComparison.Ordinal));
        if (deletedRunId is not null)
        {
            return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.DeletedIdentityConflict, null, null);
        }

        var runIdMatch = FindUniqueByRunId(persistedRuns, run.Id);
        if (runIdMatch is not null)
        {
            return CustomLoopRunStoreResult.VersionConflict(runIdMatch, expectedLifecycleVersion: 0);
        }

        var activeLoopRuns = persistedRuns.Where(item => string.Equals(item.LoopId, run.LoopId, StringComparison.Ordinal) && !item.IsTerminal).ToArray();
        if (activeLoopRuns.Length > 1)
        {
            throw new FormatException($"Custom loop `{run.LoopId}` has more than one nonterminal run. The persisted state requires review.");
        }

        var activeLoopRun = activeLoopRuns.SingleOrDefault();
        if (activeLoopRun is not null)
        {
            return CustomLoopRunStoreResult.NonterminalRunExists(activeLoopRun);
        }

        var quota = CalculateQuota(artifacts);
        if (quota.RetainedTraceCount >= quota.MaximumTraceCount
            || quota.AccountedTraceUtf8Bytes > quota.MaximumWorkspaceUtf8Bytes - quota.MaximumPerTraceUtf8Bytes)
        {
            return CustomLoopRunStoreResult.LimitExceeded();
        }

        var path = GetRunPath(run.LoopId, run.Id);
        EnsureSafeDirectory(Path.GetDirectoryName(path)!, create: true);
        EnsureSafeArtifactPath(path, mustExist: false);
        await WriteArtifactAsync(path, serialized, overwrite: false, cancellationToken);
        return CustomLoopRunStoreResult.Created(run);
    }

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

    public async Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
    {
        var safeLoopId = CustomLoopArtifactIdentifier.Require(loopId, nameof(loopId));
        var matches = (await ReadAllRunsAsync(cancellationToken)).Where(run => string.Equals(run.LoopId, safeLoopId, StringComparison.Ordinal) && !run.IsTerminal).ToArray();
        if (matches.Length > 1)
        {
            throw new FormatException($"Custom loop `{safeLoopId}` has more than one nonterminal run. The persisted state requires review.");
        }

        return matches.SingleOrDefault();
    }

    public async Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount < 1 || maximumCount > CustomLoopLimits.MaxRecentRunsPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), maximumCount, $"Recent run page size must be between 1 and {CustomLoopLimits.MaxRecentRunsPageSize}.");
        }

        var summaries = (await ReadAllArtifactsAsync(cancellationToken))
            .Select(artifact => artifact.Run is not null ? ToSummary(artifact.Run) : ToSummary(artifact.Tombstone!));
        return summaries
            .OrderByDescending(summary => summary.UpdatedAtUtc)
            .ThenByDescending(summary => summary.CreatedAtUtc)
            .ThenBy(summary => summary.Id, StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();
    }

    public async Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
    {
        var runs = await ReadAllRunsAsync(cancellationToken);
        return runs
            .Where(run => !run.IsTerminal)
            .OrderBy(run => run.CreatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<CustomLoopTraceQuota> GetTraceQuotaAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_runsRoot))
        {
            return CustomLoopTraceQuota.Empty();
        }

        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        return CalculateQuota(await ReadAllArtifactsAsync(cancellationToken));
    }

    public async Task<CustomLoopTraceInspection?> InspectTraceAsync(string runId, CancellationToken cancellationToken = default)
    {
        var safeRunId = CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        await using var mutation = await AcquireMutationLockAsync(cancellationToken);
        var matches = EnumerateArtifactLocations().Where(location => string.Equals(location.RunId, safeRunId, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1)
        {
            throw new FormatException($"Custom loop run id `{safeRunId}` exists in more than one loop directory. The persisted state requires review.");
        }

        if (matches.Length == 0)
        {
            return null;
        }

        var artifact = await ReadArtifactAsync(matches[0], cancellationToken);
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
        if (existingOperation is null)
        {
            if (EnumerateTraceDeletionOperationPaths().Count >= CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
            {
                return new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.TombstoneLimitExceeded, null, CustomLoopTraceDeletionIntegrity.Complete);
            }

            await WriteTraceDeletionOperationAsync(operation, overwrite: false, cancellationToken);
        }
        else if (existingOperation.State == CustomLoopTraceDeletionOperationState.OutcomeCommitted)
        {
            return existingOperation.ToStoreResult() with { Status = existingOperation.Outcome == CustomLoopTraceDeletionStoreStatus.Deleted ? CustomLoopTraceDeletionStoreStatus.AlreadyDeleted : existingOperation.Outcome };
        }

        var artifacts = await ReadAllArtifactsAsync(cancellationToken);
        var matches = artifacts.Where(artifact => string.Equals(artifact.Location.RunId, mutation.Request.RunId, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1)
        {
            throw new FormatException($"Custom loop run id `{mutation.Request.RunId}` exists in more than one loop directory. The persisted state requires review.");
        }

        if (matches.Length == 0)
        {
            return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.NotFound, null, cancellationToken);
        }

        var artifact = matches[0];
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

        var tombstoneCount = artifacts.Count(item => item.Tombstone is not null);
        if (tombstoneCount >= CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
        {
            return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.TombstoneLimitExceeded, null, cancellationToken);
        }

        var completedAtUtc = run.CompletedAtUtc ?? throw new FormatException("A terminal custom-loop run must have a completion timestamp before trace deletion.");
        var deletedAtUtc = mutation.RequestedAtUtc < completedAtUtc ? completedAtUtc : mutation.RequestedAtUtc;
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
        await WriteArtifactAsync(artifact.Location.Path, SerializeTombstoneBounded(tombstone), overwrite: true, cancellationToken);
        return await CommitDeletionOutcomeAsync(operation, CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, cancellationToken);
    }

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
        if (tombstone is not null)
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

            await WriteArtifactAsync(path, SerializeTombstoneBounded(tombstone), overwrite: true, cancellationToken);
        }

        var updated = operation with { UpdatedAtUtc = Max(operation.UpdatedAtUtc, DateTimeOffset.UtcNow), Tombstone = tombstone, Integrity = integrity };
        await WriteTraceDeletionOperationAsync(updated, overwrite: true, cancellationToken);
        return CustomLoopTraceDeletionAuditMarkStatus.Marked;
    }

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
        var serialized = SerializeBounded(candidate, artifact.PersistedBytes);
        ValidateReservedTraceCapacity(current, candidate, artifact.PersistedUtf8Bytes, serialized.LongLength);
        await WriteArtifactAsync(matches[0].Path, serialized, overwrite: true, cancellationToken);
        return CustomLoopRunStoreResult.Updated(candidate);
    }

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

        var serialized = SerializeBounded(run, artifact.PersistedBytes);
        ValidateReservedTraceCapacity(current, run, artifact.PersistedUtf8Bytes, serialized.LongLength);

        await WriteArtifactAsync(matches[0].Path, serialized, overwrite: true, cancellationToken);
        return CustomLoopRunStoreResult.Updated(run);
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
            && right.TraceReservationUtf8Bytes is null;
    }

    private async Task<IReadOnlyList<CustomLoopRunRecord>> ReadAllRunsAsync(CancellationToken cancellationToken)
    {
        return (await ReadAllArtifactsAsync(cancellationToken)).Where(artifact => artifact.Run is not null).Select(artifact => artifact.Run!).ToArray();
    }

    private async Task<IReadOnlyList<RunArtifact>> ReadAllArtifactsAsync(CancellationToken cancellationToken)
    {
        var locations = EnumerateArtifactLocations();
        var artifacts = new List<RunArtifact>(locations.Count);
        var runIds = new HashSet<string>(StringComparer.Ordinal);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = await ReadArtifactAsync(location, cancellationToken);
            var runId = artifact.Run?.Id ?? artifact.Tombstone?.RunId ?? throw new FormatException($"Custom loop trace `{location.Path}` contains an unsupported artifact.");
            var admissionOperationId = artifact.Run?.AdmissionOperationId ?? artifact.Tombstone!.AdmissionOperationId;
            if (!runIds.Add(runId))
            {
                throw new FormatException($"Custom loop run id `{runId}` is duplicated. The persisted state requires review.");
            }

            if (!operationIds.Add(admissionOperationId))
            {
                throw new FormatException($"Admission operation id `{admissionOperationId}` is duplicated. The persisted state requires review.");
            }

            artifacts.Add(artifact);
        }

        if (artifacts.Count(artifact => artifact.Run is not null) > CustomLoopLimits.MaxRunTracesPerWorkspace)
        {
            throw new FormatException($"Custom loop run storage contains more than {CustomLoopLimits.MaxRunTracesPerWorkspace} live traces. No trace was pruned automatically.");
        }

        if (artifacts.Count(artifact => artifact.Tombstone is not null) > CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
        {
            throw new FormatException($"Custom loop run storage contains more than {CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace} terminal-trace tombstones.");
        }

        return artifacts;
    }

    private static CustomLoopTraceQuota CalculateQuota(IReadOnlyList<RunArtifact> artifacts)
    {
        long liveTraceBytes = 0;
        long tombstoneBytes = 0;
        long accountedBytes = 0;
        var activeReservations = 0;
        var liveTraceCount = 0;
        var tombstoneCount = 0;
        foreach (var artifact in artifacts)
        {
            if (artifact.Tombstone is not null)
            {
                tombstoneCount++;
                tombstoneBytes = checked(tombstoneBytes + artifact.PersistedUtf8Bytes);
                accountedBytes = checked(accountedBytes + artifact.PersistedUtf8Bytes);
                continue;
            }

            var run = artifact.Run ?? throw new FormatException($"Custom loop trace `{artifact.Location.Path}` contains an unsupported artifact.");
            liveTraceCount++;
            liveTraceBytes = checked(liveTraceBytes + artifact.PersistedUtf8Bytes);
            if (run.IsTerminal)
            {
                var warningReservation = HasTerminalIntegrityWarning(run) ? 0 : CustomLoopLimits.MaxTraceControlEventUtf8Bytes;
                accountedBytes = checked(accountedBytes + artifact.PersistedUtf8Bytes + warningReservation);
                if (warningReservation > 0)
                {
                    activeReservations++;
                }
            }
            else
            {
                activeReservations++;
                accountedBytes = checked(accountedBytes + CustomLoopLimits.MaxRunTraceUtf8Bytes);
            }
        }

        return new CustomLoopTraceQuota(
            liveTraceCount,
            liveTraceBytes,
            accountedBytes,
            activeReservations,
            CustomLoopLimits.MaxRunTracesPerWorkspace,
            CustomLoopLimits.MaxRunTraceWorkspaceUtf8Bytes,
            CustomLoopLimits.MaxRunTraceUtf8Bytes,
            tombstoneCount,
            tombstoneBytes,
            CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace);
    }

    private IReadOnlyList<RunArtifactLocation> EnumerateArtifactLocations()
    {
        if (!Directory.Exists(_runsRoot))
        {
            return [];
        }

        EnsureSafeDirectory(_runsRoot, create: false);
        var rootFiles = Directory.EnumerateFiles(_runsRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (rootFiles.Any(path => !string.Equals(Path.GetFileName(path), MutationLockFileName, StringComparison.Ordinal)))
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

    private async Task<RunArtifact> ReadArtifactAsync(RunArtifactLocation location, CancellationToken cancellationToken)
    {
        var utf8Json = await ReadBoundedArtifactAsync(location.Path, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = JsonOptions.MaxDepth });
            RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            var persistedHash = ComputeHash(utf8Json);
            if (document.RootElement.TryGetProperty("artifactKind", out var artifactKind))
            {
                if (artifactKind.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException($"Custom loop trace `{location.Path}` has an unsupported artifact kind.");
                }

                if (string.Equals(artifactKind.GetString(), CustomLoopTraceTombstone.CurrentArtifactKind, StringComparison.Ordinal))
                {
                    RequireCompleteContract(document.RootElement, typeof(CustomLoopTraceTombstone), "$");
                    var tombstone = JsonSerializer.Deserialize<CustomLoopTraceTombstone>(utf8Json, JsonOptions) ?? throw new FormatException($"Custom loop trace tombstone `{location.Path}` was empty.");
                    ValidateTombstone(tombstone);
                    if (!string.Equals(tombstone.RunId, location.RunId, StringComparison.Ordinal) || !string.Equals(tombstone.LoopId, location.LoopId, StringComparison.Ordinal))
                    {
                        throw new FormatException($"Custom loop trace tombstone `{location.Path}` identity does not match its containing directory and filename.");
                    }

                    if (utf8Json.LongLength > CustomLoopLimits.MaxRunTraceTombstoneUtf8Bytes)
                    {
                        throw new FormatException($"Custom loop trace tombstone `{location.Path}` exceeds {CustomLoopLimits.MaxRunTraceTombstoneUtf8Bytes} UTF-8 bytes.");
                    }

                    return new RunArtifact(location, null, tombstone, persistedHash, utf8Json.LongLength, utf8Json);
                }

                if (CustomLoopRunArtifactCodec.IsEnvelope(document.RootElement))
                {
                    var run = CustomLoopRunArtifactCodec.Decode(utf8Json);
                    ValidateCanonicalRun(run);
                    if (!string.Equals(run.Id, location.RunId, StringComparison.Ordinal) || !string.Equals(run.LoopId, location.LoopId, StringComparison.Ordinal))
                    {
                        throw new FormatException($"Custom loop run `{location.Path}` identity does not match its containing directory and filename.");
                    }

                    return new RunArtifact(location, run, null, persistedHash, utf8Json.LongLength, utf8Json);
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

    private async Task<byte[]> ReadBoundedArtifactAsync(string path, CancellationToken cancellationToken)
    {
        EnsureSafeArtifactPath(path, mustExist: true);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException($"Custom loop run `{path}` must contain between 1 and {CustomLoopLimits.MaxRunTraceUtf8Bytes} UTF-8 bytes.");
        }

        var content = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(content, cancellationToken);
        return content;
    }

    private static byte[] SerializeBounded(CustomLoopRunRecord run, byte[]? previousEnvelope = null)
    {
        var content = CustomLoopRunArtifactCodec.Encode(run, previousEnvelope);
        if (content.Length > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException($"Custom loop run `{run.Id}` exceeds the {CustomLoopLimits.MaxRunTraceUtf8Bytes}-byte trace limit.");
        }

        return content;
    }

    private static void ValidateReservedTraceCapacity(CustomLoopRunRecord current, CustomLoopRunRecord candidate, long currentUtf8Bytes, long candidateUtf8Bytes)
    {
        var appended = candidate.Events.Skip(current.Events.Length).ToArray();
        var delta = Math.Max(0, candidateUtf8Bytes - currentUtf8Bytes);
        var toolEvidenceBudget = appended.Where(item => item.ToolEvidence is not null).Sum(item => (long)GetToolEvidencePhaseUtf8Bytes(item.ToolEvidence!));
        var appendedAttemptStarts = appended.Where(item => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted).ToArray();
        var priorAttemptStarts = current.Events.Where(item => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted).ToArray();
        var closesAttempt = appended.Any(item => item.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.ExitDecisionCompleted or CustomLoopRunEventKind.NodeAttemptFailed);
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
            throw new FormatException($"A provider-attempt start exceeded its reserved maximum serialized footprint ({delta} > {attemptStartBudget}).");
        }

        if (closesAttempt && delta > CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes)
        {
            throw new FormatException("A provider-attempt outcome exceeded its reserved maximum serialized footprint.");
        }

        var controlEventCount = candidate.Events.Count(IsLifecycleControlEvent);
        if (controlEventCount > MaximumLifecycleControlEvents(candidate))
        {
            throw new FormatException("The run consumed lifecycle/control slots reserved for terminalization or its one optional post-terminal integrity warning.");
        }

        if (lifecycleEvents > 0 && delta > checked((long)lifecycleEvents * CustomLoopLimits.MaxTraceControlEventUtf8Bytes))
        {
            throw new FormatException("A lifecycle control event exceeded its permanent reserved serialized footprint.");
        }

        var committedAndReserved = CalculateRequiredTraceCapacity(candidate, candidateUtf8Bytes);
        if (committedAndReserved > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException("The run trace lacks atomically reserved capacity for all mandatory provider/tool evidence, remaining lifecycle/control events, and terminal/integrity evidence.");
        }
    }

    internal static long CalculateRequiredTraceCapacity(CustomLoopRunRecord run, long persistedUtf8Bytes)
    {
        if (persistedUtf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(persistedUtf8Bytes));
        }

        if (run.IsTerminal)
        {
            return checked(persistedUtf8Bytes + (HasTerminalIntegrityWarning(run) ? 0 : CustomLoopLimits.MaxTraceControlEventUtf8Bytes));
        }

        var maximumAttempts = CustomLoopLimits.GetMaximumModelAttempts(run.AdmittedDefinition.InferenceSteps.Length, run.AdmittedDefinition.ExitPolicy.MaxAdditionalIterations);
        var startedAttempts = run.Events.Count(item => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted);
        if (startedAttempts > maximumAttempts)
        {
            throw new FormatException("The run contains more provider-attempt starts than its admitted traversal can execute.");
        }

        var maximumRecordedToolRequests = run.AdmittedDefinition.ToolAssignments.Length == 0 ? 0 : CustomLoopLimits.MaxRecordedGovernedToolRequestsPerRun;
        var recordedToolRequests = run.Events.Count(item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.RequestReserved);
        if (recordedToolRequests > maximumRecordedToolRequests)
        {
            throw new FormatException("The run contains more governed tool-request reservations than its finite evidence bound permits.");
        }

        var controlEventCount = run.Events.Count(IsLifecycleControlEvent);
        if (controlEventCount > MaximumLifecycleControlEvents(run))
        {
            throw new FormatException("The run consumed lifecycle/control slots reserved for terminalization or its one optional post-terminal integrity warning.");
        }

        var outstanding = CalculateOutstandingReservation(run);
        var futureAttempts = maximumAttempts - startedAttempts;
        var materializedAttemptShapes = run.Events
            .Where(item => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted)
            .Select(AttemptStartIdentity)
            .ToHashSet();
        var remainingInferenceStarts = run.AdmittedDefinition.InferenceSteps.Count(step => !materializedAttemptShapes.Contains(new AttemptStartShape(IsExit: false, step.Id)));
        var remainingExitStarts = run.AdmittedDefinition.ExitPolicy.MaxAdditionalIterations > 0 && !materializedAttemptShapes.Contains(new AttemptStartShape(IsExit: true, "exit")) ? 1 : 0;
        var remainingDistinctStarts = remainingInferenceStarts + remainingExitStarts;
        var firstOverallStartPending = startedAttempts == 0 && remainingDistinctStarts > 0;
        var remainingLaterDistinctStarts = remainingDistinctStarts - (firstOverallStartPending ? 1 : 0);
        var futureToolRequests = maximumRecordedToolRequests - recordedToolRequests;
        var remainingControlReserve = CustomLoopLimits.MaxTraceControlReserveUtf8Bytes - checked(controlEventCount * CustomLoopLimits.MaxTraceControlEventUtf8Bytes);
        return checked(
            persistedUtf8Bytes
            + outstanding.Utf8Bytes
            + checked((long)futureAttempts * CustomLoopLimits.MaxFutureAttemptEvidenceReservationUtf8Bytes)
            + (firstOverallStartPending ? CustomLoopLimits.MaxFirstAttemptStartSurchargeUtf8Bytes : 0)
            + checked((long)remainingLaterDistinctStarts * CustomLoopLimits.MaxFirstDistinctNodeAttemptStartSurchargeUtf8Bytes)
            + checked((long)futureToolRequests * CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes)
            + remainingControlReserve
            + CustomLoopLimits.MaxPermanentTerminalIntegrityReserveUtf8Bytes);
    }

    private static int GetToolEvidencePhaseUtf8Bytes(CustomLoopToolTraceEvidence evidence)
    {
        return evidence.Phase switch
        {
            CustomLoopToolEvidencePhase.RequestReserved => CustomLoopLimits.MaxGovernedToolRequestEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.GovernanceDecided => CustomLoopLimits.MaxGovernedToolGovernanceEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.OutcomeObserved when !evidence.ReturnedToModel => CustomLoopLimits.MaxGovernedToolOutcomeEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.OutcomeObserved => CustomLoopLimits.MaxGovernedToolReturnEvidenceUtf8Bytes,
            CustomLoopToolEvidencePhase.IntegrityFailed => CustomLoopLimits.MaxGovernedToolReturnEvidenceUtf8Bytes,
            _ => 0
        };
    }

    private static bool IsLifecycleControlEvent(CustomLoopRunEvent item)
    {
        return item.Kind is CustomLoopRunEventKind.LifecycleChanged or CustomLoopRunEventKind.IntegrityWarning;
    }

    private static bool HasTerminalIntegrityWarning(CustomLoopRunRecord run)
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
            throw new FormatException("A custom-loop run cannot hold more than one provider-attempt trace reservation.");
        }

        foreach (var group in run.Events.Where(item => item.ToolEvidence is not null).GroupBy(item => item.ToolEvidence!.RequestCorrelationId, StringComparer.Ordinal))
        {
            var reservation = group.SingleOrDefault(item => item.ToolEvidence!.Phase == CustomLoopToolEvidencePhase.RequestReserved);
            if (reservation is null)
            {
                throw new FormatException("Governed tool evidence exists without one exact request reservation.");
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
        var content = JsonSerializer.SerializeToUtf8Bytes(tombstone, JsonOptions);
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
        var integrity = status == CustomLoopTraceDeletionStoreStatus.Deleted
            ? tombstone?.OutcomeIntegrity ?? CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit
            : CustomLoopTraceDeletionIntegrity.Complete;
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
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = JsonOptions.MaxDepth });
            RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            RequireCompleteContract(document.RootElement, typeof(CustomLoopTraceDeletionOperation), "$");
            var operation = JsonSerializer.Deserialize<CustomLoopTraceDeletionOperation>(utf8Json, JsonOptions) ?? throw new FormatException($"Custom loop trace-deletion operation `{path}` was empty.");
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

        var paths = Directory.EnumerateFiles(_traceDeletionOperationsRoot, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, PathComparer).ToArray();
        if (paths.Length > CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
        {
            throw new FormatException($"Custom loop trace-deletion operation storage exceeds its explicit {CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace}-artifact limit.");
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

    private async Task WriteTraceDeletionOperationAsync(CustomLoopTraceDeletionOperation operation, bool overwrite, CancellationToken cancellationToken)
    {
        ValidateDeletionOperation(operation);
        var content = JsonSerializer.SerializeToUtf8Bytes(operation, JsonOptions);
        if (content.Length + 1 > CustomLoopLimits.MaxRunTraceDeletionOperationUtf8Bytes)
        {
            throw new FormatException($"Custom loop trace-deletion operation `{operation.OperationId}` exceeds the {CustomLoopLimits.MaxRunTraceDeletionOperationUtf8Bytes}-byte limit.");
        }

        var terminated = new byte[content.Length + 1];
        content.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        var path = GetTraceDeletionOperationPath(operation.OperationId);
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
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw new FormatException($"{label} `{path}` must contain between 1 and {maximumBytes} UTF-8 bytes.");
        }

        var content = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(content, cancellationToken);
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
            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task WriteArtifactAsync(string path, byte[] content, bool overwrite, CancellationToken cancellationToken)
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
            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
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
                try
                {
                    if (File.Exists(_mutationLockPath))
                    {
                        RejectReparsePoint(_mutationLockPath);
                    }

                    var stream = new FileStream(_mutationLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                    RejectReparsePoint(_mutationLockPath);
                    return new MutationLease(stream, _processMutationGate);
                }
                catch (IOException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
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
                || operation.Tombstone.OutcomeIntegrity != operation.Integrity)
            {
                throw new FormatException("Trace-deletion operation outcome does not match its tombstone identity or integrity state.");
            }
        }
        else if (operation.Tombstone is not null && operation.Outcome != CustomLoopTraceDeletionStoreStatus.OperationConflict)
        {
            throw new FormatException("A rejected trace-deletion operation cannot retain an unrelated tombstone.");
        }

        if (operation.Outcome is not CustomLoopTraceDeletionStoreStatus.Deleted and not CustomLoopTraceDeletionStoreStatus.AlreadyDeleted
            && operation.Integrity != CustomLoopTraceDeletionIntegrity.Complete)
        {
            throw new FormatException("A non-mutating trace-deletion outcome must be durably complete.");
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

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new FormatException($"`{parameterName}` must use UTC offset zero.");
        }
    }

    private static bool IsActor(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CustomLoopLimits.MaxArtifactIdCharacters && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '@' or ':');

    private static bool IsSurface(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CustomLoopLimits.MaxArtifactIdCharacters && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string ComputeHash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static CustomLoopRunRecord? FindUniqueByRunId(IReadOnlyList<CustomLoopRunRecord> runs, string runId)
    {
        return runs.SingleOrDefault(run => string.Equals(run.Id, runId, StringComparison.Ordinal));
    }

    private static CustomLoopRunRecord? FindUniqueByOperation(IReadOnlyList<CustomLoopRunRecord> runs, string operationId)
    {
        return runs.SingleOrDefault(run => string.Equals(run.AdmissionOperationId, operationId, StringComparison.Ordinal));
    }

    private static bool SameAdmissionRequest(CustomLoopRunRecord existing, CustomLoopRunRecord candidate)
    {
        return string.Equals(existing.AdmissionOperationId, candidate.AdmissionOperationId, StringComparison.Ordinal)
            && string.Equals(existing.AdmissionRequestHash, candidate.AdmissionRequestHash, StringComparison.Ordinal);
    }

    private static CustomLoopRunSummary ToSummary(CustomLoopRunRecord run)
    {
        return new CustomLoopRunSummary(run.Id, run.LoopId, run.AdmissionOperationId, run.AdmittedDefinition.DefinitionVersion, run.Status, run.CreatedAtUtc, run.UpdatedAtUtc, run.CompletedAtUtc, run.Checkpoint.Iteration, run.Checkpoint.NextStepIndex, run.FailureCode, IsDeleted: false);
    }

    private static CustomLoopRunSummary ToSummary(CustomLoopTraceTombstone tombstone)
    {
        return new CustomLoopRunSummary(tombstone.RunId, tombstone.LoopId, tombstone.AdmissionOperationId, tombstone.DefinitionVersion, tombstone.TerminalStatus, tombstone.CreatedAtUtc, tombstone.DeletedAtUtc, tombstone.CompletedAtUtc, 0, 0, null, IsDeleted: true);
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
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonOptions.PropertyNamingPolicy!.ConvertName(property.Name);
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

    private sealed record RunArtifactLocation(string Path, string LoopId, string RunId);

    private sealed record RunArtifact(RunArtifactLocation Location, CustomLoopRunRecord? Run, CustomLoopTraceTombstone? Tombstone, string PersistedHash, long PersistedUtf8Bytes, byte[] PersistedBytes);

    private sealed record TraceReservation(long Utf8Bytes, long? EarliestSequence);

    private readonly record struct AttemptStartShape(bool IsExit, string StepId);

    private sealed class MutationLease : IAsyncDisposable
    {
        private readonly FileStream _stream;
        private readonly SemaphoreSlim _processGate;
        public MutationLease(FileStream stream, SemaphoreSlim processGate)
        {
            _stream = stream;
            _processGate = processGate;
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _processGate.Release();
        }
    }
}
