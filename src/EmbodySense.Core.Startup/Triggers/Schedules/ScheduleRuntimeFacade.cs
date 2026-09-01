using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Triggers.Schedules;

/// <summary>Exposes explicit one-shot schedule storage, control, and evaluation without a background host.</summary>
public sealed class ScheduleRuntimeFacade : IDisposable
{
    private const int MaximumInitialRecurrenceProbes = ScheduleContractLimits.MaxFinalizationEvidenceItems + 1;
    private readonly IScheduleStorePort _store;
    private readonly ScheduleDueOccurrenceEvaluator _evaluator;
    private readonly IScheduleTimeZonePort _timeZone;
    private readonly TimeProvider _timeProvider;
    private readonly IDisposable? _ownedResource;
    private int _disposed;

    internal ScheduleRuntimeFacade(
        IScheduleStorePort store,
        IScheduleCurrentEvidencePort currentEvidence,
        IScheduleOverlapPort overlap,
        IScheduleTimeZonePort timeZone,
        ITriggerQueueAdmissionPort queue,
        ITriggerDeliveryAdmissionHistoryPort queueHistory,
        TimeProvider timeProvider,
        IDisposable? ownedResource = null)
    {
        var boundaryStore = new ScheduleRuntimeStoreBoundary(store);
        _store = boundaryStore;
        _evaluator = new ScheduleDueOccurrenceEvaluator(
            boundaryStore,
            currentEvidence,
            overlap,
            timeZone,
            queue,
            queueHistory,
            timeProvider);
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownedResource = ownedResource;
    }

    /// <summary>Reads one exact immutable definition and optimistic state snapshot.</summary>
    public Task<ScheduleStoreReadResult> ReadAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scheduleId);
        cancellationToken.ThrowIfCancellationRequested();
        return _store.ReadAsync(scheduleId, cancellationToken);
    }

    /// <summary>Resolves trusted time-zone evidence and atomically creates one immutable definition and initial state.</summary>
    /// <remarks>
    /// Callers cannot supply an initial state, UTC mapping, rules fingerprint, or optimistic revision. The facade constructs
    /// revision 1 only from the validated definition and its retained composition-owned time-zone source.
    /// </remarks>
    public Task<ScheduleRuntimeCreateResult> CreateAsync(
        ScheduleDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return definition is null
            ? Task.FromResult(Creation(ScheduleRuntimeCreateStatus.Corrupt))
            : CreateAsync(definition, definition.Enabled, cancellationToken);
    }

    /// <summary>Creates one immutable definition with a separately validated revision-1 enablement state.</summary>
    /// <remarks>
    /// The immutable definition remains the durable policy for future state validation. A caller may stage a valid
    /// immutable definition disabled, but may never stage an initially enabled state for a permanently disabled definition.
    /// Existing callers should use the overload without <paramref name="initialEnabled"/> to retain definition enablement.
    /// </remarks>
    /// <param name="definition">The immutable, validated schedule definition.</param>
    /// <param name="initialEnabled">Whether the revision-1 state permits due-occurrence claims.</param>
    /// <param name="cancellationToken">Cancels before a durable create boundary.</param>
    /// <returns>The closed creation outcome and authoritative state when available.</returns>
    public async Task<ScheduleRuntimeCreateResult> CreateAsync(
        ScheduleDefinition definition,
        bool initialEnabled,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _))
        {
            return Creation(ScheduleRuntimeCreateStatus.Corrupt);
        }

        if (initialEnabled && !definition.Enabled)
        {
            return Creation(ScheduleRuntimeCreateStatus.Corrupt);
        }

        var existing = await ReadForCreateAsync(definition.ScheduleId, cancellationToken).ConfigureAwait(false);
        if (existing.Status != ScheduleStoreReadStatus.NotFound)
        {
            return ClassifyExisting(existing, definitionHash!, initialEnabled);
        }

        var initial = await BuildInitialStateAsync(definition, definitionHash!, initialEnabled, cancellationToken).ConfigureAwait(false);
        if (initial.Status != ScheduleRuntimeCreateStatus.Created || initial.CurrentState is null)
        {
            return initial;
        }

        var created = await _store.CreateAsync(
            new ScheduleStoreCreateRequest(definition, initial.CurrentState, definitionHash!),
            cancellationToken).ConfigureAwait(false);

        if (created.Status is not (ScheduleStoreMutationStatus.Conflict or ScheduleStoreMutationStatus.AlreadyExists))
        {
            return FromStore(created);
        }

        var raced = await ReadForCreateAsync(definition.ScheduleId, cancellationToken).ConfigureAwait(false);
        return raced.Status == ScheduleStoreReadStatus.NotFound
            ? Creation(ScheduleRuntimeCreateStatus.Corrupt)
            : ClassifyExisting(raced, definitionHash!, initialEnabled);
    }

    /// <summary>Evaluates, admits, and durably finalizes at most one due occurrence.</summary>
    public Task<ScheduleEvaluationResult> EvaluateOnceAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scheduleId);
        return _evaluator.EvaluateAsync(scheduleId, cancellationToken);
    }

    /// <summary>Optimistically enables or disables the exact caller-observed state snapshot.</summary>
    /// <remarks>
    /// Control never changes the immutable definition or erases pending recovery work. A racing evaluator or controller
    /// returns <see cref="ScheduleStoreMutationStatus.Conflict"/> through the canonical store compare-exchange contract.
    /// </remarks>
    public async Task<ScheduleStoreMutationResult> SetEnabledAsync(
        ScheduleState expectedState,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ScheduleContractValidator.ValidateState(expectedState).IsValid)
        {
            return Mutation(ScheduleStoreMutationStatus.Corrupt);
        }

        var expected = ScheduleContractCopy.Copy(expectedState)!;
        if (expected.Enabled == enabled)
        {
            var current = await ReadForCreateAsync(expected.ScheduleId, cancellationToken).ConfigureAwait(false);
            if (current.Status != ScheduleStoreReadStatus.Found)
            {
                return Mutation(
                    current.Status switch
                    {
                        ScheduleStoreReadStatus.NotFound => ScheduleStoreMutationStatus.Conflict,
                        ScheduleStoreReadStatus.Unavailable => ScheduleStoreMutationStatus.Unavailable,
                        ScheduleStoreReadStatus.Backpressured => ScheduleStoreMutationStatus.Backpressured,
                        _ => ScheduleStoreMutationStatus.Corrupt,
                    },
                    current.State);
            }

            var validCurrent = current.State is not null
                && ScheduleContractValidator.ValidateDefinitionStateComposition(current.Definition, current.State).IsValid;
            if (!validCurrent || !SameState(expected, current.State!))
            {
                return Mutation(
                    !validCurrent
                        ? ScheduleStoreMutationStatus.Corrupt
                        : ScheduleStoreMutationStatus.Conflict,
                    current.State);
            }

            return Mutation(ScheduleStoreMutationStatus.AlreadyExists, current.State);
        }

        if (expected.StateRevision >= ScheduleContractLimits.MaxRevision)
        {
            return Mutation(ScheduleStoreMutationStatus.Corrupt);
        }

        var replacement = expected with
        {
            StateRevision = checked(expected.StateRevision + 1),
            Enabled = enabled,
        };
        return await _store.CompareExchangeAsync(
            new ScheduleStateCompareExchange(expected, replacement),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases production-owned retained workspace handles without starting or stopping background work.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ownedResource?.Dispose();
        }
    }

    private async Task<ScheduleRuntimeCreateResult> BuildInitialStateAsync(
        ScheduleDefinition definition,
        string definitionHash,
        bool initialEnabled,
        CancellationToken cancellationToken)
    {
        DateTimeOffset recordedAtUtc;
        try
        {
            recordedAtUtc = _timeProvider.GetUtcNow();
        }
        catch
        {
            return Creation(ScheduleRuntimeCreateStatus.Unavailable);
        }

        if (!IsUtc(recordedAtUtc))
        {
            return Creation(ScheduleRuntimeCreateStatus.Corrupt);
        }

        var skipped = new List<ScheduleOccurrenceDispositionEvidence>();
        var local = definition.Recurrence.FirstLocalOccurrence;
        for (var probe = 0; probe < MaximumInitialRecurrenceProbes; probe++)
        {
            var ordinal = probe + 1L;
            ScheduleTimeZoneResolution resolution;
            try
            {
                resolution = await _timeZone.ResolveLocalAsync(
                    definition.TimeZone,
                    local,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Creation(ScheduleRuntimeCreateStatus.Unavailable);
            }

            var resolutionFailure = ValidateResolution(definition, local, resolution);
            if (resolutionFailure is not null)
            {
                return Creation(resolutionFailure.Value);
            }

            DateTimeOffset? selectedUtc = resolution.Status switch
            {
                ScheduleTimeZoneResolutionStatus.Unique => resolution.EarlierUtc,
                ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime
                    => definition.DaylightSaving.AmbiguousLocalTime == ScheduleAmbiguousLocalTimePolicy.EarlierUtc
                        ? resolution.EarlierUtc
                        : resolution.LaterUtc,
                ScheduleTimeZoneResolutionStatus.InvalidLocalTime
                    when definition.DaylightSaving.InvalidLocalTime == ScheduleInvalidLocalTimePolicy.ShiftForward
                    => resolution.EarlierUtc,
                _ => null,
            };

            if (selectedUtc is not null)
            {
                var occurrence = new ScheduleOccurrence(
                    ScheduleOccurrence.CurrentSchemaVersion,
                    ordinal,
                    local,
                    selectedUtc.Value,
                    definition.TimeZone);
                return CreateInitialState(definition, definitionHash, initialEnabled, occurrence, recordedAtUtc, skipped);
            }

            if (skipped.Count == ScheduleContractLimits.MaxDispositionEvidenceItems)
            {
                return Creation(ScheduleRuntimeCreateStatus.BoundExceeded);
            }

            skipped.Add(new ScheduleOccurrenceDispositionEvidence(
                ScheduleOccurrenceDispositionEvidence.CurrentSchemaVersion,
                ordinal,
                ordinal,
                1,
                local,
                local,
                null,
                null,
                definition.TimeZone,
                ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped,
                null,
                "invalid-local-time-skipped",
                recordedAtUtc));

            if (definition.Recurrence.Kind == ScheduleRecurrenceKind.Once)
            {
                return CreateInitialState(definition, definitionHash, initialEnabled, null, recordedAtUtc, skipped);
            }

            if (definition.Recurrence.Kind == ScheduleRecurrenceKind.FixedInterval)
            {
                return await ResolveFirstFixedIntervalAfterSkipAsync(
                    definition,
                    definitionHash,
                    initialEnabled,
                    resolution.EarlierUtc!.Value,
                    recordedAtUtc,
                    skipped,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!TryNextNominal(definition.Recurrence, ordinal + 1, out local))
            {
                return CreateInitialState(definition, definitionHash, initialEnabled, null, recordedAtUtc, skipped);
            }
        }

        return Creation(ScheduleRuntimeCreateStatus.BoundExceeded);
    }

    private async Task<ScheduleRuntimeCreateResult> ResolveFirstFixedIntervalAfterSkipAsync(
        ScheduleDefinition definition,
        string definitionHash,
        bool initialEnabled,
        DateTimeOffset firstValidBoundaryUtc,
        DateTimeOffset recordedAtUtc,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence> skipped,
        CancellationToken cancellationToken)
    {
        var ticks = (decimal)firstValidBoundaryUtc.UtcDateTime.Ticks
            + definition.Recurrence.FixedIntervalSeconds!.Value * TimeSpan.TicksPerSecond;
        if (ticks > MaximumSupportedTicks())
        {
            return CreateInitialState(definition, definitionHash, initialEnabled, null, recordedAtUtc, skipped);
        }

        var nextUtc = new DateTimeOffset((long)ticks, TimeSpan.Zero);
        ScheduleInstantResolution resolution;
        try
        {
            resolution = await _timeZone.ResolveInstantAsync(
                definition.TimeZone,
                nextUtc,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Creation(ScheduleRuntimeCreateStatus.Unavailable);
        }

        var resolutionFailure = ValidateInstantResolution(definition, resolution);
        if (resolutionFailure is not null)
        {
            return Creation(resolutionFailure.Value);
        }

        var occurrence = new ScheduleOccurrence(
            ScheduleOccurrence.CurrentSchemaVersion,
            2,
            resolution.ScheduledLocal,
            nextUtc,
            definition.TimeZone);
        return CreateInitialState(definition, definitionHash, initialEnabled, occurrence, recordedAtUtc, skipped);
    }

    private static ScheduleRuntimeCreateResult CreateInitialState(
        ScheduleDefinition definition,
        string definitionHash,
        bool initialEnabled,
        ScheduleOccurrence? occurrence,
        DateTimeOffset recordedAtUtc,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence> skipped)
    {
        var state = new ScheduleState(
            ScheduleState.CurrentSchemaVersion,
            definition.ScheduleId,
            definition.Revision,
            definitionHash,
            1,
            initialEnabled,
            occurrence,
            null,
            null,
            recordedAtUtc,
            null,
            skipped,
            []);
        return ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state).IsValid
            ? Creation(ScheduleRuntimeCreateStatus.Created, state)
            : Creation(ScheduleRuntimeCreateStatus.Corrupt);
    }

    private static ScheduleRuntimeCreateStatus? ValidateResolution(
        ScheduleDefinition definition,
        DateTime local,
        ScheduleTimeZoneResolution? resolution)
    {
        if (resolution is null
            || !Enum.IsDefined(resolution.Status)
            || resolution.Status is ScheduleTimeZoneResolutionStatus.Unknown or ScheduleTimeZoneResolutionStatus.Corrupt)
        {
            return ScheduleRuntimeCreateStatus.Corrupt;
        }

        if (resolution.Status == ScheduleTimeZoneResolutionStatus.Unavailable)
        {
            return ScheduleRuntimeCreateStatus.Unavailable;
        }

        if (resolution.Status == ScheduleTimeZoneResolutionStatus.Backpressured)
        {
            return ScheduleRuntimeCreateStatus.Backpressured;
        }

        if (!string.Equals(resolution.RulesFingerprint, definition.TimeZone.RulesFingerprint, StringComparison.Ordinal))
        {
            return ScheduleRuntimeCreateStatus.Corrupt;
        }

        var valid = resolution.Status switch
        {
            ScheduleTimeZoneResolutionStatus.Unique
                => resolution.ResolvedLocal == local && IsUtc(resolution.EarlierUtc) && resolution.LaterUtc is null,
            ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime
                => resolution.ResolvedLocal == local
                    && IsUtc(resolution.EarlierUtc)
                    && IsUtc(resolution.LaterUtc)
                    && resolution.EarlierUtc < resolution.LaterUtc,
            ScheduleTimeZoneResolutionStatus.InvalidLocalTime
                => resolution.ResolvedLocal.Kind == DateTimeKind.Unspecified
                    && resolution.ResolvedLocal > local
                    && IsUtc(resolution.EarlierUtc)
                    && resolution.LaterUtc is null,
            _ => false,
        };
        return valid ? null : ScheduleRuntimeCreateStatus.Corrupt;
    }

    private static ScheduleRuntimeCreateStatus? ValidateInstantResolution(
        ScheduleDefinition definition,
        ScheduleInstantResolution? resolution)
    {
        if (resolution is null
            || !Enum.IsDefined(resolution.Status)
            || resolution.Status is ScheduleInstantResolutionStatus.Unknown or ScheduleInstantResolutionStatus.Corrupt)
        {
            return ScheduleRuntimeCreateStatus.Corrupt;
        }

        if (resolution.Status != ScheduleInstantResolutionStatus.Resolved)
        {
            return resolution.Status == ScheduleInstantResolutionStatus.Unavailable
                ? ScheduleRuntimeCreateStatus.Unavailable
                : ScheduleRuntimeCreateStatus.Backpressured;
        }

        return string.Equals(resolution.RulesFingerprint, definition.TimeZone.RulesFingerprint, StringComparison.Ordinal)
            && resolution.ScheduledLocal.Kind == DateTimeKind.Unspecified
            && resolution.ScheduledLocal.Year is >= ScheduleContractLimits.MinimumSupportedYear
                and <= ScheduleContractLimits.MaximumSupportedYear
            ? null
            : ScheduleRuntimeCreateStatus.Corrupt;
    }

    private async Task<ScheduleStoreReadResult> ReadForCreateAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken)
        => await _store.ReadAsync(scheduleId, cancellationToken).ConfigureAwait(false);

    private static ScheduleRuntimeCreateResult ClassifyExisting(
        ScheduleStoreReadResult read,
        string expectedHash,
        bool expectedInitialEnabled)
    {
        if (read.Status != ScheduleStoreReadStatus.Found)
        {
            return Creation(MapRead(read.Status), read.State);
        }

        if (!ScheduleContractHash.TryComputeDefinition(read.Definition, out var currentHash, out _)
            || read.State is null
            || !ScheduleContractValidator.ValidateDefinitionStateComposition(read.Definition, read.State).IsValid)
        {
            return Creation(ScheduleRuntimeCreateStatus.Corrupt, read.State);
        }

        return string.Equals(currentHash, expectedHash, StringComparison.Ordinal)
            && (read.State!.StateRevision > 1 || read.State.Enabled == expectedInitialEnabled)
            ? Creation(ScheduleRuntimeCreateStatus.AlreadyExists, read.State)
            : Creation(ScheduleRuntimeCreateStatus.Conflict, read.State);
    }

    private static bool SameState(ScheduleState left, ScheduleState right)
        => ScheduleContractHash.TryComputeState(left, out var leftHash, out _)
            && ScheduleContractHash.TryComputeState(right, out var rightHash, out _)
            && string.Equals(leftHash, rightHash, StringComparison.Ordinal);

    private static bool TryNextNominal(
        ScheduleRecurrenceRule recurrence,
        long ordinal,
        out DateTime local)
    {
        decimal periodTicks = recurrence.Kind switch
        {
            ScheduleRecurrenceKind.Daily => TimeSpan.TicksPerDay,
            ScheduleRecurrenceKind.Weekly => 7m * TimeSpan.TicksPerDay,
            _ => 0,
        };
        var ticks = recurrence.FirstLocalOccurrence.Ticks + (ordinal - 1m) * periodTicks;
        if (periodTicks <= 0
            || ticks < DateTime.MinValue.Ticks
            || ticks > new DateTime(ScheduleContractLimits.MaximumSupportedYear, 12, 31, 23, 59, 59, DateTimeKind.Unspecified).Ticks)
        {
            local = default;
            return false;
        }

        local = new DateTime((long)ticks, DateTimeKind.Unspecified);
        return true;
    }

    private static decimal MaximumSupportedTicks()
        => new DateTime(
            ScheduleContractLimits.MaximumSupportedYear + 1,
            1,
            1,
            0,
            0,
            0,
            DateTimeKind.Unspecified).Ticks - 1m;

    private static ScheduleRuntimeCreateStatus MapRead(ScheduleStoreReadStatus status) => status switch
    {
        ScheduleStoreReadStatus.Unavailable => ScheduleRuntimeCreateStatus.Unavailable,
        ScheduleStoreReadStatus.Backpressured => ScheduleRuntimeCreateStatus.Backpressured,
        _ => ScheduleRuntimeCreateStatus.Corrupt,
    };

    private static bool IsUtc(DateTimeOffset? value)
        => value is { } instant
            && instant != default
            && instant.Offset == TimeSpan.Zero
            && instant.Year is >= ScheduleContractLimits.MinimumSupportedYear and <= ScheduleContractLimits.MaximumSupportedYear;

    private static ScheduleStoreMutationResult Mutation(
        ScheduleStoreMutationStatus status,
        ScheduleState? state = null)
        => new(status, ScheduleContractCopy.Copy(state));

    private static ScheduleRuntimeCreateResult Creation(
        ScheduleRuntimeCreateStatus status,
        ScheduleState? state = null)
        => new(status, ScheduleContractCopy.Copy(state));

    private static ScheduleRuntimeCreateResult FromStore(ScheduleStoreMutationResult result)
        => Creation(
            result.Status switch
            {
                ScheduleStoreMutationStatus.Applied => ScheduleRuntimeCreateStatus.Created,
                ScheduleStoreMutationStatus.AlreadyExists => ScheduleRuntimeCreateStatus.AlreadyExists,
                ScheduleStoreMutationStatus.Conflict => ScheduleRuntimeCreateStatus.Conflict,
                ScheduleStoreMutationStatus.Unavailable => ScheduleRuntimeCreateStatus.Unavailable,
                ScheduleStoreMutationStatus.Backpressured => ScheduleRuntimeCreateStatus.Backpressured,
                _ => ScheduleRuntimeCreateStatus.Corrupt,
            },
            result.CurrentState);
}
