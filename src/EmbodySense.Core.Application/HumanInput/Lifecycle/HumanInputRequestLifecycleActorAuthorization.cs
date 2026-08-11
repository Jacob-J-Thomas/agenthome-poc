namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

public sealed partial record HumanInputRequestLifecycleActorAuthorization
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputRequestLifecycleActorAuthorization {{ Status = {Status}, OperationId = {OperationId}, RequestHash = {RequestHash}, EvaluatedAtUtc = {EvaluatedAtUtc:O} }}";
}
