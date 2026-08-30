using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

public sealed class GovernedLoopHumanReviewNodeCatalogContractTests
{
    [Fact]
    public void Catalog_is_closed_advertised_not_yet_composed_port_free_and_authority_free()
    {
        var descriptor = GovernedLoopHumanReviewNodeCatalogContract.Descriptor;

        Assert.Equal(new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanReview, GovernedLoopHumanReviewVocabulary.TypeId, 1), descriptor.Descriptor);
        Assert.True(descriptor.IsAdvertised);
        Assert.False(descriptor.IsExecutable);
        Assert.False(descriptor.IsLegalEntry);
        Assert.False(descriptor.IsLegalTerminal);
        Assert.Equal([GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure], descriptor.AllowedControlOutcomes);
        Assert.Equal([GovernedLoopControlCondition.Success], descriptor.RequiredControlOutcomes);
        Assert.Equal(GovernedLoopJoinPolicy.None, descriptor.JoinPolicy);
        Assert.Equal(1, descriptor.MinimumIncomingControlEdges);
        Assert.False(descriptor.AllowsCycle);
        Assert.Empty(descriptor.Ports);
        Assert.Collection(
            descriptor.Parameters,
            policy =>
            {
                Assert.Equal(GovernedLoopHumanReviewNodeCatalogContract.ReviewPolicyIdParameter, policy.Id);
                Assert.Equal(GovernedLoopParameterValueKind.Enumeration, policy.ValueKind);
                Assert.Equal([GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerPolicyId], policy.AllowedValues);
            },
            role =>
            {
                Assert.Equal(GovernedLoopHumanReviewNodeCatalogContract.ReviewerRoleIdParameter, role.Id);
                Assert.Equal(GovernedLoopParameterValueKind.Enumeration, role.ValueKind);
                Assert.Equal([GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId], role.AllowedValues);
            },
            scope =>
            {
                Assert.Equal(GovernedLoopHumanReviewNodeCatalogContract.ApprovalScopeIdParameter, scope.Id);
                Assert.Equal(GovernedLoopParameterValueKind.Identifier, scope.ValueKind);
            });
        Assert.Empty(descriptor.RequiredCapabilityIds);
        Assert.Equal(new GovernedLoopNodeResourceBudget(1, 0, CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation, 0), descriptor.ResourceBudget);
    }

    [Fact]
    public void Resolution_catalog_and_node_semantics_require_the_exact_server_owned_schema()
    {
        var descriptor = GovernedLoopHumanReviewNodeCatalogContract.Descriptor;
        var node = Node();

        Assert.True(GovernedLoopHumanReviewNodeCatalogContract.TryResolve(descriptor.Descriptor, out var resolved));
        Assert.Equal(descriptor, resolved);
        Assert.True(GovernedLoopSequentialNodeDescriptors.IsHumanReview(descriptor.Descriptor));
        Assert.True(GovernedLoopSequentialNodeDescriptors.IsSupported(descriptor.Descriptor));
        Assert.True(GovernedLoopHumanReviewNodeCatalogContract.HasExactCatalogSemantics(descriptor));
        Assert.True(GovernedLoopHumanReviewNodeCatalogContract.HasExactCatalogStructure(descriptor with { IsExecutable = true }));
        Assert.True(GovernedLoopHumanReviewNodeCatalogContract.HasExactNodeSemantics(node));
        Assert.False(GovernedLoopHumanReviewNodeCatalogContract.TryResolve(descriptor.Descriptor with { Version = 2 }, out _));
        Assert.False(GovernedLoopHumanReviewNodeCatalogContract.HasExactCatalogSemantics(descriptor with { IsExecutable = true }));
        Assert.False(GovernedLoopHumanReviewNodeCatalogContract.HasExactCatalogStructure(descriptor with { RequiredCapabilityIds = ["org.embodysense/workspace-read"] }));
        Assert.False(GovernedLoopHumanReviewNodeCatalogContract.HasExactNodeSemantics(node with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create(["org.embodysense/workspace-read"]) }));
        Assert.False(GovernedLoopHumanReviewNodeCatalogContract.HasExactNodeSemantics(node with { Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [GovernedLoopHumanReviewNodeCatalogContract.ReviewPolicyIdParameter] = "other-policy", [GovernedLoopHumanReviewNodeCatalogContract.ReviewerRoleIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId, [GovernedLoopHumanReviewNodeCatalogContract.ApprovalScopeIdParameter] = "review-scope-one" } }));
        Assert.False(GovernedLoopHumanReviewNodeCatalogContract.HasExactNodeSemantics(node with { Ports = [new GovernedLoopPortDefinition("review", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", false)] }));
    }

    private static GovernedLoopNodeDefinition Node()
        => new(
            "human-review",
            GovernedLoopHumanReviewNodeCatalogContract.Descriptor.Descriptor,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GovernedLoopHumanReviewNodeCatalogContract.ReviewPolicyIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerPolicyId,
                [GovernedLoopHumanReviewNodeCatalogContract.ReviewerRoleIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
                [GovernedLoopHumanReviewNodeCatalogContract.ApprovalScopeIdParameter] = "review-scope-one",
            },
            null,
            null,
            null,
            null);
}
