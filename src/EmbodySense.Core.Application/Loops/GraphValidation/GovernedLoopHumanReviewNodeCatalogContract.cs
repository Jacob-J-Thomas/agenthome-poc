using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Defines the exact schema-1 catalog contract for a governed Human Review boundary.</summary>
/// <remarks>The graph selects only a closed server-owned policy identifier. Reviewer identity, eligibility proof, authorization evidence, previews, and deadlines are created at the trusted admission boundary. Startup composition owns the separately mutable executable posture.</remarks>
public static class GovernedLoopHumanReviewNodeCatalogContract
{
    /// <summary>Gets the only server-resolved reviewer policy supported by the local POC.</summary>
    public const string LocalReviewerPolicyId = "local-review-policy";

    /// <summary>Gets the sole surface-neutral reviewer role admitted by the local server-owned policy.</summary>
    public const string LocalReviewerRoleId = "governed-reviewer";

    /// <summary>Gets the parameter selecting the server-owned Human Review policy.</summary>
    public const string ReviewPolicyIdParameter = "review-policy-id";

    /// <summary>Gets the bounded opaque policy-approved subject identifier.</summary>
    public const string ApprovalScopeIdParameter = "approval-scope-id";

    /// <summary>Gets the graph parameter carrying the policy-resolved reviewer role, not a browser-supplied identity.</summary>
    public const string ReviewerRoleIdParameter = "reviewer-role-id";

    private static readonly IReadOnlyList<GovernedLoopControlCondition> _success = Array.AsReadOnly([GovernedLoopControlCondition.Success]);
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _successFailure = Array.AsReadOnly([GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure]);
    private static readonly GovernedLoopNodeCatalogDescriptor _descriptor = CreateDescriptor();

    /// <summary>Gets the sole exact Human Review descriptor declaration.</summary>
    public static GovernedLoopNodeCatalogDescriptor Descriptor => _descriptor;

    /// <summary>Resolves the only supported Human Review descriptor without aliases or version fallback.</summary>
    /// <param name="descriptor">The descriptor to resolve.</param>
    /// <param name="contract">The canonical contract when resolution succeeds.</param>
    /// <returns><see langword="true"/> only for the exact schema-1 Human Review descriptor.</returns>
    public static bool TryResolve(GovernedLoopNodeDescriptor? descriptor, out GovernedLoopNodeCatalogDescriptor? contract)
    {
        contract = descriptor is not null && Equals(descriptor, _descriptor.Descriptor) ? _descriptor : null;
        return contract is not null;
    }

    /// <summary>Gets whether a catalog entry retains every immutable Human Review executable property.</summary>
    /// <param name="candidate">The catalog entry to compare.</param>
    /// <returns><see langword="true"/> only for an exact semantic match.</returns>
    public static bool HasExactCatalogSemantics(GovernedLoopNodeCatalogDescriptor? candidate)
        => HasExactCatalogStructure(candidate)
            && candidate!.IsExecutable == _descriptor.IsExecutable;

    /// <summary>Gets whether a catalog entry retains the immutable Human Review contract independently of its current executable posture.</summary>
    /// <param name="candidate">The catalog entry to compare.</param>
    /// <returns><see langword="true"/> only when the structural Human Review descriptor contract is exact.</returns>
    /// <remarks>
    /// The descriptor is advertised but non-executable until Startup composes the ordered admission and release dependencies.
    /// That later composition may set <see cref="GovernedLoopNodeCatalogDescriptor.IsExecutable"/> without changing the
    /// graph schema, policy, ports, authority, or bounded resource contract validated here.
    /// </remarks>
    public static bool HasExactCatalogStructure(GovernedLoopNodeCatalogDescriptor? candidate)
        => candidate is not null
            && Equals(candidate.Descriptor, _descriptor.Descriptor)
            && candidate.IsAdvertised == _descriptor.IsAdvertised
            && candidate.IsLegalEntry == _descriptor.IsLegalEntry
            && candidate.IsLegalTerminal == _descriptor.IsLegalTerminal
            && candidate.AllowedControlOutcomes.SequenceEqual(_descriptor.AllowedControlOutcomes)
            && candidate.RequiredControlOutcomes.SequenceEqual(_descriptor.RequiredControlOutcomes)
            && candidate.JoinPolicy == _descriptor.JoinPolicy
            && candidate.MinimumIncomingControlEdges == _descriptor.MinimumIncomingControlEdges
            && candidate.AllowsCycle == _descriptor.AllowsCycle
            && candidate.CycleIterationBudgetParameterId is null
            && candidate.CycleTimeBudgetMillisecondsParameterId is null
            && candidate.Ports.Count == 0
            && candidate.Parameters.Count == _descriptor.Parameters.Count
            && candidate.Parameters.Zip(_descriptor.Parameters).All(pair => HasExactParameterSemantics(pair.First, pair.Second))
            && candidate.RequiredCapabilityIds.Count == 0
            && Equals(candidate.ResourceBudget, _descriptor.ResourceBudget);

    /// <summary>Gets whether a node retains its exact port-free, authority-free, server-policy review contract.</summary>
    /// <param name="node">The normalized graph node.</param>
    /// <returns><see langword="true"/> only when its server-owned policy and opaque scope selection are exact.</returns>
    public static bool HasExactNodeSemantics(GovernedLoopNodeDefinition? node)
        => node is not null
            && TryResolve(node.Descriptor, out _)
            && node.Ports.Count == 0
            && node.AuthorityCeiling.CapabilityIds.Count == 0
            && node.ModelRoutingPolicy is null
            && node.AuthoredInputDataClasses is null
            && node.RetryPolicy is null
            && node.HumanInputConfiguration is null
            && node.Parameters.Count == 3
            && node.Parameters.TryGetValue(ReviewPolicyIdParameter, out var policyId)
            && string.Equals(policyId, LocalReviewerPolicyId, StringComparison.Ordinal)
            && node.Parameters.TryGetValue(ReviewerRoleIdParameter, out var reviewerRoleId)
            && string.Equals(reviewerRoleId, LocalReviewerRoleId, StringComparison.Ordinal)
            && node.Parameters.TryGetValue(ApprovalScopeIdParameter, out var approvalScopeId)
            && CustomLoopArtifactIdentifier.IsValid(approvalScopeId);

    private static GovernedLoopNodeCatalogDescriptor CreateDescriptor()
        => new(
            GovernedLoopSequentialNodeDescriptors.HumanReview,
            IsAdvertised: true,
            IsExecutable: false,
            IsLegalEntry: false,
            IsLegalTerminal: false,
            _successFailure,
            _success,
            GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges: 1,
            AllowsCycle: false,
            CycleIterationBudgetParameterId: null,
            CycleTimeBudgetMillisecondsParameterId: null,
            Array.Empty<GovernedLoopCatalogPortContract>(),
            Array.AsReadOnly([
                new GovernedLoopCatalogParameterContract(ReviewPolicyIdParameter, GovernedLoopParameterValueKind.Enumeration, true, LocalReviewerPolicyId.Length, LocalReviewerPolicyId.Length, null, null, Array.AsReadOnly([LocalReviewerPolicyId])),
                new GovernedLoopCatalogParameterContract(ReviewerRoleIdParameter, GovernedLoopParameterValueKind.Enumeration, true, LocalReviewerRoleId.Length, LocalReviewerRoleId.Length, null, null, Array.AsReadOnly([LocalReviewerRoleId])),
                new GovernedLoopCatalogParameterContract(ApprovalScopeIdParameter, GovernedLoopParameterValueKind.Identifier, true, 1, CustomLoopLimits.MaxArtifactIdCharacters, null, null, Array.Empty<string>()),
            ]),
            Array.Empty<string>(),
            new GovernedLoopNodeResourceBudget(1, 0, CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation, 0));

    private static bool HasExactParameterSemantics(GovernedLoopCatalogParameterContract? candidate, GovernedLoopCatalogParameterContract canonical)
        => candidate is not null
            && string.Equals(candidate.Id, canonical.Id, StringComparison.Ordinal)
            && candidate.ValueKind == canonical.ValueKind
            && candidate.Required == canonical.Required
            && candidate.MinimumCharacters == canonical.MinimumCharacters
            && candidate.MaximumCharacters == canonical.MaximumCharacters
            && candidate.MaximumUtf8Bytes == canonical.MaximumUtf8Bytes
            && candidate.AllowLeadingOption == canonical.AllowLeadingOption
            && candidate.AllowResponseFileReference == canonical.AllowResponseFileReference
            && candidate.MinimumInteger == canonical.MinimumInteger
            && candidate.MaximumInteger == canonical.MaximumInteger
            && candidate.AllowedValues.SequenceEqual(canonical.AllowedValues, StringComparer.Ordinal);
}
