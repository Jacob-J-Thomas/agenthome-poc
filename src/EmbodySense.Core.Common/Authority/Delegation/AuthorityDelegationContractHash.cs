using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Authority.Delegation;

/// <summary>Computes domain-separated canonical hashes for schema-1 delegated-authority evidence.</summary>
public static class AuthorityDelegationContractHash
{
    /// <summary>Computes the canonical exact parent-evidence hash.</summary>
    public static string ComputeParentEvidenceHash(AuthorityDelegationParentEvidenceReference parentEvidence)
    {
        RequireValid(AuthorityDelegationContractValidator.ValidateForHash(parentEvidence), nameof(parentEvidence));
        using var writer = new AuthorityDelegationCanonicalHashWriter("authority-delegation-parent-evidence-v1");
        AppendParentEvidence(writer, parentEvidence);
        return writer.Digest();
    }

    /// <summary>Returns parent evidence carrying its canonical content hash.</summary>
    public static AuthorityDelegationParentEvidenceReference Apply(AuthorityDelegationParentEvidenceReference parentEvidence)
    {
        ArgumentNullException.ThrowIfNull(parentEvidence);
        return parentEvidence with { ContentHash = ComputeParentEvidenceHash(parentEvidence) };
    }

    /// <summary>Gets whether parent evidence retains its exact canonical content hash.</summary>
    public static bool Matches(AuthorityDelegationParentEvidenceReference? parentEvidence)
        => parentEvidence is not null && Matches(parentEvidence.ContentHash, () => ComputeParentEvidenceHash(parentEvidence));

    /// <summary>Computes the canonical linkage hash for one exact parent revocation scope.</summary>
    public static string ComputeRevocationLinkHash(AuthorityDelegationRevocationLink revocationLink)
    {
        RequireValid(AuthorityDelegationContractValidator.ValidateForHash(revocationLink), nameof(revocationLink));
        using var writer = new AuthorityDelegationCanonicalHashWriter("authority-delegation-revocation-link-v1");
        AppendGrantReference(writer, revocationLink.ParentGrant);
        writer.Append(revocationLink.ParentAdmissionReceiptHash);
        writer.Append(revocationLink.WorkspaceId);
        writer.Append(revocationLink.ParentRunId);
        writer.Append(revocationLink.ParentExecutionGeneration);
        return writer.Digest();
    }

    /// <summary>Returns a revocation link carrying its canonical linkage hash.</summary>
    public static AuthorityDelegationRevocationLink Apply(AuthorityDelegationRevocationLink revocationLink)
    {
        ArgumentNullException.ThrowIfNull(revocationLink);
        return revocationLink with { LinkageHash = ComputeRevocationLinkHash(revocationLink) };
    }

    /// <summary>Gets whether a revocation link retains its exact canonical linkage hash.</summary>
    public static bool Matches(AuthorityDelegationRevocationLink? revocationLink)
        => revocationLink is not null && Matches(revocationLink.LinkageHash, () => ComputeRevocationLinkHash(revocationLink));

    /// <summary>Computes the hash of one exact authority ceiling and capability-pin set.</summary>
    public static string ComputeAuthorityScopeHash(AuthorityCeiling ceiling, IReadOnlyList<CapabilityAdmissionPin> pins)
    {
        RequireValid(AuthorityDelegationContractValidator.ValidateAuthorityScopeForHash(ceiling, pins), nameof(ceiling));
        using var writer = new AuthorityDelegationCanonicalHashWriter("authority-delegation-authority-scope-v1");
        AppendCeiling(writer, ceiling);
        AppendPins(writer, pins);
        return writer.Digest();
    }

    /// <summary>Computes the canonical hash of one hash-only subset proof.</summary>
    public static string ComputeSubsetProofHash(AuthorityDelegationSubsetProof proof)
    {
        RequireValid(AuthorityDelegationContractValidator.ValidateForHash(proof), nameof(proof));
        using var writer = new AuthorityDelegationCanonicalHashWriter("authority-delegation-subset-proof-v1");
        writer.Append(proof.ParentEvidenceHash);
        writer.Append(proof.ParentAuthorityScopeHash);
        writer.Append(proof.DelegatedAuthorityScopeHash);
        writer.Append(proof.TargetMaximumEvidenceHash);
        writer.Append(proof.NarrowingDimensions.Count);
        foreach (var dimension in proof.NarrowingDimensions)
        {
            writer.Append((int)dimension);
        }

        return writer.Digest();
    }

    /// <summary>Returns a subset proof carrying its canonical content hash.</summary>
    public static AuthorityDelegationSubsetProof Apply(AuthorityDelegationSubsetProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return proof with { ContentHash = ComputeSubsetProofHash(proof) };
    }

    /// <summary>Gets whether a subset proof retains its exact canonical content hash.</summary>
    public static bool Matches(AuthorityDelegationSubsetProof? proof)
        => proof is not null && Matches(proof.ContentHash, () => ComputeSubsetProofHash(proof));

    /// <summary>Computes the canonical complete envelope hash.</summary>
    public static string ComputeEnvelopeHash(AuthorityDelegationEnvelope envelope)
    {
        RequireValid(AuthorityDelegationContractValidator.ValidateForHash(envelope), nameof(envelope));
        using var writer = new AuthorityDelegationCanonicalHashWriter("authority-delegation-envelope-v1");
        writer.Append(envelope.SchemaVersion);
        writer.Append(envelope.EnvelopeId);
        writer.Append(envelope.ParentEvidence.ContentHash);
        AppendTarget(writer, envelope.Target);
        AppendCeiling(writer, envelope.DelegatedCeiling);
        AppendPins(writer, envelope.DelegatedCapabilityPins);
        writer.Append(envelope.TargetClass);
        writer.Append(envelope.OperationClass);
        writer.Append(envelope.Purpose.Value);
        AppendBoundary(writer, envelope.Boundary);
        writer.Append(envelope.RevocationLink.LinkageHash);
        writer.Append(envelope.SubsetProof.ContentHash);
        writer.Append(envelope.IssuedAtUtc);
        return writer.Digest();
    }

    /// <summary>Returns an envelope carrying its canonical content hash.</summary>
    public static AuthorityDelegationEnvelope Apply(AuthorityDelegationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope with { ContentHash = ComputeEnvelopeHash(envelope) };
    }

    /// <summary>Gets whether an envelope retains its exact canonical content hash.</summary>
    public static bool Matches(AuthorityDelegationEnvelope? envelope)
        => envelope is not null && Matches(envelope.ContentHash, () => ComputeEnvelopeHash(envelope));

    internal static bool IsCanonicalHash(string? value)
        => value?.Length == AuthorityDelegationContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void AppendParentEvidence(AuthorityDelegationCanonicalHashWriter writer, AuthorityDelegationParentEvidenceReference parentEvidence)
    {
        writer.Append(parentEvidence.WorkspaceId);
        AppendExecution(writer, parentEvidence.ParentExecution);
        writer.Append(parentEvidence.OriginNodeId);
        writer.Append(parentEvidence.OriginNodeAttempt);
        writer.Append(parentEvidence.ParentAdmissionReceiptHash);
        writer.Append(parentEvidence.ActorId.Value);
        AppendGrantReference(writer, parentEvidence.GrantReference);
        AppendGrantBinding(writer, parentEvidence.GrantBinding);
        writer.Append(parentEvidence.OriginBindingEvidenceHash);
        writer.Append(parentEvidence.GrantDependencyEvidenceHash);
        writer.Append(parentEvidence.EvaluatedAtUtc);
    }

    private static void AppendExecution(AuthorityDelegationCanonicalHashWriter writer, GovernedLoopExecutionBinding binding)
    {
        writer.Append(binding.SchemaVersion);
        writer.Append(binding.RunId);
        writer.Append(binding.Revision.SchemaVersion);
        writer.Append(binding.Revision.GraphId);
        writer.Append(binding.Revision.RevisionId);
        writer.Append(binding.Revision.ExecutableHash);
        writer.Append(binding.ExecutionGeneration);
    }

    private static void AppendGrantReference(AuthorityDelegationCanonicalHashWriter writer, AuthorityGrantReference reference)
    {
        writer.Append(reference.GrantId.Value);
        writer.Append(reference.Revision.Value);
        writer.Append(reference.ContentHash);
    }

    private static void AppendGrantBinding(AuthorityDelegationCanonicalHashWriter writer, AuthorityGrantBinding binding)
    {
        writer.Append(binding.Profile.Reference.ProfileId.Value);
        writer.Append(binding.Profile.Reference.Revision.Value);
        writer.Append(binding.Profile.ContentHash.Value);
        AppendRole(writer, binding.Role);
        AppendLoop(writer, binding.Loop);
    }

    private static void AppendTarget(AuthorityDelegationCanonicalHashWriter writer, AuthorityDelegationTargetBinding target)
    {
        writer.Append((int)target.Kind);
        AppendRole(writer, target.Role);
        writer.Append(target.Loop is not null);
        if (target.Loop is not null)
        {
            AppendLoop(writer, target.Loop);
        }

        writer.Append(target.NodeId);
        writer.Append(target.BindingEvidenceHash);
    }

    private static void AppendRole(AuthorityDelegationCanonicalHashWriter writer, ContextualRoleRevisionPin role)
    {
        writer.Append(role.Identity.RoleId);
        writer.Append(role.Identity.Revision);
        writer.Append(role.ContentHash);
    }

    private static void AppendLoop(AuthorityDelegationCanonicalHashWriter writer, GovernedLoopRevisionPublicationPin loop)
    {
        writer.Append(loop.SchemaVersion);
        writer.Append(loop.Revision.SchemaVersion);
        writer.Append(loop.Revision.GraphId);
        writer.Append(loop.Revision.RevisionId);
        writer.Append(loop.Revision.ExecutableHash);
        writer.Append(loop.PublicationOperationId);
        writer.Append(loop.ValidationEvidenceHash);
    }

    private static void AppendCeiling(AuthorityDelegationCanonicalHashWriter writer, AuthorityCeiling ceiling)
    {
        var capabilities = ceiling.Capabilities.OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Version.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Hash.Value, StringComparer.Ordinal)
            .ToArray();
        writer.Append(capabilities.Length);
        foreach (var capability in capabilities)
        {
            writer.Append(capability.Id.Value);
            writer.Append(capability.Version.Value);
            writer.Append(capability.Hash.Value);
        }

        var dataClasses = ceiling.DataClasses.OrderBy(value => value.Value, StringComparer.Ordinal).ToArray();
        writer.Append(dataClasses.Length);
        foreach (var dataClass in dataClasses)
        {
            writer.Append(dataClass.Value);
        }

        writer.Append(ceiling.MaxTargetCount);
        writer.Append((int)ceiling.MaxSideEffectClass);
        writer.Append(ceiling.AllowsRecurrence);
        writer.Append(ceiling.AllowsExternalPublication);
        writer.Append(ceiling.AllowsIrreversibleAction);
    }

    private static void AppendPins(AuthorityDelegationCanonicalHashWriter writer, IReadOnlyList<CapabilityAdmissionPin> pins)
    {
        var ordered = pins.OrderBy(value => value.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
            .ThenBy(value => value.DescriptorIdentity.Version.Value, StringComparer.Ordinal)
            .ThenBy(value => value.DescriptorIdentity.Hash.Value, StringComparer.Ordinal)
            .ToArray();
        writer.Append(ordered.Length);
        foreach (var pin in ordered)
        {
            writer.Append(pin.DescriptorIdentity.Id.Value);
            writer.Append(pin.DescriptorIdentity.Version.Value);
            writer.Append(pin.DescriptorIdentity.Hash.Value);
            writer.Append((int)pin.Kind);
            writer.Append(pin.Implementation.ProviderId.Value);
            writer.Append(pin.Implementation.ImplementationId);
            writer.Append((int)pin.Provenance.Kind);
            writer.Append(pin.Provenance.SourceUri);
            writer.Append(pin.Provenance.SourceRevision);
            writer.Append(pin.Provenance.Integrity?.Value);
            writer.Append(pin.Artifact.Checksum?.Value);
            writer.Append(pin.Artifact.Signature);
            writer.Append(pin.SafeDescription);
        }
    }

    private static void AppendBoundary(AuthorityDelegationCanonicalHashWriter writer, AuthorityDelegationBoundary boundary)
    {
        writer.Append(boundary.EffectiveAtUtc);
        writer.Append(boundary.ExpiresAtUtc);
        writer.Append((int)boundary.CompletionConstraint);
    }

    private static bool Matches(string? actual, Func<string> expectedFactory)
    {
        if (!IsCanonicalHash(actual))
        {
            return false;
        }

        try
        {
            var actualBytes = Encoding.ASCII.GetBytes(actual!);
            var expectedBytes = Encoding.ASCII.GetBytes(expectedFactory());
            return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RequireValid(AuthorityDelegationContractValidationResult validation, string parameterName)
    {
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Delegated-authority contract is invalid at {validation.Errors[0].Path}.", parameterName);
        }
    }
}
