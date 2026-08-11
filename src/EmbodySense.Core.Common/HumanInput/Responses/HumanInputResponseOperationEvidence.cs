namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseOperationEvidence
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputResponseOperationEvidence {{ SchemaVersion = {SchemaVersion}, OperationId = {OperationId}, CommandHash = {CommandHash}, Kind = {Kind}, Outcome = {Outcome}, FailureCode = {FailureCode}, Request = {Request}, RecordedAtUtc = {RecordedAtUtc:O}, PrivateAttribution = [REDACTED] }}";
}
