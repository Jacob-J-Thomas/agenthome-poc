namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Classifies the exact retained evidence named by one sequential handler result.</summary>
public enum GovernedLoopSequentialNodeEvidenceKind
{
    /// <summary>No supported evidence kind was produced.</summary>
    Unknown = 0,
    /// <summary>Names definitive retained node-completion evidence.</summary>
    CompletedOutcome,
    /// <summary>Names definitive retained node-rejection evidence.</summary>
    DefinitiveRejection,
    /// <summary>Names retained ambiguity evidence requiring durable review attention.</summary>
    AmbiguityAttention,
    /// <summary>Names retained evidence that intentionally requested human review before dispatch or continuation.</summary>
    ReviewRequested,
}
