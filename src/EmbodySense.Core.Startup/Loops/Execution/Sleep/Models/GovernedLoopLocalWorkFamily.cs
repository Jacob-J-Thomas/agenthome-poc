namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Identifies one bounded local background-work family.</summary>
public enum GovernedLoopLocalWorkFamily
{
    /// <summary>One durable schedule due-occurrence evaluation.</summary>
    Schedule = 1,

    /// <summary>One durable trigger-queue selection and dispatch.</summary>
    Trigger = 2,

    /// <summary>One durable wake delivery or prepared-wake reconciliation.</summary>
    Wake = 3,

    /// <summary>One durable Human Input response continuation recovery attempt.</summary>
    HumanInput = 4
}
