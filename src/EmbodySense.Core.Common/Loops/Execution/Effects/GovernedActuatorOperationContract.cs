using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Effects;

/// <summary>Validates, hashes, and snapshots immutable schema-1 actuator-operation metadata.</summary>
public static class GovernedActuatorOperationContract
{
    private const string Domain = "embodysense.governed-actuator-operation.v1";

    /// <summary>Gets whether a stable operation id uses the bounded canonical capability-path grammar.</summary>
    public static bool IsOperationId(string? value)
        => CapabilityIdentifierRules.IsPath(value, GovernedLoopEffectAttemptContractLimits.MaxOperationIdCharacters);

    /// <summary>Creates one validated descriptor and applies its canonical content hash.</summary>
    public static GovernedActuatorOperationDescriptor Create(
        int schemaVersion,
        CapabilityDescriptorIdentity capability,
        CapabilityImplementationIdentity implementation,
        string operationId,
        string riskSummary,
        GovernedActuatorTargetSemantics targetSemantics,
        GovernedActuatorIdempotencyPosture idempotency,
        bool requiresOptimisticPrecondition,
        GovernedActuatorApprovalPosture approval,
        bool unattendedEligible,
        GovernedActuatorCancellationPosture cancellation,
        GovernedActuatorAmbiguityPosture ambiguity,
        bool requiresBeforeEvidence,
        bool requiresAfterEvidence,
        bool requiresOutcomeEvidence)
    {
        var candidate = new GovernedActuatorOperationDescriptor(
            schemaVersion,
            capability,
            implementation,
            operationId,
            riskSummary,
            targetSemantics,
            idempotency,
            requiresOptimisticPrecondition,
            approval,
            unattendedEligible,
            cancellation,
            ambiguity,
            requiresBeforeEvidence,
            requiresAfterEvidence,
            requiresOutcomeEvidence,
            string.Empty);
        var error = ValidateForHash(candidate);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(operationId));
        }

        return candidate with { ContentHash = Compute(candidate) };
    }

    /// <summary>Returns a bounded structured reason code when a descriptor is invalid; otherwise <see langword="null"/>.</summary>
    public static string? Validate(GovernedActuatorOperationDescriptor? descriptor)
    {
        var error = ValidateForHash(descriptor);
        if (error is not null)
        {
            return error;
        }

        return IsCanonicalSha256(descriptor!.ContentHash)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(descriptor.ContentHash),
                Encoding.ASCII.GetBytes(Compute(descriptor)))
            ? null
            : "operation-content-hash-mismatch";
    }

    /// <summary>Computes the domain-separated canonical descriptor hash, excluding <c>ContentHash</c>.</summary>
    public static string Compute(GovernedActuatorOperationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var error = ValidateForHash(descriptor);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(descriptor));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Domain);
        Append(hash, descriptor.SchemaVersion);
        Append(hash, descriptor.Capability.Id.Value);
        Append(hash, descriptor.Capability.Version.Value);
        Append(hash, descriptor.Capability.Hash.Value);
        Append(hash, descriptor.Implementation.ProviderId.Value);
        Append(hash, descriptor.Implementation.ImplementationId);
        Append(hash, descriptor.OperationId);
        Append(hash, descriptor.RiskSummary);
        Append(hash, (int)descriptor.TargetSemantics);
        Append(hash, (int)descriptor.Idempotency);
        Append(hash, descriptor.RequiresOptimisticPrecondition);
        Append(hash, (int)descriptor.Approval);
        Append(hash, descriptor.UnattendedEligible);
        Append(hash, (int)descriptor.Cancellation);
        Append(hash, (int)descriptor.Ambiguity);
        Append(hash, descriptor.RequiresBeforeEvidence);
        Append(hash, descriptor.RequiresAfterEvidence);
        Append(hash, descriptor.RequiresOutcomeEvidence);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string? ValidateForHash(GovernedActuatorOperationDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return "operation-required";
        }
        if (descriptor.SchemaVersion != GovernedLoopEffectAttemptContractLimits.CurrentSchemaVersion)
        {
            return "operation-schema-unsupported";
        }
        if (descriptor.Capability?.Id is null
            || descriptor.Capability.Version is null
            || descriptor.Capability.Hash is null
            || !CapabilityId.TryParse(descriptor.Capability.Id.Value, out _, out _)
            || !CapabilityVersion.TryParse(descriptor.Capability.Version.Value, out _, out _)
            || !CapabilityDescriptorHash.TryParse(descriptor.Capability.Hash.Value, out _, out _))
        {
            return "operation-capability-pin-invalid";
        }
        if (descriptor.Implementation?.ProviderId is null
            || !CapabilityProviderId.TryParse(descriptor.Implementation.ProviderId.Value, out _, out _)
            || !CapabilityIdentifierRules.IsPath(descriptor.Implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters))
        {
            return "operation-implementation-pin-invalid";
        }
        if (!IsOperationId(descriptor.OperationId))
        {
            return "operation-id-invalid";
        }
        if (!CapabilityTextRules.IsSafeNormalized(descriptor.RiskSummary, GovernedLoopEffectAttemptContractLimits.MaxRiskSummaryCharacters, allowEmpty: false))
        {
            return "operation-risk-summary-invalid";
        }
        if (!IsSupported(descriptor.TargetSemantics)
            || !IsSupported(descriptor.Idempotency)
            || !IsSupported(descriptor.Approval)
            || !IsSupported(descriptor.Cancellation)
            || descriptor.Ambiguity != GovernedActuatorAmbiguityPosture.ReconciliationRequired)
        {
            return "operation-posture-invalid";
        }
        if (!descriptor.RequiresOutcomeEvidence)
        {
            // Schema 1's canonical OutcomeObserved/Committed effect phases require
            // one bounded outcome-evidence reference. Advertising an operation that
            // cannot provide it would make every conclusive adapter result
            // structurally impossible to retain.
            return "operation-outcome-evidence-required";
        }
        return null;
    }

    private static bool IsSupported<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => Enum.IsDefined(value) && Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0;

    private static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
        => Append(hash, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, bool value)
        => Append(hash, value ? "1" : "0");
}
