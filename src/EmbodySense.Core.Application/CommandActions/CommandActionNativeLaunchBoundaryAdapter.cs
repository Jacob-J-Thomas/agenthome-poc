using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.CommandActions;

internal sealed class CommandActionNativeLaunchBoundaryAdapter(IGovernedActuatorDispatchBoundary boundary) : ICommandActionNativeLaunchBoundary
{
    public async Task<CommandActionNativeOutcome> CrossAsync(
        Func<CancellationToken, Task<CommandActionNativeOutcome>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        CommandActionNativeOutcome? observed = null;
        var external = await boundary.CrossAsync(
            async token =>
            {
                observed = await callback(token).ConfigureAwait(false);
                if (observed is null
                    || !Enum.IsDefined(observed.Kind)
                    || !CommandActionFingerprint.IsEvidenceIdentifier(observed.OutcomeEvidenceId))
                {
                    throw new InvalidOperationException("The native command host returned an incomplete outcome.");
                }
                return new GovernedActuatorExternalOutcome(
                    observed.Kind == CommandActionNativeOutcomeKind.Succeeded ? GovernedLoopEffectOutcome.Succeeded : GovernedLoopEffectOutcome.Failed,
                    observed.OutcomeEvidenceId,
                    null);
            },
            cancellationToken).ConfigureAwait(false);
        if (observed is null || !string.Equals(observed.OutcomeEvidenceId, external.OutcomeEvidenceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The native command outcome conflicted with the canonical effect boundary.");
        }
        return observed;
    }
}
