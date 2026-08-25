using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Loops.InvocationPreparation;

internal sealed record InvocationPreparationModelTerms(bool IsEligible, bool IsUnavailable, CapabilityId? ProfileId, string? SourceRevisionHash, GovernedModelProfileMetadata? Metadata, string? AdapterRegistryRevisionHash, string Detail)
{
    public static InvocationPreparationModelTerms Eligible(CapabilityId? profileId, string? sourceRevisionHash, GovernedModelProfileMetadata? metadata, string? adapterRegistryRevisionHash)
        => new(true, false, profileId, sourceRevisionHash, metadata, adapterRegistryRevisionHash, string.Empty);

    public static InvocationPreparationModelTerms Ineligible(string detail) => new(false, false, null, null, null, null, detail);

    public static InvocationPreparationModelTerms Unavailable(string detail) => new(false, true, null, null, null, null, detail);
}
