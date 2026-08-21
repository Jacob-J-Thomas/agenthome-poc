namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Identifies one exact non-secret admission proof bound into canonical evidence.</summary>
public enum GovernedLoopAdmissionEvidenceKind
{
    /// <summary>No supported evidence kind is present.</summary>
    Unknown = 0,

    /// <summary>The exact contextual-role revision proof.</summary>
    ContextualRoleRevision = 1,

    /// <summary>The exact authority-grant revision proof.</summary>
    AuthorityGrant = 2,

    /// <summary>The exact loop-publication proof.</summary>
    LoopPublication = 3,

    /// <summary>The exact immutable graph-artifact proof.</summary>
    GraphArtifact = 4,

    /// <summary>The exact non-executable graph-layout proof.</summary>
    GraphLayout = 5,

    /// <summary>The exact effective authority-ceiling proof.</summary>
    EffectiveAuthority = 6,

    /// <summary>The exact capability-resolution snapshot proof.</summary>
    CapabilityAdmission = 7,

    /// <summary>The exact deterministic model-routing admission snapshot proof, including explicit empty routing.</summary>
    ModelRoutingAdmission = 8
}
