namespace EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

/// <summary>Identifies the bounded server-side preparation outcome for one selected graph revision.</summary>
public enum GovernedLoopInvocationPreparationStatus
{
    /// <summary>The status is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>One or more currently eligible exact grants are available.</summary>
    Ready = 1,
    /// <summary>No eligible exact grant exists and a least-authority confirmation preview is available.</summary>
    ConfirmationRequired = 2,
    /// <summary>The supplied object selector is malformed.</summary>
    Invalid = 3,
    /// <summary>The selected graph or revision does not exist.</summary>
    NotFound = 4,
    /// <summary>The selected revision is no longer the current published revision.</summary>
    Stale = 5,
    /// <summary>Current policy or implemented capability evidence cannot support least-authority preparation.</summary>
    Ineligible = 6,
    /// <summary>Required current authority evidence is unavailable or ambiguous.</summary>
    Unavailable = 7,
}
