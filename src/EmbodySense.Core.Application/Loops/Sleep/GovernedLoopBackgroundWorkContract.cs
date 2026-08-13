using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Validates bounded detached background-work queries and candidate snapshots.</summary>
public static class GovernedLoopBackgroundWorkContract
{
    /// <summary>Gets whether an enumeration query has one UTC observation instant and an admitted per-family bound.</summary>
    public static bool IsValidReadRequest(DateTimeOffset observedAtUtc, int perFamilyMax)
        => observedAtUtc.Offset == TimeSpan.Zero
            && perFamilyMax is > 0 and <= GovernedLoopBackgroundWorkContractLimits.MaxCandidatesPerFamily;

    /// <summary>Gets whether one result has an exact closed shape, bounded families, valid candidates, and no duplicates.</summary>
    public static bool IsValid(GovernedLoopBackgroundWorkReadResult? result, int perFamilyMax)
    {
        if (result is null
            || !Enum.IsDefined(result.Status)
            || !Enum.IsDefined(result.ScheduleStatus)
            || !Enum.IsDefined(result.WakeStatus)
            || !Enum.IsDefined(result.WakeReconciliationStatus)
            || perFamilyMax is <= 0 or > GovernedLoopBackgroundWorkContractLimits.MaxCandidatesPerFamily
            || result.ScheduleCandidates is null
            || result.WakeCandidates is null
            || result.WakeReconciliationCandidates is null
            || result.ScheduleCandidates.Count > perFamilyMax
            || result.WakeCandidates.Count > perFamilyMax
            || result.WakeReconciliationCandidates.Count > perFamilyMax
            || result.SchedulePageTruncated && (result.ScheduleStatus != GovernedLoopBackgroundWorkReadStatus.Found || result.ScheduleCandidates.Count != perFamilyMax)
            || result.WakePageTruncated && (result.WakeStatus != GovernedLoopBackgroundWorkReadStatus.Found || result.WakeCandidates.Count != perFamilyMax)
            || result.WakeReconciliationPageTruncated && (result.WakeReconciliationStatus != GovernedLoopBackgroundWorkReadStatus.Found || result.WakeReconciliationCandidates.Count != perFamilyMax)
            || !result.ScheduleCandidates.All(item => item is not null)
            || !result.WakeCandidates.All(IsValid)
            || !result.WakeReconciliationCandidates.All(IsValid)
            || result.ScheduleCandidates.Select(item => item.Value).Distinct(StringComparer.Ordinal).Count() != result.ScheduleCandidates.Count
            || result.WakeCandidates.Select(WakeKey).Distinct(StringComparer.Ordinal).Count() != result.WakeCandidates.Count
            || result.WakeReconciliationCandidates.Select(ReconciliationKey).Distinct(StringComparer.Ordinal).Count() != result.WakeReconciliationCandidates.Count)
        {
            return false;
        }

        return IsValidFamily(result.ScheduleStatus, result.ScheduleCandidates.Count)
            && IsValidFamily(result.WakeStatus, result.WakeCandidates.Count)
            && IsValidFamily(result.WakeReconciliationStatus, result.WakeReconciliationCandidates.Count)
            && result.Status == Summarize(
                result.ScheduleStatus,
                result.WakeStatus,
                result.WakeReconciliationStatus);
    }

    private static bool IsValidFamily(GovernedLoopBackgroundWorkReadStatus status, int count)
        => status switch
        {
            GovernedLoopBackgroundWorkReadStatus.Found => count > 0,
            GovernedLoopBackgroundWorkReadStatus.Empty
                or GovernedLoopBackgroundWorkReadStatus.Backpressured
                or GovernedLoopBackgroundWorkReadStatus.Corrupt
                or GovernedLoopBackgroundWorkReadStatus.Unavailable => count == 0,
            _ => false
        };

    private static GovernedLoopBackgroundWorkReadStatus Summarize(
        GovernedLoopBackgroundWorkReadStatus scheduleStatus,
        GovernedLoopBackgroundWorkReadStatus wakeStatus,
        GovernedLoopBackgroundWorkReadStatus wakeReconciliationStatus)
    {
        GovernedLoopBackgroundWorkReadStatus[] statuses = [scheduleStatus, wakeStatus, wakeReconciliationStatus];
        if (statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Found))
        {
            return GovernedLoopBackgroundWorkReadStatus.Found;
        }

        if (statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Corrupt))
        {
            return GovernedLoopBackgroundWorkReadStatus.Corrupt;
        }

        if (statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Unavailable))
        {
            return GovernedLoopBackgroundWorkReadStatus.Unavailable;
        }

        return statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Backpressured)
            ? GovernedLoopBackgroundWorkReadStatus.Backpressured
            : GovernedLoopBackgroundWorkReadStatus.Empty;
    }

    private static bool IsValid(GovernedLoopWakeRequest? request)
        => request is not null
            && IsCanonicalHash(request.CheckpointId)
            && IsCanonicalHash(request.CheckpointHash)
            && (request.AuthenticationEvidenceHash is null || IsCanonicalHash(request.AuthenticationEvidenceHash));

    private static bool IsValid(GovernedLoopWakeReconciliationRequest? request)
        => request is not null
            && IsCanonicalHash(request.CheckpointId)
            && IsCanonicalHash(request.WakeId);

    private static string WakeKey(GovernedLoopWakeRequest request)
        => string.Join('\n', request.CheckpointId, request.CheckpointHash, request.AuthenticationEvidenceHash);

    private static string ReconciliationKey(GovernedLoopWakeReconciliationRequest request)
        => string.Join('\n', request.CheckpointId, request.WakeId);

    private static bool IsCanonicalHash(string? value)
        => value is { Length: GovernedLoopSleepContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
