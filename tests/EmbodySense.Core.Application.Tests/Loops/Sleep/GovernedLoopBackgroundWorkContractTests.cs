using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

public sealed class GovernedLoopBackgroundWorkContractTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Public_port_and_closed_statuses_expose_one_bounded_query_surface()
    {
        var method = Assert.Single(typeof(IGovernedLoopBackgroundWorkSource).GetMethods());

        Assert.Equal("ReadAsync", method.Name);
        Assert.Equal(
            [typeof(GovernedLoopBackgroundWorkFamily), typeof(DateTimeOffset), typeof(int), typeof(CancellationToken)],
            method.GetParameters().Select(item => item.ParameterType));
        Assert.Equal(["Schedule", "Wake", "WakeReconciliation"], Enum.GetNames<GovernedLoopBackgroundWorkFamily>());
        Assert.Equal(["Found", "Empty", "Backpressured", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopBackgroundWorkReadStatus>());
        Assert.Equal(256, GovernedLoopBackgroundWorkContractLimits.MaxCandidatesPerFamily);
    }

    [Fact]
    public void Query_requires_utc_and_a_finite_positive_per_family_bound()
    {
        Assert.True(GovernedLoopBackgroundWorkContract.IsValidReadRequest(DateTimeOffset.UnixEpoch, 1));
        Assert.True(GovernedLoopBackgroundWorkContract.IsValidReadRequest(DateTimeOffset.UnixEpoch, GovernedLoopBackgroundWorkContractLimits.MaxCandidatesPerFamily));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValidReadRequest(DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1)), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValidReadRequest(DateTimeOffset.UnixEpoch, 0));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValidReadRequest(DateTimeOffset.UnixEpoch, GovernedLoopBackgroundWorkContractLimits.MaxCandidatesPerFamily + 1));
    }

    [Fact]
    public void Found_result_accepts_exact_detached_candidates_from_each_family()
    {
        var result = Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [Schedule("schedule-1")],
            [new GovernedLoopWakeRequest(HashA, HashB)],
            [new GovernedLoopWakeReconciliationRequest(HashA, HashB)]);

        Assert.True(GovernedLoopBackgroundWorkContract.IsValid(result, 1));
    }

    [Fact]
    public void Empty_and_failure_postures_carry_no_candidates()
    {
        Assert.True(GovernedLoopBackgroundWorkContract.IsValid(Result(GovernedLoopBackgroundWorkReadStatus.Empty), 1));
        Assert.True(GovernedLoopBackgroundWorkContract.IsValid(Result(GovernedLoopBackgroundWorkReadStatus.Backpressured), 1));
        Assert.True(GovernedLoopBackgroundWorkContract.IsValid(Result(GovernedLoopBackgroundWorkReadStatus.Corrupt), 1));
        Assert.True(GovernedLoopBackgroundWorkContract.IsValid(Result(GovernedLoopBackgroundWorkReadStatus.Unavailable), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(
            GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
                GovernedLoopBackgroundWorkReadStatus.Found,
                [],
                GovernedLoopBackgroundWorkReadStatus.Found,
                [],
                GovernedLoopBackgroundWorkReadStatus.Found,
                []),
            1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(GovernedLoopBackgroundWorkReadStatus.Empty, [Schedule("schedule-1")]), 1));
    }

    [Fact]
    public void Independent_family_statuses_preserve_healthy_candidates_beside_fail_closed_siblings()
    {
        var reconciliation = new GovernedLoopWakeReconciliationRequest(HashA, HashB);
        var result = GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Backpressured,
            [],
            GovernedLoopBackgroundWorkReadStatus.Backpressured,
            [],
            GovernedLoopBackgroundWorkReadStatus.Found,
            [reconciliation]);

        Assert.True(GovernedLoopBackgroundWorkContract.IsValid(result, 1));
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, result.Status);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Backpressured, result.ScheduleStatus);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Backpressured, result.WakeStatus);
        Assert.Equal(GovernedLoopBackgroundWorkReadStatus.Found, result.WakeReconciliationStatus);
        Assert.NotSame(reconciliation, Assert.Single(result.WakeReconciliationCandidates));
    }

    [Fact]
    public void Truncated_family_pages_require_a_full_found_page_and_remain_separate_from_failure_posture()
    {
        var valid = GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [Schedule("schedule-1"), Schedule("schedule-2")],
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            schedulePageTruncated: true);
        var shortPage = GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [Schedule("schedule-1")],
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            schedulePageTruncated: true);
        var failure = GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Backpressured,
            [],
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            schedulePageTruncated: true);

        Assert.True(GovernedLoopBackgroundWorkContract.IsValid(valid, 2));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(shortPage, 2));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(failure, 2));
    }

    [Fact]
    public void Malformed_over_bound_and_duplicate_candidates_fail_closed()
    {
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(null, 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result((GovernedLoopBackgroundWorkReadStatus)99), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Found,
            null!,
            [],
            []), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [],
            null!,
            []), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [],
            [],
            null!), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [null!]), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            wakeCandidates: [null!]), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            reconciliationCandidates: [null!]), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [Schedule("schedule-1"), Schedule("schedule-2")]), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            [Schedule("schedule-1"), Schedule("schedule-1")]), 2));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            wakeCandidates: [new GovernedLoopWakeRequest(HashA, HashB), new GovernedLoopWakeRequest(HashA, HashB)]), 2));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            reconciliationCandidates: [new GovernedLoopWakeReconciliationRequest(HashA, HashB), new GovernedLoopWakeReconciliationRequest(HashA, HashB)]), 2));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            wakeCandidates: [new GovernedLoopWakeRequest("bad", HashB)]), 1));
        Assert.False(GovernedLoopBackgroundWorkContract.IsValid(Result(
            GovernedLoopBackgroundWorkReadStatus.Found,
            reconciliationCandidates: [new GovernedLoopWakeReconciliationRequest(HashA, "bad")]), 1));
    }

    [Fact]
    public void Result_defensively_copies_lists_and_candidate_values()
    {
        var schedule = Schedule("schedule-1");
        var wake = new GovernedLoopWakeRequest(HashA, HashB);
        var reconciliation = new GovernedLoopWakeReconciliationRequest(HashA, HashB);
        var schedules = new List<ScheduleId> { schedule };
        var wakes = new List<GovernedLoopWakeRequest> { wake };
        var reconciliations = new List<GovernedLoopWakeReconciliationRequest> { reconciliation };
        var result = Result(GovernedLoopBackgroundWorkReadStatus.Found, schedules, wakes, reconciliations);

        schedules.Clear();
        wakes.Clear();
        reconciliations.Clear();

        Assert.Single(result.ScheduleCandidates);
        Assert.Single(result.WakeCandidates);
        Assert.Single(result.WakeReconciliationCandidates);
        Assert.NotSame(schedule, result.ScheduleCandidates[0]);
        Assert.NotSame(wake, result.WakeCandidates[0]);
        Assert.NotSame(reconciliation, result.WakeReconciliationCandidates[0]);
    }

    private static GovernedLoopBackgroundWorkReadResult Result(
        GovernedLoopBackgroundWorkReadStatus status,
        IReadOnlyList<ScheduleId>? scheduleCandidates = null,
        IReadOnlyList<GovernedLoopWakeRequest>? wakeCandidates = null,
        IReadOnlyList<GovernedLoopWakeReconciliationRequest>? reconciliationCandidates = null)
        => GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(status, scheduleCandidates ?? [], wakeCandidates ?? [], reconciliationCandidates ?? []);

    private static ScheduleId Schedule(string value)
    {
        Assert.True(ScheduleId.TryParse(value, out var scheduleId));
        return scheduleId!;
    }
}
