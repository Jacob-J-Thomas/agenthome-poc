using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

public sealed class GovernedLoopConditionEvaluatorTests
{
    [Theory]
    [InlineData("true", GovernedLoopControlCondition.True)]
    [InlineData("false", GovernedLoopControlCondition.False)]
    public void Boolean_condition_selects_exactly_one_branch(string json, GovernedLoopControlCondition expected)
    {
        var result = GovernedLoopConditionEvaluator.Evaluate(BooleanCondition(), Value(GovernedLoopValueKind.Boolean, json));

        Assert.Equal(GovernedLoopConditionEvaluationStatus.Selected, result.Status);
        Assert.Equal(expected, result.SelectedOutcome);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData("match", GovernedLoopControlCondition.True)]
    [InlineData("other", GovernedLoopControlCondition.False)]
    public void Exact_text_condition_uses_ordinal_comparison(string text, GovernedLoopControlCondition expected)
    {
        var result = GovernedLoopConditionEvaluator.Evaluate(ExactTextCondition("match"), Text(text));

        Assert.Equal(GovernedLoopConditionEvaluationStatus.Selected, result.Status);
        Assert.Equal(expected, result.SelectedOutcome);
    }

    [Theory]
    [InlineData("approve", GovernedLoopControlCondition.True)]
    [InlineData("reject", GovernedLoopControlCondition.False)]
    public void Model_decision_accepts_only_two_exact_governed_tokens(string text, GovernedLoopControlCondition expected)
    {
        var result = GovernedLoopConditionEvaluator.Evaluate(ModelDecisionCondition(), Text(text));

        Assert.Equal(GovernedLoopConditionEvaluationStatus.Selected, result.Status);
        Assert.Equal(expected, result.SelectedOutcome);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(" approve ")]
    [InlineData("Approve")]
    public void Unrecognized_or_nonexact_model_decision_fails_closed_without_echo(string text)
    {
        var result = GovernedLoopConditionEvaluator.Evaluate(ModelDecisionCondition(), Text(text));

        Assert.Equal(GovernedLoopConditionEvaluationStatus.InvalidDecision, result.Status);
        Assert.Equal(GovernedLoopControlCondition.Unknown, result.SelectedOutcome);
        Assert.Equal("condition.decision.unrecognized", result.ErrorCode);
        Assert.DoesNotContain(text, result.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_port_parameter_value_and_null_substitutions_fail_closed()
    {
        var source = ModelDecisionCondition();
        var substitutions = new (GovernedLoopNodeDefinition? Node, GovernedLoopTypedValue? Value)[]
        {
            (source with { Descriptor = source.Descriptor with { Version = 2 } }, Text("approve")),
            (source with { Ports = [source.Ports[0] with { Id = "value" }] }, Text("approve")),
            (source with { Ports = [source.Ports[0] with { Direction = GovernedLoopPortDirection.Output }] }, Text("approve")),
            (source with { Parameters = new Dictionary<string, string>(source.Parameters, StringComparer.Ordinal) { ["other"] = "value" } }, Text("approve")),
            (source with { Parameters = source.Parameters.Where(parameter => parameter.Key != GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter).ToDictionary() }, Text("approve")),
            (source with { Parameters = new Dictionary<string, string>(source.Parameters, StringComparer.Ordinal) { [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "01" } }, Text("approve")),
            (source with { Parameters = new Dictionary<string, string>(source.Parameters, StringComparer.Ordinal) { [GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter] = "e\u0301" } }, Text("approve")),
            (source, Value(GovernedLoopValueKind.Boolean, "true")),
            (source, Value(GovernedLoopValueKind.Text, "null")),
            (null, Text("approve")),
            (source, null),
        };

        foreach (var (node, value) in substitutions)
        {
            var result = GovernedLoopConditionEvaluator.Evaluate(node, value);
            Assert.Equal(GovernedLoopConditionEvaluationStatus.InvalidContract, result.Status);
            Assert.Equal(GovernedLoopControlCondition.Unknown, result.SelectedOutcome);
            Assert.NotNull(result.ErrorCode);
        }
    }

    [Fact]
    public void Distinct_model_decision_tokens_are_mandatory()
    {
        var source = ModelDecisionCondition();
        var duplicate = source with
        {
            Parameters = new Dictionary<string, string>(source.Parameters, StringComparer.Ordinal)
            {
                [GovernedLoopTopologyNodeVocabulary.FalseDecisionParameter] = "approve"
            }
        };

        var result = GovernedLoopConditionEvaluator.Evaluate(duplicate, Text("approve"));

        Assert.Equal(GovernedLoopConditionEvaluationStatus.InvalidContract, result.Status);
        Assert.Equal("condition.decision.contract", result.ErrorCode);
    }

    private static GovernedLoopNodeDefinition BooleanCondition()
        => Condition(
            GovernedLoopSequentialNodeDescriptors.BooleanCondition,
            GovernedLoopTopologyNodeVocabulary.ValuePort,
            new Dictionary<string, string>());

    private static GovernedLoopNodeDefinition ExactTextCondition(string expected)
        => Condition(
            GovernedLoopSequentialNodeDescriptors.ExactTextCondition,
            GovernedLoopTopologyNodeVocabulary.ValuePort,
            new Dictionary<string, string> { [GovernedLoopTopologyNodeVocabulary.ExpectedParameter] = expected });

    private static GovernedLoopNodeDefinition ModelDecisionCondition()
        => Condition(
            GovernedLoopSequentialNodeDescriptors.ModelDecisionCondition,
            GovernedLoopTopologyNodeVocabulary.DecisionPort,
            new Dictionary<string, string>
            {
                [GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter] = "approve",
                [GovernedLoopTopologyNodeVocabulary.FalseDecisionParameter] = "reject",
            });

    private static GovernedLoopNodeDefinition Condition(
        GovernedLoopNodeDescriptor descriptor,
        string portId,
        IReadOnlyDictionary<string, string> parameters)
        => new(
            "condition",
            descriptor,
            [new GovernedLoopPortDefinition(portId, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "value", true)],
            GovernedLoopAuthorityCeiling.Create([]),
            parameters);

    private static GovernedLoopTypedValue Text(string text)
        => Value(GovernedLoopValueKind.Text, System.Text.Json.JsonSerializer.Serialize(text));

    private static GovernedLoopTypedValue Value(GovernedLoopValueKind kind, string json)
    {
        Assert.True(GovernedLoopTypedValue.TryCreate(1, kind, json, out var value, out var validation), string.Join(",", validation.Errors.Select(error => error.Code)));
        return value!;
    }
}
