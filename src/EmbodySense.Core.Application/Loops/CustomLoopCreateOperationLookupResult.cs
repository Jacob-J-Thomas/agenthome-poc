using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopCreateOperationLookupResult(
    CustomLoopCreateOperationLookupStatus Status,
    CustomLoopDefinition? Definition,
    CustomLoopOperationIntegrity OperationIntegrity)
{
    public static CustomLoopCreateOperationLookupResult NotFound() => new(CustomLoopCreateOperationLookupStatus.NotFound, null, CustomLoopOperationIntegrity.NotTracked);

    public static CustomLoopCreateOperationLookupResult PendingDefinitionCommit(CustomLoopDefinition definition) => new(CustomLoopCreateOperationLookupStatus.PendingDefinitionCommit, definition, CustomLoopOperationIntegrity.PendingOutcomeAudit);

    public static CustomLoopCreateOperationLookupResult Committed(CustomLoopDefinition definition, CustomLoopOperationIntegrity integrity) => new(CustomLoopCreateOperationLookupStatus.Committed, definition, integrity);
}
