using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Defines the narrow canonical governed-loop invocation seam used only by authenticated trigger dispatch.</summary>
internal interface ITriggerGovernedLoopInvoker
{
    /// <summary>Invokes one exact publication and grant through the runtime's server-owned admission boundary.</summary>
    /// <param name="input">The exact trigger-namespaced operation, immutable pins, and strict UTF-8 prompt.</param>
    /// <param name="actorContext">The revalidated actor, surface, workspace, and role binding.</param>
    /// <param name="envelope">The complete selected canonical delivery retained as immutable provenance.</param>
    /// <param name="cancellationToken">A token honored through safe invocation boundaries.</param>
    /// <returns>The canonical admission and execution posture with validated exact outcome evidence when available.</returns>
    Task<GovernedLoopRunInvocationResponse> InvokeAsync(
        GovernedLoopRunInvocationInput input,
        TriggerActorContext actorContext,
        TriggerDeliveryEnvelope envelope,
        CancellationToken cancellationToken = default);
}
