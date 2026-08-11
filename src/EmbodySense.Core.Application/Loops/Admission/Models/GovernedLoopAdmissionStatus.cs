namespace EmbodySense.Core.Application.Loops.Admission.Models;

/// <summary>Identifies one closed governed-loop admission application disposition.</summary>
public enum GovernedLoopAdmissionStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,

    /// <summary>A new immutable admitted terminal outcome committed durably.</summary>
    Admitted = 1,

    /// <summary>The exact request replayed its previously committed immutable outcome.</summary>
    Replayed = 2,

    /// <summary>A new immutable definitive rejection committed durably.</summary>
    Rejected = 3,

    /// <summary>The workspace-global operation identity is already bound to different caller-stable intent.</summary>
    Conflict = 4,

    /// <summary>The request failed bounded contract validation before durable intent.</summary>
    Invalid = 5,

    /// <summary>No durable intent began because a required exact dependency was unavailable.</summary>
    Unavailable = 6,

    /// <summary>Available evidence cannot prove one safe admission outcome.</summary>
    Ambiguous = 7
}
