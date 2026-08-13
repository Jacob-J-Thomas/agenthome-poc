using System.Text.Json;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Triggers.Schedules;

internal sealed class ScheduleRuntimeStoreBoundary(IScheduleStorePort store) : IScheduleStorePort
{
    private readonly IScheduleStorePort _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<ScheduleStoreReadResult> ReadAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var read = await _store.ReadAsync(scheduleId, cancellationToken).ConfigureAwait(false);
            return NormalizeRead(scheduleId, read);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ReadResult(StoreReadFailureStatus(exception));
        }
    }

    public async Task<ScheduleStoreMutationResult> CreateAsync(
        ScheduleStoreCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await _store.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            return NormalizeCreateMutation(result, request.Definition, request.InitialState);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Mutation(StoreMutationFailureStatus(exception));
        }
    }

    public async Task<ScheduleStoreMutationResult> CompareExchangeAsync(
        ScheduleStateCompareExchange request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await _store.CompareExchangeAsync(request, cancellationToken).ConfigureAwait(false);
            return NormalizeCompareExchangeMutation(result, request.Expected, request.Replacement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Mutation(StoreMutationFailureStatus(exception));
        }
    }

    private static ScheduleStoreReadResult NormalizeRead(
        ScheduleId scheduleId,
        ScheduleStoreReadResult? read)
    {
        if (read is null
            || !Enum.IsDefined(read.Status)
            || read.Status == ScheduleStoreReadStatus.Unknown)
        {
            return ReadResult(ScheduleStoreReadStatus.Corrupt);
        }

        if (read.Status != ScheduleStoreReadStatus.Found)
        {
            return read.Definition is null && read.State is null
                ? ReadResult(read.Status)
                : ReadResult(ScheduleStoreReadStatus.Corrupt);
        }

        var definition = ScheduleContractCopy.Copy(read.Definition);
        var state = ScheduleContractCopy.Copy(read.State);
        return definition is not null
            && state is not null
            && Equals(scheduleId, definition.ScheduleId)
            && Equals(scheduleId, state.ScheduleId)
            && ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state).IsValid
            ? ReadResult(ScheduleStoreReadStatus.Found, definition, state)
            : ReadResult(ScheduleStoreReadStatus.Corrupt);
    }

    private static ScheduleStoreMutationResult NormalizeCreateMutation(
        ScheduleStoreMutationResult? result,
        ScheduleDefinition definition,
        ScheduleState initialState)
    {
        if (result is null
            || !Enum.IsDefined(result.Status)
            || result.Status == ScheduleStoreMutationStatus.Unknown)
        {
            return Mutation(ScheduleStoreMutationStatus.Corrupt);
        }

        if (result.Status is ScheduleStoreMutationStatus.AlreadyExists or ScheduleStoreMutationStatus.Conflict)
        {
            return Mutation(result.Status);
        }

        var current = ScheduleContractCopy.Copy(result.CurrentState);
        if (current is not null
            && !ScheduleContractValidator.ValidateDefinitionStateComposition(definition, current).IsValid)
        {
            return Mutation(ScheduleStoreMutationStatus.Corrupt);
        }

        return result.Status switch
        {
            ScheduleStoreMutationStatus.Applied when current is not null && SameState(initialState, current)
                => Mutation(ScheduleStoreMutationStatus.Applied, current),
            ScheduleStoreMutationStatus.Corrupt or ScheduleStoreMutationStatus.Unavailable or ScheduleStoreMutationStatus.Backpressured
                when current is null => Mutation(result.Status),
            _ => Mutation(ScheduleStoreMutationStatus.Corrupt),
        };
    }

    private static ScheduleStoreMutationResult NormalizeCompareExchangeMutation(
        ScheduleStoreMutationResult? result,
        ScheduleState expected,
        ScheduleState replacement)
    {
        if (result is null
            || !Enum.IsDefined(result.Status)
            || result.Status == ScheduleStoreMutationStatus.Unknown)
        {
            return Mutation(ScheduleStoreMutationStatus.Corrupt);
        }

        var current = ScheduleContractCopy.Copy(result.CurrentState);
        if (current is not null && !IsBoundCurrentState(expected, current))
        {
            return Mutation(ScheduleStoreMutationStatus.Corrupt);
        }

        return result.Status switch
        {
            ScheduleStoreMutationStatus.Applied when current is not null && SameState(replacement, current)
                => Mutation(ScheduleStoreMutationStatus.Applied, current),
            ScheduleStoreMutationStatus.Conflict
                => Mutation(ScheduleStoreMutationStatus.Conflict, current),
            ScheduleStoreMutationStatus.Corrupt
                => Mutation(ScheduleStoreMutationStatus.Corrupt, current),
            ScheduleStoreMutationStatus.Unavailable or ScheduleStoreMutationStatus.Backpressured
                when current is null => Mutation(result.Status),
            _ => Mutation(ScheduleStoreMutationStatus.Corrupt),
        };
    }

    private static bool IsBoundCurrentState(ScheduleState expected, ScheduleState current)
        => ScheduleContractValidator.ValidateState(current).IsValid
            && Equals(expected.ScheduleId, current.ScheduleId)
            && expected.DefinitionRevision == current.DefinitionRevision
            && string.Equals(expected.DefinitionHash, current.DefinitionHash, StringComparison.Ordinal);

    private static bool SameState(ScheduleState left, ScheduleState right)
        => ScheduleContractHash.TryComputeState(left, out var leftHash, out _)
            && ScheduleContractHash.TryComputeState(right, out var rightHash, out _)
            && string.Equals(leftHash, rightHash, StringComparison.Ordinal);

    private static ScheduleStoreReadStatus StoreReadFailureStatus(Exception exception)
        => IsCorruptStoreFailure(exception)
            ? ScheduleStoreReadStatus.Corrupt
            : ScheduleStoreReadStatus.Unavailable;

    private static ScheduleStoreMutationStatus StoreMutationFailureStatus(Exception exception)
        => IsCorruptStoreFailure(exception)
            ? ScheduleStoreMutationStatus.Corrupt
            : ScheduleStoreMutationStatus.Unavailable;

    private static bool IsCorruptStoreFailure(Exception exception)
        => exception is FormatException
            or InvalidDataException
            or JsonException
            or InvalidOperationException
            or ArgumentException
            or NullReferenceException
            or OverflowException;

    private static ScheduleStoreReadResult ReadResult(
        ScheduleStoreReadStatus status,
        ScheduleDefinition? definition = null,
        ScheduleState? state = null)
        => new(status, ScheduleContractCopy.Copy(definition), ScheduleContractCopy.Copy(state));

    private static ScheduleStoreMutationResult Mutation(
        ScheduleStoreMutationStatus status,
        ScheduleState? state = null)
        => new(status, ScheduleContractCopy.Copy(state));
}
