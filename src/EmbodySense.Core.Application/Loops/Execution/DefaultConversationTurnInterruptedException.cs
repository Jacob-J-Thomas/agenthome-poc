namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Simulates abrupt process loss for deterministic checkpoint and restart tests.
/// </summary>
public sealed class DefaultConversationTurnInterruptedException : Exception
{
    /// <summary>Initializes an interruption at the supplied evidence boundary.</summary>
    /// <param name="message">The interruption detail.</param>
    public DefaultConversationTurnInterruptedException(string message)
        : base(message)
    {
    }
}
