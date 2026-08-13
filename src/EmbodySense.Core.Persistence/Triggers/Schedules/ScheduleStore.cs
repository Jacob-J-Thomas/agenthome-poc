using System.ComponentModel;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Triggers.Models;
using EmbodySense.Core.Persistence.Triggers.Schedules.Models;

namespace EmbodySense.Core.Persistence.Triggers.Schedules;

/// <summary>Persists one bounded workspace schedule catalog through immutable crash-safe generations.</summary>
/// <remarks>
/// Mutations are serialized across processes. Caller cancellation is honored through the last check before staging; once
/// staging starts, the immutable-generation protocol reaches a durable decision without observing caller cancellation.
/// Exact retries resolve publication-boundary ambiguity through the canonical definition and state hashes.
/// </remarks>
public sealed class ScheduleStore : IScheduleStorePort, IScheduleDeliveryProvenancePort
{
    private const int SchemaVersion = 1;
    private const int MaximumConfiguredSchedules = 4_096;
    private const int MaximumConfiguredCatalogBytes = 64 * 1024 * 1024;
    private const int MaximumConfiguredDurabilityArtifacts = 64;
    private readonly TriggerQueueArtifactGuard _guard;
    private readonly int _maximumCatalogBytes;
    private readonly int _maximumSchedules;
    private readonly ITriggerQueueDurabilityObserver _observer;

    /// <summary>Initializes a bounded workspace-scoped schedule store.</summary>
    public ScheduleStore(WorkspacePaths paths, ScheduleStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        options ??= new ScheduleStoreOptions();
        ValidateOptions(options);
        _maximumSchedules = options.MaxSchedules;
        _maximumCatalogBytes = options.MaxCatalogUtf8Bytes;
        _observer = new ScheduleStoreDurabilityObserver(options.DurableBoundaryObserver);
        var scheduleRoot = paths.AgentFile(Path.Combine("triggers", "schedules"));
        _guard = new TriggerQueueArtifactGuard(paths.RootPath, scheduleRoot, options.MaxDurabilityArtifacts);
    }

    /// <inheritdoc />
    public async Task<ScheduleStoreReadResult> ReadAsync(ScheduleId scheduleId, CancellationToken cancellationToken = default)
    {
        if (!IsValidScheduleId(scheduleId))
        {
            return ReadResult(ScheduleStoreReadStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, _) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var entry = catalog.Entries.SingleOrDefault(candidate => candidate.Definition.ScheduleId.Equals(scheduleId));
            return entry is null
                ? ReadResult(ScheduleStoreReadStatus.NotFound)
                : ReadResult(ScheduleStoreReadStatus.Found, entry.Definition, entry.State);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsBackpressure(exception))
        {
            return ReadResult(ScheduleStoreReadStatus.Backpressured);
        }
        catch (Exception exception) when (IsCorruption(exception))
        {
            return ReadResult(ScheduleStoreReadStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return ReadResult(ScheduleStoreReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<ScheduleDeliveryProvenanceResult> ResolveAsync(
        TriggerDeliveryEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (envelope is null
            || !TriggerDeliveryValidator.Validate(envelope).IsValid
            || !TriggerDeliveryHash.TryCompute(envelope, out var envelopeHash, out _))
        {
            return ProvenanceResult(ScheduleDeliveryProvenanceStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, _) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var exact = new List<ScheduleDeliveryProvenanceEvidence>();
            var exactPendingFinalization = 0;
            var identityConflict = false;
            foreach (var entry in catalog.Entries)
            {
                foreach (var candidate in Candidates(entry.Definition, entry.DefinitionHash, entry.State))
                {
                    var deliveryMatch = Equals(candidate.Identity.DeliveryId, envelope!.DeliveryId);
                    var deduplicationMatch = Equals(candidate.Identity.DeduplicationId, envelope.DeduplicationId);
                    if (!deliveryMatch && !deduplicationMatch)
                    {
                        continue;
                    }

                    if (deliveryMatch
                        && deduplicationMatch
                        && string.Equals(candidate.Result.CanonicalEnvelopeHash, envelopeHash, StringComparison.Ordinal)
                        && ScheduleDeliveryProvenanceValidator.Matches(candidate, envelope))
                    {
                        exact.Add(candidate);
                    }
                    else
                    {
                        identityConflict = true;
                    }
                }

                var pending = entry.State.PendingDelivery;
                if (pending is null)
                {
                    continue;
                }

                var pendingDeliveryMatch = Equals(pending.Identity.DeliveryId, envelope!.DeliveryId);
                var pendingDeduplicationMatch = Equals(pending.Identity.DeduplicationId, envelope.DeduplicationId);
                if (!pendingDeliveryMatch && !pendingDeduplicationMatch)
                {
                    continue;
                }

                if (pendingDeliveryMatch
                    && pendingDeduplicationMatch
                    && ScheduleDeliveryProvenanceValidator.MatchesPendingFinalization(
                        entry.Definition,
                        entry.DefinitionHash,
                        pending,
                        envelope))
                {
                    exactPendingFinalization++;
                }
                else
                {
                    identityConflict = true;
                }
            }

            if (exact.Count > 1
                || exactPendingFinalization > 1
                || exact.Count > 0 && exactPendingFinalization > 0)
            {
                return ProvenanceResult(ScheduleDeliveryProvenanceStatus.Ambiguous);
            }

            if (identityConflict)
            {
                return ProvenanceResult(ScheduleDeliveryProvenanceStatus.Conflict);
            }

            if (exact.Count == 1)
            {
                return ProvenanceResult(ScheduleDeliveryProvenanceStatus.Found, exact[0]);
            }

            return exactPendingFinalization == 1
                ? ProvenanceResult(ScheduleDeliveryProvenanceStatus.PendingFinalization)
                : ProvenanceResult(ScheduleDeliveryProvenanceStatus.NotFound);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsBackpressure(exception))
        {
            return ProvenanceResult(ScheduleDeliveryProvenanceStatus.Backpressured);
        }
        catch (Exception exception) when (IsCorruption(exception))
        {
            return ProvenanceResult(ScheduleDeliveryProvenanceStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return ProvenanceResult(ScheduleDeliveryProvenanceStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<ScheduleStoreMutationResult> CreateAsync(ScheduleStoreCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryCaptureCreate(request, out var definition, out var state, out var definitionHash, out var stateHash))
        {
            return MutationResult(ScheduleStoreMutationStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var existing = catalog.Entries.SingleOrDefault(entry => entry.Definition.ScheduleId.Equals(definition!.ScheduleId));
            if (existing is not null)
            {
                var exactReplay = string.Equals(existing.DefinitionHash, definitionHash, StringComparison.Ordinal)
                    && string.Equals(existing.StateHash, stateHash, StringComparison.Ordinal);
                return MutationResult(
                    exactReplay ? ScheduleStoreMutationStatus.AlreadyExists : ScheduleStoreMutationStatus.Conflict,
                    existing.State);
            }

            if (catalog.Entries.Count >= _maximumSchedules)
            {
                return MutationResult(ScheduleStoreMutationStatus.Backpressured);
            }

            var candidate = new ScheduleStoreCatalog(
                SchemaVersion,
                checked(catalog.Generation + 1),
                catalog.Entries.Append(new ScheduleStoreEntry(definition!, definitionHash!, state!, stateHash!)));
            var content = ScheduleStoreCodec.Serialize(candidate, _maximumCatalogBytes);
            cancellationToken.ThrowIfCancellationRequested();
            await _guard.WriteAsync(
                content,
                identity.Artifacts,
                identity.TombstoneCount,
                identity.Precursors,
                candidate.Generation,
                _observer,
                mutationLock).ConfigureAwait(false);
            return MutationResult(ScheduleStoreMutationStatus.Applied, state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsBackpressure(exception))
        {
            return MutationResult(ScheduleStoreMutationStatus.Backpressured);
        }
        catch (Exception exception) when (IsCorruption(exception))
        {
            return MutationResult(ScheduleStoreMutationStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return MutationResult(ScheduleStoreMutationStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<ScheduleStoreMutationResult> CompareExchangeAsync(ScheduleStateCompareExchange request, CancellationToken cancellationToken = default)
    {
        if (!TryCaptureExchange(request, out var expected, out var replacement, out var expectedHash, out var replacementHash))
        {
            return MutationResult(ScheduleStoreMutationStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var existing = catalog.Entries.SingleOrDefault(entry => entry.Definition.ScheduleId.Equals(expected!.ScheduleId));
            if (existing is null)
            {
                return MutationResult(ScheduleStoreMutationStatus.Conflict);
            }

            if (!ScheduleContractValidator.ValidateDefinitionStateComposition(existing.Definition, expected).IsValid
                || !ScheduleContractValidator.ValidateDefinitionStateComposition(existing.Definition, replacement).IsValid
                || !ScheduleStateTransitionValidator.Validate(existing.Definition, expected, replacement).IsValid)
            {
                return MutationResult(ScheduleStoreMutationStatus.Corrupt, existing.State);
            }

            if (string.Equals(existing.StateHash, replacementHash, StringComparison.Ordinal))
            {
                return MutationResult(ScheduleStoreMutationStatus.Applied, existing.State);
            }

            if (!string.Equals(existing.StateHash, expectedHash, StringComparison.Ordinal))
            {
                return MutationResult(ScheduleStoreMutationStatus.Conflict, existing.State);
            }

            var replacementEntry = existing with { State = replacement!, StateHash = replacementHash! };
            var candidate = new ScheduleStoreCatalog(
                SchemaVersion,
                checked(catalog.Generation + 1),
                catalog.Entries.Select(entry => ReferenceEquals(entry, existing) ? replacementEntry : entry));
            var content = ScheduleStoreCodec.Serialize(candidate, _maximumCatalogBytes);
            cancellationToken.ThrowIfCancellationRequested();
            await _guard.WriteAsync(
                content,
                identity.Artifacts,
                identity.TombstoneCount,
                identity.Precursors,
                candidate.Generation,
                _observer,
                mutationLock).ConfigureAwait(false);
            return MutationResult(ScheduleStoreMutationStatus.Applied, replacement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsBackpressure(exception))
        {
            return MutationResult(ScheduleStoreMutationStatus.Backpressured);
        }
        catch (Exception exception) when (IsCorruption(exception))
        {
            return MutationResult(ScheduleStoreMutationStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return MutationResult(ScheduleStoreMutationStatus.Unavailable);
        }
    }

    private async Task<(ScheduleStoreCatalog Catalog, TriggerQueueReadResult Identity)> LoadAsync(
        TriggerQueueMutationLease mutationLock,
        CancellationToken cancellationToken)
    {
        var identity = await _guard.ReadLatestAsync(
            _maximumCatalogBytes,
            _observer,
            mutationLock,
            cancellationToken).ConfigureAwait(false);
        if (identity.LatestContent is null)
        {
            return (new ScheduleStoreCatalog(SchemaVersion, 0, []), identity);
        }

        var catalog = ScheduleStoreCodec.Deserialize(identity.LatestContent, _maximumSchedules, _maximumCatalogBytes);
        if (identity.Artifacts.Count == 0 || catalog.Generation != identity.Artifacts[^1].Generation)
        {
            throw new FormatException("The schedule catalog generation does not match its immutable artifact name.");
        }

        return (catalog, identity);
    }

    private static bool TryCaptureCreate(
        ScheduleStoreCreateRequest? request,
        out ScheduleDefinition? definition,
        out ScheduleState? state,
        out string? definitionHash,
        out string? stateHash)
    {
        definition = null;
        state = null;
        definitionHash = null;
        stateHash = null;
        try
        {
            definition = ScheduleContractCopy.Copy(request?.Definition);
            state = ScheduleContractCopy.Copy(request?.InitialState);
            return request is not null
                && ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state).IsValid
                && state!.StateRevision == 1
                && ScheduleContractHash.TryComputeDefinition(definition!, out definitionHash, out _)
                && string.Equals(definitionHash, request.CanonicalDefinitionHash, StringComparison.Ordinal)
                && ScheduleContractHash.TryComputeState(state!, out stateHash, out _);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool TryCaptureExchange(
        ScheduleStateCompareExchange? request,
        out ScheduleState? expected,
        out ScheduleState? replacement,
        out string? expectedHash,
        out string? replacementHash)
    {
        expected = null;
        replacement = null;
        expectedHash = null;
        replacementHash = null;
        try
        {
            expected = ScheduleContractCopy.Copy(request?.Expected);
            replacement = ScheduleContractCopy.Copy(request?.Replacement);
            return request is not null
                && ScheduleContractValidator.ValidateState(expected).IsValid
                && ScheduleContractValidator.ValidateState(replacement).IsValid
                && Equals(expected!.ScheduleId, replacement!.ScheduleId)
                && expected.DefinitionRevision == replacement.DefinitionRevision
                && string.Equals(expected.DefinitionHash, replacement.DefinitionHash, StringComparison.Ordinal)
                && expected.StateRevision < ScheduleContractLimits.MaxRevision
                && replacement.StateRevision == expected.StateRevision + 1
                && ScheduleContractHash.TryComputeState(expected, out expectedHash, out _)
                && ScheduleContractHash.TryComputeState(replacement, out replacementHash, out _);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    private static ScheduleStoreReadResult ReadResult(
        ScheduleStoreReadStatus status,
        ScheduleDefinition? definition = null,
        ScheduleState? state = null)
        => new(status, ScheduleContractCopy.Copy(definition), ScheduleContractCopy.Copy(state));

    private static ScheduleStoreMutationResult MutationResult(
        ScheduleStoreMutationStatus status,
        ScheduleState? state = null)
        => new(status, ScheduleContractCopy.Copy(state));

    private static ScheduleDeliveryProvenanceResult ProvenanceResult(
        ScheduleDeliveryProvenanceStatus status,
        ScheduleDeliveryProvenanceEvidence? evidence = null)
        => new(status, ScheduleContractCopy.Copy(evidence));

    private static IEnumerable<ScheduleDeliveryProvenanceEvidence> Candidates(
        ScheduleDefinition definition,
        string definitionHash,
        ScheduleState state)
    {
        foreach (var terminal in state.TerminalDeliveryEvidence.Where(item =>
                     item.Result.Kind is ScheduleDeliveryResultKind.Queued or ScheduleDeliveryResultKind.Replayed))
        {
            yield return Evidence(definition, definitionHash, terminal.Occurrence, terminal.Identity, terminal.Result);
        }
    }

    private static ScheduleDeliveryProvenanceEvidence Evidence(
        ScheduleDefinition definition,
        string definitionHash,
        ScheduleOccurrence occurrence,
        ScheduleOccurrenceIdentity identity,
        ScheduleDeliveryResultEvidence result)
        => new(
            ScheduleDeliveryProvenanceEvidence.CurrentSchemaVersion,
            definition,
            definitionHash,
            occurrence,
            identity,
            result);

    private static bool IsValidScheduleId(ScheduleId? scheduleId)
        => scheduleId is not null && ScheduleId.TryParse(scheduleId.Value, out _);

    private static bool IsBackpressure(Exception exception)
        => exception is ScheduleStoreCodecLimitException or TriggerQueuePersistenceBackpressureException
            || exception is InvalidOperationException invalidOperation
                && (invalidOperation.Message.Contains("configured byte bound", StringComparison.Ordinal)
                    || invalidOperation.Message.Contains("bounded aggregate byte limit", StringComparison.Ordinal)
                    || invalidOperation.Message.Contains("authenticated tombstones", StringComparison.Ordinal));

    private static bool IsCorruption(Exception exception)
        => exception is FormatException
            or InvalidOperationException
            or OverflowException
            or ArgumentException
            or Win32Exception;

    private static bool IsUnavailable(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or PlatformNotSupportedException
            or NotSupportedException
            or ScheduleStoreBoundaryObserverException;

    private static void ValidateOptions(ScheduleStoreOptions options)
    {
        if (options.MaxSchedules is < 1 or > MaximumConfiguredSchedules)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The schedule count bound is outside the supported range.");
        }

        if (options.MaxCatalogUtf8Bytes is < 1 or > MaximumConfiguredCatalogBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The schedule byte bound is outside the supported range.");
        }

        if (options.MaxDurabilityArtifacts is < 1 or > MaximumConfiguredDurabilityArtifacts)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The schedule durability-artifact bound is outside the supported range.");
        }
    }
}
