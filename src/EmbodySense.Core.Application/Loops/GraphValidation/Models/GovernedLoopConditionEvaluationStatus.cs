namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Classifies one dependency-free deterministic Condition evaluation.</summary>
public enum GovernedLoopConditionEvaluationStatus
{
    /// <summary>No supported decision was produced.</summary>
    Unknown = 0,
    /// <summary>Exactly one legal branch outcome was selected.</summary>
    Selected,
    /// <summary>The descriptor, port, parameters, or value do not match the exact executable contract.</summary>
    InvalidContract,
    /// <summary>A model decision matched neither exact admitted branch value.</summary>
    InvalidDecision,
}
