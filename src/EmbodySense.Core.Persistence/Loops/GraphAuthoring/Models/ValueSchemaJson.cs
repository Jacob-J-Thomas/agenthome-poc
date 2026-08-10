namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record ValueSchemaJson(
    string? Id,
    string? Kind,
    bool Nullable,
    string? Format,
    string? ElementSchemaId);
