using System.Text.Json;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Hash-binds exact model routing for every reachable Inference node to one immutable governed-loop admission.</summary>
public sealed class GovernedModelRoutingAdmissionSnapshot
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelRoutingAdmissionSnapshot(string workspaceId, string admissionOperationId, string admissionIntentHash, string executionBindingReferenceHash, string runId, string graphId, string graphRevisionId, string graphExecutableHash, long executionGeneration, string owningRoleId, long owningRoleRevision, string owningRoleContentHash, string capabilityAdmissionReferenceHash, string authorityAdmissionReferenceHash, long? capabilityCatalogRevision, CapabilityId? resolvedDefaultProfileId, string? defaultSourceRevisionHash, string? adapterRegistryRevisionHash, DateTimeOffset evaluatedAtUtc, IReadOnlyList<GovernedModelRoutingAdmissionEntry> entries)
    {
        WorkspaceId = workspaceId;
        AdmissionOperationId = admissionOperationId;
        AdmissionIntentHash = admissionIntentHash;
        ExecutionBindingReferenceHash = executionBindingReferenceHash;
        RunId = runId;
        GraphId = graphId;
        GraphRevisionId = graphRevisionId;
        GraphExecutableHash = graphExecutableHash;
        ExecutionGeneration = executionGeneration;
        OwningRoleId = owningRoleId;
        OwningRoleRevision = owningRoleRevision;
        OwningRoleContentHash = owningRoleContentHash;
        CapabilityAdmissionReferenceHash = capabilityAdmissionReferenceHash;
        AuthorityAdmissionReferenceHash = authorityAdmissionReferenceHash;
        CapabilityCatalogRevision = capabilityCatalogRevision;
        ResolvedDefaultProfileId = resolvedDefaultProfileId;
        DefaultSourceRevisionHash = defaultSourceRevisionHash;
        AdapterRegistryRevisionHash = adapterRegistryRevisionHash;
        EvaluatedAtUtc = evaluatedAtUtc;
        Entries = GovernedModelContractRules.RetainSnapshot(entries, GovernedModelContractLimits.MaxAdmissionEntries, nameof(entries));
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-routing-admission.v1", WriteCanonical);
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion => GovernedModelContractLimits.CurrentSchemaVersion;
    /// <summary>Gets the exact workspace identity.</summary>
    public string WorkspaceId { get; }
    /// <summary>Gets the idempotent admission operation identity.</summary>
    public string AdmissionOperationId { get; }
    /// <summary>Gets the canonical immutable admission-intent hash resolved before the final receipt exists.</summary>
    public string AdmissionIntentHash { get; }
    /// <summary>Gets the canonical exact execution-binding reference hash.</summary>
    public string ExecutionBindingReferenceHash { get; }
    /// <summary>Gets the exact server-owned admitted run identity.</summary>
    public string RunId { get; }
    /// <summary>Gets the exact immutable graph identity.</summary>
    public string GraphId { get; }
    /// <summary>Gets the exact immutable graph revision identity.</summary>
    public string GraphRevisionId { get; }
    /// <summary>Gets the exact executable graph hash.</summary>
    public string GraphExecutableHash { get; }
    /// <summary>Gets the exact positive execution generation admitted for this run.</summary>
    public long ExecutionGeneration { get; }
    /// <summary>Gets the exact owning contextual-role ID.</summary>
    public string OwningRoleId { get; }
    /// <summary>Gets the exact owning contextual-role revision.</summary>
    public long OwningRoleRevision { get; }
    /// <summary>Gets the exact owning-role semantic content hash.</summary>
    public string OwningRoleContentHash { get; }
    /// <summary>Gets the exact generic capability-admission reference hash.</summary>
    public string CapabilityAdmissionReferenceHash { get; }
    /// <summary>Gets the exact non-circular authority-admission reference hash.</summary>
    public string AuthorityAdmissionReferenceHash { get; }
    /// <summary>Gets the exact coherent capability-catalog revision used to resolve candidates, or null for explicit empty routing.</summary>
    public long? CapabilityCatalogRevision { get; }
    /// <summary>Gets the exact configured default profile resolved for Inherit, or null when no Inherit selector was evaluated.</summary>
    public CapabilityId? ResolvedDefaultProfileId { get; }
    /// <summary>Gets the exact configured-default source revision for Inherit resolution, or null when no Inherit selector was evaluated.</summary>
    public string? DefaultSourceRevisionHash { get; }
    /// <summary>Gets the one coherent exact adapter-registry revision used for all candidates, or null for explicit empty routing.</summary>
    public string? AdapterRegistryRevisionHash { get; }
    /// <summary>Gets the trusted UTC evaluation time.</summary>
    public DateTimeOffset EvaluatedAtUtc { get; }
    /// <summary>Gets entries ordered by canonical node ID.</summary>
    public IReadOnlyList<GovernedModelRoutingAdmissionEntry> Entries { get; }
    /// <summary>Gets the canonical complete snapshot hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a complete immutable routing admission snapshot.</summary>
    public static GovernedModelRoutingAdmissionSnapshot Create(int schemaVersion, string workspaceId, string admissionOperationId, string admissionIntentHash, string executionBindingReferenceHash, string runId, string graphId, string graphRevisionId, string graphExecutableHash, long executionGeneration, string owningRoleId, long owningRoleRevision, string owningRoleContentHash, string capabilityAdmissionReferenceHash, string authorityAdmissionReferenceHash, long? capabilityCatalogRevision, CapabilityId? resolvedDefaultProfileId, string? defaultSourceRevisionHash, string? adapterRegistryRevisionHash, DateTimeOffset evaluatedAtUtc, IEnumerable<GovernedModelRoutingAdmissionEntry> entries)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        if (evaluatedAtUtc == default || evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The routing admission evaluation time must be non-default UTC.", nameof(evaluatedAtUtc));
        }

        var values = GovernedModelContractRules.RequireCanonicalSet(entries, nameof(entries), value => value.NodeId, maximum: GovernedModelContractLimits.MaxAdmissionEntries);

        if (values.Any(value => !GovernedModelContractValidator.IsValid(value)))
        {
            throw new ArgumentException("Every routing admission entry must be a complete canonical value.", nameof(entries));
        }
        if ((values.Count == 0) != (capabilityCatalogRevision is null)
            || capabilityCatalogRevision is < 0)
        {
            throw new ArgumentException("A coherent nonnegative catalog revision is required exactly when routing contains candidates.", nameof(capabilityCatalogRevision));
        }
        if ((values.Count == 0) != (adapterRegistryRevisionHash is null))
        {
            throw new ArgumentException("A coherent adapter-registry revision is required exactly when routing contains candidates.", nameof(adapterRegistryRevisionHash));
        }
        if ((resolvedDefaultProfileId is null) != (defaultSourceRevisionHash is null)
            || resolvedDefaultProfileId is not null && (!CapabilityId.TryParse(resolvedDefaultProfileId.Value, out var parsedDefault, out _) || !resolvedDefaultProfileId.Equals(parsedDefault)))
        {
            throw new ArgumentException("Resolved default identity and exact source revision must be present together and canonical.", nameof(resolvedDefaultProfileId));
        }

        return new GovernedModelRoutingAdmissionSnapshot(
            ContextualRoleWorkspaceId.IsValid(workspaceId) ? workspaceId : throw new ArgumentException("Workspace identity must use the canonical workspace-sha256 scope.", nameof(workspaceId)),
            CustomLoopArtifactIdentifier.Require(admissionOperationId, nameof(admissionOperationId), GovernedModelContractLimits.MaxIdentifierCharacters),
            GovernedModelContractRules.RequireHash(admissionIntentHash, nameof(admissionIntentHash)),
            GovernedModelContractRules.RequireHash(executionBindingReferenceHash, nameof(executionBindingReferenceHash)),
            CustomLoopArtifactIdentifier.Require(runId, nameof(runId), GovernedLoopExecutionLimits.MaxIdentifierCharacters),
            CustomLoopArtifactIdentifier.Require(graphId, nameof(graphId)),
            CustomLoopArtifactIdentifier.Require(graphRevisionId, nameof(graphRevisionId)),
            GovernedModelContractRules.RequireHash(graphExecutableHash, nameof(graphExecutableHash)),
            GovernedModelContractRules.RequireQuantity(executionGeneration, long.MaxValue, nameof(executionGeneration), positive: true),
            ContextualRoleId.IsValid(owningRoleId) ? owningRoleId : throw new ArgumentException("Owning role identity must be canonical.", nameof(owningRoleId)),
            GovernedModelContractRules.RequireQuantity(owningRoleRevision, long.MaxValue, nameof(owningRoleRevision), positive: true),
            GovernedModelContractRules.RequireHash(owningRoleContentHash, nameof(owningRoleContentHash)),
            GovernedModelContractRules.RequireHash(capabilityAdmissionReferenceHash, nameof(capabilityAdmissionReferenceHash)),
            GovernedModelContractRules.RequireHash(authorityAdmissionReferenceHash, nameof(authorityAdmissionReferenceHash)),
            capabilityCatalogRevision,
            resolvedDefaultProfileId,
            defaultSourceRevisionHash is null ? null : GovernedModelContractRules.RequireHash(defaultSourceRevisionHash, nameof(defaultSourceRevisionHash)),
            adapterRegistryRevisionHash is null ? null : GovernedModelContractRules.RequireHash(adapterRegistryRevisionHash, nameof(adapterRegistryRevisionHash)),
            evaluatedAtUtc,
            values);
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("admissionOperationId", AdmissionOperationId);
        writer.WriteString("adapterRegistryRevisionHash", AdapterRegistryRevisionHash);
        writer.WriteString("admissionIntentHash", AdmissionIntentHash);
        writer.WriteString("authorityAdmissionReferenceHash", AuthorityAdmissionReferenceHash);
        writer.WriteString("capabilityAdmissionReferenceHash", CapabilityAdmissionReferenceHash);
        if (CapabilityCatalogRevision is { } catalogRevision)
        {
            writer.WriteNumber("capabilityCatalogRevision", catalogRevision);
        }
        else
        {
            writer.WriteNull("capabilityCatalogRevision");
        }
        writer.WriteString("defaultSourceRevisionHash", DefaultSourceRevisionHash);
        writer.WriteString("resolvedDefaultProfileId", ResolvedDefaultProfileId?.Value);
        GovernedModelContractHash.WriteStrings(writer, "entryHashes", Entries.Select(value => value.ContentHash));
        writer.WriteString("evaluatedAtUtc", EvaluatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("executionBindingReferenceHash", ExecutionBindingReferenceHash);
        writer.WriteString("graphExecutableHash", GraphExecutableHash);
        writer.WriteString("graphId", GraphId);
        writer.WriteString("graphRevisionId", GraphRevisionId);
        writer.WriteNumber("executionGeneration", ExecutionGeneration);
        writer.WriteString("owningRoleId", OwningRoleId);
        writer.WriteNumber("owningRoleRevision", OwningRoleRevision);
        writer.WriteString("owningRoleContentHash", OwningRoleContentHash);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("runId", RunId);
        writer.WriteString("workspaceId", WorkspaceId);
        writer.WriteEndObject();
    }
}
