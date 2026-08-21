namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Identifies the conclusive external result represented by retained command evidence.</summary>
public enum CommandActionNativeOutcomeKind
{
    /// <summary>The registered command completed successfully.</summary>
    Succeeded = 1,
    /// <summary>The registered command completed with one conclusive closed failure.</summary>
    Failed = 2,
}
