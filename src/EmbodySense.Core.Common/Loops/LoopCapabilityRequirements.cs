using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Common.Loops;

/// <summary>Maps the first-wave loop authoring vocabulary to bounded capability dependency manifests.</summary>
/// <remarks>The legacy loop capability and custom-tool values are authoring inputs only; exact admitted pins are runtime truth.</remarks>
public static class LoopCapabilityRequirements
{
    /// <summary>Gets the exact catalog identifier for the built-in conversation-turn implementation.</summary>
    public static CapabilityId ConversationTurnId { get; } = ParseId("org.embodysense/conversation-turn");

    /// <summary>Gets the exact catalog identifier for the built-in governed workspace-command implementation.</summary>
    public static CapabilityId WorkspaceCommandId { get; } = ParseId("org.embodysense/workspace-command");

    /// <summary>Creates the declared requirements for the default conversation loop.</summary>
    public static CapabilityDependencyManifest CreateDefaultConversationManifest()
    {
        return Create("default-conversation", includeWorkspaceCommand: true);
    }

    /// <summary>Creates requirements for one authored custom loop from its explicit tool assignments.</summary>
    public static CapabilityDependencyManifest CreateCustomLoopManifest(string loopId, IReadOnlyCollection<CustomLoopToolAssignment> assignments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loopId);
        ArgumentNullException.ThrowIfNull(assignments);
        return Create("custom-" + Digest(loopId), assignments.Count > 0);
    }

    /// <summary>Returns the maximum catalog identities explicitly assigned by the loop authoring contract.</summary>
    public static IReadOnlyList<CapabilityId> GetAssignedCapabilityIds(CapabilityDependencyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Required.Concat(manifest.Optional).Select(item => item.CapabilityId).Distinct().OrderBy(item => item.Value, StringComparer.Ordinal).ToArray();
    }

    private static CapabilityDependencyManifest Create(string subjectSuffix, bool includeWorkspaceCommand)
    {
        _ = CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var range, out _);
        var required = new List<CapabilityDependency> { new(ConversationTurnId, range!) };
        if (includeWorkspaceCommand)
        {
            required.Add(new CapabilityDependency(WorkspaceCommandId, range!));
        }

        return new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            ParseId("org.embodysense/loop-" + subjectSuffix),
            required,
            [],
            new CapabilityDependencyArtifactMetadata(null, null));
    }

    private static CapabilityId ParseId(string value)
    {
        if (!CapabilityId.TryParse(value, out var id, out var error))
        {
            throw new InvalidOperationException(error?.Message ?? "The loop capability identifier is invalid.");
        }

        return id!;
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];
}
