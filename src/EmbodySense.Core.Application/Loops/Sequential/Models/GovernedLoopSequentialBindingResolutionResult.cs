using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Returns the exact bounded data bindings materialized for one deterministic sequential node.</summary>
public sealed record GovernedLoopSequentialBindingResolutionResult(
    bool IsResolved,
    IReadOnlyList<GovernedLoopTypedBindingValue> Inputs,
    string? FailureCode,
    string? FailurePath)
{
    /// <summary>Creates one successful immutable input snapshot.</summary>
    internal static GovernedLoopSequentialBindingResolutionResult Resolved(GovernedLoopTypedBindingValue[] inputs)
        => new(true, Array.AsReadOnly(inputs), null, null);

    /// <summary>Creates one closed, value-free resolution rejection.</summary>
    internal static GovernedLoopSequentialBindingResolutionResult Rejected(string code, string path)
        => new(false, Array.Empty<GovernedLoopTypedBindingValue>(), code, path);
}
