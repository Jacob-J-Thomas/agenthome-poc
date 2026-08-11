namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseArtifact
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputResponseArtifact {{ SchemaVersion = {SchemaVersion}, ResponseId = {ResponseId}, Request = {Request}, SubmittedAtUtc = {SubmittedAtUtc:O}, PrivacyClass = {PrivacyClass}, ValueHash = {ValueHash}, ResponseHash = {ResponseHash}, PrivateData = [REDACTED] }}";
}
