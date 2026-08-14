using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Returns the exact bounded data bindings materialized for one deterministic sequential node.</summary>
public sealed record GovernedLoopSequentialBindingResolutionResult(
    bool IsResolved,
    IReadOnlyList<GovernedLoopTypedBindingValue> Inputs,
    string? FailureCode,
    string? FailurePath);
