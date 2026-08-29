namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

/// <summary>Classifies one canonical Human Input continuation discovery page.</summary>
public enum HumanInputResponseContinuationRecoveryPageStatus
{
    /// <summary>The page was read and validated from canonical state.</summary>
    Current = 1,

    /// <summary>The request or persisted page shape was invalid.</summary>
    Invalid = 2,

    /// <summary>The canonical source could not be read safely.</summary>
    Unavailable = 3,
}
