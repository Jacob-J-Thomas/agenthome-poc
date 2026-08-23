namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Identifies whether a provider supplied authoritative usage for one dimension.</summary>
public enum GovernedModelUsageEvidenceStatus
{
    /// <summary>The provider explicitly supplied no authoritative value.</summary>
    Unavailable = 1,
    /// <summary>The provider supplied an authoritative value.</summary>
    Authoritative = 2
}
