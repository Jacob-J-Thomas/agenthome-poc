using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Retains the exact admitted model pin and conclusive provider-usage ledger evidence for one completed attempt.</summary>
public sealed class GovernedModelAttemptExecutionEvidence
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelAttemptExecutionEvidence(
        CapabilityId profileId,
        string profilePinHash,
        string configurationHash,
        string providerId,
        string adapterId,
        string modelId,
        LlmInferenceSurface responseSurface,
        string reservationEntryHash,
        string terminalUsageEntryHash,
        GovernedModelUsageLedgerPhase terminalUsagePhase,
        LlmInferenceUsageEvidence usage,
        bool usageUnknown)
    {
        ProfileId = profileId;
        ProfilePinHash = profilePinHash;
        ConfigurationHash = configurationHash;
        ProviderId = providerId;
        AdapterId = adapterId;
        ModelId = modelId;
        ResponseSurface = responseSurface;
        ReservationEntryHash = reservationEntryHash;
        TerminalUsageEntryHash = terminalUsageEntryHash;
        TerminalUsagePhase = terminalUsagePhase;
        Usage = usage;
        UsageUnknown = usageUnknown;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-attempt-execution-evidence.v1", WriteCanonical);
    }

    /// <summary>Gets the exact admitted generic ModelProfile capability ID.</summary>
    public CapabilityId ProfileId { get; }
    /// <summary>Gets the immutable admitted profile-pin hash.</summary>
    public string ProfilePinHash { get; }
    /// <summary>Gets the non-secret exact adapter configuration hash.</summary>
    public string ConfigurationHash { get; }
    /// <summary>Gets the safe provider coordinate from the exact profile.</summary>
    public string ProviderId { get; }
    /// <summary>Gets the safe adapter coordinate from the exact profile.</summary>
    public string AdapterId { get; }
    /// <summary>Gets the safe model coordinate from the exact profile.</summary>
    public string ModelId { get; }
    /// <summary>Gets the exact provider-response surface.</summary>
    public LlmInferenceSurface ResponseSurface { get; }
    /// <summary>Gets the durable pre-transport reservation entry hash.</summary>
    public string ReservationEntryHash { get; }
    /// <summary>Gets the conclusive terminal usage-ledger entry hash.</summary>
    public string TerminalUsageEntryHash { get; }
    /// <summary>Gets the terminal usage-ledger phase.</summary>
    public GovernedModelUsageLedgerPhase TerminalUsagePhase { get; }
    /// <summary>Gets authoritative-or-explicitly-unavailable provider usage.</summary>
    public LlmInferenceUsageEvidence Usage { get; }
    /// <summary>Gets whether any bounded dimension remains unknown and conservatively reserved.</summary>
    public bool UsageUnknown { get; }
    /// <summary>Gets the canonical complete evidence hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates bounded completed-attempt model evidence.</summary>
    public static GovernedModelAttemptExecutionEvidence Create(
        int schemaVersion,
        CapabilityId profileId,
        string profilePinHash,
        string configurationHash,
        string providerId,
        string adapterId,
        string modelId,
        LlmInferenceSurface responseSurface,
        string reservationEntryHash,
        string terminalUsageEntryHash,
        GovernedModelUsageLedgerPhase terminalUsagePhase,
        LlmInferenceUsageEvidence usage,
        bool usageUnknown)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(usage);
        if (!CapabilityId.TryParse(profileId.Value, out var parsedProfile, out _)
            || !profileId.Equals(parsedProfile)
            || !Enum.IsDefined(responseSurface)
            || responseSurface == LlmInferenceSurface.Unknown
            || terminalUsagePhase != GovernedModelUsageLedgerPhase.Reconciled
            || !GovernedModelContractValidator.IsValid(usage))
        {
            throw new ArgumentException("Completed model-attempt evidence must use canonical exact identities and reconciled usage.");
        }

        return new GovernedModelAttemptExecutionEvidence(
            profileId,
            GovernedModelContractRules.RequireHash(profilePinHash, nameof(profilePinHash)),
            GovernedModelContractRules.RequireHash(configurationHash, nameof(configurationHash)),
            GovernedModelContractRules.RequireIdentifier(providerId, nameof(providerId)),
            GovernedModelContractRules.RequireIdentifier(adapterId, nameof(adapterId)),
            GovernedModelContractRules.RequireIdentifier(modelId, nameof(modelId)),
            responseSurface,
            GovernedModelContractRules.RequireHash(reservationEntryHash, nameof(reservationEntryHash)),
            GovernedModelContractRules.RequireHash(terminalUsageEntryHash, nameof(terminalUsageEntryHash)),
            terminalUsagePhase,
            usage,
            usageUnknown);
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("adapterId", AdapterId);
        writer.WriteString("configurationHash", ConfigurationHash);
        writer.WriteString("modelId", ModelId);
        writer.WriteString("profileId", ProfileId.Value);
        writer.WriteString("profilePinHash", ProfilePinHash);
        writer.WriteString("providerId", ProviderId);
        writer.WriteString("reservationEntryHash", ReservationEntryHash);
        writer.WriteNumber("responseSurface", (int)ResponseSurface);
        writer.WriteString("terminalUsageEntryHash", TerminalUsageEntryHash);
        writer.WriteNumber("terminalUsagePhase", (int)TerminalUsagePhase);
        writer.WriteString("usageHash", Usage.ContentHash);
        writer.WriteBoolean("usageUnknown", UsageUnknown);
        writer.WriteEndObject();
    }
}
