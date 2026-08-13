namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Reports one bounded family attempt while subsystem evidence remains authoritative.</summary>
/// <param name="Status">The closed attempt outcome.</param>
/// <param name="ReasonCode">One bounded value-free reason code.</param>
public sealed record GovernedLoopLocalWorkResult(
    GovernedLoopLocalWorkResultStatus Status,
    string ReasonCode);
