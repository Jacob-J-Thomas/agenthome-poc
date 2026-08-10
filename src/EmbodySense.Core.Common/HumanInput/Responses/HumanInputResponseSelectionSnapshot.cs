using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Creates bounded deep snapshots of immutable selected response sets before durable use.</summary>
public static class HumanInputResponseSelectionSnapshot
{
    /// <summary>Captures and validates an independent selection snapshot against one exact request and bounded active response set.</summary>
    /// <param name="request">The exact retained request version.</param>
    /// <param name="selection">The potentially caller-owned selection.</param>
    /// <param name="activeResponses">The bounded exact active response artifacts in durable response-operation order.</param>
    /// <param name="snapshot">The validated deep snapshot when successful.</param>
    /// <param name="validation">The deterministic selection validation result.</param>
    /// <returns><see langword="true"/> when a complete valid snapshot was captured; otherwise, <see langword="false"/>.</returns>
    public static bool TryCapture(HumanInputRequest? request, HumanInputResponseSelection? selection, IReadOnlyList<HumanInputResponseArtifact>? activeResponses, out HumanInputResponseSelection? snapshot, out HumanInputResponseValidationResult validation)
    {
        if (selection is null || !HumanInputResponseSelectionHash.IsBounded(selection))
        {
            snapshot = null;
            validation = HumanInputResponseContractValidator.ValidateSelection(request, selection, activeResponses);
            return false;
        }

        snapshot = selection with
        {
            Request = selection.Request with { },
            Responses = selection.Responses.Select(reference => reference is null
                ? null!
                : reference with { Request = reference.Request with { } }).ToImmutableArray()
        };
        validation = HumanInputResponseContractValidator.ValidateSelection(request, snapshot, activeResponses);
        if (validation.IsValid)
        {
            return true;
        }
        snapshot = null;
        return false;
    }
}
