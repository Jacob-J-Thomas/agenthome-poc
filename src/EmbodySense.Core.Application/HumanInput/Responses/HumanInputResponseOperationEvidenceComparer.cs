using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

internal static class HumanInputResponseOperationEvidenceComparer
{
    internal static bool ExactEquals(
        HumanInputResponseOperationEvidence? left,
        HumanInputResponseOperationEvidence? right)
        => ReferenceEquals(left, right)
            || left is not null
                && right is not null
                && left.SchemaVersion == right.SchemaVersion
                && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
                && string.Equals(left.CommandHash, right.CommandHash, StringComparison.Ordinal)
                && left.Kind == right.Kind
                && left.Outcome == right.Outcome
                && left.FailureCode == right.FailureCode
                && Equals(left.Request, right.Request)
                && Equals(left.ExpectedBinding, right.ExpectedBinding)
                && Equals(left.ObservedBinding, right.ObservedBinding)
                && left.ExpectedLifecycleVersion == right.ExpectedLifecycleVersion
                && left.ExpectedLifecycleStatus == right.ExpectedLifecycleStatus
                && Equals(left.PreviousHead, right.PreviousHead)
                && Equals(left.ResultHead, right.ResultHead)
                && ArtifactEquals(left.AttemptedResponse, right.AttemptedResponse)
                && Equals(left.SubmittedResponse, right.SubmittedResponse)
                && !left.TargetResponses.IsDefault
                && !right.TargetResponses.IsDefault
                && left.TargetResponses.SequenceEqual(right.TargetResponses)
                && Equals(left.Selection, right.Selection)
                && left.ActorId.Equals(right.ActorId)
                && string.Equals(left.ActorRoleId, right.ActorRoleId, StringComparison.Ordinal)
                && string.Equals(left.AuthenticationEvidenceHash, right.AuthenticationEvidenceHash, StringComparison.Ordinal)
                && string.Equals(left.EligibilityEvidenceHash, right.EligibilityEvidenceHash, StringComparison.Ordinal)
                && left.RecordedAtUtc == right.RecordedAtUtc;

    internal static bool ArtifactEquals(
        HumanInputResponseArtifact? left,
        HumanInputResponseArtifact? right)
        => left is null || right is null
            ? left is null && right is null
            : left.SchemaVersion == right.SchemaVersion
                && string.Equals(left.ResponseId, right.ResponseId, StringComparison.Ordinal)
                && Equals(left.Request, right.Request)
                && Equals(left.Binding, right.Binding)
                && left.ActorId.Equals(right.ActorId)
                && string.Equals(left.RespondentRoleId, right.RespondentRoleId, StringComparison.Ordinal)
                && left.SubmittedAtUtc == right.SubmittedAtUtc
                && left.PrivacyClass == right.PrivacyClass
                && ValueEquals(left.Value, right.Value)
                && string.Equals(left.Explanation, right.Explanation, StringComparison.Ordinal)
                && string.Equals(left.ValueHash, right.ValueHash, StringComparison.Ordinal)
                && string.Equals(left.ResponseHash, right.ResponseHash, StringComparison.Ordinal);

    private static bool ValueEquals(
        HumanInputResponseValue left,
        HumanInputResponseValue right)
        => left.Kind == right.Kind
            && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
            && string.Equals(left.ChoiceId, right.ChoiceId, StringComparison.Ordinal)
            && left.Confirmation == right.Confirmation
            && NullableSequenceEquals(left.StructuredFields, right.StructuredFields)
            && Equals(left.Reference, right.Reference);

    private static bool NullableSequenceEquals<T>(
        System.Collections.Immutable.ImmutableArray<T>? left,
        System.Collections.Immutable.ImmutableArray<T>? right)
        where T : class
        => left is null || right is null
            ? left is null && right is null
            : !left.Value.IsDefault
                && !right.Value.IsDefault
                && left.Value.SequenceEqual(right.Value);
}
