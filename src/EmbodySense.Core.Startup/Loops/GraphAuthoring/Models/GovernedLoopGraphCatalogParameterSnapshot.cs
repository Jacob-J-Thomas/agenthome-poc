namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Projects one exact server-cataloged executable parameter contract.</summary>
public sealed record GovernedLoopGraphCatalogParameterSnapshot(
    string Id,
    string ValueKind,
    bool Required,
    int MinimumCharacters,
    int MaximumCharacters,
    long? MinimumInteger,
    long? MaximumInteger,
    IReadOnlyList<string> AllowedValues);
