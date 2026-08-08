namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Classifies one dependency-resolution observation without granting authority.</summary>
public enum CapabilityDependencyResolutionOutcome
{
    /// <summary>The dependency resolved to an exact catalog pin.</summary>
    Selected = 1,

    /// <summary>An unavailable optional dependency was visibly omitted.</summary>
    OmittedOptional = 2,

    /// <summary>No declared catalog candidate exists for the exact capability identity.</summary>
    Missing = 3,

    /// <summary>Catalog candidates exist but none satisfies every declared range.</summary>
    Incompatible = 4,

    /// <summary>Candidate provenance or exact pins are ambiguous or conflicting.</summary>
    Conflict = 5,

    /// <summary>The dependency graph contains a cycle.</summary>
    Cyclic = 6,

    /// <summary>A candidate is not server-verified or has mismatched integrity evidence.</summary>
    Untrusted = 7,

    /// <summary>A bounded resolver limit was exceeded.</summary>
    LimitExceeded = 8,

    /// <summary>A manifest or candidate violates its closed contract.</summary>
    Invalid = 9
}
