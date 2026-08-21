using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

internal sealed class GovernedLoopRevisionReferenceJsonConverter : JsonConverter<GovernedLoopRevisionReference>
{
    public override GovernedLoopRevisionReference Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var document = JsonSerializer.Deserialize<RevisionReferenceDocument>(ref reader, options)
            ?? throw new JsonException("A governed-loop revision reference is required.");
        try
        {
            return GovernedLoopRevisionReference.Create(
                document.SchemaVersion,
                document.GraphId ?? string.Empty,
                document.RevisionId ?? string.Empty,
                document.ExecutableHash ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The governed-loop revision reference is invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        GovernedLoopRevisionReference value,
        JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(
            writer,
            new RevisionReferenceDocument(
                value.SchemaVersion,
                value.GraphId,
                value.RevisionId,
                value.ExecutableHash),
            options);
    }

    private sealed record RevisionReferenceDocument(
        int SchemaVersion,
        string? GraphId,
        string? RevisionId,
        string? ExecutableHash);
}
