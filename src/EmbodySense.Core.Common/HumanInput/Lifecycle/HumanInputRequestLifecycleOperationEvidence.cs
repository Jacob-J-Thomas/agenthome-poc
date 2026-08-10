namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

public sealed partial record HumanInputRequestLifecycleOperationEvidence
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputRequestLifecycleOperationEvidence {{ SchemaVersion = {SchemaVersion}, OperationId = {OperationId}, RequestHash = {RequestHash}, Kind = {Kind}, Outcome = {Outcome}, FailureCode = {FailureCode}, TargetRequestId = {TargetRequestId}, RecordedAtUtc = {RecordedAtUtc:O} }}";
}
