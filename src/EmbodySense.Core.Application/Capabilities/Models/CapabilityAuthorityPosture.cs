namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Describes the non-granting authority relationship of dependency evidence.</summary>
public enum CapabilityAuthorityPosture
{
    /// <summary>The evidence is metadata only and grants no runtime authority.</summary>
    MetadataOnly = 1,
    /// <summary>The evidence belongs to a persisted definition that separately owns assignment semantics.</summary>
    AssignedDefinition = 2,
    /// <summary>The evidence is retained historical provenance and is not executable authority.</summary>
    HistoricalEvidence = 3
}
