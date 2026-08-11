namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

public sealed partial record HumanInputRequestLifecycleActorAuthorizationRequest
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputRequestLifecycleActorAuthorizationRequest {{ OperationId = {Command?.OperationId}, RequestHash = {RequestHash}, EvaluatedAtUtc = {EvaluatedAtUtc:O} }}";
}
