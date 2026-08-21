using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions;

/// <summary>Adapts the local native callback to the canonical durable effect-attempt dispatch boundary.</summary>
internal sealed class WorkspaceActionNativeDispatchBoundaryAdapter : IWorkspaceActionNativeDispatchBoundary
{
    private readonly IGovernedActuatorDispatchBoundary _inner;

    public WorkspaceActionNativeDispatchBoundaryAdapter(IGovernedActuatorDispatchBoundary inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<WorkspaceActionNativeOutcome> CrossAsync(
        Func<CancellationToken, Task<WorkspaceActionNativeOutcome>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var external = await _inner.CrossAsync(
            async token =>
            {
                var local = await callback(token).ConfigureAwait(false);
                return new GovernedActuatorExternalOutcome(
                    GovernedLoopEffectOutcome.Succeeded,
                    local.OutcomeEvidenceId,
                    local.AfterEvidenceId);
            },
            cancellationToken).ConfigureAwait(false);
        if (external.Outcome != GovernedLoopEffectOutcome.Succeeded
            || external.AfterEvidenceId is null)
        {
            throw new InvalidOperationException("The canonical effect boundary returned a non-success workspace outcome after a successful native commit.");
        }
        return new WorkspaceActionNativeOutcome(external.OutcomeEvidenceId, external.AfterEvidenceId);
    }
}
