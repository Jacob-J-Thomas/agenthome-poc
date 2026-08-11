namespace EmbodySense.Core.Common.HumanInput.Models;

public sealed partial record HumanInputResponse
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputResponse {{ RequestId = {RequestId}, RequestVersionId = {RequestVersionId}, SubmittedAtUtc = {SubmittedAtUtc:O}, ValueKind = {Value?.Kind}, PrivateAttribution = [REDACTED] }}";
}
