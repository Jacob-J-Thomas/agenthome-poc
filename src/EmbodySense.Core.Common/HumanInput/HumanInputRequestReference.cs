using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.HumanInput.Models;

public sealed partial record HumanInputRequestReference
{
    /// <summary>Creates an exact reference only from one valid canonical request.</summary>
    /// <param name="request">The request to validate and reference.</param>
    /// <param name="reference">The exact reference when validation succeeds.</param>
    /// <param name="validation">The deterministic request validation result.</param>
    /// <returns><see langword="true"/> when the request is valid and referenced exactly; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreate(HumanInputRequest? request, out HumanInputRequestReference? reference, out HumanInputValidationResult validation)
    {
        validation = HumanInputValidator.ValidateRequest(request);
        if (!validation.IsValid || request is null)
        {
            reference = null;
            return false;
        }

        reference = new HumanInputRequestReference(CurrentSchemaVersion, request.RequestId, request.RequestVersionId, request.RequestHash);
        return true;
    }

    /// <summary>Determines whether this reference exactly identifies the supplied valid request.</summary>
    /// <param name="request">The request to validate and compare.</param>
    /// <returns><see langword="true"/> only when schema, identifiers, and canonical hash match exactly.</returns>
    public bool Matches(HumanInputRequest? request)
    {
        if (SchemaVersion != CurrentSchemaVersion
            || !HumanInputIdentifier.IsValid(RequestId)
            || !HumanInputIdentifier.IsValid(RequestVersionId)
            || !IsSha256(RequestHash)
            || request is null
            || !HumanInputValidator.ValidateRequest(request).IsValid
            || !string.Equals(RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(RequestVersionId, request.RequestVersionId, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(RequestHash);
        var actual = Encoding.ASCII.GetBytes(request.RequestHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <inheritdoc />
    public override string ToString() => $"HumanInputRequestReference {{ SchemaVersion = {SchemaVersion}, RequestId = {RequestId}, RequestVersionId = {RequestVersionId}, RequestHash = {RequestHash} }}";

    private static bool IsSha256(string? value) => value is { Length: HumanInputLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
