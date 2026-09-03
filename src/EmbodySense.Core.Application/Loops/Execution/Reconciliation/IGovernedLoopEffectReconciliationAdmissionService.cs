using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Publishes one exact durable ReviewBlocked actuator ambiguity into the canonical reconciliation case store.</summary>
/// <remarks>This boundary creates attention evidence only. It cannot invoke an actuator, resume a run, apply a disposition, or resolve an effect.</remarks>
public interface IGovernedLoopEffectReconciliationAdmissionService
{
    /// <summary>Admits or exactly replays one value-free reconciliation case after its run and frontier are durably review-blocked.</summary>
    /// <param name="run">The exact terminal canonical run.</param>
    /// <param name="binding">The exact reconciliation-required effect binding retained by the ambiguity event.</param>
    /// <param name="cancellationToken">Cancels the bounded publication before a closed result is available.</param>
    /// <returns>A closed admission posture. No result authorizes original-effect dispatch.</returns>
    Task<GovernedLoopEffectReconciliationAdmissionResult> AdmitAsync(CustomLoopRunRecord run, GovernedLoopEffectReconciliationBinding binding, CancellationToken cancellationToken = default);
}
