using System.Globalization;
using System.Text;
using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Persistence.WorkspaceActions.Models;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Authenticates one bounded content-bearing internal artifact to its exact effect attempt and before evidence.</summary>
internal sealed record WorkspaceActionAttemptArtifactMarker(
    int SchemaVersion,
    WorkspaceActionAttemptArtifactKind Kind,
    string ArtifactReference,
    string BeforeEvidenceId,
    string BeforeEvidenceHash,
    string TargetFingerprint,
    string TargetReference,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration,
    string ContentHash,
    long ByteCount,
    DateTimeOffset CreatedAtUtc,
    string MarkerHash)
{
    public const int MaximumEncodedBytes = 2048;

    public static WorkspaceActionAttemptArtifactMarker CreateStage(
        string stageName,
        WorkspaceActionBeforeEvidence before,
        WorkspaceActionNativeExecutionRequest request,
        string afterHash,
        long byteCount,
        DateTimeOffset createdAtUtc)
        => Create(
            WorkspaceActionAttemptArtifactKind.Stage,
            stageName,
            before,
            request,
            afterHash,
            byteCount,
            createdAtUtc);

    public static WorkspaceActionAttemptArtifactMarker CreateQuarantine(
        string quarantineReference,
        WorkspaceActionBeforeEvidence before,
        WorkspaceActionNativeExecutionRequest request,
        DateTimeOffset createdAtUtc)
        => Create(
            WorkspaceActionAttemptArtifactKind.QuarantineReservation,
            quarantineReference,
            before,
            request,
            before.ContentHash!,
            before.ByteCount,
            createdAtUtc);

    public string Encode()
    {
        var canonical = CanonicalWithoutHash(this);
        return canonical + MarkerHash + "\n";
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out WorkspaceActionAttemptArtifactMarker? marker)
    {
        marker = null;
        if (encoded.Length is < 1 or > MaximumEncodedBytes)
        {
            return false;
        }
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(encoded);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        var fields = text.Split('\n', StringSplitOptions.None);
        if (fields.Length != 15
            || fields[^1].Length != 0
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var schemaVersion)
            || !Enum.TryParse<WorkspaceActionAttemptArtifactKind>(fields[1], ignoreCase: false, out var kind)
            || !long.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || !long.TryParse(fields[11], NumberStyles.None, CultureInfo.InvariantCulture, out var byteCount)
            || !DateTimeOffset.TryParseExact(fields[12], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var createdAtUtc))
        {
            return false;
        }
        var candidate = new WorkspaceActionAttemptArtifactMarker(
            schemaVersion,
            kind,
            fields[2],
            fields[3],
            fields[4],
            fields[5],
            fields[6],
            fields[7],
            fields[8],
            generation,
            fields[10],
            byteCount,
            createdAtUtc,
            fields[13]);
        if (!IsValid(candidate))
        {
            return false;
        }
        marker = candidate;
        return true;
    }

    public bool MatchesBefore(WorkspaceActionBeforeEvidence before)
        => WorkspaceActionEvidenceContract.ValidateBefore(before) is null
            && string.Equals(BeforeEvidenceId, before.EvidenceId, StringComparison.Ordinal)
            && string.Equals(BeforeEvidenceHash, before.ContentHashOfRecord, StringComparison.Ordinal)
            && string.Equals(TargetFingerprint, before.TargetFingerprint, StringComparison.Ordinal)
            && string.Equals(TargetReference, before.TargetReference, StringComparison.Ordinal);

    public bool HasSameArtifactBinding(WorkspaceActionAttemptArtifactMarker other)
        => other is not null
            && SchemaVersion == other.SchemaVersion
            && Kind == other.Kind
            && string.Equals(ArtifactReference, other.ArtifactReference, StringComparison.Ordinal)
            && string.Equals(BeforeEvidenceId, other.BeforeEvidenceId, StringComparison.Ordinal)
            && string.Equals(BeforeEvidenceHash, other.BeforeEvidenceHash, StringComparison.Ordinal)
            && string.Equals(TargetFingerprint, other.TargetFingerprint, StringComparison.Ordinal)
            && string.Equals(TargetReference, other.TargetReference, StringComparison.Ordinal)
            && string.Equals(EffectId, other.EffectId, StringComparison.Ordinal)
            && string.Equals(IdempotencyOperationId, other.IdempotencyOperationId, StringComparison.Ordinal)
            && EffectGeneration == other.EffectGeneration
            && string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal)
            && ByteCount == other.ByteCount;

    private static WorkspaceActionAttemptArtifactMarker Create(
        WorkspaceActionAttemptArtifactKind kind,
        string artifactReference,
        WorkspaceActionBeforeEvidence before,
        WorkspaceActionNativeExecutionRequest request,
        string contentHash,
        long byteCount,
        DateTimeOffset createdAtUtc)
    {
        var candidate = new WorkspaceActionAttemptArtifactMarker(
            WorkspaceActionContractLimits.CurrentSchemaVersion,
            kind,
            artifactReference,
            before.EvidenceId,
            before.ContentHashOfRecord,
            before.TargetFingerprint,
            before.TargetReference,
            request.EffectId,
            request.IdempotencyOperationId,
            request.EffectGeneration,
            contentHash,
            byteCount,
            createdAtUtc,
            string.Empty);
        candidate = candidate with { MarkerHash = ComputeHash(candidate) };
        if (!IsValid(candidate))
        {
            throw new ArgumentException("Workspace action attempt artifact marker is outside the closed schema-1 contract.");
        }
        return candidate;
    }

    private static bool IsValid(WorkspaceActionAttemptArtifactMarker marker)
    {
        if (marker.SchemaVersion != WorkspaceActionContractLimits.CurrentSchemaVersion
            || !Enum.IsDefined(marker.Kind)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(marker.BeforeEvidenceId)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(marker.BeforeEvidenceHash)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(marker.TargetFingerprint)
            || !WorkspaceRelativeFileTarget.TryParse(marker.TargetReference, out _, out _)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(marker.EffectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(marker.IdempotencyOperationId)
            || marker.EffectGeneration < 1
            || !WorkspaceActionFingerprint.IsCanonicalSha256(marker.ContentHash)
            || marker.ByteCount is < 0 or > WorkspaceActionContractLimits.MaxAfterImageBytes
            || marker.CreatedAtUtc == default
            || marker.CreatedAtUtc.Offset != TimeSpan.Zero
            || !WorkspaceActionFingerprint.IsCanonicalSha256(marker.MarkerHash)
            || !string.Equals(marker.MarkerHash, ComputeHash(marker), StringComparison.Ordinal))
        {
            return false;
        }
        var expectedReference = marker.Kind switch
        {
            WorkspaceActionAttemptArtifactKind.Stage => "stage-" + WorkspaceActionFingerprint.Compute(
                "embodysense.workspace-action-stage.v1",
                marker.BeforeEvidenceHash,
                marker.EffectId,
                marker.IdempotencyOperationId,
                marker.EffectGeneration.ToString(CultureInfo.InvariantCulture),
                marker.ContentHash) + ".stage",
            WorkspaceActionAttemptArtifactKind.QuarantineReservation => "quarantine-" + WorkspaceActionFingerprint.Compute(
                "embodysense.workspace-delete-quarantine.v1",
                marker.BeforeEvidenceHash,
                marker.EffectId,
                marker.IdempotencyOperationId,
                marker.EffectGeneration.ToString(CultureInfo.InvariantCulture)),
            _ => string.Empty,
        };
        return string.Equals(marker.ArtifactReference, expectedReference, StringComparison.Ordinal)
            && Encoding.UTF8.GetByteCount(marker.Encode()) <= MaximumEncodedBytes;
    }

    private static string ComputeHash(WorkspaceActionAttemptArtifactMarker marker)
        => WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-action-attempt-artifact-marker.v1",
            CanonicalWithoutHash(marker));

    private static string CanonicalWithoutHash(WorkspaceActionAttemptArtifactMarker marker)
        => string.Join(
            '\n',
            marker.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            marker.Kind.ToString(),
            marker.ArtifactReference,
            marker.BeforeEvidenceId,
            marker.BeforeEvidenceHash,
            marker.TargetFingerprint,
            marker.TargetReference,
            marker.EffectId,
            marker.IdempotencyOperationId,
            marker.EffectGeneration.ToString(CultureInfo.InvariantCulture),
            marker.ContentHash,
            marker.ByteCount.ToString(CultureInfo.InvariantCulture),
            marker.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)) + "\n";
}
