using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Common.Credentials.Leases;

/// <summary>Creates and validates exact, value-free, short-lived credential lease evidence.</summary>
public static class CredentialLeaseContract
{
    private const string IntentDomain = "embodysense.credential-lease-intent.v1";
    private const string VersionDomain = "embodysense.credential-lease-version.v1";
    private const string TargetDomain = "embodysense.credential-lease-target.v1";
    private const string EvidenceDomain = "embodysense.credential-lease-evidence.v1";

    private static readonly JsonSerializerOptions _hashOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Returns an immutable intent carrying its exact domain-separated content hash.</summary>
    public static CredentialLeaseIntent ApplyIntentHash(CredentialLeaseIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var candidate = intent with { ContentHash = string.Empty };
        var reason = ValidateIntent(candidate, requireHash: false);
        if (reason is not null)
        {
            throw new ArgumentException(reason, nameof(intent));
        }

        return candidate with { ContentHash = Compute(IntentDomain, candidate) };
    }

    /// <summary>Creates the first durable pre-redemption version for an exact intent.</summary>
    public static CredentialLeaseAttemptVersion Prepare(CredentialLeaseIntent intent, DateTimeOffset recordedAtUtc)
    {
        RequireIntent(intent);
        if (!IsUtc(recordedAtUtc) || recordedAtUtc < intent.IssuedAtUtc || recordedAtUtc >= intent.EffectiveExpiresAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(recordedAtUtc));
        }

        return ApplyVersionHash(new CredentialLeaseAttemptVersion(
            CredentialLeaseAttemptVersion.CurrentSchemaVersion,
            intent.LeaseId,
            1,
            intent.ContentHash,
            CredentialLeasePhase.IntentPrepared,
            CredentialLeaseOutcome.Pending,
            recordedAtUtc,
            null,
            null,
            null,
            null,
            string.Empty));
    }

    /// <summary>Advances one attempt through one legal, immutable, directly hash-linked transition.</summary>
    public static CredentialLeaseAttemptVersion Advance(
        CredentialLeaseIntent intent,
        CredentialLeaseAttemptVersion current,
        CredentialLeasePhase phase,
        DateTimeOffset recordedAtUtc,
        string? currentAuthorityEvidenceHash = null,
        string? registryEvidenceHash = null,
        CredentialFailureCode? failureCode = null)
    {
        RequireIntent(intent);
        RequireVersion(intent, current);
        if (!IsAllowedTransition(current.Phase, phase) || !IsUtc(recordedAtUtc) || recordedAtUtc < current.RecordedAtUtc)
        {
            throw new InvalidOperationException("The requested credential-lease phase is not a legal direct successor.");
        }
        if (phase is CredentialLeasePhase.Authorized or CredentialLeasePhase.RedemptionBoundaryReached
            && recordedAtUtc >= intent.EffectiveExpiresAtUtc)
        {
            throw new InvalidOperationException("The credential-lease redemption boundary must be entered before exact expiry.");
        }

        var authority = currentAuthorityEvidenceHash ?? current.CurrentAuthorityEvidenceHash;
        var registry = registryEvidenceHash ?? current.RegistryEvidenceHash;
        var outcome = OutcomeFor(phase);
        var next = new CredentialLeaseAttemptVersion(
            CredentialLeaseAttemptVersion.CurrentSchemaVersion,
            intent.LeaseId,
            checked(current.Version + 1),
            intent.ContentHash,
            phase,
            outcome,
            recordedAtUtc,
            authority,
            registry,
            failureCode,
            current.ContentHash,
            string.Empty);
        var hashed = ApplyVersionHash(next);
        if (!IsDirectSuccessor(intent, current, hashed))
        {
            throw new InvalidOperationException("The requested credential-lease version changed immutable evidence.");
        }

        return hashed;
    }

    /// <summary>Creates a bounded immutable attempt history after validating the complete direct chain.</summary>
    public static CredentialLeaseAttemptHistory CreateHistory(CredentialLeaseIntent intent, IEnumerable<CredentialLeaseAttemptVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var history = new CredentialLeaseAttemptHistory(CredentialLeaseAttemptHistory.CurrentSchemaVersion, intent, versions.ToArray());
        var reason = Validate(history);
        return reason is null ? history : throw new ArgumentException(reason, nameof(versions));
    }

    /// <summary>Returns a bounded reason code when an intent is invalid; otherwise <see langword="null"/>.</summary>
    public static string? Validate(CredentialLeaseIntent? intent) => ValidateIntent(intent, requireHash: true);

    /// <summary>Returns a bounded reason code when an attempt history is malformed, forked, or disconnected.</summary>
    public static string? Validate(CredentialLeaseAttemptHistory? history)
    {
        if (history is null || history.SchemaVersion != CredentialLeaseAttemptHistory.CurrentSchemaVersion)
        {
            return "credential-lease-history-schema-invalid";
        }

        var intentReason = Validate(history.Intent);
        if (intentReason is not null)
        {
            return intentReason;
        }
        if (history.Versions is null || history.Versions.Count is < 1 or > CredentialLeaseContractLimits.MaximumVersions)
        {
            return "credential-lease-history-bounds-invalid";
        }

        for (var index = 0; index < history.Versions.Count; index++)
        {
            var version = history.Versions[index];
            var reason = ValidateVersion(history.Intent, version);
            if (reason is not null)
            {
                return reason;
            }
            if (index == 0)
            {
                if (version.Version != 1 || version.Phase != CredentialLeasePhase.IntentPrepared || version.PreviousContentHash is not null)
                {
                    return "credential-lease-history-first-version-invalid";
                }
            }
            else if (!IsDirectSuccessor(history.Intent, history.Versions[index - 1], version))
            {
                return "credential-lease-history-disconnected";
            }
        }

        return null;
    }

    /// <summary>Gets whether a validated version is the exact direct successor of another.</summary>
    public static bool IsDirectSuccessor(CredentialLeaseIntent intent, CredentialLeaseAttemptVersion current, CredentialLeaseAttemptVersion next)
    {
        if (ValidateVersion(intent, current) is not null
            || ValidateVersion(intent, next) is not null
            || next.Version != current.Version + 1
            || next.RecordedAtUtc < current.RecordedAtUtc
            || !string.Equals(next.PreviousContentHash, current.ContentHash, StringComparison.Ordinal)
            || !string.Equals(next.LeaseId, current.LeaseId, StringComparison.Ordinal)
            || !string.Equals(next.IntentHash, current.IntentHash, StringComparison.Ordinal)
            || !IsAllowedTransition(current.Phase, next.Phase))
        {
            return false;
        }

        if (current.Phase == CredentialLeasePhase.IntentPrepared && next.Phase == CredentialLeasePhase.Authorized)
        {
            return next.CurrentAuthorityEvidenceHash is not null && next.RegistryEvidenceHash is not null;
        }

        return string.Equals(next.CurrentAuthorityEvidenceHash, current.CurrentAuthorityEvidenceHash, StringComparison.Ordinal)
            && string.Equals(next.RegistryEvidenceHash, current.RegistryEvidenceHash, StringComparison.Ordinal);
    }

    /// <summary>Computes a domain-separated, value-free fingerprint for a server-resolved target representation.</summary>
    public static string ComputeTargetFingerprint(string targetClass, ReadOnlySpan<byte> canonicalServerTarget)
    {
        if (!IsToken(targetClass) || canonicalServerTarget.IsEmpty || canonicalServerTarget.Length > CredentialLeaseContractLimits.MaximumRecordUtf8Bytes)
        {
            throw new ArgumentException("The target fingerprint input is invalid.", nameof(canonicalServerTarget));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, TargetDomain);
        Append(hash, targetClass);
        Append(hash, canonicalServerTarget);
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Derives the generation-unique public evidence identity for one credential-use attempt.</summary>
    /// <param name="credentialUseOperationId">The canonical credential-use operation identity.</param>
    /// <param name="credentialUseGeneration">The positive attempt generation under that operation.</param>
    /// <returns>A deterministic identity that cannot alias another generation of the operation.</returns>
    /// <exception cref="ArgumentException">The operation identity or generation is invalid.</exception>
    public static CredentialContractId ComputeEvidenceId(string credentialUseOperationId, long credentialUseGeneration)
    {
        if (!IsId(credentialUseOperationId) || credentialUseGeneration < 1)
        {
            throw new ArgumentException("The credential-use evidence identity input is invalid.", nameof(credentialUseOperationId));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, EvidenceDomain);
        Append(hash, credentialUseOperationId);
        Append(hash, credentialUseGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var value = "credential-evidence-" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return CredentialContractId.TryParse(value, out var evidenceId, out _)
            ? evidenceId!
            : throw new InvalidOperationException("The canonical credential-use evidence identity is invalid.");
    }

    /// <summary>Computes the exact earliest authoritative lease-entry deadline.</summary>
    public static DateTimeOffset ComputeEffectiveExpiry(DateTimeOffset issuedAtUtc, CredentialLeaseDeadlines deadlines)
    {
        ArgumentNullException.ThrowIfNull(deadlines);
        if (!IsUtc(issuedAtUtc))
        {
            throw new ArgumentOutOfRangeException(nameof(issuedAtUtc));
        }

        var values = new DateTimeOffset?[]
        {
            issuedAtUtc + CredentialLeaseContractLimits.MaximumEntryLifetime,
            deadlines.ProofExpiresAtUtc,
            deadlines.ReferenceExpiresAtUtc,
            deadlines.ScopeExpiresAtUtc,
            deadlines.GrantExpiresAtUtc,
            deadlines.DelegationExpiresAtUtc,
            deadlines.ProfileExpiresAtUtc,
            deadlines.EffectExpiresAtUtc,
            deadlines.RuntimeExpiresAtUtc,
        };
        if (values.Any(value => value is not null && (!IsUtc(value.Value) || value.Value <= issuedAtUtc)))
        {
            throw new ArgumentException("Every applicable lease deadline must be UTC and later than issuance.", nameof(deadlines));
        }

        return values.Where(value => value is not null).Min(value => value!.Value);
    }

    private static CredentialLeaseAttemptVersion ApplyVersionHash(CredentialLeaseAttemptVersion version)
    {
        var candidate = version with { ContentHash = string.Empty };
        var reason = ValidateVersionForHash(candidate, requireHash: false);
        if (reason is not null)
        {
            throw new ArgumentException(reason, nameof(version));
        }

        return candidate with { ContentHash = Compute(VersionDomain, candidate) };
    }

    private static string? ValidateIntent(CredentialLeaseIntent? intent, bool requireHash)
    {
        if (intent is null || intent.SchemaVersion != CredentialLeaseIntent.CurrentSchemaVersion)
        {
            return "credential-lease-intent-schema-invalid";
        }
        if (!IsId(intent.LeaseId) || !IsId(intent.CredentialUseOperationId) || intent.CredentialUseGeneration < 1)
        {
            return "credential-lease-intent-identity-invalid";
        }
        if (!ValidateExecution(intent.Execution)
            || !ValidateAuthority(intent.Authority)
            || !ValidateEffect(intent.Effect)
            || !ValidateCapability(intent.Capability)
            || !ValidateProfile(intent.Profile)
            || !ValidateRegistry(intent.Registry)
            || !ValidateTarget(intent.Target))
        {
            return "credential-lease-intent-scope-invalid";
        }
        if (!IsUtc(intent.IssuedAtUtc) || intent.Deadlines is null)
        {
            return "credential-lease-intent-time-invalid";
        }

        DateTimeOffset expectedExpiry;
        try
        {
            expectedExpiry = ComputeEffectiveExpiry(intent.IssuedAtUtc, intent.Deadlines);
        }
        catch (ArgumentException)
        {
            return "credential-lease-intent-time-invalid";
        }
        if (intent.EffectiveExpiresAtUtc != expectedExpiry)
        {
            return "credential-lease-intent-expiry-invalid";
        }

        if (requireHash && (!IsPrefixedHash(intent.ContentHash) || !FixedTimeEquals(intent.ContentHash, Compute(IntentDomain, intent with { ContentHash = string.Empty }))))
        {
            return "credential-lease-intent-hash-invalid";
        }
        if (!requireHash && intent.ContentHash.Length != 0)
        {
            return "credential-lease-intent-hash-invalid";
        }

        return null;
    }

    private static string? ValidateVersion(CredentialLeaseIntent intent, CredentialLeaseAttemptVersion? version)
    {
        var reason = ValidateVersionForHash(version, requireHash: true);
        if (reason is not null)
        {
            return reason;
        }
        return version!.RecordedAtUtc < intent.IssuedAtUtc
            || version.Phase is CredentialLeasePhase.IntentPrepared or CredentialLeasePhase.Authorized or CredentialLeasePhase.RedemptionBoundaryReached
                && version.RecordedAtUtc >= intent.EffectiveExpiresAtUtc
            ? "credential-lease-version-time-invalid"
            : !string.Equals(version.LeaseId, intent.LeaseId, StringComparison.Ordinal)
            || !string.Equals(version.IntentHash, intent.ContentHash, StringComparison.Ordinal)
            ? "credential-lease-version-intent-mismatch"
            : null;
    }

    private static string? ValidateVersionForHash(CredentialLeaseAttemptVersion? version, bool requireHash)
    {
        if (version is null || version.SchemaVersion != CredentialLeaseAttemptVersion.CurrentSchemaVersion)
        {
            return "credential-lease-version-schema-invalid";
        }
        if (!IsId(version.LeaseId) || version.Version is < 1 or > CredentialLeaseContractLimits.MaximumVersions || !IsPrefixedHash(version.IntentHash))
        {
            return "credential-lease-version-identity-invalid";
        }
        if (!Enum.IsDefined(version.Phase) || !Enum.IsDefined(version.Outcome) || !IsUtc(version.RecordedAtUtc) || !MatchesOutcome(version.Phase, version.Outcome))
        {
            return "credential-lease-version-posture-invalid";
        }
        if (!ValidEvidence(version.CurrentAuthorityEvidenceHash) || !ValidEvidence(version.RegistryEvidenceHash) || !ValidEvidence(version.PreviousContentHash))
        {
            return "credential-lease-version-evidence-invalid";
        }

        if (version.Phase == CredentialLeasePhase.IntentPrepared && (version.CurrentAuthorityEvidenceHash is not null || version.RegistryEvidenceHash is not null)
            || version.Phase is CredentialLeasePhase.Authorized or CredentialLeasePhase.RedemptionBoundaryReached or CredentialLeasePhase.Redeemed or CredentialLeasePhase.RedemptionFailed or CredentialLeasePhase.RedemptionAmbiguous
                && (version.CurrentAuthorityEvidenceHash is null || version.RegistryEvidenceHash is null)
            || version.Phase is CredentialLeasePhase.IntentPrepared or CredentialLeasePhase.Authorized or CredentialLeasePhase.RedemptionBoundaryReached && version.FailureCode is not null
            || version.Phase == CredentialLeasePhase.Redeemed && version.FailureCode is not null
            || version.Phase is CredentialLeasePhase.NotRedeemed or CredentialLeasePhase.RedemptionFailed or CredentialLeasePhase.RedemptionAmbiguous && version.FailureCode is null
            || version.Phase == CredentialLeasePhase.RedemptionFailed && version.FailureCode == CredentialFailureCode.OutcomeUncertain
            || version.Phase == CredentialLeasePhase.RedemptionAmbiguous && version.FailureCode != CredentialFailureCode.OutcomeUncertain)
        {
            return "credential-lease-version-failure-invalid";
        }
        if (version.FailureCode is { } failure && !Enum.IsDefined(failure))
        {
            return "credential-lease-version-failure-invalid";
        }

        if (requireHash && (!IsPrefixedHash(version.ContentHash) || !FixedTimeEquals(version.ContentHash, Compute(VersionDomain, version with { ContentHash = string.Empty }))))
        {
            return "credential-lease-version-hash-invalid";
        }
        if (!requireHash && version.ContentHash.Length != 0)
        {
            return "credential-lease-version-hash-invalid";
        }

        return null;
    }

    private static bool ValidateExecution(CredentialLeaseExecutionScope? value)
        => value is not null
            && IsId(value.WorkspaceId)
            && IsId(value.ActorId)
            && IsHash(value.ActorAuthenticationEvidenceHash)
            && IsHash(value.AttributionEvidenceHash)
            && IsHash(value.AdmissionReceiptHash)
            && IsId(value.RunId)
            && IsId(value.GraphId)
            && IsId(value.GraphRevisionId)
            && IsHash(value.GraphExecutableHash)
            && value.ExecutionGeneration >= 1
            && IsId(value.RoleId)
            && value.RoleRevision >= 1
            && IsHash(value.RoleContentHash)
            && IsId(value.LoopId)
            && IsId(value.LoopRevisionId)
            && value.DeclaredLoopRevision >= 0
            && IsHash(value.LoopPublicationHash);

    private static bool ValidateAuthority(CredentialLeaseAuthorityScope? value)
        => value is not null
            && IsId(value.AuthorityProofId)
            && IsHash(value.AuthorityProofHash)
            && IsId(value.AuthorityProfileId)
            && value.AuthorityProfileRevision >= 1
            && IsHash(value.AuthorityProfileHash)
            && IsId(value.GrantId)
            && value.GrantRevision >= 1
            && IsHash(value.GrantHash)
            && IsHash(value.AuthorityBoundaryHash)
            && IsHash(value.CurrentAuthorityDecisionHash)
            && ValidEvidence(value.DelegationEnvelopeHash);

    private static bool ValidateEffect(CredentialLeaseEffectScope? value)
        => value is not null
            && IsId(value.NodeId)
            && value.NodeAttempt >= 1
            && IsId(value.EffectId)
            && IsId(value.EffectOperationId)
            && IsId(value.IdempotencyOperationId)
            && value.EffectGeneration >= 1
            && IsHash(value.EffectAttemptHash)
            && Enum.IsDefined((GovernedLoopEffectBoundaryKind)value.BoundaryKind);

    private static bool ValidateCapability(CredentialLeaseCapabilityScope? value)
        => value is not null
            && CapabilityId.TryParse(value.CapabilityId, out _, out _)
            && CapabilityVersion.TryParse(value.CapabilityVersion, out _, out _)
            && IsHash(value.CapabilityDescriptorHash)
            && CapabilityProviderId.TryParse(value.CapabilityProviderId, out _, out _)
            && CapabilityIdentifierRules.IsPath(value.CapabilityImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters)
            && CapabilitySecretRequirement.TryParse(value.SecretRequirement, out _, out _);

    private static bool ValidateProfile(CredentialLeaseProfileScope? value)
        => value is not null
            && Enum.IsDefined(value.Applicability)
            && (value.Applicability == CredentialLeaseProfileApplicability.NotApplicable && value.ProfileId is null && value.ProfileHash is null
                || value.Applicability == CredentialLeaseProfileApplicability.Applicable && CapabilityId.TryParse(value.ProfileId, out _, out _) && IsHash(value.ProfileHash));

    private static bool ValidateRegistry(CredentialLeaseRegistryScope? value)
        => value is not null
            && CredentialReferenceId.TryParse(value.ReferenceId, out _, out _)
            && IsHash(value.BindingHash)
            && value.RegistryRevision >= 1
            && IsId(value.ConsentReferenceId)
            && CredentialProviderId.TryParse(value.ProviderId, out _, out _);

    private static bool ValidateTarget(CredentialLeaseTargetScope? value)
        => value is not null
            && IsToken(value.TargetClass)
            && IsPrefixedHash(value.TargetFingerprint)
            && IsToken(value.OperationClass)
            && CredentialContractText.IsSafeText(value.Purpose, CredentialLeaseContractLimits.MaximumPurposeCharacters);

    private static CredentialLeaseOutcome OutcomeFor(CredentialLeasePhase phase) => phase switch
    {
        CredentialLeasePhase.IntentPrepared or CredentialLeasePhase.Authorized or CredentialLeasePhase.RedemptionBoundaryReached => CredentialLeaseOutcome.Pending,
        CredentialLeasePhase.NotRedeemed => CredentialLeaseOutcome.NotRedeemed,
        CredentialLeasePhase.Redeemed => CredentialLeaseOutcome.Redeemed,
        CredentialLeasePhase.RedemptionFailed => CredentialLeaseOutcome.Failed,
        CredentialLeasePhase.RedemptionAmbiguous => CredentialLeaseOutcome.Ambiguous,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static bool MatchesOutcome(CredentialLeasePhase phase, CredentialLeaseOutcome outcome)
    {
        try
        {
            return OutcomeFor(phase) == outcome;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsAllowedTransition(CredentialLeasePhase current, CredentialLeasePhase next) => (current, next) switch
    {
        (CredentialLeasePhase.IntentPrepared, CredentialLeasePhase.Authorized or CredentialLeasePhase.NotRedeemed) => true,
        (CredentialLeasePhase.Authorized, CredentialLeasePhase.RedemptionBoundaryReached or CredentialLeasePhase.NotRedeemed) => true,
        (CredentialLeasePhase.RedemptionBoundaryReached, CredentialLeasePhase.Redeemed or CredentialLeasePhase.RedemptionFailed or CredentialLeasePhase.RedemptionAmbiguous) => true,
        _ => false,
    };

    private static void RequireIntent(CredentialLeaseIntent intent)
    {
        var reason = Validate(intent);
        if (reason is not null)
        {
            throw new ArgumentException(reason, nameof(intent));
        }
    }

    private static void RequireVersion(CredentialLeaseIntent intent, CredentialLeaseAttemptVersion version)
    {
        var reason = ValidateVersion(intent, version);
        if (reason is not null)
        {
            throw new ArgumentException(reason, nameof(version));
        }
    }

    private static string Compute<T>(string domain, T value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        Append(hash, JsonSerializer.SerializeToUtf8Bytes(value, _hashOptions));
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) => Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static bool FixedTimeEquals(string left, string right)
        => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool IsId(string? value) => CredentialContractText.IsToken(value, CredentialContractLimits.MaxIdCharacters);
    private static bool IsToken(string? value) => CredentialContractText.IsToken(value, CredentialContractLimits.MaxTokenCharacters);
    private static bool ValidEvidence(string? value) => value is null || IsHash(value);
    private static bool IsHash(string? value) => IsPrefixedHash(value) || IsBareHash(value);
    private static bool IsBareHash(string? value) => value?.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsPrefixedHash(string? value) => value?.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && IsBareHash(value[7..]);
}
