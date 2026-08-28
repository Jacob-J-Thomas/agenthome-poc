using EmbodySense.Core.Common.Loops.HumanInput.Policies;

namespace EmbodySense.Core.Application.HumanInput.Policies.Models;

/// <summary>Returns the safe result of one exact Human Input policy source lookup.</summary>
/// <param name="Status">The closed lookup status.</param>
/// <param name="Policy">The detached exact policy revision only when <paramref name="Status"/> is <see cref="HumanInputPolicySourceReadStatus.Ready"/>.</param>
/// <param name="StoreGeneration">The monotonic source generation observed by the lookup, or zero when unavailable.</param>
public sealed record HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus Status, HumanInputPolicyArtifact? Policy, long StoreGeneration);
