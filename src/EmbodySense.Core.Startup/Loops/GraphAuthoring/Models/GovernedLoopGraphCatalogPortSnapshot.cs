namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Projects one exact server-cataloged typed port.</summary>
public sealed record GovernedLoopGraphCatalogPortSnapshot(
    string Id,
    string Direction,
    string BindingKind,
    IReadOnlyList<string> AllowedValueKinds,
    bool Required);
