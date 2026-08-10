namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseLifecycleCommand
{
    /// <inheritdoc />
    public override string ToString()
        => $"HumanInputResponseLifecycleCommand {{ SchemaVersion = {SchemaVersion}, OperationId = {OperationId}, Kind = {Kind}, RequestId = {RequestId}, CommandHash = {CommandHash} }}";
}
