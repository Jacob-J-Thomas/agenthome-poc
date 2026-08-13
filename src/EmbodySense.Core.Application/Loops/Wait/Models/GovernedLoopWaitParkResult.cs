using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Wait.Models;

/// <summary>Returns one bounded executable Wait park outcome.</summary>
/// <param name="Status">The closed park posture.</param>
/// <param name="Evidence">The exact committed or replayed park evidence.</param>
/// <param name="Run">The exact canonical run observed after parking or reconciliation.</param>
/// <param name="Detail">A bounded value-free result detail.</param>
public sealed record GovernedLoopWaitParkResult(
    GovernedLoopWaitParkResultStatus Status,
    GovernedLoopWaitParkEvidence? Evidence = null,
    CustomLoopRunRecord? Run = null,
    string? Detail = null);
