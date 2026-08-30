using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Defines the immutable node-derived fields that bind a Human Review request to its admitted ordered graph.</summary>
/// <remarks>The release service rechecks these values before its whole-run compare-exchange so an immutable request cannot
/// be replayed against a graph target, layout precondition, or server-approved scope payload other than the one that parked it.</remarks>
internal static class HumanReviewOrderedNodeBindingContract
{
    internal static bool TryGetApprovalScopeId(GovernedLoopNodeDefinition? graphNode, out string? approvalScopeId)
    {
        approvalScopeId = null;
        if (!GovernedLoopHumanReviewNodeCatalogContract.HasExactNodeSemantics(graphNode)
            || !graphNode!.Parameters.TryGetValue(GovernedLoopHumanReviewNodeCatalogContract.ApprovalScopeIdParameter, out var value)) return false;

        approvalScopeId = value;
        return true;
    }

    internal static bool TryGetReviewerRoleId(GovernedLoopNodeDefinition? graphNode, out string? reviewerRoleId)
    {
        reviewerRoleId = null;
        if (!GovernedLoopHumanReviewNodeCatalogContract.HasExactNodeSemantics(graphNode)
            || !graphNode!.Parameters.TryGetValue(GovernedLoopHumanReviewNodeCatalogContract.ReviewerRoleIdParameter, out var value)) return false;

        reviewerRoleId = value;
        return true;
    }

    internal static bool Matches(HumanReviewBinding binding, GovernedLoopSequentialAdapterBinding adapter, GovernedLoopNodeDefinition graphNode)
        => TryGetApprovalScopeId(graphNode, out var approvalScopeId)
            && string.Equals(binding.TargetHash, adapter.GraphArtifactHash, StringComparison.Ordinal)
            && string.Equals(binding.PreconditionHash, adapter.GraphLayoutHash, StringComparison.Ordinal)
            && string.Equals(binding.PayloadHash, ComputePayloadHash(adapter, binding.NodeId, approvalScopeId!), StringComparison.Ordinal);

    internal static string ComputePayloadHash(GovernedLoopSequentialAdapterBinding adapter, string nodeId, string approvalScopeId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', "human-review-node-payload-v1", adapter.ContentHash, nodeId, approvalScopeId))));
}
