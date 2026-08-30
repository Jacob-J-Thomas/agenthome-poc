using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Projects the typed authority-profile identity into the raw SHA-256 field used by a strict Human Review binding.</summary>
/// <remarks>The authority model deliberately prefixes profile identities with <c>sha256:</c>, while Human Review hash fields
/// are fixed-width raw SHA-256 digests. This projection retains exactly the typed profile's digest and rejects no alternative identity.</remarks>
internal static class HumanReviewAuthorityBindingHash
{
    internal static string FromProfile(AuthorityProfileHash profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Value["sha256:".Length..];
    }
}
