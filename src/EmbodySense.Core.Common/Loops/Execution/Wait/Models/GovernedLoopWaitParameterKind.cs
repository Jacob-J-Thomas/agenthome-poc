namespace EmbodySense.Core.Common.Loops.Execution.Wait.Models;

/// <summary>Identifies the sole typed parameter carried by one admitted Wait descriptor.</summary>
public enum GovernedLoopWaitParameterKind
{
    /// <summary>The Wait becomes eligible at one exact canonical UTC instant.</summary>
    UtcTimestamp = 1,

    /// <summary>The Wait becomes eligible only after authenticating one exact governed event reference.</summary>
    AuthenticatedEventReference = 2
}
