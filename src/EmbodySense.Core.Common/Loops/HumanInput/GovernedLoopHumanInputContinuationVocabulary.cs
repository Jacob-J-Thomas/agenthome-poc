namespace EmbodySense.Core.Common.Loops.HumanInput;

/// <summary>Defines reserved identifiers used only by the canonical Human Input response-continuation bridge.</summary>
public static class GovernedLoopHumanInputContinuationVocabulary
{
    /// <summary>Gets the reserved authenticated-event reference prefix for durable Human Input response wakes.</summary>
    /// <remarks>Authored Wait conditions must reject this prefix so the generic continuation relay cannot misroute user-authored events.</remarks>
    public const string AuthenticatedEventReferencePrefix = "human-input-response-";
}
