namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseLifecycleStoreMutation
{
    /// <inheritdoc />
    public override string ToString()
        => $"HumanInputResponseLifecycleStoreMutation {{ ExpectedStoreGeneration = {ExpectedStoreGeneration}, OperationId = {Operation.OperationId}, HasResponse = {ResponseToAppend is not null}, HasSelection = {SelectionToAppend is not null}, HasHead = {RequestHeadToWrite is not null} }}";
}
