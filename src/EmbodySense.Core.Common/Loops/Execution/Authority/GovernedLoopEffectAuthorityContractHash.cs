using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Authority;

/// <summary>Computes and verifies canonical hashes for immutable effect-authority decisions.</summary>
public static class GovernedLoopEffectAuthorityContractHash
{
    /// <summary>Computes the canonical lowercase SHA-256 hash of one structurally valid decision.</summary>
    /// <param name="decision">The complete decision; its current <c>ContentHash</c> value is excluded.</param>
    /// <returns>The canonical lowercase SHA-256 digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="decision"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any immutable decision field is invalid.</exception>
    public static string Compute(GovernedLoopEffectAuthorityDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var validation = GovernedLoopEffectAuthorityContractValidator.ValidateForHash(decision);
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Effect-authority decision is invalid at {validation.Errors[0].Path}.", nameof(decision));
        }

        var canonical = new StringBuilder(2_048);
        Append(canonical, "governed-loop-effect-authority-decision-v1");
        Append(canonical, decision.SchemaVersion);
        Append(canonical, decision.RunId);
        Append(canonical, decision.ExecutionGeneration);
        Append(canonical, decision.NodeId);
        Append(canonical, decision.NodeAttempt);
        Append(canonical, decision.EffectOperationId);
        Append(canonical, decision.CorrelationId);
        Append(canonical, (int)decision.BoundaryKind);
        Append(canonical, decision.AdmissionReceiptHash);
        AppendProof(canonical, decision.AdmittedAuthority);
        Append(canonical, decision.CurrentAuthority is not null);
        if (decision.CurrentAuthority is not null)
        {
            AppendProof(canonical, decision.CurrentAuthority);
        }

        AppendCeiling(canonical, decision.RequiredAuthority);
        AppendCeiling(canonical, decision.EffectiveAuthority);
        AppendPins(canonical, decision.RequiredCapabilityPins);
        Append(canonical, (int)decision.Disposition);
        Append(canonical, (int)decision.Reason);
        Append(canonical, decision.EvaluatedAtUtc);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    /// <summary>Returns an immutable decision copy carrying its canonical content hash.</summary>
    /// <param name="decision">The complete structurally valid decision.</param>
    /// <returns>The decision with its canonical hash applied.</returns>
    public static GovernedLoopEffectAuthorityDecision Apply(GovernedLoopEffectAuthorityDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision with { ContentHash = Compute(decision) };
    }

    /// <summary>Computes the canonical decision hash using the explicit decision-oriented API name.</summary>
    /// <param name="decision">The complete structurally valid decision.</param>
    /// <returns>The canonical lowercase SHA-256 digest.</returns>
    public static string ComputeDecisionHash(GovernedLoopEffectAuthorityDecision decision) => Compute(decision);

    /// <summary>Gets whether the supplied decision carries its exact canonical content hash.</summary>
    /// <param name="decision">The candidate decision.</param>
    /// <returns><see langword="true"/> only when recomputed immutable content matches in fixed time.</returns>
    public static bool Matches(GovernedLoopEffectAuthorityDecision? decision)
    {
        if (decision is null || !IsCanonical(decision.ContentHash))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(decision.ContentHash),
                Encoding.ASCII.GetBytes(Compute(decision)));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AppendProof(StringBuilder canonical, GovernedLoopEffectAuthorityProof proof)
    {
        Append(canonical, proof.SchemaVersion);
        Append(canonical, proof.Grant.GrantId.Value);
        Append(canonical, proof.Grant.Revision.Value);
        Append(canonical, proof.Grant.ContentHash);
        AppendBinding(canonical, proof.Binding);
        Append(canonical, (int)proof.GrantStatus);
        Append(canonical, (int)proof.GrantPosture);
        Append(canonical, proof.Boundary.EffectiveAtUtc);
        Append(canonical, proof.Boundary.ExpiresAtUtc is not null);
        if (proof.Boundary.ExpiresAtUtc is { } expiry)
        {
            Append(canonical, expiry);
        }

        Append(canonical, (int)proof.Boundary.CompletionConstraint);
        AppendCeiling(canonical, proof.Ceiling);
        AppendPins(canonical, proof.CapabilityPins);
        AppendPins(canonical, proof.ObservedCapabilityPins);
        Append(canonical, proof.DependencyEvidenceHash);
    }

    private static void AppendBinding(StringBuilder canonical, AuthorityGrantBinding binding)
    {
        Append(canonical, binding.Profile.Reference.ProfileId.Value);
        Append(canonical, binding.Profile.Reference.Revision.Value);
        Append(canonical, binding.Profile.ContentHash.Value);
        Append(canonical, binding.Role.Identity.RoleId);
        Append(canonical, binding.Role.Identity.Revision);
        Append(canonical, binding.Role.ContentHash);
        Append(canonical, binding.Loop.SchemaVersion);
        Append(canonical, binding.Loop.Revision.GraphId);
        Append(canonical, binding.Loop.Revision.RevisionId);
        Append(canonical, binding.Loop.Revision.ExecutableHash);
        Append(canonical, binding.Loop.PublicationOperationId);
        Append(canonical, binding.Loop.ValidationEvidenceHash);
    }

    private static void AppendCeiling(StringBuilder canonical, AuthorityCeiling ceiling)
    {
        Append(canonical, ceiling.Capabilities.Count);
        foreach (var capability in ceiling.Capabilities
            .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Version.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Hash.Value, StringComparer.Ordinal))
        {
            AppendIdentity(canonical, capability);
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
    }

    private static void AppendPins(StringBuilder canonical, IReadOnlyList<CapabilityAdmissionPin> pins)
    {
        Append(canonical, pins.Count);
        foreach (var pin in pins
            .OrderBy(item => item.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
            .ThenBy(item => item.DescriptorIdentity.Version.Value, StringComparer.Ordinal)
            .ThenBy(item => item.DescriptorIdentity.Hash.Value, StringComparer.Ordinal))
        {
            AppendIdentity(canonical, pin.DescriptorIdentity);
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

    private static void AppendIdentity(StringBuilder canonical, CapabilityDescriptorIdentity identity)
    {
        Append(canonical, identity.Id.Value);
        Append(canonical, identity.Version.Value);
        Append(canonical, identity.Hash.Value);
    }

    private static bool IsCanonical(string? value)
        => value?.Length == GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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
}
