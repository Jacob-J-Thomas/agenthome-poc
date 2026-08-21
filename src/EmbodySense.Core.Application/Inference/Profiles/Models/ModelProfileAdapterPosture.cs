namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Preserves safe exact adapter registration evidence without a client, endpoint, executable, or secret.</summary>
/// <param name="Status">The current structured posture.</param>
/// <param name="ProfileMetadataHash">The exact metadata hash checked by the registry.</param>
/// <param name="RegistryRevisionHash">The exact safe registry revision hash.</param>
public sealed record ModelProfileAdapterPosture(ModelProfileAdapterPostureStatus Status, string ProfileMetadataHash, string RegistryRevisionHash);
