namespace EmbodySense.Core.Common.HumanInput.Models;

public sealed partial record HumanInputRequest
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputRequest {{ SchemaVersion = {SchemaVersion}, RequestId = {RequestId}, RequestVersionId = {RequestVersionId}, PrivacyClass = {PrivacyClass}, RequestHash = {RequestHash} }}";
}
