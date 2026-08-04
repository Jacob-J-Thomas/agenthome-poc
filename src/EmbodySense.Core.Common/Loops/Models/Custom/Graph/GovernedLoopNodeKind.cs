namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>
/// Classifies the schema-1 governed custom-loop node descriptors understood by the contract.
/// </summary>
/// <remarks>A defined kind identifies contract shape only; it does not assert that a runtime can execute the descriptor.</remarks>
public enum GovernedLoopNodeKind
{
    /// <summary>An undefined node kind.</summary>
    Unknown = 0,
    /// <summary>A loop entry trigger.</summary>
    Trigger,
    /// <summary>A model inference operation.</summary>
    Inference,
    /// <summary>A deterministic value transformation.</summary>
    Transform,
    /// <summary>A value or policy validation.</summary>
    Validate,
    /// <summary>An explicit state read or write.</summary>
    State,
    /// <summary>A conditional control decision.</summary>
    Condition,
    /// <summary>A control or value join.</summary>
    Join,
    /// <summary>A bounded wait point.</summary>
    Wait,
    /// <summary>A governed actuator invocation.</summary>
    Action,
    /// <summary>A human review gate.</summary>
    HumanReview,
    /// <summary>An explicit human input request.</summary>
    HumanInput,
    /// <summary>A governed child-loop invocation.</summary>
    ChildLoop,
    /// <summary>A successful terminal.</summary>
    Exit,
    /// <summary>A failed terminal.</summary>
    Fail
}
