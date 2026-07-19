using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopAuthoringCatalog(
    string RoleId,
    LoopDefinitionSnapshot SystemDefault,
    IReadOnlyList<LoopDefinitionSnapshot> CustomDefinitions,
    LoopAuthoringLimits Limits,
    LoopToolCatalog Tools)
{
    public LoopRunModelSnapshot? RuntimeModel { get; init; }
}
