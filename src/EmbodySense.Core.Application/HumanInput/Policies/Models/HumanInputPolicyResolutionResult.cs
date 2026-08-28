using EmbodySense.Core.Common.Loops.HumanInput.Policies;

namespace EmbodySense.Core.Application.HumanInput.Policies.Models;

/// <summary>Returns a checkpoint-bindable Human Input policy snapshot only after exact fail-closed resolution.</summary>
/// <param name="Status">The closed resolution status.</param>
/// <param name="Snapshot">The exact trusted-time snapshot only when <paramref name="Status"/> is <see cref="HumanInputPolicyResolutionStatus.Resolved"/>.</param>
public sealed record HumanInputPolicyResolutionResult(HumanInputPolicyResolutionStatus Status, HumanInputPolicyResolutionSnapshot? Snapshot);
