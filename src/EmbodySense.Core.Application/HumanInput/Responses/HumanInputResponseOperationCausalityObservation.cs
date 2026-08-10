namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseOperationCausalityObservation
{
    /// <inheritdoc />
    public override string ToString()
        => $"HumanInputResponseOperationCausalityObservation {{ OperationId = {Evidence?.OperationId}, HasSnapshot = {Snapshot is not null} }}";
}
