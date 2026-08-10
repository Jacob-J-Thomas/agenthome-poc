namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Classifies durable evidence completeness independently from an external effect outcome.</summary>
public enum GovernedLoopEffectEvidenceStatus
{
    /// <summary>No supported evidence posture was supplied.</summary>
    Unknown = 0,
    /// <summary>Required evidence is not yet complete.</summary>
    Pending,
    /// <summary>All evidence required by the current phase is durably retained.</summary>
    Complete,
    /// <summary>An outcome was retained but required audit or evidence completion failed.</summary>
    Incomplete,
    /// <summary>Retained observations conflict and require reconciliation.</summary>
    Conflicting
}
