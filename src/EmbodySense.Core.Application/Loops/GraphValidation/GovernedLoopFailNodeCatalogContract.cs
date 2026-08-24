using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Defines the sole canonical schema-1 Fail terminal and its explicit-failure parameter posture.</summary>
public static class GovernedLoopFailNodeCatalogContract
{
    private static readonly IReadOnlyList<GovernedLoopNodeCatalogDescriptor> _descriptors = Array.AsReadOnly(new[]
    {
        new GovernedLoopNodeCatalogDescriptor(
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Fail, GovernedLoopFailNodeVocabulary.TypeId, GovernedLoopFailNodeVocabulary.DescriptorVersion),
            IsAdvertised: true,
            IsExecutable: true,
            IsLegalEntry: false,
            IsLegalTerminal: true,
            Array.Empty<GovernedLoopControlCondition>(),
            Array.Empty<GovernedLoopControlCondition>(),
            GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges: 1,
            AllowsCycle: false,
            CycleIterationBudgetParameterId: null,
            CycleTimeBudgetMillisecondsParameterId: null,
            Array.Empty<GovernedLoopCatalogPortContract>(),
            Array.AsReadOnly(new[]
            {
                new GovernedLoopCatalogParameterContract(GovernedLoopFailNodeVocabulary.CodeParameter, GovernedLoopParameterValueKind.Text, Required: false, 1, GovernedLoopFailureEvidenceContract.MaxServerCodeCharacters, null, null, Array.Empty<string>()),
                new GovernedLoopCatalogParameterContract(GovernedLoopFailNodeVocabulary.ExplanationParameter, GovernedLoopParameterValueKind.Text, Required: false, 1, GovernedLoopFailureEvidenceContract.MaxSafeDetailCharacters, null, null, Array.Empty<string>()),
            }),
            Array.Empty<string>(),
            new GovernedLoopNodeResourceBudget(1, GovernedLoopFailureEvidenceContract.MaxServerCodeCharacters + GovernedLoopFailureEvidenceContract.MaxSafeDetailCharacters, CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation, 0)),
    });

    /// <summary>Gets the exact Fail terminal declaration.</summary>
    public static IReadOnlyList<GovernedLoopNodeCatalogDescriptor> Descriptors => _descriptors;

    /// <summary>Resolves only the exact Fail terminal descriptor without aliases or version fallback.</summary>
    public static bool TryResolve(GovernedLoopNodeDescriptor? descriptor, out GovernedLoopNodeCatalogDescriptor? contract)
    {
        contract = descriptor is not null && Equals(descriptor, _descriptors[0].Descriptor) ? _descriptors[0] : null;
        return contract is not null;
    }

    /// <summary>Gets whether one admitted Fail node either consumes incoming classified failure evidence or declares one exact explicit failure.</summary>
    public static bool HasExactNodeSemantics(GovernedLoopNodeDefinition? node, IReadOnlyList<GovernedLoopControlEdgeDefinition>? incomingEdges)
    {
        if (node is null
            || incomingEdges is null
            || !TryResolve(node.Descriptor, out _)
            || node.Ports.Count != 0
            || node.AuthorityCeiling.CapabilityIds.Count != 0
            || node.ModelRoutingPolicy is not null
            || node.AuthoredInputDataClasses is not null)
        {
            return false;
        }

        if (node.Parameters.Count == 0)
        {
            return incomingEdges.Count == 1 && incomingEdges[0].Condition == GovernedLoopControlCondition.Failure;
        }

        return node.Parameters.Count == 2
            && node.Parameters.TryGetValue(GovernedLoopFailNodeVocabulary.CodeParameter, out var code)
            && node.Parameters.TryGetValue(GovernedLoopFailNodeVocabulary.ExplanationParameter, out var explanation)
            && GovernedLoopFailureEvidenceContract.IsServerCode(code)
            && GovernedLoopFailureEvidenceContract.IsSafeDetail(explanation)
            && explanation is not null;
    }
}
