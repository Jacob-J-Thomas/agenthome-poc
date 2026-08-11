using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Creates bounded deep snapshots of authenticated response artifacts before durable use.</summary>
public static class HumanInputResponseArtifactSnapshot
{
    /// <summary>Captures an independent bounded attempted-artifact snapshot without applying request-relative response-schema rules.</summary>
    /// <param name="artifact">The potentially caller-owned attempted response artifact.</param>
    /// <param name="snapshot">The bounded deep snapshot when successful.</param>
    /// <param name="validation">The deterministic bounded-artifact validation result.</param>
    /// <returns><see langword="true"/> when the artifact is structurally bounded and both retained hashes match; otherwise, <see langword="false"/>.</returns>
    /// <remarks>This method intentionally does not establish request eligibility or response-schema validity. It exists so failed inspected submissions can retain exact, bounded evidence.</remarks>
    public static bool TryCaptureBoundedAttempt(
        HumanInputResponseArtifact? artifact,
        out HumanInputResponseArtifact? snapshot,
        out HumanInputResponseValidationResult validation)
    {
        if (!IsBoundedAttempt(artifact))
        {
            snapshot = null;
            validation = InvalidAttempt("The attempted response artifact is malformed, exceeds schema-1 bounds, or carries mismatched hashes.");
            return false;
        }

        try
        {
            snapshot = Snapshot(artifact!);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = InvalidAttempt("The bounded attempted response artifact changed while its snapshot was captured.");
            return false;
        }

        if (!IsBoundedAttempt(snapshot))
        {
            snapshot = null;
            validation = InvalidAttempt("The bounded attempted response artifact changed while its snapshot was captured.");
            return false;
        }

        validation = new HumanInputResponseValidationResult([]);
        return true;
    }

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
            snapshot = Snapshot(artifact);
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

    private static bool IsBoundedAttempt(HumanInputResponseArtifact? artifact)
        => artifact is not null
            && artifact.SchemaVersion == HumanInputResponseArtifact.CurrentSchemaVersion
            && HumanInputResponseArtifactHash.IsBounded(artifact)
            && artifact.SubmittedAtUtc != default
            && artifact.SubmittedAtUtc.Offset == TimeSpan.Zero
            && Enum.IsDefined(artifact.PrivacyClass)
            && artifact.PrivacyClass != HumanInputPrivacyClass.Unknown
            && HumanInputResponseArtifactHash.Matches(artifact);

    private static HumanInputResponseArtifact Snapshot(HumanInputResponseArtifact artifact)
        => artifact with
        {
            Request = artifact.Request with { },
            Binding = artifact.Binding with { },
            Value = HumanInputResponseValueSnapshot.Capture(artifact.Value)
        };

    private static HumanInputResponseValidationResult InvalidAttempt(string message)
        => new([new HumanInputResponseValidationError(HumanInputResponseValidationErrorCode.InvalidValue, "$", message)]);
}
