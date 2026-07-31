using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Provides the complete authoring surface for one persisted system role.
/// </summary>
/// <param name="RoleId">The authoritative system role identifier.</param>
/// <param name="SystemDefault">The read-only default-conversation definition projected into the authoring shape.</param>
/// <param name="CustomDefinitions">Persisted custom definitions visible to the role.</param>
/// <param name="Limits">Effective validation, execution, governance, and retention limits.</param>
/// <param name="Tools">The role-derived assignable tool catalog and custom-loop authority ceiling.</param>
public sealed record LoopAuthoringCatalog(
    string RoleId,
    LoopDefinitionSnapshot SystemDefault,
    IReadOnlyList<LoopDefinitionSnapshot> CustomDefinitions,
    LoopAuthoringLimits Limits,
    LoopToolCatalog Tools)
{
    /// <summary>
    /// Gets the optional provider/model projection supplied by the hosting interface.
    /// </summary>
    public LoopRunModelSnapshot? RuntimeModel { get; init; }
}
