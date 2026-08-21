namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Identifies the graph lifecycle mutations exposed to interface adapters.</summary>
public enum GovernedLoopGraphMutationKind
{
    /// <summary>Creates the first immutable draft.</summary>
    CreateDraft = 1,
    /// <summary>Creates an immutable successor draft.</summary>
    ReplaceDraft = 2,
    /// <summary>Publishes the exact current draft.</summary>
    Publish = 3,
    /// <summary>Disables the exact current publication.</summary>
    Disable = 4,
    /// <summary>Archives the exact current publication.</summary>
    Archive = 5,
}
