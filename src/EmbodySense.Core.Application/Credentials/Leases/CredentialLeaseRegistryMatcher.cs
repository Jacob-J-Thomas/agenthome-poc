using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials.Leases;

/// <summary>Matches one exact lease intent against a coherent current registry snapshot without granting authority.</summary>
public static class CredentialLeaseRegistryMatcher
{
    /// <summary>Returns a closed failure and exact value-free snapshot hash for one current registry read.</summary>
    public static CredentialLeaseRegistryMatch Match(CredentialLeaseIntent intent, CredentialRegistryReadResult? read, DateTimeOffset trustedNowUtc)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (read is null || !read.Succeeded || read.RegistryRevision is null || trustedNowUtc.Offset != TimeSpan.Zero)
        {
            return CredentialLeaseRegistryMatch.Rejected(CredentialFailureCode.Unavailable);
        }

        if (read.RegistryRevision != intent.Registry.RegistryRevision)
        {
            return CredentialLeaseRegistryMatch.Rejected(CredentialFailureCode.Conflict);
        }
        var entry = read.Entries.SingleOrDefault(candidate => string.Equals(candidate.Reference.Id.Value, intent.Registry.ReferenceId, StringComparison.Ordinal));
        if (entry is null)
        {
            return CredentialLeaseRegistryMatch.Rejected(CredentialFailureCode.NotFound);
        }
        if (!CredentialContractJson.TryHash(entry.Binding, out var computedBindingHash, out _)
            || !computedBindingHash!.FixedTimeEquals(entry.BindingHash)
            || !string.Equals(entry.BindingHash.Value, intent.Registry.BindingHash, StringComparison.Ordinal)
            || !string.Equals(entry.ConsentReference.Value, intent.Registry.ConsentReferenceId, StringComparison.Ordinal)
            || !string.Equals(entry.Reference.ProviderId.Value, intent.Registry.ProviderId, StringComparison.Ordinal)
            || !string.Equals(entry.Binding.ReferenceId.Value, intent.Registry.ReferenceId, StringComparison.Ordinal)
            || !string.Equals(entry.Binding.Scope.WorkspaceId, intent.Execution.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(entry.Binding.Capability.Id.Value, intent.Capability.CapabilityId, StringComparison.Ordinal)
            || !string.Equals(entry.Binding.Capability.Version.Value, intent.Capability.CapabilityVersion, StringComparison.Ordinal)
            || !string.Equals(entry.Binding.Capability.Hash.Value, intent.Capability.CapabilityDescriptorHash, StringComparison.Ordinal)
            || !string.Equals(entry.Binding.Implementation.ProviderId.Value, intent.Capability.CapabilityProviderId, StringComparison.Ordinal)
            || !string.Equals(entry.Binding.Implementation.ImplementationId, intent.Capability.CapabilityImplementationId, StringComparison.Ordinal)
            || !string.Equals(entry.Binding.Requirement.Name, intent.Capability.SecretRequirement, StringComparison.Ordinal)
            || !MatchesOptional(entry.Binding.Scope.RoleId, intent.Execution.RoleId)
            || !MatchesOptional(entry.Binding.Scope.LoopId, intent.Execution.LoopId)
            || entry.Binding.Scope.LoopRevision is { } loopRevision && loopRevision != intent.Execution.DeclaredLoopRevision
            || !MatchesOptional(entry.Binding.Scope.NodeId, intent.Effect.NodeId)
            || !MatchesOptional(entry.Binding.Scope.Service, intent.Target.TargetClass)
            || !MatchesOptional(entry.Binding.Scope.OperationClass, intent.Target.OperationClass)
            || !MatchesOptional(entry.Binding.Scope.ActorId, intent.Execution.ActorId)
            || entry.Binding.Scope.Target is { } target
                && !string.Equals(intent.Target.TargetFingerprint, CredentialLeaseContract.ComputeTargetFingerprint(intent.Target.TargetClass, Encoding.UTF8.GetBytes(target)), StringComparison.Ordinal)
            || entry.Binding.Scope.NotBeforeUtc is { } notBefore && trustedNowUtc < notBefore
            || entry.Binding.Scope.NotAfterUtc is { } notAfter && trustedNowUtc >= notAfter)
        {
            return CredentialLeaseRegistryMatch.Rejected(CredentialFailureCode.Conflict);
        }
        if (!entry.ConsentGranted)
        {
            return CredentialLeaseRegistryMatch.Rejected(CredentialFailureCode.Unauthorized);
        }
        if (entry.Reference.Status != CredentialLifecycleStatus.Active)
        {
            var code = entry.Reference.Status == CredentialLifecycleStatus.Revoked ? CredentialFailureCode.Revoked : entry.Reference.Status == CredentialLifecycleStatus.Expired ? CredentialFailureCode.Expired : CredentialFailureCode.Unauthorized;
            return CredentialLeaseRegistryMatch.Rejected(code);
        }
        if (entry.Reference.ExpiresAtUtc is { } expiry && trustedNowUtc >= expiry)
        {
            return CredentialLeaseRegistryMatch.Rejected(CredentialFailureCode.Expired);
        }
        if (entry.Health != CredentialProviderHealthStatus.Available)
        {
            return CredentialLeaseRegistryMatch.Rejected(entry.Health == CredentialProviderHealthStatus.Revoked ? CredentialFailureCode.Revoked : entry.Health == CredentialProviderHealthStatus.Expired ? CredentialFailureCode.Expired : CredentialFailureCode.Unavailable);
        }

        return CredentialLeaseRegistryMatch.Accepted(ComputeEvidenceHash(intent, entry, read.RegistryRevision.Value));
    }

    private static string ComputeEvidenceHash(CredentialLeaseIntent intent, CredentialRegistryEntry entry, long registryRevision)
    {
        var canonical = string.Join('\n',
            "embodysense.credential-lease-registry-evidence.v1",
            intent.Execution.WorkspaceId,
            intent.Registry.ReferenceId,
            intent.Registry.BindingHash,
            registryRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.ConsentReference.Value,
            entry.ConsentGranted ? "granted" : "denied",
            entry.Reference.ProviderId.Value,
            ((int)entry.Reference.Status).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)entry.Health).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool MatchesOptional(string? ceiling, string exact)
        => ceiling is null || string.Equals(ceiling, exact, StringComparison.Ordinal);
}
