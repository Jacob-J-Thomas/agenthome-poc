using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Admission;

/// <summary>Computes domain-separated canonical hashes for schema-1 governed-loop admission contracts.</summary>
public static class GovernedLoopAdmissionContractHash
{
    /// <summary>Computes the canonical exact-intent hash.</summary>
    /// <param name="intent">The stable server-owned intent.</param>
    /// <returns>A lowercase SHA-256 digest.</returns>
    public static string ComputeIntentHash(GovernedLoopAdmissionIntent intent)
    {
        RequireValid(GovernedLoopAdmissionValidator.ValidateForHash(intent), nameof(intent));
        var canonical = Begin("governed-loop-admission-intent-v1");
        Append(canonical, intent.SchemaVersion);
        Append(canonical, intent.WorkspaceId);
        Append(canonical, intent.OperationId);
        Append(canonical, intent.RequestHash);
        Append(canonical, GovernedLoopRevisionContractHash.ComputePublicationPinHash(intent.Publication));
        Append(canonical, ComputeAuthorityGrantReferenceHash(intent.AuthorityGrant));
        Append(canonical, ComputeContextualRoleReferenceHash(intent.Role));
        Append(canonical, intent.ActorId.Value);
        Append(canonical, intent.Surface);
        Append(canonical, intent.GraphArtifactHash);
        Append(canonical, intent.GraphLayoutHash);
        return Digest(canonical);
    }

    /// <summary>Computes the canonical hash of one successful evidence record.</summary>
    /// <param name="evidence">The exact successful evidence.</param>
    /// <returns>A lowercase SHA-256 digest.</returns>
    public static string ComputeEvidenceHash(GovernedLoopAdmissionEvidence evidence)
    {
        RequireValid(GovernedLoopAdmissionValidator.ValidateForHash(evidence), nameof(evidence));
        var canonical = Begin("governed-loop-admission-evidence-v1");
        Append(canonical, evidence.SchemaVersion);
        Append(canonical, evidence.IntentHash);
        AppendBinding(canonical, evidence.Binding);
        AppendGrantProfile(canonical, evidence.GrantProfile);
        AppendGrantBoundary(canonical, evidence.GrantBoundary);
        Append(canonical, evidence.GrantDependencyEvidenceHash);
        Append(canonical, ComputeAuthorityCeilingReferenceHash(evidence.EffectiveAuthority));
        Append(canonical, ComputeCapabilityAdmissionReferenceHash(evidence.CapabilityAdmission));
        AppendReferences(canonical, evidence.References);
        Append(canonical, evidence.EvaluatedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns a successful evidence copy with its canonical content hash applied.</summary>
    public static GovernedLoopAdmissionEvidence Apply(GovernedLoopAdmissionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence with { ContentHash = ComputeEvidenceHash(evidence) };
    }

    /// <summary>Gets whether a successful evidence record retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopAdmissionEvidence? evidence)
        => evidence is not null && Matches(evidence.ContentHash, () => ComputeEvidenceHash(evidence));

    /// <summary>Computes the canonical hash of one definitive rejection.</summary>
    public static string ComputeRejectionHash(GovernedLoopAdmissionRejection rejection)
    {
        RequireValid(GovernedLoopAdmissionValidator.ValidateForHash(rejection), nameof(rejection));
        var canonical = Begin("governed-loop-admission-rejection-v1");
        Append(canonical, rejection.SchemaVersion);
        Append(canonical, ComputeIntentHash(rejection.Intent));
        Append(canonical, (int)rejection.FailureCode);
        Append(canonical, rejection.AuthorityDenial is not null);
        if (rejection.AuthorityDenial is not null)
        {
            Append(canonical, ComputeAuthorityDenialProofHash(rejection.AuthorityDenial));
        }

        Append(canonical, rejection.CapabilityDenial is not null);
        if (rejection.CapabilityDenial is not null)
        {
            Append(canonical, ComputeCapabilityDenialProofHash(rejection.CapabilityDenial));
        }

        AppendReferences(canonical, rejection.References);
        Append(canonical, rejection.RejectedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns a rejection copy with its canonical content hash applied.</summary>
    public static GovernedLoopAdmissionRejection Apply(GovernedLoopAdmissionRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return rejection with { ContentHash = ComputeRejectionHash(rejection) };
    }

    /// <summary>Gets whether a rejection retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopAdmissionRejection? rejection)
        => rejection is not null && Matches(rejection.ContentHash, () => ComputeRejectionHash(rejection));

    /// <summary>Computes the canonical hash of one successful receipt.</summary>
    public static string ComputeReceiptHash(GovernedLoopAdmissionReceipt receipt)
    {
        RequireValid(GovernedLoopAdmissionValidator.ValidateForHash(receipt), nameof(receipt));
        var canonical = Begin("governed-loop-admission-receipt-v1");
        Append(canonical, receipt.SchemaVersion);
        Append(canonical, ComputeIntentHash(receipt.Intent));
        Append(canonical, receipt.Evidence.ContentHash);
        Append(canonical, receipt.RecordedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns a successful receipt copy with its canonical content hash applied.</summary>
    public static GovernedLoopAdmissionReceipt Apply(GovernedLoopAdmissionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt with { ContentHash = ComputeReceiptHash(receipt) };
    }

    /// <summary>Gets whether a successful receipt retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopAdmissionReceipt? receipt)
        => receipt is not null && Matches(receipt.ContentHash, () => ComputeReceiptHash(receipt));

    /// <summary>Computes the canonical hash of one definitive terminal outcome.</summary>
    public static string ComputeTerminalOutcomeHash(GovernedLoopAdmissionTerminalOutcome outcome)
    {
        RequireValid(GovernedLoopAdmissionValidator.ValidateForHash(outcome), nameof(outcome));
        var canonical = Begin("governed-loop-admission-terminal-outcome-v1");
        Append(canonical, outcome.SchemaVersion);
        Append(canonical, ComputeIntentHash(outcome.Intent));
        Append(canonical, (int)outcome.Disposition);
        Append(canonical, outcome.Receipt?.ContentHash);
        Append(canonical, outcome.Rejection?.ContentHash);
        Append(canonical, outcome.RecordedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns a terminal-outcome copy with its canonical content hash applied.</summary>
    public static GovernedLoopAdmissionTerminalOutcome Apply(GovernedLoopAdmissionTerminalOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome with { ContentHash = ComputeTerminalOutcomeHash(outcome) };
    }

    /// <summary>Gets whether a terminal outcome retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopAdmissionTerminalOutcome? outcome)
        => outcome is not null && Matches(outcome.ContentHash, () => ComputeTerminalOutcomeHash(outcome));

    /// <summary>Creates the complete canonical evidence-reference set for one successful admission.</summary>
    public static IReadOnlyList<GovernedLoopAdmissionEvidenceReference> CreateEvidenceReferences(
        GovernedLoopAdmissionIntent intent,
        AuthorityCeiling effectiveAuthority,
        CapabilityAdmissionSnapshot capabilityAdmission)
    {
        RequireValid(GovernedLoopAdmissionValidator.ValidateForHash(intent), nameof(intent));
        return Array.AsReadOnly(new[]
        {
            new GovernedLoopAdmissionEvidenceReference(GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision, ComputeContextualRoleReferenceHash(intent.Role)),
            new GovernedLoopAdmissionEvidenceReference(GovernedLoopAdmissionEvidenceKind.AuthorityGrant, ComputeAuthorityGrantReferenceHash(intent.AuthorityGrant)),
            new GovernedLoopAdmissionEvidenceReference(GovernedLoopAdmissionEvidenceKind.LoopPublication, GovernedLoopRevisionContractHash.ComputePublicationPinHash(intent.Publication)),
            new GovernedLoopAdmissionEvidenceReference(GovernedLoopAdmissionEvidenceKind.GraphArtifact, ReferenceDigest("governed-loop-admission-graph-artifact-reference-v1", intent.GraphArtifactHash)),
            new GovernedLoopAdmissionEvidenceReference(GovernedLoopAdmissionEvidenceKind.GraphLayout, ReferenceDigest("governed-loop-admission-graph-layout-reference-v1", intent.GraphLayoutHash)),
            new GovernedLoopAdmissionEvidenceReference(GovernedLoopAdmissionEvidenceKind.EffectiveAuthority, ComputeAuthorityCeilingReferenceHash(effectiveAuthority)),
            new GovernedLoopAdmissionEvidenceReference(GovernedLoopAdmissionEvidenceKind.CapabilityAdmission, ComputeCapabilityAdmissionReferenceHash(capabilityAdmission))
        });
    }

    /// <summary>Creates the exact canonical evidence-reference set for one definitive rejection.</summary>
    /// <param name="intent">The complete server-owned immutable admission intent.</param>
    /// <param name="failureCode">The supported definitive failure classification.</param>
    /// <param name="authorityDenial">The structured authority proof required only for authority denial.</param>
    /// <param name="capabilityDenial">The structured capability proof required only for capability-policy denial.</param>
    /// <returns>A defensively wrapped, canonically ordered reference set whose hashes are derived only from structured evidence.</returns>
    /// <exception cref="ArgumentException">Thrown when the intent, failure classification, or proof composition is invalid.</exception>
    public static IReadOnlyList<GovernedLoopAdmissionEvidenceReference> CreateRejectionEvidenceReferences(
        GovernedLoopAdmissionIntent intent,
        GovernedLoopAdmissionFailureCode failureCode,
        GovernedLoopAdmissionAuthorityDenialProof? authorityDenial = null,
        GovernedLoopAdmissionCapabilityDenialProof? capabilityDenial = null)
    {
        RequireValid(GovernedLoopAdmissionValidator.ValidateForHash(intent), nameof(intent));
        if (!Enum.IsDefined(failureCode) || failureCode == GovernedLoopAdmissionFailureCode.None)
        {
            throw new ArgumentException("A supported definitive admission failure is required.", nameof(failureCode));
        }

        RequireValid(
            GovernedLoopAdmissionValidator.ValidateRejectionProofsForHash(failureCode, authorityDenial, capabilityDenial),
            nameof(failureCode));
        var references = GovernedLoopAdmissionValidator.RequiredRejectionEvidenceKinds(failureCode)
            .Select(kind => new GovernedLoopAdmissionEvidenceReference(
                kind,
                ComputeRejectionEvidenceReferenceHash(intent, failureCode, kind, authorityDenial, capabilityDenial)))
            .ToArray();
        return Array.AsReadOnly(references);
    }

    /// <summary>Computes the domain-separated digest of one exact contextual-role revision pin.</summary>
    public static string ComputeContextualRoleReferenceHash(ContextualRoleRevisionPin role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (role.Identity is null || !ContextualRoleId.IsValid(role.Identity.RoleId) || role.Identity.Revision < 1 || !IsCanonicalHash(role.ContentHash))
        {
            throw new ArgumentException("Contextual-role reference must be exact and canonical.", nameof(role));
        }

        var canonical = Begin("governed-loop-admission-role-reference-v1");
        Append(canonical, role.Identity.RoleId);
        Append(canonical, role.Identity.Revision);
        Append(canonical, role.ContentHash);
        return Digest(canonical);
    }

    /// <summary>Computes the domain-separated digest of one exact authority-grant reference.</summary>
    public static string ComputeAuthorityGrantReferenceHash(AuthorityGrantReference grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.GrantId is null || grant.Revision is null || !AuthorityGrantHash.IsCanonical(grant.ContentHash))
        {
            throw new ArgumentException("Authority-grant reference must be exact and canonical.", nameof(grant));
        }

        var canonical = Begin("governed-loop-admission-grant-reference-v1");
        Append(canonical, grant.GrantId.Value);
        Append(canonical, grant.Revision.Value);
        Append(canonical, grant.ContentHash);
        return Digest(canonical);
    }

    /// <summary>Computes the domain-separated digest of one effective authority ceiling.</summary>
    public static string ComputeAuthorityCeilingReferenceHash(AuthorityCeiling ceiling)
    {
        ArgumentNullException.ThrowIfNull(ceiling);
        if (!AuthorityProfileValidator.ValidateCeiling(ceiling).IsValid)
        {
            throw new ArgumentException("Effective authority ceiling must satisfy the bounded authority contract.", nameof(ceiling));
        }

        var canonical = Begin("governed-loop-admission-authority-ceiling-v1");
        Append(canonical, ceiling.Capabilities.Count);
        foreach (var capability in ceiling.Capabilities.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ThenBy(item => item.Version.Value, StringComparer.Ordinal).ThenBy(item => item.Hash.Value, StringComparer.Ordinal))
        {
            Append(canonical, capability.Id.Value);
            Append(canonical, capability.Version.Value);
            Append(canonical, capability.Hash.Value);
        }

        Append(canonical, ceiling.DataClasses.Count);
        foreach (var dataClass in ceiling.DataClasses.OrderBy(item => item.Value, StringComparer.Ordinal))
        {
            Append(canonical, dataClass.Value);
        }

        Append(canonical, ceiling.MaxTargetCount);
        Append(canonical, (int)ceiling.MaxSideEffectClass);
        Append(canonical, ceiling.AllowsRecurrence);
        Append(canonical, ceiling.AllowsExternalPublication);
        Append(canonical, ceiling.AllowsIrreversibleAction);
        return Digest(canonical);
    }

    /// <summary>Computes the domain-separated digest of one exact capability-admission snapshot.</summary>
    public static string ComputeCapabilityAdmissionReferenceHash(CapabilityAdmissionSnapshot capabilityAdmission)
    {
        ArgumentNullException.ThrowIfNull(capabilityAdmission);
        if (!GovernedLoopAdmissionCapabilityGuard.IsValid(capabilityAdmission))
        {
            throw new ArgumentException("Capability admission snapshot must be exact, canonical, and bounded.", nameof(capabilityAdmission));
        }

        var canonical = Begin("governed-loop-admission-capability-snapshot-v1");
        Append(canonical, capabilityAdmission.SchemaVersion);
        Append(canonical, capabilityAdmission.WorkspaceScopeId);
        Append(canonical, capabilityAdmission.RequirementsHash);
        AppendCapabilityPins(canonical, capabilityAdmission.Pins);
        AppendCapabilityEvidence(canonical, capabilityAdmission.Evidence);
        Append(canonical, capabilityAdmission.AdmittedAtUtc);
        return Digest(canonical);
    }

    internal static bool IsCanonicalHash(string? value)
        => value?.Length == GovernedLoopAdmissionLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ComputeRejectionEvidenceReferenceHash(
        GovernedLoopAdmissionIntent intent,
        GovernedLoopAdmissionFailureCode failureCode,
        GovernedLoopAdmissionEvidenceKind kind,
        GovernedLoopAdmissionAuthorityDenialProof? authorityDenial,
        GovernedLoopAdmissionCapabilityDenialProof? capabilityDenial)
        => kind switch
        {
            GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision => ComputeContextualRoleReferenceHash(intent.Role),
            GovernedLoopAdmissionEvidenceKind.AuthorityGrant => ComputeAuthorityGrantReferenceHash(intent.AuthorityGrant),
            GovernedLoopAdmissionEvidenceKind.LoopPublication => GovernedLoopRevisionContractHash.ComputePublicationPinHash(intent.Publication),
            GovernedLoopAdmissionEvidenceKind.GraphArtifact => ReferenceDigest("governed-loop-admission-graph-artifact-reference-v1", intent.GraphArtifactHash),
            GovernedLoopAdmissionEvidenceKind.GraphLayout => ReferenceDigest("governed-loop-admission-graph-layout-reference-v1", intent.GraphLayoutHash),
            GovernedLoopAdmissionEvidenceKind.EffectiveAuthority when failureCode == GovernedLoopAdmissionFailureCode.AuthorityDenied => ComputeAuthorityCeilingReferenceHash(authorityDenial!.EffectiveCeiling),
            GovernedLoopAdmissionEvidenceKind.EffectiveAuthority when failureCode == GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied => ComputeAuthorityCeilingReferenceHash(capabilityDenial!.EffectiveAuthority),
            GovernedLoopAdmissionEvidenceKind.CapabilityAdmission when failureCode == GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied => ComputeCapabilityDenialProofHash(capabilityDenial!),
            _ => throw new ArgumentException("The rejection evidence kind is not supported by the failure classification.", nameof(kind))
        };

    private static string ComputeAuthorityDenialProofHash(GovernedLoopAdmissionAuthorityDenialProof proof)
    {
        RequireValid(
            GovernedLoopAdmissionValidator.ValidateRejectionProofsForHash(
                GovernedLoopAdmissionFailureCode.AuthorityDenied,
                proof,
                null),
            nameof(proof));
        var canonical = Begin("governed-loop-admission-authority-denial-proof-v1");
        Append(canonical, proof.SchemaVersion);
        Append(canonical, ComputeAuthorityCeilingReferenceHash(proof.CandidateCeiling));
        Append(canonical, ComputeAuthorityCeilingReferenceHash(proof.EffectiveCeiling));
        AppendBoundaryReceipt(canonical, proof.BoundaryReceipt);
        return Digest(canonical);
    }

    private static string ComputeCapabilityDenialProofHash(GovernedLoopAdmissionCapabilityDenialProof proof)
    {
        RequireValid(
            GovernedLoopAdmissionValidator.ValidateRejectionProofsForHash(
                GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied,
                null,
                proof),
            nameof(proof));
        _ = CapabilityDependencyManifestHash.TryCompute(proof.Requirements, out var requirementsHash, out _);
        var canonical = Begin("governed-loop-admission-capability-denial-proof-v1");
        Append(canonical, proof.SchemaVersion);
        Append(canonical, requirementsHash!.Value);
        Append(canonical, proof.RequirementsHash);
        Append(canonical, ComputeAuthorityCeilingReferenceHash(proof.EffectiveAuthority));
        Append(canonical, proof.Violations.Count);
        foreach (var violation in proof.Violations)
        {
            Append(canonical, violation.DependencyId.Value);
            Append(canonical, violation.CompatibleVersionRange.Value);
            Append(canonical, (int)violation.Reason);
        }

        Append(canonical, proof.EvaluatedAtUtc);
        return Digest(canonical);
    }

    private static void AppendBoundaryReceipt(StringBuilder canonical, AuthorityBoundaryReceipt receipt)
    {
        Append(canonical, receipt.SchemaVersion);
        Append(canonical, (int)receipt.Decision);
        Append(canonical, receipt.Conditions.Count);
        foreach (var condition in receipt.Conditions)
        {
            Append(canonical, (int)condition.Decision);
            Append(canonical, (int)condition.Reason);
        }

        Append(canonical, receipt.Profiles.Count);
        foreach (var profile in receipt.Profiles)
        {
            Append(canonical, profile.ProfileId.Value);
            Append(canonical, profile.Revision.Value);
        }

        Append(canonical, receipt.EvaluatedAtUtc);
    }

    private static string ReferenceDigest(string domain, string hash)
    {
        if (!IsCanonicalHash(hash))
        {
            throw new ArgumentException("Evidence reference must contain a canonical lowercase SHA-256 digest.", nameof(hash));
        }

        var canonical = Begin(domain);
        Append(canonical, hash);
        return Digest(canonical);
    }

    private static void AppendBinding(StringBuilder canonical, GovernedLoopExecutionBinding binding)
    {
        Append(canonical, binding.SchemaVersion);
        Append(canonical, binding.RunId);
        Append(canonical, binding.Revision.SchemaVersion);
        Append(canonical, binding.Revision.GraphId);
        Append(canonical, binding.Revision.RevisionId);
        Append(canonical, binding.Revision.ExecutableHash);
        Append(canonical, binding.ExecutionGeneration);
    }

    private static void AppendGrantProfile(StringBuilder canonical, AuthorityGrantProfilePin profile)
    {
        Append(canonical, profile.Reference.ProfileId.Value);
        Append(canonical, profile.Reference.Revision.Value);
        Append(canonical, profile.ContentHash.Value);
    }

    private static void AppendGrantBoundary(StringBuilder canonical, AuthorityGrantBoundary boundary)
    {
        Append(canonical, boundary.EffectiveAtUtc);
        Append(canonical, boundary.ExpiresAtUtc is not null);
        if (boundary.ExpiresAtUtc is { } expiry)
        {
            Append(canonical, expiry);
        }

        Append(canonical, (int)boundary.CompletionConstraint);
    }

    private static void AppendReferences(StringBuilder canonical, IReadOnlyList<GovernedLoopAdmissionEvidenceReference> references)
    {
        Append(canonical, references.Count);
        foreach (var reference in references)
        {
            Append(canonical, (int)reference.Kind);
            Append(canonical, reference.EvidenceHash);
        }
    }

    private static void AppendCapabilityPins(StringBuilder canonical, IReadOnlyList<CapabilityAdmissionPin> pins)
    {
        Append(canonical, pins.Count);
        foreach (var pin in pins
            .OrderBy(item => item.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
            .ThenBy(item => item.DescriptorIdentity.Version.Value, StringComparer.Ordinal)
            .ThenBy(item => item.DescriptorIdentity.Hash.Value, StringComparer.Ordinal))
        {
            AppendCapabilityIdentity(canonical, pin.DescriptorIdentity);
            Append(canonical, (int)pin.Kind);
            Append(canonical, pin.Implementation.ProviderId.Value);
            Append(canonical, pin.Implementation.ImplementationId);
            Append(canonical, (int)pin.Provenance.Kind);
            Append(canonical, pin.Provenance.SourceUri);
            Append(canonical, pin.Provenance.SourceRevision);
            Append(canonical, pin.Provenance.Integrity?.Value);
            Append(canonical, pin.Artifact.Checksum?.Value);
            Append(canonical, pin.Artifact.Signature);
            Append(canonical, pin.SafeDescription);
        }
    }

    private static void AppendCapabilityEvidence(StringBuilder canonical, IReadOnlyList<CapabilityAdmissionEvidence> evidence)
    {
        Append(canonical, evidence.Count);
        foreach (var item in evidence
            .OrderBy(item => item.SubjectId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.DependencyId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.IsOptional))
        {
            Append(canonical, item.SubjectId.Value);
            Append(canonical, item.DependencyId.Value);
            Append(canonical, item.CompatibleVersionRange.Value);
            Append(canonical, item.IsOptional);
            Append(canonical, item.Outcome);
            Append(canonical, item.SelectedIdentity is not null);
            if (item.SelectedIdentity is not null)
            {
                AppendCapabilityIdentity(canonical, item.SelectedIdentity);
            }

            Append(canonical, item.Detail);
        }
    }

    private static void AppendCapabilityIdentity(StringBuilder canonical, CapabilityDescriptorIdentity identity)
    {
        Append(canonical, identity.Id.Value);
        Append(canonical, identity.Version.Value);
        Append(canonical, identity.Hash.Value);
    }

    private static StringBuilder Begin(string domain)
    {
        var canonical = new StringBuilder(1_024);
        Append(canonical, domain);
        return canonical;
    }

    private static void Append(StringBuilder canonical, bool value) => Append(canonical, value ? "true" : "false");

    private static void Append(StringBuilder canonical, DateTimeOffset value) => Append(canonical, value.ToString("O", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, int value) => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, long value) => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

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
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();

    private static bool Matches(string? actual, Func<string> compute)
    {
        if (!IsCanonicalHash(actual))
        {
            return false;
        }

        try
        {
            var expectedBytes = Encoding.ASCII.GetBytes(compute());
            var actualBytes = Encoding.ASCII.GetBytes(actual!);
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void RequireValid(GovernedLoopAdmissionValidationResult validation, string parameterName)
    {
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Admission contract is invalid at {validation.Errors[0].Path}.", parameterName);
        }
    }
}
