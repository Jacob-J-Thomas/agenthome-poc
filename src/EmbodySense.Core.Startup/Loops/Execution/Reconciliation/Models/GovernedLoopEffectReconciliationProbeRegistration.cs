using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

internal sealed record GovernedLoopEffectReconciliationProbeRegistration(
    GovernedLoopEffectReconciliationContractMetadata Contract,
    IGovernedLoopEffectReconciliationProbe Probe);
