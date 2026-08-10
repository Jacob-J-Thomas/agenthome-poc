using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Computes domain-separated canonical hashes for payload-agnostic revision lifecycle contracts.</summary>
public static class GovernedLoopRevisionContractHash
{
    /// <summary>Computes the canonical lowercase SHA-256 hash of one validated immutable artifact.</summary>
    /// <param name="artifact">The validated artifact.</param>
    /// <returns>The canonical artifact hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="artifact"/> is not a valid schema-1 contract.</exception>
    public static string ComputeArtifactHash(GovernedLoopRevisionArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        GovernedLoopRevisionContractGuard.RequireValid(GovernedLoopRevisionContractValidator.Validate(artifact), nameof(artifact));
        var canonical = Begin("governed-loop-revision-artifact-v1");
        Append(canonical, artifact.SchemaVersion);
        Append(canonical, artifact.Revision);
        Append(canonical, artifact.PredecessorRevision);
        Append(canonical, artifact.RollbackSourcePublication is null ? null : ComputePublicationPinHash(artifact.RollbackSourcePublication));
        Append(canonical, artifact.CreationOperationId);
        Append(canonical, artifact.CreatedByActorId);
        Append(canonical, artifact.CreatedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Computes the canonical lowercase SHA-256 hash of one validated exact publication pin.</summary>
    /// <param name="pin">The validated publication pin.</param>
    /// <returns>The canonical publication-pin hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pin"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pin"/> is not a valid schema-1 contract.</exception>
    public static string ComputePublicationPinHash(GovernedLoopRevisionPublicationPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        GovernedLoopRevisionContractGuard.RequireValid(GovernedLoopRevisionContractValidator.Validate(pin), nameof(pin));
        var canonical = Begin("governed-loop-revision-publication-pin-v1");
        Append(canonical, pin.SchemaVersion);
        Append(canonical, pin.Revision);
        Append(canonical, pin.PublicationOperationId);
        Append(canonical, pin.ValidationEvidenceHash);
        return Digest(canonical);
    }

    /// <summary>Computes the canonical lowercase SHA-256 hash of one validated lifecycle head.</summary>
    /// <param name="head">The validated lifecycle head.</param>
    /// <returns>The canonical lifecycle-head hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="head"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="head"/> is not a valid schema-1 contract.</exception>
    public static string ComputeLifecycleHeadHash(GovernedLoopRevisionLifecycleHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        GovernedLoopRevisionContractGuard.RequireValid(GovernedLoopRevisionContractValidator.Validate(head), nameof(head));
        var canonical = Begin("governed-loop-revision-lifecycle-head-v1");
        Append(canonical, head.SchemaVersion);
        Append(canonical, head.GraphId);
        Append(canonical, head.LifecycleVersion);
        Append(canonical, (int)head.Status);
        Append(canonical, head.DraftRevision);
        Append(canonical, head.PublishedRevision is null ? null : ComputePublicationPinHash(head.PublishedRevision));
        Append(canonical, head.LastOperationId);
        Append(canonical, head.UpdatedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Computes the canonical lowercase SHA-256 hash of one validated operation-evidence record.</summary>
    /// <param name="evidence">The validated operation evidence.</param>
    /// <returns>The canonical operation-evidence hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="evidence"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="evidence"/> is not a valid schema-1 contract.</exception>
    public static string ComputeOperationEvidenceHash(GovernedLoopRevisionOperationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        GovernedLoopRevisionContractGuard.RequireValid(GovernedLoopRevisionContractValidator.Validate(evidence), nameof(evidence));
        var canonical = Begin("governed-loop-revision-operation-evidence-v1");
        Append(canonical, evidence.SchemaVersion);
        Append(canonical, evidence.OperationId);
        Append(canonical, evidence.ActorId);
        Append(canonical, evidence.RequestHash);
        Append(canonical, (int)evidence.Kind);
        Append(canonical, (int)evidence.Outcome);
        Append(canonical, (int)evidence.FailureCode);
        Append(canonical, evidence.PreviousHead is null ? null : ComputeLifecycleHeadHash(evidence.PreviousHead));
        Append(canonical, evidence.ResultHead is null ? null : ComputeLifecycleHeadHash(evidence.ResultHead));
        Append(canonical, evidence.CandidateRevision);
        Append(canonical, evidence.TargetRevision);
        Append(canonical, evidence.RollbackSourcePublication is null ? null : ComputePublicationPinHash(evidence.RollbackSourcePublication));
        Append(canonical, evidence.AuthorityEvidenceHash);
        Append(canonical, evidence.PublicationValidationEvidenceHash);
        Append(canonical, evidence.RecordedAtUtc);
        return Digest(canonical);
    }

    private static StringBuilder Begin(string domain)
    {
        var canonical = new StringBuilder(512);
        Append(canonical, domain);
        return canonical;
    }

    private static void Append(StringBuilder canonical, GovernedLoopRevisionReference? revision)
    {
        if (revision is null)
        {
            Append(canonical, value: null);
            return;
        }

        Append(canonical, revision.SchemaVersion);
        Append(canonical, revision.GraphId);
        Append(canonical, revision.RevisionId);
        Append(canonical, revision.ExecutableHash);
    }

    private static void Append(StringBuilder canonical, DateTimeOffset value)
        => Append(canonical, value.ToString("O", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, int value)
        => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, long value)
        => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }

        canonical.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(value);
    }

    private static string Digest(StringBuilder canonical)
    {
        var bytes = Encoding.UTF8.GetBytes(canonical.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
