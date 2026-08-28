using EmbodySense.Core.Common.Loops.HumanInput.Policies;

namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Captures the exact immutable policy and source generation that one recoverable publication must advance.</summary>
internal sealed record HumanInputPolicyFileStorePublicationIntent(long ExpectedStoreGeneration, HumanInputPolicyArtifact Artifact);
