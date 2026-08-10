using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

/// <summary>Evaluates the one canonical deterministic automatic response selection after an exact durable-order append.</summary>
public static class HumanInputResponseAutomaticPolicyDecision
{
    /// <summary>Safely evaluates whether an exact active response sequence satisfies its authored automatic policy.</summary>
    /// <param name="request">The exact immutable request version.</param>
    /// <param name="selectionId">The operation identity used if the exact append satisfies policy.</param>
    /// <param name="selectedAtUtc">The trusted UTC exact-append instant.</param>
    /// <param name="activeResponses">Active response artifacts in durable response-operation order after the exact append.</param>
    /// <param name="selection">The exact required selection, or null when policy is not yet satisfied or is manual.</param>
    /// <returns><see langword="true"/> when inputs and the resulting policy decision are valid; otherwise <see langword="false"/>.</returns>
    public static bool TryEvaluate(
        HumanInputRequest? request,
        string? selectionId,
        DateTimeOffset selectedAtUtc,
        IReadOnlyList<HumanInputResponseArtifact>? activeResponses,
        out HumanInputResponseSelection? selection)
    {
        selection = null;
        try
        {
            if (request is null
                || !HumanInputValidator.ValidateRequest(request).IsValid
                || !HumanInputIdentifier.IsValid(selectionId)
                || selectedAtUtc == default
                || selectedAtUtc.Offset != TimeSpan.Zero
                || selectedAtUtc < request.Timing.RequestedAtUtc
                || selectedAtUtc > request.Timing.ExpiresAtUtc
                || activeResponses is null
                || activeResponses.Count > HumanInputResponseContractLimits.MaxResponsesPerRequest
                || activeResponses.Any(response => !HumanInputResponseContractValidator.ValidateArtifact(request, response).IsValid))
            {
                return false;
            }

            var selected = request.ResponsePolicy.Kind switch
            {
                HumanInputResponsePolicyKind.FirstValid => activeResponses.Take(1).ToArray(),
                HumanInputResponsePolicyKind.Quorum => FirstQuorum(activeResponses, request.ResponsePolicy.RequiredResponseCount ?? 0),
                HumanInputResponsePolicyKind.NamedRoles => ResponsesForEveryRole(activeResponses, request.ResponsePolicy.OrderedRoleIds),
                HumanInputResponsePolicyKind.Merge => MergeContributors(activeResponses, request.ResponsePolicy.RequiredResponseCount ?? 0, request.ResponsePolicy.OrderedRoleIds),
                HumanInputResponsePolicyKind.ManualSelection => null,
                _ => null,
            };
            if (selected is null || selected.Count == 0)
            {
                return true;
            }

            var references = new HumanInputResponseReference[selected.Count];
            for (var index = 0; index < selected.Count; index++)
            {
                if (!HumanInputResponseReference.TryCreate(request, selected[index], out var reference, out _)
                    || reference is null)
                {
                    return false;
                }
                references[index] = reference;
            }
            var candidate = HumanInputResponseSelectionHash.Apply(
                new HumanInputResponseSelection(
                    HumanInputResponseContractLimits.CurrentSchemaVersion,
                    selectionId!,
                    RequestReference(request),
                    request.ResponsePolicy.Kind,
                    references.ToImmutableArray(),
                    null,
                    null,
                    selectedAtUtc,
                    string.Empty));
            if (!HumanInputResponseContractValidator.ValidateSelection(request, candidate, activeResponses).IsValid)
            {
                return false;
            }
            selection = candidate;
            return true;
        }
        catch (Exception)
        {
            selection = null;
            return false;
        }
    }

    private static IReadOnlyList<HumanInputResponseArtifact>? FirstQuorum(
        IEnumerable<HumanInputResponseArtifact> activeResponses,
        int requiredCount)
    {
        var byValueHash = new Dictionary<string, List<HumanInputResponseArtifact>>(StringComparer.Ordinal);
        foreach (var response in activeResponses)
        {
            if (!byValueHash.TryGetValue(response.ValueHash, out var matching))
            {
                matching = [];
                byValueHash.Add(response.ValueHash, matching);
            }
            matching.Add(response);
            if (matching.Count == requiredCount)
            {
                return matching;
            }
        }
        return null;
    }

    private static IReadOnlyList<HumanInputResponseArtifact>? ResponsesForEveryRole(
        IEnumerable<HumanInputResponseArtifact> activeResponses,
        ImmutableArray<string>? orderedRoleIds)
    {
        if (orderedRoleIds is not { } roles || roles.IsDefault)
        {
            return null;
        }
        var active = activeResponses.ToArray();
        var selected = new List<HumanInputResponseArtifact>(roles.Length);
        foreach (var roleId in roles)
        {
            var response = active.SingleOrDefault(candidate => string.Equals(candidate.RespondentRoleId, roleId, StringComparison.Ordinal));
            if (response is null)
            {
                return null;
            }
            selected.Add(response);
        }
        return selected;
    }

    private static IReadOnlyList<HumanInputResponseArtifact>? MergeContributors(
        IEnumerable<HumanInputResponseArtifact> activeResponses,
        int requiredCount,
        ImmutableArray<string>? orderedRoleIds)
    {
        if (orderedRoleIds is not { } roles || roles.IsDefault)
        {
            return null;
        }
        var active = activeResponses.ToArray();
        var selected = roles
            .Select(roleId => active.SingleOrDefault(candidate => string.Equals(candidate.RespondentRoleId, roleId, StringComparison.Ordinal)))
            .Where(response => response is not null)
            .Select(response => response!)
            .ToArray();
        return selected.Length >= requiredCount ? selected : null;
    }

    private static HumanInputRequestReference RequestReference(HumanInputRequest request)
        => new(HumanInputResponseContractLimits.CurrentSchemaVersion, request.RequestId, request.RequestVersionId, request.RequestHash);
}
