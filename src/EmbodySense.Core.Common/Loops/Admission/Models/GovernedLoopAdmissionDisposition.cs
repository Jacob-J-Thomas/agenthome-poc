namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Classifies one terminal, non-reinterpretable admission disposition.</summary>
public enum GovernedLoopAdmissionDisposition
{
    /// <summary>No supported disposition is present.</summary>
    Unknown = 0,

    /// <summary>The exact immutable evidence was admitted.</summary>
    Admitted = 1,

    /// <summary>The prepared intent was definitively rejected by exact admission evidence.</summary>
    Rejected = 2
}
