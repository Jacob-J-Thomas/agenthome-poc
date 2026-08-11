namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>Classifies durable canonical sequential-node attempt evidence.</summary>
public enum CustomLoopSequentialNodeEvidenceKind
{
    /// <summary>No supported evidence kind was retained.</summary>
    Unknown = 0,

    /// <summary>The irreversible dispatch boundary may have been crossed, but no terminal outcome is yet durable.</summary>
    DispatchStarted,

    /// <summary>A definitive successful node outcome is durable.</summary>
    CompletedOutcome,

    /// <summary>A definitive non-actuating rejection is durable.</summary>
    DefinitiveRejection,

    /// <summary>Ambiguous outcome evidence requiring durable attention is retained.</summary>
    AmbiguityAttention,
}
