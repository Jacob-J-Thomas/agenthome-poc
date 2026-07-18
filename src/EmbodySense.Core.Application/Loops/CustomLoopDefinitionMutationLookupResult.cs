namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopDefinitionMutationLookupResult(CustomLoopDefinitionMutationLookupStatus Status, CustomLoopDefinitionMutationOperation? Operation)
{
    public static CustomLoopDefinitionMutationLookupResult NotFound() => new(CustomLoopDefinitionMutationLookupStatus.NotFound, null);

    public static CustomLoopDefinitionMutationLookupResult Found(CustomLoopDefinitionMutationOperation operation)
    {
        var status = operation.State == CustomLoopDefinitionMutationState.PendingMutation ? CustomLoopDefinitionMutationLookupStatus.PendingMutation : CustomLoopDefinitionMutationLookupStatus.OutcomeCommitted;
        return new CustomLoopDefinitionMutationLookupResult(status, operation);
    }
}
