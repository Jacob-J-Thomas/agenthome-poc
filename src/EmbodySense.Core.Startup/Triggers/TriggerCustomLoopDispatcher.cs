using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Adapts durable trigger intent to the existing governed custom-loop runtime gate.</summary>
internal sealed class TriggerCustomLoopDispatcher : ITriggerWorkerDispatcher
{
    private readonly ITriggerCustomLoopInvoker _legacyInvoker;
    private readonly ITriggerGovernedLoopInvoker _governedInvoker;

    /// <summary>Initializes dispatch through one retained custom-loop runtime.</summary>
    /// <param name="legacyInvoker">The retained legacy-definition runtime seam.</param>
    /// <param name="governedInvoker">The canonical governed-publication runtime seam.</param>
    public TriggerCustomLoopDispatcher(ITriggerCustomLoopInvoker legacyInvoker, ITriggerGovernedLoopInvoker governedInvoker)
    {
        _legacyInvoker = legacyInvoker ?? throw new ArgumentNullException(nameof(legacyInvoker));
        _governedInvoker = governedInvoker ?? throw new ArgumentNullException(nameof(governedInvoker));
    }

    /// <inheritdoc />
    public async Task<TriggerWorkerDispatchResult> DispatchAsync(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Loop.Kind switch
        {
            TriggerLoopTargetKind.LegacyDefinition => await DispatchLegacyAsync(envelope, intent, cancellationToken).ConfigureAwait(false),
            TriggerLoopTargetKind.GovernedPublication => await DispatchGovernedAsync(envelope, intent, cancellationToken).ConfigureAwait(false),
            _ => new TriggerWorkerDispatchResult(TriggerDispatchOutcome.NeedsReview, "The selected trigger target kind is unsupported and was not invoked."),
        };
    }

    private async Task<TriggerWorkerDispatchResult> DispatchLegacyAsync(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, CancellationToken cancellationToken)
    {
        var preparation = TriggerCustomLoopDispatchProtocol.Prepare(envelope, intent);
        if (preparation.Rejection is not null)
        {
            return preparation.Rejection;
        }

        var response = await _legacyInvoker.InvokeAsync(preparation.Input!, preparation.ActorContext!, cancellationToken).ConfigureAwait(false);
        return TriggerCustomLoopDispatchProtocol.Map(envelope, intent, response);
    }

    private async Task<TriggerWorkerDispatchResult> DispatchGovernedAsync(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, CancellationToken cancellationToken)
    {
        var preparation = TriggerGovernedLoopDispatchProtocol.Prepare(envelope, intent);
        if (preparation.Rejection is not null)
        {
            return preparation.Rejection;
        }

        var response = await _governedInvoker.InvokeAsync(preparation.Input!, preparation.ActorContext!, envelope, cancellationToken).ConfigureAwait(false);
        return TriggerGovernedLoopDispatchProtocol.Map(envelope, intent, response);
    }
}
