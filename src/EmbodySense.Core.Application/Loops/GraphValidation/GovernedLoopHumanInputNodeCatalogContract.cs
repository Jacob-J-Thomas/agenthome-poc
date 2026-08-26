using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Defines the closed executable catalog contract for the data-only schema-1 Human Input graph node.</summary>
public static class GovernedLoopHumanInputNodeCatalogContract
{
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _success =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Success });
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _successFailure =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure });
    private static readonly GovernedLoopValueKindSet _responseKinds = GovernedLoopValueKindSet.Create(
        [GovernedLoopValueKind.Text, GovernedLoopValueKind.Boolean, GovernedLoopValueKind.Object]);
    private static readonly GovernedLoopNodeCatalogDescriptor _descriptor = CreateDescriptor();

    /// <summary>Gets the exact Human Input descriptor declaration.</summary>
    public static GovernedLoopNodeCatalogDescriptor Descriptor => _descriptor;

    /// <summary>Resolves the one exact Human Input descriptor without aliases or fallback.</summary>
    /// <param name="descriptor">The descriptor to resolve.</param>
    /// <param name="contract">The canonical descriptor when resolution succeeds.</param>
    /// <returns><see langword="true"/> only for the exact schema-1 Human Input descriptor.</returns>
    public static bool TryResolve(GovernedLoopNodeDescriptor? descriptor, out GovernedLoopNodeCatalogDescriptor? contract)
    {
        contract = GovernedLoopHumanInputVocabulary.IsSupported(descriptor) ? _descriptor : null;
        return contract is not null;
    }

    /// <summary>Gets whether a catalog entry retains every reserved Human Input descriptor semantic.</summary>
    /// <param name="candidate">The catalog entry to compare.</param>
    /// <returns><see langword="true"/> only for an exact semantic match.</returns>
    public static bool HasExactCatalogSemantics(GovernedLoopNodeCatalogDescriptor? candidate)
        => candidate is not null
            && Equals(candidate.Descriptor, _descriptor.Descriptor)
            && candidate.IsAdvertised == _descriptor.IsAdvertised
            && candidate.IsExecutable == _descriptor.IsExecutable
            && candidate.IsLegalEntry == _descriptor.IsLegalEntry
            && candidate.IsLegalTerminal == _descriptor.IsLegalTerminal
            && candidate.AllowedControlOutcomes.SequenceEqual(_descriptor.AllowedControlOutcomes)
            && candidate.RequiredControlOutcomes.SequenceEqual(_descriptor.RequiredControlOutcomes)
            && candidate.JoinPolicy == _descriptor.JoinPolicy
            && candidate.MinimumIncomingControlEdges == _descriptor.MinimumIncomingControlEdges
            && candidate.AllowsCycle == _descriptor.AllowsCycle
            && candidate.CycleIterationBudgetParameterId is null
            && candidate.CycleTimeBudgetMillisecondsParameterId is null
            && candidate.Ports.Count == 1
            && HasExactPortSemantics(candidate.Ports[0], _descriptor.Ports[0])
            && candidate.Parameters.Count == 0
            && candidate.RequiredCapabilityIds.Count == 0
            && Equals(candidate.ResourceBudget, _descriptor.ResourceBudget);

    /// <summary>Gets whether the node retains its exact configuration and response-schema binding after catalog admission.</summary>
    /// <param name="node">The normalized graph node.</param>
    /// <param name="schemas">The normalized graph schemas indexed by identifier.</param>
    /// <returns><see langword="true"/> only when the Human Input configuration is complete and data-only.</returns>
    public static bool HasExactSchemaSemantics(
        GovernedLoopNodeDefinition? node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
        => GovernedLoopHumanInputNodeConfigurationValidator.HasExactNodeSemantics(node, schemas);

    private static GovernedLoopNodeCatalogDescriptor CreateDescriptor()
        => new(
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
            IsAdvertised: true,
            IsExecutable: true,
            IsLegalEntry: false,
            IsLegalTerminal: false,
            _successFailure,
            _success,
            GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges: 1,
            AllowsCycle: false,
            CycleIterationBudgetParameterId: null,
            CycleTimeBudgetMillisecondsParameterId: null,
            Array.AsReadOnly(new[]
            {
                new GovernedLoopCatalogPortContract(
                    GovernedLoopHumanInputVocabulary.ResponsePortId,
                    GovernedLoopPortDirection.Output,
                    GovernedLoopBindingKind.Data,
                    _responseKinds,
                    Required: true),
            }),
            Array.Empty<GovernedLoopCatalogParameterContract>(),
            Array.Empty<string>(),
            new GovernedLoopNodeResourceBudget(
                Attempts: 1,
                PayloadCharacters: 0,
                EvidenceItems: CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation,
                ResourceUnits: 0));

    private static bool HasExactPortSemantics(GovernedLoopCatalogPortContract? candidate, GovernedLoopCatalogPortContract canonical)
        => candidate is not null
            && string.Equals(candidate.Id, canonical.Id, StringComparison.Ordinal)
            && candidate.Direction == canonical.Direction
            && candidate.BindingKind == canonical.BindingKind
            && Equals(candidate.AllowedValueKinds, canonical.AllowedValueKinds)
            && candidate.Required == canonical.Required;
}
