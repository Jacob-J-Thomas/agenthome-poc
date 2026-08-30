using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopCoordinatorRepairContractTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', 64);

    [Fact]
    public void Repair_evidence_is_canonical_and_binds_the_exact_failed_generation()
    {
        var ownership = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(1, "coordinator", "owner", 3, _now.AddMinutes(-2), string.Empty));
        var readiness = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairReadiness(1, _workspaceId, "coordinator", true, true, true, true, _now, string.Empty));
        var disposition = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairDisposition(
            1,
            _workspaceId,
            "coordinator",
            "repair-operation",
            "operator",
            ownership,
            Hash('a'),
            Hash('b'),
            Hash('c'),
            readiness,
            _now,
            string.Empty));

        Assert.True(GovernedLoopSleepContractValidator.Validate(readiness).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.Validate(disposition).IsValid);
        Assert.True(GovernedLoopSleepContractHash.Matches(disposition));
        Assert.False(GovernedLoopSleepContractValidator.Validate(disposition with { LatestFailureHash = Hash('d') }).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(disposition with { DependencyReadiness = readiness with { WorkspaceId = "other" } }).IsValid);
    }

    [Fact]
    public void Repair_evidence_rejects_unready_dependencies()
    {
        var ownership = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(1, "coordinator", "owner", 1, _now, string.Empty));
        var readiness = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairReadiness(1, _workspaceId, "coordinator", true, true, false, true, _now, string.Empty));
        var disposition = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairDisposition(
            1,
            _workspaceId,
            "coordinator",
            "repair-operation",
            "operator",
            ownership,
            Hash('a'),
            Hash('b'),
            Hash('c'),
            readiness,
            _now,
            string.Empty));

        Assert.True(GovernedLoopSleepContractValidator.Validate(readiness).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(disposition).IsValid);
        Assert.False(GovernedLoopCoordinatorRepairReadinessContract.IsReady(readiness));
    }

    private static string Hash(char value) => new(value, 64);
}
