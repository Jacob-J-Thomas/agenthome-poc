using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopAuthoringCatalog(
    string RoleId,
    LoopDefinitionSnapshot SystemDefault,
    IReadOnlyList<LoopDefinitionSnapshot> CustomDefinitions,
    LoopAuthoringLimits Limits,
    LoopToolCatalog Tools)
{
    public LoopRunModelSnapshot? RuntimeModel { get; init; }
}
