using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Triggers.Schedules;

/// <summary>Projects exact durable governed-run overlap without deciding schedule policy.</summary>
/// <remarks>
/// The adapter reads the unique nonterminal run for the target loop, validates its complete durable
/// shape, reconstructs its exact admitted target, and hashes both the query and observed run posture.
/// A run of another immutable revision is evidence for a clear exact-target result, not an overlap.
/// </remarks>
public sealed class ScheduleRunOverlapAdapter : IScheduleOverlapPort
{
    private const string EvidenceDomain = "embodysense-schedule-overlap-evidence-v1";
    private readonly ICustomLoopRunStore _runStore;

    /// <summary>Creates an overlap adapter over the composition-owned durable run store.</summary>
    /// <param name="runStore">The store that proves the unique current nonterminal run per loop.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runStore"/> is null.</exception>
    public ScheduleRunOverlapAdapter(ICustomLoopRunStore runStore)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    }

    /// <inheritdoc />
    public async Task<ScheduleOverlapResult> GetStatusAsync(
        TriggerLoopReference target,
        ScheduleOccurrenceIdentity occurrenceIdentity,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TriggerLoopReferenceHash.TryCompute(target, out var targetHash, out _)
            || !IsValidIdentity(occurrenceIdentity)
            || !IsSupportedUtc(observedAtUtc))
        {
            return Failure(ScheduleOverlapStatus.Corrupt);
        }

        try
        {
            var activeRun = await _runStore.GetNonterminalByLoopAsync(target.LoopId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (activeRun is null)
            {
                return Result(ScheduleOverlapStatus.Clear, targetHash!, occurrenceIdentity, observedAtUtc, null, null);
            }

            if (activeRun.IsTerminal
                || !string.Equals(activeRun.LoopId, target.LoopId, StringComparison.Ordinal)
                || activeRun.CreatedAtUtc > observedAtUtc
                || activeRun.UpdatedAtUtc > observedAtUtc
                || !CustomLoopRunValidator.Validate(activeRun).IsValid
                || !TryGetAdmittedTargetHash(activeRun, out var activeTargetHash))
            {
                return Failure(ScheduleOverlapStatus.Corrupt);
            }

            var status = string.Equals(targetHash, activeTargetHash, StringComparison.Ordinal)
                ? ScheduleOverlapStatus.Active
                : ScheduleOverlapStatus.Clear;
            return Result(status, targetHash!, occurrenceIdentity, observedAtUtc, activeRun, activeTargetHash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CryptographicException)
        {
            return Failure(ScheduleOverlapStatus.Unavailable);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or JsonException)
        {
            return Failure(ScheduleOverlapStatus.Corrupt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failure(ScheduleOverlapStatus.Unavailable);
        }
    }

    private static bool TryGetAdmittedTargetHash(CustomLoopRunRecord run, out string? targetHash)
    {
        targetHash = null;
        TriggerLoopReference? target;
        bool created;
        if (run.SequentialAdapterBinding is { } binding)
        {
            created = TriggerDeliveryFactory.TryCreateGovernedLoopReference(
                binding.AdmissionReceipt.Intent.Publication,
                binding.AdmissionReceipt.Intent.AuthorityGrant,
                out target,
                out _);
        }
        else
        {
            created = TriggerDeliveryFactory.TryCreateLoopReference(
                run.AdmittedDefinition.Id,
                run.AdmittedDefinition.DefinitionVersion,
                run.AdmittedDefinition.ContentHash,
                out target,
                out _);
        }

        return created && TriggerLoopReferenceHash.TryCompute(target, out targetHash, out _);
    }

    private static ScheduleOverlapResult Result(
        ScheduleOverlapStatus status,
        string targetHash,
        ScheduleOccurrenceIdentity identity,
        DateTimeOffset observedAtUtc,
        CustomLoopRunRecord? activeRun,
        string? activeTargetHash)
    {
        var canonical = string.Join(
            '\n',
            EvidenceDomain,
            status == ScheduleOverlapStatus.Active ? "active" : "clear",
            targetHash,
            identity.OccurrenceId.Value,
            identity.DeliveryId.Value,
            identity.DeduplicationId.Value,
            observedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            activeRun?.Id ?? "none",
            activeRun?.LifecycleVersion.ToString(CultureInfo.InvariantCulture) ?? "none",
            activeRun?.Status.ToString() ?? "none",
            activeRun?.AdmissionOperationId ?? "none",
            activeTargetHash ?? "none");
        var evidenceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new ScheduleOverlapResult(status, evidenceHash);
    }

    private static bool IsValidIdentity(ScheduleOccurrenceIdentity? identity)
        => identity?.OccurrenceId is not null
            && identity.DeliveryId is not null
            && identity.DeduplicationId is not null
            && ScheduleOccurrenceId.TryParse(identity.OccurrenceId.Value, out _)
            && TriggerDeliveryId.TryParse(identity.DeliveryId.Value, out _)
            && TriggerDeduplicationId.TryParse(identity.DeduplicationId.Value, out _);

    private static bool IsSupportedUtc(DateTimeOffset value)
        => value.Offset == TimeSpan.Zero
            && value.Year is >= ScheduleContractLimits.MinimumSupportedYear and <= ScheduleContractLimits.MaximumSupportedYear;

    private static ScheduleOverlapResult Failure(ScheduleOverlapStatus status)
        => new(status, null);
}
