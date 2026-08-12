using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Persistence.ContextualRoles.Models;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Persistence.ContextualRoles;

internal static class ContextualRoleArtifactCodec
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);

    public static T Deserialize<T>(byte[] bytes, string artifactName)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, _jsonOptions) ?? throw new FormatException($"{artifactName} was empty.");
        }
        catch (JsonException exception)
        {
            throw new FormatException($"{artifactName} contains invalid, unknown, or unsupported schema-1 JSON.", exception);
        }
    }

    public static ContextualRoleWorkspaceAnchor Seal(ContextualRoleWorkspaceAnchor value)
    {
        var unsigned = value with { IntegrityHash = string.Empty };
        return unsigned with { IntegrityHash = Hash(Serialize(unsigned)) };
    }

    public static ContextualRoleRevisionArtifact Seal(ContextualRoleRevisionArtifact value)
    {
        var unsigned = value with { IntegrityHash = string.Empty };
        return unsigned with { IntegrityHash = Hash(Serialize(unsigned)) };
    }

    public static ContextualRolePrimaryStateArtifact Seal(ContextualRolePrimaryStateArtifact value)
    {
        var unsigned = value with { IntegrityHash = string.Empty };
        return unsigned with { IntegrityHash = Hash(Serialize(unsigned)) };
    }

    public static ContextualRoleMutationIntentArtifact Seal(ContextualRoleMutationIntentArtifact value)
    {
        var unsigned = value with { IntegrityHash = string.Empty };
        return unsigned with { IntegrityHash = Hash(Serialize(unsigned)) };
    }

    public static ContextualRoleLifecycleProofArtifact Seal(ContextualRoleLifecycleProofArtifact value)
    {
        var unsigned = value with { IntegrityHash = string.Empty };
        return unsigned with { IntegrityHash = Hash(Serialize(unsigned)) };
    }

    public static ContextualRoleMutationResultArtifact Seal(ContextualRoleMutationResultArtifact value)
    {
        var unsigned = value with { IntegrityHash = string.Empty };
        return unsigned with { IntegrityHash = Hash(Serialize(unsigned)) };
    }

    public static void Validate(ContextualRoleWorkspaceAnchor value)
    {
        if (value.SchemaVersion != SchemaVersion
            || !ContextualRoleWorkspaceId.IsValid(value.WorkspaceId)
            || !IsHash(value.CanonicalRootHash)
            || value.RootCreationTimeUtcTicks <= 0
            || !IsUtc(value.CreatedAtUtc)
            || !Matches(value.IntegrityHash, Seal(value).IntegrityHash))
        {
            throw new FormatException("Contextual-role workspace anchor is malformed or has failed integrity validation.");
        }
    }

    public static void Validate(ContextualRoleRevisionArtifact value, string anchorHash)
    {
        if (value.SchemaVersion != SchemaVersion
            || !Matches(value.WorkspaceAnchorHash, anchorHash)
            || !ContextualRoleRevisionValidator.Validate(value.Revision).IsValid
            || !Matches(value.IntegrityHash, Seal(value).IntegrityHash))
        {
            throw new FormatException("Contextual-role immutable revision artifact is malformed or has failed integrity validation.");
        }
    }

    public static void Validate(ContextualRolePrimaryStateArtifact value, string anchorHash)
    {
        if (value.SchemaVersion != SchemaVersion
            || !Matches(value.WorkspaceAnchorHash, anchorHash)
            || !ContextualRoleId.IsValid(value.RoleId)
            || value.CurrentIdentity is null
            || !string.Equals(value.RoleId, value.CurrentIdentity.RoleId, StringComparison.Ordinal)
            || value.CurrentIdentity.Revision < 1
            || value.State is < ContextualRoleLifecycleState.Active or > ContextualRoleLifecycleState.Tombstoned
            || !ContextualRoleId.IsValid(value.LastOperationId)
            || value.LastMutationKind is < ContextualRoleRevisionMutationKind.Create or > ContextualRoleRevisionMutationKind.Tombstone
            || value.Sequence < 1
            || !IsUtc(value.UpdatedAtUtc)
            || !Matches(value.IntegrityHash, Seal(value).IntegrityHash))
        {
            throw new FormatException("Contextual-role primary lifecycle artifact is malformed or has failed integrity validation.");
        }
    }

    public static void Validate(ContextualRoleMutationIntentArtifact value, string anchorHash)
    {
        var requestErrors = ContextualRoleRevisionMutationRequestValidator.Validate(value.Request);
        if (value.SchemaVersion != SchemaVersion
            || !Matches(value.WorkspaceAnchorHash, anchorHash)
            || requestErrors.Count != 0
            || value.PriorState is not null && !NestedStateIsValid(value.PriorState, anchorHash)
            || value.PlannedState is not null && !NestedStateIsValid(value.PlannedState, anchorHash)
            || value.IntendedOutcome is not (ContextualRoleRevisionMutationStatus.Accepted or ContextualRoleRevisionMutationStatus.Conflict)
            || !IsUtc(value.RecordedAtUtc)
            || !Matches(value.IntegrityHash, Seal(value).IntegrityHash))
        {
            throw new FormatException("Contextual-role mutation intent artifact is malformed or has failed integrity validation.");
        }

        if (value.PriorState is not null && !string.Equals(value.PriorState.RoleId, value.Request.RoleId, StringComparison.Ordinal)
            || value.PlannedState is not null && !string.Equals(value.PlannedState.RoleId, value.Request.RoleId, StringComparison.Ordinal)
            || value.IntendedOutcome == ContextualRoleRevisionMutationStatus.Accepted && value.PlannedState is null)
        {
            throw new FormatException("Contextual-role mutation intent does not preserve one exact role lineage.");
        }
    }

    public static void Validate(ContextualRoleLifecycleProofArtifact value, string anchorHash)
    {
        if (value.SchemaVersion != SchemaVersion
            || !Matches(value.WorkspaceAnchorHash, anchorHash)
            || !EvidenceIsValid(value.Evidence)
            || !Matches(value.IntegrityHash, Seal(value).IntegrityHash))
        {
            throw new FormatException("Contextual-role lifecycle proof is malformed or has failed integrity validation.");
        }
    }

    public static void Validate(ContextualRoleMutationResultArtifact value, string anchorHash)
    {
        if (value.SchemaVersion != SchemaVersion
            || !Matches(value.WorkspaceAnchorHash, anchorHash)
            || value.Status is not (ContextualRoleRevisionMutationStatus.Accepted or ContextualRoleRevisionMutationStatus.Conflict or ContextualRoleRevisionMutationStatus.Recovered)
            || !ContextualRoleId.IsValid(value.OperationId)
            || !IsHash(value.RequestHash)
            || value.Kind is < ContextualRoleRevisionMutationKind.Create or > ContextualRoleRevisionMutationKind.Tombstone
            || !EvidenceIsValid(value.Evidence)
            || value.Status != value.Evidence.Outcome
            || !string.Equals(value.OperationId, value.Evidence.OperationId, StringComparison.Ordinal)
            || !Matches(value.RequestHash, value.Evidence.RequestHash)
            || value.Kind != value.Evidence.Kind
            || value.Revision is not null && !ContextualRoleRevisionValidator.Validate(value.Revision).IsValid
            || !Matches(value.IntegrityHash, Seal(value).IntegrityHash))
        {
            throw new FormatException("Contextual-role immutable replay result is malformed or has failed integrity validation.");
        }
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static bool Equivalent(ContextualRolePrimaryStateArtifact? left, ContextualRolePrimaryStateArtifact? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return Matches(left.IntegrityHash, right.IntegrityHash);
    }

    private static bool NestedStateIsValid(ContextualRolePrimaryStateArtifact value, string anchorHash)
    {
        try
        {
            Validate(value, anchorHash);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool EvidenceIsValid(ContextualRoleLifecycleEvidence? evidence)
    {
        return evidence is not null
            && evidence.SchemaVersion == SchemaVersion
            && ContextualRoleId.IsValid(evidence.OperationId)
            && IsHash(evidence.RequestHash)
            && evidence.Kind is >= ContextualRoleRevisionMutationKind.Create and <= ContextualRoleRevisionMutationKind.Tombstone
            && ContextualRoleId.IsValid(evidence.RoleId)
            && ContextualRoleId.IsValid(evidence.ActorId)
            && (evidence.PreviousIdentity is null || IdentityIsValid(evidence.PreviousIdentity, evidence.RoleId))
            && (evidence.PreviousStateHash is null || IsHash(evidence.PreviousStateHash))
            && (evidence.PreviousIdentity is null) == (evidence.PreviousStateHash is null)
            && (evidence.CurrentIdentity is null || IdentityIsValid(evidence.CurrentIdentity, evidence.RoleId))
            && (evidence.CurrentStateHash is null || IsHash(evidence.CurrentStateHash))
            && (evidence.CurrentIdentity is null) == (evidence.CurrentStateHash is null)
            && (evidence.CurrentIdentity is null ? evidence.Sequence == 0 : evidence.Sequence > 0)
            && evidence.State is >= ContextualRoleLifecycleState.Active and <= ContextualRoleLifecycleState.Absent
            && (evidence.State == ContextualRoleLifecycleState.Absent) == (evidence.CurrentIdentity is null)
            && evidence.Outcome is ContextualRoleRevisionMutationStatus.Accepted or ContextualRoleRevisionMutationStatus.Conflict or ContextualRoleRevisionMutationStatus.Recovered
            && IsUtc(evidence.RequestedAtUtc)
            && IsUtc(evidence.RecordedAtUtc)
            && (evidence.Outcome != ContextualRoleRevisionMutationStatus.Recovered || evidence.Recovered)
            && (evidence.Outcome != ContextualRoleRevisionMutationStatus.Accepted || !evidence.Recovered);
    }

    private static bool IdentityIsValid(EmbodySense.Core.Common.ContextualRoles.Models.ContextualRoleRevisionIdentity identity, string roleId)
        => string.Equals(identity.RoleId, roleId, StringComparison.Ordinal) && identity.Revision > 0;

    private static bool IsHash(string? value) => value is { Length: ContextualRoleLimits.Sha256HexCharacters }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool Matches(string? left, string? right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left ?? string.Empty);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
