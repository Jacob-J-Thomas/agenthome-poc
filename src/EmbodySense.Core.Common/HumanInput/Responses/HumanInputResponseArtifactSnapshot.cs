using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Creates bounded deep snapshots of authenticated response artifacts before durable use.</summary>
public static class HumanInputResponseArtifactSnapshot
{
    /// <summary>Captures and validates an independent artifact snapshot against one exact immutable request.</summary>
    /// <param name="request">The exact retained request version.</param>
    /// <param name="artifact">The potentially caller-owned response artifact.</param>
    /// <param name="snapshot">The validated deep snapshot when successful.</param>
    /// <param name="validation">The deterministic response validation result.</param>
    /// <returns><see langword="true"/> when a complete valid snapshot was captured; otherwise, <see langword="false"/>.</returns>
    public static bool TryCapture(HumanInputRequest? request, HumanInputResponseArtifact? artifact, out HumanInputResponseArtifact? snapshot, out HumanInputResponseValidationResult validation)
    {
        if (artifact is null || !HumanInputResponseArtifactHash.IsBounded(artifact))
        {
            snapshot = null;
            validation = HumanInputResponseContractValidator.ValidateArtifact(request, artifact);
            return false;
        }

        try
        {
            snapshot = artifact with
            {
                Request = artifact.Request with { },
                Binding = artifact.Binding with { },
                Value = HumanInputResponseValueSnapshot.Capture(artifact.Value)
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = new HumanInputResponseValidationResult([new HumanInputResponseValidationError(HumanInputResponseValidationErrorCode.InvalidValue, "$", "The bounded response artifact changed while its snapshot was captured.")]);
            return false;
        }

        validation = HumanInputResponseContractValidator.ValidateArtifact(request, snapshot);
        if (validation.IsValid)
        {
            return true;
        }
        snapshot = null;
        return false;
    }
}
