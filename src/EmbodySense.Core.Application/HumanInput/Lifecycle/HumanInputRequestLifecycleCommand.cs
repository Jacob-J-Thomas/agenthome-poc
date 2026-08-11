namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

public sealed partial record HumanInputRequestLifecycleCommand
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputRequestLifecycleCommand {{ SchemaVersion = {SchemaVersion}, OperationId = {OperationId}, Kind = {Kind}, RequestId = {RequestId}, ExpectedLifecycleVersion = {ExpectedLifecycleVersion}, ExpectedLifecycleStatus = {ExpectedLifecycleStatus}, RequestHash = {RequestHash} }}";
}
