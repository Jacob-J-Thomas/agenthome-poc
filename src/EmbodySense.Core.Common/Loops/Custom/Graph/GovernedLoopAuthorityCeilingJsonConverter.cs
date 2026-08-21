using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

internal sealed class GovernedLoopAuthorityCeilingJsonConverter : JsonConverter<GovernedLoopAuthorityCeiling>
{
    public override GovernedLoopAuthorityCeiling Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var document = JsonSerializer.Deserialize<AuthorityCeilingDocument>(ref reader, options)
            ?? throw new JsonException("An authority ceiling object is required.");
        if (document.CapabilityIds is null)
        {
            throw new JsonException("Authority ceiling capabilityIds are required.");
        }

        try
        {
            return GovernedLoopAuthorityCeiling.Create(document.CapabilityIds);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Authority ceiling capabilityIds are invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        GovernedLoopAuthorityCeiling value,
        JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new AuthorityCeilingDocument(value.CapabilityIds), options);
    }

    private sealed record AuthorityCeilingDocument(IReadOnlyList<string>? CapabilityIds);
}
