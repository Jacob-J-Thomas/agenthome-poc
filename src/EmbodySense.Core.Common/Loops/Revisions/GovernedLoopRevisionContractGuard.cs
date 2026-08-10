using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

internal static class GovernedLoopRevisionContractGuard
{
    internal static void RequireValid(GovernedLoopRevisionValidationResult result, string parameterName)
    {
        if (!result.IsValid)
        {
            throw new ArgumentException($"Governed-loop revision contract is invalid at {result.Errors[0].Path}: {result.Errors[0].Message}", parameterName);
        }
    }

    internal static GovernedLoopRevisionReference CopyRevision(GovernedLoopRevisionReference revision, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(revision, parameterName);
        return GovernedLoopRevisionReference.Create(revision.SchemaVersion, revision.GraphId, revision.RevisionId, revision.ExecutableHash);
    }

    internal static GovernedLoopRevisionReference? CopyOptionalRevision(GovernedLoopRevisionReference? revision, string parameterName)
        => revision is null ? null : CopyRevision(revision, parameterName);

    internal static string RequireIdentifier(string? value, string parameterName)
        => CustomLoopArtifactIdentifier.Require(value, parameterName, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters);

    internal static string RequireHash(string? value, string parameterName)
    {
        if (!IsHash(value))
        {
            throw new ArgumentException("Governed-loop revision hashes must be canonical lowercase SHA-256 hexadecimal values.", parameterName);
        }

        return value!;
    }

    internal static string? RequireOptionalHash(string? value, string parameterName) => value is null ? null : RequireHash(value, parameterName);

    internal static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (!IsUtc(value))
        {
            throw new ArgumentException("Governed-loop revision timestamps must be non-default UTC values with zero offset.", parameterName);
        }

        return value;
    }

    internal static bool IsIdentifier(string? value)
        => CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters);

    internal static bool IsHash(string? value)
        => value is { Length: GovernedLoopRevisionContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    internal static bool IsVersion(long value) => value is >= 1 and <= GovernedLoopRevisionContractLimits.MaxLifecycleVersion;

    internal static bool IsReferenceValid(GovernedLoopRevisionReference? revision)
    {
        return revision is not null
            && revision.SchemaVersion == GovernedLoopRevisionContractLimits.CurrentSchemaVersion
            && IsIdentifier(revision.GraphId)
            && IsIdentifier(revision.RevisionId)
            && IsHash(revision.ExecutableHash);
    }

    internal static bool IsSameReference(GovernedLoopRevisionReference? left, GovernedLoopRevisionReference? right)
    {
        return left is not null
            && right is not null
            && left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal)
            && string.Equals(left.ExecutableHash, right.ExecutableHash, StringComparison.Ordinal);
    }

    internal static bool IsSameRevisionIdentity(GovernedLoopRevisionReference? left, GovernedLoopRevisionReference? right)
    {
        return left is not null
            && right is not null
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal);
    }

    internal static bool HasConflictingRevisionContent(GovernedLoopRevisionReference? left, GovernedLoopRevisionReference? right)
    {
        return IsSameRevisionIdentity(left, right)
            && !string.Equals(left!.ExecutableHash, right!.ExecutableHash, StringComparison.Ordinal);
    }

    internal static bool IsSameGraph(GovernedLoopRevisionReference? revision, string graphId)
        => revision is not null && string.Equals(revision.GraphId, graphId, StringComparison.Ordinal);

    internal static GovernedLoopRevisionPublicationPin? CopyOptionalPin(GovernedLoopRevisionPublicationPin? pin, string parameterName)
    {
        if (pin is null)
        {
            return null;
        }

        var copy = new GovernedLoopRevisionPublicationPin(
            pin.SchemaVersion,
            CopyRevision(pin.Revision, $"{parameterName}.{nameof(pin.Revision)}"),
            pin.PublicationOperationId,
            pin.ValidationEvidenceHash);
        RequireValid(GovernedLoopRevisionContractValidator.Validate(copy), parameterName);
        return copy;
    }

    internal static GovernedLoopRevisionLifecycleHead? CopyOptionalHead(GovernedLoopRevisionLifecycleHead? head, string parameterName)
    {
        if (head is null)
        {
            return null;
        }

        var copy = new GovernedLoopRevisionLifecycleHead(
            head.SchemaVersion,
            head.GraphId,
            head.LifecycleVersion,
            head.Status,
            CopyOptionalRevision(head.DraftRevision, $"{parameterName}.{nameof(head.DraftRevision)}"),
            CopyOptionalPin(head.PublishedRevision, $"{parameterName}.{nameof(head.PublishedRevision)}"),
            head.LastOperationId,
            head.UpdatedAtUtc);
        RequireValid(GovernedLoopRevisionContractValidator.Validate(copy), parameterName);
        return copy;
    }
}
