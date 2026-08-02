using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Adapts durable trigger intent to the existing governed custom-loop runtime gate.</summary>
internal sealed class TriggerCustomLoopDispatcher : ITriggerWorkerDispatcher
{
    private readonly ITriggerCustomLoopInvoker _invoker;

    /// <summary>Initializes dispatch through one retained custom-loop runtime.</summary>
    /// <param name="invoker">The governed runtime invocation seam.</param>
    public TriggerCustomLoopDispatcher(ITriggerCustomLoopInvoker invoker)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    /// <inheritdoc />
    public async Task<TriggerWorkerDispatchResult> DispatchAsync(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default)
    {
        var preparation = TriggerCustomLoopDispatchProtocol.Prepare(envelope, intent);
        if (preparation.Rejection is not null)
        {
            return preparation.Rejection;
        }

        var response = await _invoker.InvokeAsync(preparation.Input!, cancellationToken).ConfigureAwait(false);
        return TriggerCustomLoopDispatchProtocol.Map(envelope, intent, response);
    }
}
