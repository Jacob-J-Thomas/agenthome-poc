using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Persistence.Inference.Profiles.Models;

internal sealed record ModelProfileMetadataRevision(
    int SchemaVersion,
    string ProfileId,
    long ProfileGeneration,
    string OperationId,
    string? ExpectedSourceRevisionHash,
    GovernedModelProfileMetadata Metadata,
    string? PreviousSourceRevisionHash,
    string SourceRevisionHash,
    bool AdvancesProfile = true);
