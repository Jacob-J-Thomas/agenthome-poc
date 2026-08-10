namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseActorAuthentication
{
    /// <inheritdoc />
    public override string ToString()
        => $"HumanInputResponseActorAuthentication {{ Status = {Status}, OperationId = {OperationId}, CommandHash = {CommandHash}, WorkspaceId = {WorkspaceId}, EvaluatedAtUtc = {EvaluatedAtUtc:O}, HasActor = {ActorId is not null}, HasEvidence = {!string.IsNullOrEmpty(AuthenticationEvidenceHash)} }}";
}
