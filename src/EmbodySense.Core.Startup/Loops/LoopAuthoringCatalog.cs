namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopAuthoringCatalog(
    string RoleId,
    LoopDefinitionSnapshot SystemDefault,
    IReadOnlyList<LoopDefinitionSnapshot> CustomDefinitions,
    LoopAuthoringLimits Limits,
    LoopToolCatalog Tools);
