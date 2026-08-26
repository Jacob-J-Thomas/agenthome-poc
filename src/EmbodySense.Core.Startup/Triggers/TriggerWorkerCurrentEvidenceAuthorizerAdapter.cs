using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Triggers.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Adapts trusted Startup current-evidence decisions to the application trigger-worker authorization port.</summary>
/// <remarks>The adapter projects selected envelope evidence without granting authority or changing the worker's durable intent boundary.</remarks>
public sealed class TriggerWorkerCurrentEvidenceAuthorizerAdapter : ITriggerDispatchAuthorizer
{
    private readonly ITriggerWorkerCurrentEvidenceAuthorizer _authorizer;

    /// <summary>Creates the application-port adapter over one composition-owned authorizer.</summary>
    /// <param name="authorizer">The trusted current-evidence authorizer retained for the adapter lifetime.</param>
    /// <exception cref="ArgumentNullException"><paramref name="authorizer"/> is <see langword="null"/>.</exception>
    public TriggerWorkerCurrentEvidenceAuthorizerAdapter(ITriggerWorkerCurrentEvidenceAuthorizer authorizer)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
    }

    /// <inheritdoc />
    public async Task<TriggerDispatchAuthorization> AuthorizeAsync(TriggerDeliveryEnvelope envelope, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var input = new TriggerWorkerCurrentEvidenceInput(
            envelope.DeliveryId.Value,
            envelope.Loop,
            envelope.Adapter.Capability.Id.Value,
            envelope.Adapter.Capability.Version.Value,
            envelope.Adapter.Capability.Hash.Value,
            envelope.Adapter.Implementation.ProviderId.Value,
            envelope.Adapter.Implementation.ImplementationId,
            envelope.ActorContext.ActorId.Value,
            envelope.ActorContext.SurfaceId,
            envelope.ActorContext.WorkspaceId,
            envelope.ActorContext.RoleId,
            envelope.Authority.Profile.ProfileId.Value,
            envelope.Authority.Profile.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var response = await _authorizer.AuthorizeAsync(input, evaluatedAtUtc, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(response);
        var status = response.Status switch
        {
            "Authorized" => TriggerDispatchAuthorizationStatus.Authorized,
            "Rejected" => TriggerDispatchAuthorizationStatus.Rejected,
            "Unavailable" => TriggerDispatchAuthorizationStatus.Unavailable,
            _ => throw new InvalidOperationException("The current-evidence authorizer returned an unsupported status.")
        };
        return new TriggerDispatchAuthorization(status, response.EvidenceHash, response.Detail);
    }
}
