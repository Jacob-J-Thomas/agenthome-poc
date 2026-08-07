namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Identifies a schema-1 control-flow edge condition.</summary>
public enum GovernedLoopControlCondition
{
    /// <summary>An undefined condition.</summary>
    Unknown = 0,
    /// <summary>Unconditional control flow.</summary>
    Always,
    /// <summary>Control flow after success.</summary>
    Success,
    /// <summary>Control flow after failure.</summary>
    Failure,
    /// <summary>Control flow after a true condition.</summary>
    True,
    /// <summary>Control flow after a false condition.</summary>
    False,
    /// <summary>Control flow after a wait expires.</summary>
    Timeout,
    /// <summary>Control flow after approval.</summary>
    Approved,
    /// <summary>Control flow after rejection.</summary>
    Rejected
}
