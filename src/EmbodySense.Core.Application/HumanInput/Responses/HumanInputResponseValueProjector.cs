using System.Text.Json;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.HumanInput.Responses;

/// <summary>Projects one validated Human Input response value into its exact data-only graph value without retaining the response envelope or attribution.</summary>
public static class HumanInputResponseValueProjector
{
    /// <summary>Projects one exact response according to its authored response schema.</summary>
    /// <param name="schema">The exact authored response schema retained by the checkpoint.</param>
    /// <param name="response">The authenticated response value to project.</param>
    /// <param name="value">The bounded canonical graph value when projection succeeds.</param>
    /// <returns><see langword="true"/> only when the response shape maps exactly to the schema-1 Human Input output contract.</returns>
    /// <remarks>Text, Choice, and Reference project to strings; Confirmation projects to a Boolean; Structured projects to a field-ID-keyed object in authored field order. This method never serializes an enclosing response, selection, actor, or attribution record.</remarks>
    public static bool TryProject(
        HumanInputResponseSchema? schema,
        HumanInputResponseValue? response,
        out GovernedLoopTypedValue? value)
    {
        value = null;
        if (schema is null || response is null || response.Kind != schema.Kind)
        {
            return false;
        }

        string? valueJson;
        GovernedLoopValueKind kind;
        switch (response.Kind)
        {
            case HumanInputResponseKind.Text when response.Text is not null:
                kind = GovernedLoopValueKind.Text;
                valueJson = JsonSerializer.Serialize(response.Text);
                break;
            case HumanInputResponseKind.Choice when response.ChoiceId is not null:
                kind = GovernedLoopValueKind.Text;
                valueJson = JsonSerializer.Serialize(response.ChoiceId);
                break;
            case HumanInputResponseKind.Confirmation when response.Confirmation is not null:
                kind = GovernedLoopValueKind.Boolean;
                valueJson = response.Confirmation.Value ? "true" : "false";
                break;
            case HumanInputResponseKind.Reference when response.Reference is not null:
                kind = GovernedLoopValueKind.Text;
                valueJson = JsonSerializer.Serialize(response.Reference.Value);
                break;
            case HumanInputResponseKind.Structured:
                kind = GovernedLoopValueKind.Object;
                if (!TryProjectStructured(schema.StructuredFields, response.StructuredFields, out valueJson))
                {
                    return false;
                }
                break;
            default:
                return false;
        }

        return GovernedLoopTypedValue.TryCreate(GovernedLoopTypedValue.CurrentSchemaVersion, kind, valueJson, out value, out _);
    }

    private static bool TryProjectStructured(
        HumanInputStructuredFieldSchema[]? schemaFields,
        System.Collections.Immutable.ImmutableArray<HumanInputStructuredFieldValue>? submittedFields,
        out string? valueJson)
    {
        valueJson = null;
        if (schemaFields is null
            || submittedFields is not { } values
            || values.IsDefault
            || schemaFields.Length == 0
            || values.Length > schemaFields.Length)
        {
            return false;
        }

        var remaining = values.ToDictionary(field => field.FieldId, StringComparer.Ordinal);
        if (remaining.Count != values.Length)
        {
            return false;
        }

        var projected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var schema in schemaFields)
        {
            if (schema is null || !remaining.Remove(schema.FieldId, out var submitted))
            {
                if (schema?.Required == true)
                {
                    return false;
                }
                continue;
            }

            var projectedValue = schema.Kind switch
            {
                HumanInputStructuredFieldKind.Text when submitted.Text is not null && submitted.ChoiceId is null => submitted.Text,
                HumanInputStructuredFieldKind.Choice when submitted.ChoiceId is not null && submitted.Text is null => submitted.ChoiceId,
                _ => null,
            };
            if (projectedValue is null)
            {
                return false;
            }

            projected.Add(schema.FieldId, projectedValue);
        }

        if (remaining.Count != 0)
        {
            return false;
        }

        valueJson = JsonSerializer.Serialize(projected);
        return true;
    }
}
