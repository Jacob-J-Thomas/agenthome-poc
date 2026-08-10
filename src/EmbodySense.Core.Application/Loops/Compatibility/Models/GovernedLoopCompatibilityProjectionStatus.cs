namespace EmbodySense.Core.Application.Loops.Compatibility.Models;

/// <summary>Classifies whether legacy evidence can honestly satisfy the canonical governed-execution contract.</summary>
public enum GovernedLoopCompatibilityProjectionStatus
{
    /// <summary>No projection classification exists.</summary>
    Unknown = 0,
    /// <summary>The source supplied one complete, valid, revision-bound canonical evidence set.</summary>
    Complete = 1,
    /// <summary>The source supplied valid unbound evidence plus explicit compatibility gaps.</summary>
    Partial = 2,
    /// <summary>The source was absent, malformed, or otherwise unsafe to project.</summary>
    Unsupported = 3
}
