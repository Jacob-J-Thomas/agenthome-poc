namespace EmbodySense.Core.Persistence.Inference.Profiles.Models;

internal sealed record ModelProfileMetadataCurrentPointer(string ProfileId, long ProfileGeneration, string SourceRevisionHash);
