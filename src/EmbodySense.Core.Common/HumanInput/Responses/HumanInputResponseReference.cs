using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseReference
{
    /// <summary>Creates an exact privacy-safe reference only from one valid response artifact.</summary>
    /// <param name="request">The exact immutable request.</param>
    /// <param name="artifact">The response artifact to validate and reference.</param>
    /// <param name="reference">The exact reference when validation succeeds.</param>
    /// <param name="validation">The deterministic response validation result.</param>
    /// <returns><see langword="true"/> when the artifact is valid and referenced exactly; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreate(HumanInputRequest? request, HumanInputResponseArtifact? artifact, out HumanInputResponseReference? reference, out HumanInputResponseValidationResult validation)
    {
        validation = HumanInputResponseContractValidator.ValidateArtifact(request, artifact);
        if (!validation.IsValid || artifact is null)
        {
            reference = null;
            return false;
        }

        reference = new HumanInputResponseReference(CurrentSchemaVersion, artifact.ResponseId, artifact.Request, artifact.ValueHash, artifact.ResponseHash);
        return true;
    }

    /// <summary>Determines whether this reference exactly identifies the supplied valid response artifact.</summary>
    /// <param name="request">The exact immutable request.</param>
    /// <param name="artifact">The artifact to validate and compare.</param>
    /// <returns><see langword="true"/> only when every reference identity and digest matches exactly.</returns>
    public bool Matches(HumanInputRequest? request, HumanInputResponseArtifact? artifact)
        => artifact is not null
            && HumanInputResponseContractValidator.ValidateArtifact(request, artifact).IsValid
            && Equals(this, new HumanInputResponseReference(CurrentSchemaVersion, artifact.ResponseId, artifact.Request, artifact.ValueHash, artifact.ResponseHash));

    /// <inheritdoc />
    public override string ToString() => $"HumanInputResponseReference {{ SchemaVersion = {SchemaVersion}, ResponseId = {ResponseId}, Request = {Request}, ValueHash = {ValueHash}, ResponseHash = {ResponseHash} }}";
}
