using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Evaluates the closed Condition vocabulary without persistence, provider access, authority widening, or ambient values.</summary>
public static class GovernedLoopConditionEvaluator
{
    /// <summary>Evaluates one exact Condition against one explicitly bound canonical typed value.</summary>
    /// <param name="node">The exact admitted Condition declaration.</param>
    /// <param name="value">The explicitly resolved canonical input value.</param>
    /// <returns>Exactly one True or False branch, or a value-free fail-closed result.</returns>
    public static GovernedLoopConditionEvaluationResult Evaluate(GovernedLoopNodeDefinition? node, GovernedLoopTypedValue? value)
    {
        if (node is null
            || value is null
            || !GovernedLoopTopologyNodeCatalogContract.TryResolve(node.Descriptor, out var contract)
            || contract is null
            || node.Descriptor.Kind != GovernedLoopNodeKind.Condition
            || node.AuthorityCeiling.CapabilityIds.Count != 0
            || node.Ports.Count != 1
            || !HasExactInputPort(node.Ports[0], contract.Ports[0], value.Kind)
            || !HasExactParameters(node, contract))
        {
            return Invalid(GovernedLoopConditionEvaluationStatus.InvalidContract, "condition.contract.invalid");
        }

        return node.Descriptor.TypeId switch
        {
            GovernedLoopTopologyNodeVocabulary.BooleanCondition => EvaluateBoolean(value),
            GovernedLoopTopologyNodeVocabulary.ExactTextCondition => EvaluateExactText(node, value),
            GovernedLoopTopologyNodeVocabulary.ModelDecisionCondition => EvaluateModelDecision(node, value),
            _ => Invalid(GovernedLoopConditionEvaluationStatus.InvalidContract, "condition.descriptor.unsupported"),
        };
    }

    private static bool HasExactInputPort(
        GovernedLoopPortDefinition actual,
        GovernedLoopCatalogPortContract expected,
        GovernedLoopValueKind valueKind)
        => string.Equals(actual.Id, expected.Id, StringComparison.Ordinal)
            && actual.Direction == GovernedLoopPortDirection.Input
            && actual.BindingKind == GovernedLoopBindingKind.Data
            && actual.Required
            && expected.Direction == actual.Direction
            && expected.BindingKind == actual.BindingKind
            && expected.Required
            && expected.AllowedValueKinds.Contains(valueKind);

    private static bool HasExactParameters(
        GovernedLoopNodeDefinition node,
        GovernedLoopNodeCatalogDescriptor contract)
    {
        var expected = contract.Parameters.ToDictionary(parameter => parameter.Id, StringComparer.Ordinal);
        if (contract.Parameters.Where(parameter => parameter.Required).Any(parameter => !node.Parameters.ContainsKey(parameter.Id)))
        {
            return false;
        }

        foreach (var parameter in node.Parameters)
        {
            if (!expected.TryGetValue(parameter.Key, out var parameterContract)
                || parameter.Value.Length < parameterContract.MinimumCharacters
                || parameter.Value.Length > parameterContract.MaximumCharacters
                || parameter.Value.Contains('\r', StringComparison.Ordinal)
                || parameter.Value.Length > 0 && (char.IsWhiteSpace(parameter.Value[0]) || char.IsWhiteSpace(parameter.Value[^1])))
            {
                return false;
            }

            if (parameterContract.ValueKind == GovernedLoopParameterValueKind.Integer
                && (!parameterContract.MinimumInteger.HasValue
                    || !parameterContract.MaximumInteger.HasValue
                    || !long.TryParse(parameter.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || !string.Equals(number.ToString(CultureInfo.InvariantCulture), parameter.Value, StringComparison.Ordinal)
                    || number < parameterContract.MinimumInteger.Value
                    || number > parameterContract.MaximumInteger.Value))
            {
                return false;
            }

            if ((parameterContract.ValueKind == GovernedLoopParameterValueKind.Text && !IsCanonicalText(parameter.Value))
                || parameterContract.ValueKind is not (GovernedLoopParameterValueKind.Text or GovernedLoopParameterValueKind.Integer))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCanonicalText(string value)
    {
        try
        {
            return value.IsNormalized(NormalizationForm.FormC)
                && !value.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control && rune.Value is not '\n' and not '\t'
                    || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static GovernedLoopConditionEvaluationResult EvaluateBoolean(GovernedLoopTypedValue value)
    {
        if (value.Kind != GovernedLoopValueKind.Boolean || value.IsNull)
        {
            return Invalid(GovernedLoopConditionEvaluationStatus.InvalidContract, "condition.value.kind");
        }

        return value.CanonicalValueJson switch
        {
            "true" => Selected(GovernedLoopControlCondition.True),
            "false" => Selected(GovernedLoopControlCondition.False),
            _ => Invalid(GovernedLoopConditionEvaluationStatus.InvalidContract, "condition.value.canonical"),
        };
    }

    private static GovernedLoopConditionEvaluationResult EvaluateExactText(GovernedLoopNodeDefinition node, GovernedLoopTypedValue value)
    {
        if (!TryReadText(value, out var actual)
            || !node.Parameters.TryGetValue(GovernedLoopTopologyNodeVocabulary.ExpectedParameter, out var expected))
        {
            return Invalid(GovernedLoopConditionEvaluationStatus.InvalidContract, "condition.value.kind");
        }

        return Selected(string.Equals(actual, expected, StringComparison.Ordinal)
            ? GovernedLoopControlCondition.True
            : GovernedLoopControlCondition.False);
    }

    private static GovernedLoopConditionEvaluationResult EvaluateModelDecision(GovernedLoopNodeDefinition node, GovernedLoopTypedValue value)
    {
        if (!TryReadText(value, out var actual)
            || !node.Parameters.TryGetValue(GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter, out var whenTrue)
            || !node.Parameters.TryGetValue(GovernedLoopTopologyNodeVocabulary.FalseDecisionParameter, out var whenFalse)
            || string.Equals(whenTrue, whenFalse, StringComparison.Ordinal))
        {
            return Invalid(GovernedLoopConditionEvaluationStatus.InvalidContract, "condition.decision.contract");
        }

        if (string.Equals(actual, whenTrue, StringComparison.Ordinal))
        {
            return Selected(GovernedLoopControlCondition.True);
        }

        return string.Equals(actual, whenFalse, StringComparison.Ordinal)
            ? Selected(GovernedLoopControlCondition.False)
            : Invalid(GovernedLoopConditionEvaluationStatus.InvalidDecision, "condition.decision.unrecognized");
    }

    private static bool TryReadText(GovernedLoopTypedValue value, out string text)
    {
        text = string.Empty;
        if (value.Kind != GovernedLoopValueKind.Text || value.IsNull)
        {
            return false;
        }

        try
        {
            text = JsonSerializer.Deserialize<string>(value.CanonicalValueJson)!;
            return text is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static GovernedLoopConditionEvaluationResult Selected(GovernedLoopControlCondition outcome)
        => new(GovernedLoopConditionEvaluationStatus.Selected, outcome, null);

    private static GovernedLoopConditionEvaluationResult Invalid(GovernedLoopConditionEvaluationStatus status, string code)
        => new(status, GovernedLoopControlCondition.Unknown, code);
}
