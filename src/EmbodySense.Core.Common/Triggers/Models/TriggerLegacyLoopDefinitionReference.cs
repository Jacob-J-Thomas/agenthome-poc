namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>Identifies one exact legacy custom-loop definition without authorizing its execution.</summary>
public sealed record TriggerLegacyLoopDefinitionReference
{
    internal TriggerLegacyLoopDefinitionReference(string loopId, int definitionVersion, string contentHash)
    {
        LoopId = loopId;
        DefinitionVersion = definitionVersion;
        ContentHash = contentHash;
    }

    /// <summary>Gets the stable custom-loop identifier.</summary>
    public string LoopId { get; }

    /// <summary>Gets the exact positive definition version.</summary>
    public int DefinitionVersion { get; }

    /// <summary>Gets the exact lowercase SHA-256 definition content hash.</summary>
    public string ContentHash { get; }
}
