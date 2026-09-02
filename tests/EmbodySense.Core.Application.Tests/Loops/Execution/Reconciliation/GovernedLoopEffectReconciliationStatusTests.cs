using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationStatusTests
{
    [Fact]
    public void Case_store_statuses_are_closed_and_fail_closed()
    {
        Assert.Equal(["Unknown", "Ready", "Invalid", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopEffectReconciliationCaseListStatus>());
        Assert.Equal(["Unknown", "Found", "NotFound", "Invalid", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopEffectReconciliationCaseReadStatus>());
        Assert.Equal(["Unknown", "Applied", "Replayed", "Conflict", "Invalid", "Corrupt", "Unavailable", "CapacityExceeded", "RepairRequired"], Enum.GetNames<GovernedLoopEffectReconciliationCaseMutationStatus>());
    }

    [Fact]
    public void Authorization_statuses_distinguish_denial_from_unavailability()
    {
        Assert.Equal(["Unknown", "Ready", "Denied", "Invalid", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopEffectReconciliationAuthorizationStatus>());
    }

    [Fact]
    public void Probe_statuses_distinguish_registry_identity_and_observation_failures()
    {
        Assert.Equal(["Unknown", "Ready", "Invalid", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopEffectReconciliationProbeRegistryListStatus>());
        Assert.Equal(["Unknown", "Found", "NotFound", "Conflict", "Invalid", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopEffectReconciliationProbeRegistryReadStatus>());
        Assert.Equal(["Unknown", "Ready", "NotFound", "Invalid", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopEffectReconciliationProbeInvocationStatus>());
    }

    [Fact]
    public void Immutable_input_and_resolution_statuses_fail_closed()
    {
        Assert.Equal(["Unknown", "Found", "NotFound", "Conflict", "Invalid", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopEffectReconciliationInputReadStatus>());
        Assert.Equal(["Unknown", "Found", "NotFound", "Invalid", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopEffectReconciliationResolutionReadStatus>());
    }
}
