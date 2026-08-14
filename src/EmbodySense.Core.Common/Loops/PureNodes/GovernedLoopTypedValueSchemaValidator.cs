using System.Globalization;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

internal static class GovernedLoopTypedValueSchemaValidator
{
    public static bool IsConformant(GovernedLoopGraphDefinition graph, GovernedLoopValueSchemaDefinition schema, GovernedLoopTypedValue value)
    {
        if (schema.Kind != value.Kind)
        {
            return false;
        }

        var schemas = graph.ValueSchemas.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (!HasValidTopology(schema, schemas, new HashSet<string>(StringComparer.Ordinal)))
        {
            return false;
        }

        using var document = JsonDocument.Parse(value.CanonicalValueJson);
        return IsConformant(document.RootElement, schema, schemas);
    }

    private static bool HasValidTopology(
        GovernedLoopValueSchemaDefinition schema,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        HashSet<string> active)
    {
        if (schema.Kind != GovernedLoopValueKind.Array)
        {
            return schema.ElementSchemaId is null;
        }

        if (schema.ElementSchemaId is null
            || !schemas.TryGetValue(schema.ElementSchemaId, out var elementSchema)
            || !active.Add(schema.Id))
        {
            return false;
        }

        var valid = HasValidTopology(elementSchema, schemas, active);
        active.Remove(schema.Id);
        return valid;
    }

    private static bool IsConformant(
        JsonElement element,
        GovernedLoopValueSchemaDefinition schema,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return schema.Nullable;
        }

        if (!MatchesKind(element, schema.Kind))
        {
            return false;
        }

        if (schema.Kind != GovernedLoopValueKind.Array)
        {
            return true;
        }

        var elementSchema = schemas[schema.ElementSchemaId!];
        return element.EnumerateArray().All(item => IsConformant(item, elementSchema, schemas));
    }

    private static bool MatchesKind(JsonElement element, GovernedLoopValueKind kind)
        => kind switch
        {
            GovernedLoopValueKind.Text => element.ValueKind == JsonValueKind.String,
            GovernedLoopValueKind.Boolean => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            GovernedLoopValueKind.Integer => element.ValueKind == JsonValueKind.Number
                && long.TryParse(element.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            GovernedLoopValueKind.Number => element.ValueKind == JsonValueKind.Number,
            GovernedLoopValueKind.Object => element.ValueKind == JsonValueKind.Object,
            GovernedLoopValueKind.Array => element.ValueKind == JsonValueKind.Array,
            _ => false
        };
}
