using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Failures;

/// <summary>Creates, authenticates, copies, and validates canonical value-free failure evidence.</summary>
public static class GovernedLoopFailureEvidenceContract
{
    /// <summary>The maximum number of causal evidence references retained by one classification.</summary>
    public const int MaxCausalEvidenceReferences = 32;
    /// <summary>The maximum length of one bounded server failure code.</summary>
    public const int MaxServerCodeCharacters = 64;
    /// <summary>The maximum length of optional safe redacted detail.</summary>
    public const int MaxSafeDetailCharacters = 256;
    /// <summary>The minimum server-owned precedence retained by one classified failure.</summary>
    public const int MinPrecedence = 1;
    /// <summary>The maximum server-owned precedence retained by one classified failure.</summary>
    public const int MaxPrecedence = 1_000;

    private static readonly string[] _secretMarkers =
    [
        "authorization:", "bearer ", "password=", "secret=", "token=", "private key", "ssh-rsa", "-----begin",
    ];

    /// <summary>Creates one validated schema-1 classification and applies its canonical content hash.</summary>
    public static GovernedLoopFailureEvidence Create(
        string evidenceId,
        string workspaceId,
        string runId,
        GovernedLoopRevisionReference revision,
        long executionGeneration,
        int activationOrdinal,
        int visitOrdinal,
        string nodeId,
        int attempt,
        GovernedLoopFailureClass failureClass,
        string serverCode,
        GovernedLoopFailureSource source,
        GovernedLoopFailureEffectCertainty effectCertainty,
        GovernedLoopFailureAuthorityPosture authorityPosture,
        GovernedLoopFailureHumanPosture humanPosture,
        GovernedLoopFailureRetrySafety retrySafety,
        GovernedLoopFailureSeverity severity,
        int precedence,
        IEnumerable<GovernedLoopFailureEvidenceReference> causalEvidence,
        string? safeDetail,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(causalEvidence);
        var candidate = new GovernedLoopFailureEvidence(
            GovernedLoopFailureEvidence.CurrentSchemaVersion,
            GovernedLoopFailureEvidence.CurrentMappingVersion,
            evidenceId,
            workspaceId,
            runId,
            revision,
            executionGeneration,
            activationOrdinal,
            visitOrdinal,
            nodeId,
            attempt,
            failureClass,
            serverCode,
            source,
            effectCertainty,
            authorityPosture,
            humanPosture,
            retrySafety,
            severity,
            precedence,
            Array.AsReadOnly(causalEvidence.Select(item => item with { }).ToArray()),
            safeDetail,
            observedAtUtc,
            string.Empty);
        RequireValid(candidate, requireHash: false);
        return candidate with { ContentHash = ComputeHash(candidate) };
    }

    /// <summary>Returns a defensive hash-verified copy of one classification.</summary>
    public static GovernedLoopFailureEvidence Copy(GovernedLoopFailureEvidence evidence)
    {
        RequireValid(evidence, requireHash: true);
        return evidence with
        {
            Revision = GovernedLoopRevisionReference.Create(evidence.Revision.SchemaVersion, evidence.Revision.GraphId, evidence.Revision.RevisionId, evidence.Revision.ExecutableHash),
            CausalEvidence = Array.AsReadOnly(evidence.CausalEvidence.Select(item => item with { }).ToArray()),
        };
    }

    /// <summary>Gets whether evidence is canonical, internally consistent, and hash-valid.</summary>
    public static bool IsValid(GovernedLoopFailureEvidence? evidence)
    {
        if (evidence is null)
        {
            return false;
        }

        try
        {
            RequireValid(evidence, requireHash: true);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Gets whether the classification is uncertainty evidence that must remain review-blocked.</summary>
    public static bool RequiresReview(GovernedLoopFailureEvidence? evidence)
        => IsValid(evidence)
            && evidence!.FailureClass is GovernedLoopFailureClass.AmbiguousExternalOutcome or GovernedLoopFailureClass.EvidenceIntegrityFailure;

    /// <summary>Computes the canonical lowercase SHA-256 digest over every field except the digest itself.</summary>
    public static string ComputeHash(GovernedLoopFailureEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        writer.WriteNumber("mappingVersion", evidence.MappingVersion);
        writer.WriteString("evidenceId", evidence.EvidenceId);
        writer.WriteString("workspaceId", evidence.WorkspaceId);
        writer.WriteString("runId", evidence.RunId);
        writer.WritePropertyName("revision");
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", evidence.Revision.SchemaVersion);
        writer.WriteString("graphId", evidence.Revision.GraphId);
        writer.WriteString("revisionId", evidence.Revision.RevisionId);
        writer.WriteString("executableHash", evidence.Revision.ExecutableHash);
        writer.WriteEndObject();
        writer.WriteNumber("executionGeneration", evidence.ExecutionGeneration);
        writer.WriteNumber("activationOrdinal", evidence.ActivationOrdinal);
        writer.WriteNumber("visitOrdinal", evidence.VisitOrdinal);
        writer.WriteString("nodeId", evidence.NodeId);
        writer.WriteNumber("attempt", evidence.Attempt);
        writer.WriteString("failureClass", Canonical(evidence.FailureClass));
        writer.WriteString("serverCode", evidence.ServerCode);
        writer.WriteString("source", Canonical(evidence.Source));
        writer.WriteString("effectCertainty", Canonical(evidence.EffectCertainty));
        writer.WriteString("authorityPosture", Canonical(evidence.AuthorityPosture));
        writer.WriteString("humanPosture", Canonical(evidence.HumanPosture));
        writer.WriteString("retrySafety", Canonical(evidence.RetrySafety));
        writer.WriteString("severity", Canonical(evidence.Severity));
        writer.WriteNumber("precedence", evidence.Precedence);
        writer.WritePropertyName("causalEvidence");
        writer.WriteStartArray();
        foreach (var reference in evidence.CausalEvidence)
        {
            writer.WriteStartObject();
            writer.WriteString("evidenceId", reference.EvidenceId);
            writer.WriteString("evidenceHash", reference.EvidenceHash);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("safeDetail", evidence.SafeDetail);
        writer.WriteString("observedAtUtc", evidence.ObservedAtUtc.ToUniversalTime());
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static void RequireValid(GovernedLoopFailureEvidence evidence, bool requireHash)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.SchemaVersion != GovernedLoopFailureEvidence.CurrentSchemaVersion || evidence.MappingVersion != GovernedLoopFailureEvidence.CurrentMappingVersion)
        {
            throw new ArgumentException("Failure evidence must use the exact supported schema and mapping versions.", nameof(evidence));
        }

        GovernedLoopExecutionContractGuard.RequireIdentifier(evidence.EvidenceId, nameof(evidence.EvidenceId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        GovernedLoopExecutionContractGuard.RequireWorkspaceId(evidence.WorkspaceId, nameof(evidence.WorkspaceId));
        GovernedLoopExecutionContractGuard.RequireIdentifier(evidence.RunId, nameof(evidence.RunId));
        ArgumentNullException.ThrowIfNull(evidence.Revision);
        var revision = GovernedLoopRevisionReference.Create(evidence.Revision.SchemaVersion, evidence.Revision.GraphId, evidence.Revision.RevisionId, evidence.Revision.ExecutableHash);
        if (!Equals(revision, evidence.Revision))
        {
            throw new ArgumentException("Failure evidence revision coordinates are not canonical.", nameof(evidence));
        }
        GovernedLoopExecutionContractGuard.RequirePositiveVersion(evidence.ExecutionGeneration, nameof(evidence.ExecutionGeneration));
        GovernedLoopExecutionContractGuard.RequireActivationOrdinal(evidence.ActivationOrdinal, nameof(evidence.ActivationOrdinal));
        GovernedLoopExecutionContractGuard.RequireVisitOrdinal(evidence.VisitOrdinal, nameof(evidence.VisitOrdinal));
        GovernedLoopExecutionContractGuard.RequireIdentifier(evidence.NodeId, nameof(evidence.NodeId));
        _ = GovernedLoopExecutionContractGuard.RequireOptionalAttempt(evidence.Attempt, nameof(evidence.Attempt));
        if (!IsDefined(evidence.FailureClass) || evidence.FailureClass == GovernedLoopFailureClass.Unknown
            || !IsDefined(evidence.Source) || evidence.Source == GovernedLoopFailureSource.Unknown
            || !IsDefined(evidence.EffectCertainty)
            || !IsDefined(evidence.AuthorityPosture)
            || !IsDefined(evidence.HumanPosture)
            || !IsDefined(evidence.RetrySafety)
            || !IsDefined(evidence.Severity) || evidence.Severity == GovernedLoopFailureSeverity.Unknown)
        {
            throw new ArgumentException("Failure evidence contains an unknown closed-taxonomy value.", nameof(evidence));
        }
        if (!IsServerCode(evidence.ServerCode) || !IsSafeDetail(evidence.SafeDetail))
        {
            throw new ArgumentException("Failure evidence code or safe detail is malformed or unsafe.", nameof(evidence));
        }
        if (evidence.Precedence is < MinPrecedence or > MaxPrecedence)
        {
            throw new ArgumentException("Failure evidence precedence is outside the closed server-owned lattice.", nameof(evidence));
        }
        if (evidence.ObservedAtUtc.Offset != TimeSpan.Zero || evidence.ObservedAtUtc == default)
        {
            throw new ArgumentException("Failure evidence observation time must be a non-default UTC value.", nameof(evidence));
        }
        if (evidence.CausalEvidence is null || evidence.CausalEvidence.Count is < 1 or > MaxCausalEvidenceReferences)
        {
            throw new ArgumentException("Failure evidence requires a bounded non-empty causal evidence set.", nameof(evidence));
        }
        string? prior = null;
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in evidence.CausalEvidence)
        {
            if (reference is null)
            {
                throw new ArgumentException("Failure causal evidence cannot contain null entries.", nameof(evidence));
            }
            GovernedLoopExecutionContractGuard.RequireIdentifier(reference.EvidenceId, nameof(reference.EvidenceId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
            GovernedLoopExecutionContractGuard.RequireSha256(reference.EvidenceHash, nameof(reference.EvidenceHash));
            var key = reference.EvidenceId + ":" + reference.EvidenceHash;
            if (!evidenceIds.Add(reference.EvidenceId) || prior is not null && string.CompareOrdinal(prior, key) >= 0)
            {
                throw new ArgumentException("Failure causal evidence must be strictly ordered and unique.", nameof(evidence));
            }
            prior = key;
        }
        RequireStateCombination(evidence);
        if (requireHash)
        {
            GovernedLoopExecutionContractGuard.RequireSha256(evidence.ContentHash, nameof(evidence.ContentHash));
            var expected = ComputeHash(evidence);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(evidence.ContentHash)))
            {
                throw new ArgumentException("Failure evidence content hash does not match its canonical fields.", nameof(evidence));
            }
        }
        else if (evidence.ContentHash.Length != 0)
        {
            throw new ArgumentException("Unhashed failure evidence must not carry a caller-supplied digest.", nameof(evidence));
        }
    }

    private static void RequireStateCombination(GovernedLoopFailureEvidence evidence)
    {
        var uncertainty = evidence.FailureClass is GovernedLoopFailureClass.AmbiguousExternalOutcome or GovernedLoopFailureClass.EvidenceIntegrityFailure;
        if (uncertainty != (evidence.Severity == GovernedLoopFailureSeverity.ReviewBlocked)
            || uncertainty && evidence.RetrySafety != GovernedLoopFailureRetrySafety.Unknown
            || evidence.FailureClass == GovernedLoopFailureClass.AmbiguousExternalOutcome && evidence.EffectCertainty != GovernedLoopFailureEffectCertainty.Ambiguous
            || evidence.FailureClass == GovernedLoopFailureClass.EvidenceIntegrityFailure && evidence.EffectCertainty != GovernedLoopFailureEffectCertainty.Unknown
            || evidence.FailureClass == GovernedLoopFailureClass.AuthorityPermissionDenied && evidence.AuthorityPosture is not (GovernedLoopFailureAuthorityPosture.Denied or GovernedLoopFailureAuthorityPosture.Revoked)
            || evidence.FailureClass == GovernedLoopFailureClass.ReviewRejected && evidence.HumanPosture != GovernedLoopFailureHumanPosture.ReviewRejected
            || evidence.FailureClass == GovernedLoopFailureClass.UserPaused && evidence.HumanPosture != GovernedLoopFailureHumanPosture.Paused
            || evidence.FailureClass == GovernedLoopFailureClass.UserCancelled && evidence.HumanPosture != GovernedLoopFailureHumanPosture.Cancelled
            || evidence.RetrySafety == GovernedLoopFailureRetrySafety.RetryableWithExactIntent && evidence.EffectCertainty is not (GovernedLoopFailureEffectCertainty.DispatchProvedNotStarted or GovernedLoopFailureEffectCertainty.EffectProvedAbsent))
        {
            throw new ArgumentException("Failure evidence posture contradicts its canonical class, certainty, or retry safety.", nameof(evidence));
        }
    }

    /// <summary>Gets whether a value is one bounded lowercase server-owned failure code.</summary>
    public static bool IsServerCode(string? value)
        => value is { Length: >= 1 and <= MaxServerCodeCharacters }
            && value[0] is >= 'a' and <= 'z'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');

    /// <summary>Gets whether optional detail is bounded, normalized, path-free, and free of common secret-bearing shapes.</summary>
    public static bool IsSafeDetail(string? value)
    {
        if (value is null)
        {
            return true;
        }
        if (!CapabilityTextRules.IsSafeNormalized(value, MaxSafeDetailCharacters, allowEmpty: false)
            || value.Contains('/')
            || value.Contains('\\'))
        {
            return false;
        }
        var lowered = value.ToLowerInvariant();
        return !_secretMarkers.Any(lowered.Contains);
    }

    private static bool IsDefined<T>(T value) where T : struct, Enum
        => Enum.IsDefined(value);

    private static string Canonical<T>(T value) where T : struct, Enum
    {
        var name = Enum.GetName(value) ?? throw new ArgumentOutOfRangeException(nameof(value));
        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('-');
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
