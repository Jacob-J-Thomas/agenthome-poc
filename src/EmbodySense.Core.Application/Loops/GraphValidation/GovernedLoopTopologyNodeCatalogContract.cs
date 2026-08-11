using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Defines the closed executable schema-1 catalog contract for deterministic Conditions and Joins.</summary>
public static class GovernedLoopTopologyNodeCatalogContract
{
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _branches =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.True, GovernedLoopControlCondition.False });
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _success =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Success });
    private static readonly IReadOnlyList<string> _noCapabilities = Array.Empty<string>();
    private static readonly GovernedLoopValueKindSet _booleanKind = GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Boolean]);
    private static readonly GovernedLoopValueKindSet _textKind = GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Text]);
    private static readonly IReadOnlyList<GovernedLoopNodeCatalogDescriptor> _descriptors = CreateDescriptors();

    /// <summary>Gets the six exact descriptor declarations in canonical descriptor-key order.</summary>
    public static IReadOnlyList<GovernedLoopNodeCatalogDescriptor> Descriptors => _descriptors;

    /// <summary>Resolves one exact topology descriptor declaration without aliases or fallback.</summary>
    public static bool TryResolve(GovernedLoopNodeDescriptor? descriptor, out GovernedLoopNodeCatalogDescriptor? contract)
    {
        contract = descriptor is null
            ? null
            : _descriptors.SingleOrDefault(candidate => Equals(candidate.Descriptor, descriptor));
        return contract is not null;
    }

    internal static bool HasExactSchemaSemantics(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
    {
        if (!TryResolve(node.Descriptor, out var contract)
            || contract is null
            || node.Ports.Count != contract.Ports.Count)
        {
            return false;
        }

        if (node.Descriptor.Kind == GovernedLoopNodeKind.Join)
        {
            return node.Ports.Count == 0 && node.Parameters.Count == 0;
        }

        var input = node.Ports.SingleOrDefault();
        if (input is null
            || !schemas.TryGetValue(input.ValueSchemaId, out var schema)
            || schema.Nullable
            || schema.Format is not null
            || schema.ElementSchemaId is not null)
        {
            return false;
        }

        return node.Descriptor.TypeId switch
        {
            GovernedLoopTopologyNodeVocabulary.BooleanCondition => schema.Kind == GovernedLoopValueKind.Boolean,
            GovernedLoopTopologyNodeVocabulary.ExactTextCondition => schema.Kind == GovernedLoopValueKind.Text,
            GovernedLoopTopologyNodeVocabulary.ModelDecisionCondition => schema.Kind == GovernedLoopValueKind.Text
                && node.Parameters.TryGetValue(GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter, out var whenTrue)
                && node.Parameters.TryGetValue(GovernedLoopTopologyNodeVocabulary.FalseDecisionParameter, out var whenFalse)
                && !string.Equals(whenTrue, whenFalse, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static IReadOnlyList<GovernedLoopNodeCatalogDescriptor> CreateDescriptors()
    {
        var descriptors = new[]
        {
            Condition(
                GovernedLoopTopologyNodeVocabulary.BooleanCondition,
                Input(GovernedLoopTopologyNodeVocabulary.ValuePort, _booleanKind),
                []),
            Condition(
                GovernedLoopTopologyNodeVocabulary.ExactTextCondition,
                Input(GovernedLoopTopologyNodeVocabulary.ValuePort, _textKind),
                [TextParameter(GovernedLoopTopologyNodeVocabulary.ExpectedParameter)]),
            Condition(
                GovernedLoopTopologyNodeVocabulary.ModelDecisionCondition,
                Input(GovernedLoopTopologyNodeVocabulary.DecisionPort, _textKind),
                [TextParameter(GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter), TextParameter(GovernedLoopTopologyNodeVocabulary.FalseDecisionParameter)]),
            Join(GovernedLoopTopologyNodeVocabulary.AllJoin, GovernedLoopJoinPolicy.All),
            Join(GovernedLoopTopologyNodeVocabulary.AnyJoin, GovernedLoopJoinPolicy.Any),
            Join(GovernedLoopTopologyNodeVocabulary.SelectedJoin, GovernedLoopJoinPolicy.Selected),
        };
        return Array.AsReadOnly(descriptors.OrderBy(DescriptorKey, StringComparer.Ordinal).ToArray());
    }

    private static GovernedLoopNodeCatalogDescriptor Condition(
        string typeId,
        GovernedLoopCatalogPortContract input,
        GovernedLoopCatalogParameterContract[] parameters)
        => new(
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Condition, typeId, GovernedLoopTopologyNodeVocabulary.DescriptorVersion),
            IsAdvertised: true,
            IsExecutable: true,
            IsLegalEntry: false,
            IsLegalTerminal: false,
            _branches,
            _branches,
            GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges: 1,
            AllowsCycle: true,
            GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter,
            GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter,
            Array.AsReadOnly(new[] { input }),
            Array.AsReadOnly(parameters.Concat(CycleBudgetParameters()).OrderBy(parameter => parameter.Id, StringComparer.Ordinal).ToArray()),
            _noCapabilities,
            new GovernedLoopNodeResourceBudget(1, CustomLoopLimits.MaxGraphNodePayloadCharacters, 1, 0));

    private static GovernedLoopNodeCatalogDescriptor Join(string typeId, GovernedLoopJoinPolicy joinPolicy)
        => new(
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, typeId, GovernedLoopTopologyNodeVocabulary.DescriptorVersion),
            IsAdvertised: true,
            IsExecutable: true,
            IsLegalEntry: false,
            IsLegalTerminal: false,
            _success,
            _success,
            joinPolicy,
            MinimumIncomingControlEdges: 2,
            AllowsCycle: false,
            null,
            null,
            Array.Empty<GovernedLoopCatalogPortContract>(),
            Array.Empty<GovernedLoopCatalogParameterContract>(),
            _noCapabilities,
            new GovernedLoopNodeResourceBudget(1, 0, 1, 0));

    private static GovernedLoopCatalogPortContract Input(string id, GovernedLoopValueKindSet kinds)
        => new(id, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, kinds, Required: true);

    private static GovernedLoopCatalogParameterContract TextParameter(string id)
        => new(id, GovernedLoopParameterValueKind.Text, Required: true, 1, CustomLoopLimits.MaxGraphParameterValueCharacters, null, null, Array.Empty<string>());

    private static GovernedLoopCatalogParameterContract[] CycleBudgetParameters()
        =>
        [
            new GovernedLoopCatalogParameterContract(GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter, GovernedLoopParameterValueKind.Integer, Required: false, 1, 5, 1, CustomLoopLimits.MaxGraphCycleIterations, Array.Empty<string>()),
            new GovernedLoopCatalogParameterContract(GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter, GovernedLoopParameterValueKind.Integer, Required: false, 1, 8, 1, CustomLoopLimits.MaxGraphCycleMilliseconds, Array.Empty<string>()),
        ];

    private static string DescriptorKey(GovernedLoopNodeCatalogDescriptor descriptor)
        => $"{(int)descriptor.Descriptor.Kind:D3}:{descriptor.Descriptor.TypeId}:{descriptor.Descriptor.Version:D10}";
}
