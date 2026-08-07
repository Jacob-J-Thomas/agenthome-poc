namespace EmbodySense.Core.Common.Credentials.Models;

internal sealed record ReferenceDto(int SchemaVersion, string Id, string Type, string Status, string OwnerId, string Purpose, string ProviderId, string CreatedAtUtc, string UpdatedAtUtc, string? ExpiresAtUtc, SortedDictionary<string, string> Metadata);
