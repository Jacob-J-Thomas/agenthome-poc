using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Loops.InvocationPreparation;

internal sealed record InvocationPreparationModelTerms(bool IsEligible, bool IsUnavailable, CapabilityId? ProfileId, string? SourceRevisionHash, GovernedModelProfileMetadata? Metadata, string Detail)
{
    public static InvocationPreparationModelTerms Eligible(CapabilityId profileId, string sourceRevisionHash, GovernedModelProfileMetadata metadata)
        => new(true, false, profileId, sourceRevisionHash, metadata, string.Empty);

    public static InvocationPreparationModelTerms Ineligible(string detail) => new(false, false, null, null, null, detail);

    public static InvocationPreparationModelTerms Unavailable(string detail) => new(false, true, null, null, null, detail);
}
