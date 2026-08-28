namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Identifies schema-1 Human Input policy publication boundaries exposed for restart verification.</summary>
public enum HumanInputPolicyFileStorePublicationBoundary
{
    /// <summary>No publication boundary has been crossed.</summary>
    Unknown = 0,

    /// <summary>The exact immutable policy publication intent was published before the artifact.</summary>
    IntentPublished = 1,

    /// <summary>The exact immutable policy artifact was published before the catalog generation advances.</summary>
    ArtifactPublished = 2,

    /// <summary>The replacement catalog generation was published before the publication intent is retired.</summary>
    GenerationPublished = 3,

    /// <summary>The exact publication intent retirement completed after the committed generation.</summary>
    /// <remarks>POSIX retains directory ordering; Windows exact-retry orphan repair does not claim a portable directory-flush ordering guarantee.</remarks>
    PublicationIntentRetired = 4
}
