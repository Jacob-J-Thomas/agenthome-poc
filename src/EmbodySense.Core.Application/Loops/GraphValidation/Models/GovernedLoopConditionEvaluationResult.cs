using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Reports one value-free deterministic Condition decision.</summary>
/// <param name="Status">The closed evaluation disposition.</param>
/// <param name="SelectedOutcome">The exact selected branch, or <see cref="GovernedLoopControlCondition.Unknown"/> on failure.</param>
/// <param name="ErrorCode">A stable value-free diagnostic code, or <see langword="null"/> on success.</param>
public sealed record GovernedLoopConditionEvaluationResult(
    GovernedLoopConditionEvaluationStatus Status,
    GovernedLoopControlCondition SelectedOutcome,
    string? ErrorCode);
