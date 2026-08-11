namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseActorAuthenticationRequest
{
    /// <inheritdoc />
    public override string ToString()
        => $"HumanInputResponseActorAuthenticationRequest {{ OperationId = {OperationId}, Kind = {Kind}, RequestId = {RequestId}, CommandHash = {CommandHash}, WorkspaceId = {WorkspaceId}, EvaluatedAtUtc = {EvaluatedAtUtc:O} }}";
}
