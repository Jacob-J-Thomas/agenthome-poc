using EmbodySense.Core.Startup.ContextualRoles.Models;

namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Returns the exact executable node catalog and safe current role choices used for authoring.</summary>
public sealed record GovernedLoopGraphCatalogResponse(
    string Status,
    string SourceEvidenceId,
    IReadOnlyList<GovernedLoopGraphCatalogNodeSnapshot> NodeDescriptors,
    ContextualRoleCatalogResponse Roles);
