namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Configures bounded schema-1 Human Input policy artifact persistence.</summary>
public sealed class HumanInputPolicyFileStoreOptions
{
    /// <summary>Gets the maximum number of immutable policy artifacts retained by one workspace source.</summary>
    public int MaximumArtifacts { get; init; } = 128;

    /// <summary>Gets the maximum UTF-8 bytes accepted for one canonical policy artifact.</summary>
    public int MaximumArtifactUtf8Bytes { get; init; } = 16 * 1024;
}
