using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Application.HumanInput.Publication;

/// <summary>Attributes one checkpoint publication to immutable admission evidence without converting that evidence into current authority.</summary>
/// <remarks>The lifecycle service separately resolves the retained grant at the first Create attempt. This authorizer only
/// proves that the service is operating on the exact server-constructed command and carries the already-admitted actor
/// as durable attribution.</remarks>
internal sealed class AdmissionBoundHumanInputRequestLifecycleActorAuthorizer : IHumanInputRequestLifecycleActorAuthorizer
{
    private readonly AuthorityActorId _actorId;
    private readonly HumanInputRequestLifecycleOperationKind _kind;
    private readonly string _operationId;
    private readonly string _requestHash;
    private readonly string _requestId;
    private readonly string _admissionReceiptHash;
    private readonly string _workspaceId;

    public AdmissionBoundHumanInputRequestLifecycleActorAuthorizer(
        HumanInputRequestLifecycleCommand command,
        AuthorityActorId actorId,
        GovernedLoopAdmissionReceipt admissionReceipt,
        string admissionReceiptHash,
        string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(command);
        _actorId = actorId ?? throw new ArgumentNullException(nameof(actorId));
        ArgumentNullException.ThrowIfNull(admissionReceipt);
        if (!string.Equals(admissionReceipt.ContentHash, admissionReceiptHash, StringComparison.Ordinal)) throw new ArgumentException("Admission receipt hash must exactly bind the retained receipt.", nameof(admissionReceiptHash));
        _admissionReceiptHash = admissionReceiptHash;
        _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
        _operationId = command.OperationId;
        _kind = command.Kind;
        _requestId = command.RequestId;
        _requestHash = command.RequestHash;
    }

    /// <inheritdoc />
    public Task<HumanInputRequestLifecycleActorAuthorization> AuthorizeAsync(
        HumanInputRequestLifecycleActorAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null
            || !string.Equals(request.Command.OperationId, _operationId, StringComparison.Ordinal)
            || request.Command.Kind != _kind
            || !string.Equals(request.Command.RequestId, _requestId, StringComparison.Ordinal)
            || !string.Equals(request.RequestHash, _requestHash, StringComparison.Ordinal)
            || !string.Equals(request.Command.RequestHash, _requestHash, StringComparison.Ordinal)
            || !HumanInputRequestLifecycleCommandHash.Matches(request.Command)
            || !string.Equals(request.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || request.EvaluatedAtUtc == default
            || request.EvaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            return Task.FromResult(Unavailable(request));
        }

        return Task.FromResult(new HumanInputRequestLifecycleActorAuthorization(
            HumanInputRequestLifecycleActorAuthorizationStatus.Authorized,
            request.Command.OperationId,
            request.RequestHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            _actorId,
            _admissionReceiptHash));
    }

    private static HumanInputRequestLifecycleActorAuthorization Unavailable(HumanInputRequestLifecycleActorAuthorizationRequest? request)
        => new(
            HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable,
            request?.Command.OperationId ?? string.Empty,
            request?.RequestHash ?? string.Empty,
            request?.WorkspaceId ?? string.Empty,
            request?.EvaluatedAtUtc ?? default,
            null,
            string.Empty);
}
