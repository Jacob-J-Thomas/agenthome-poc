using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Revalidates a default-turn exact capability pin and current loop authority immediately before tool actuation.</summary>
public sealed class DefaultConversationCapabilityAuthorityRevalidator : IToolActuationAuthorityRevalidator
{
    private readonly IDefaultConversationTurnStore _turnStore;
    private readonly LoopDefinitionStore _definitionStore;
    private readonly ICapabilityAdmissionService _capabilityAdmissionService;

    /// <summary>Creates the default-conversation pre-actuation authority boundary.</summary>
    public DefaultConversationCapabilityAuthorityRevalidator(IDefaultConversationTurnStore turnStore, LoopDefinitionStore definitionStore, ICapabilityAdmissionService capabilityAdmissionService)
    {
        _turnStore = turnStore ?? throw new ArgumentNullException(nameof(turnStore));
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _capabilityAdmissionService = capabilityAdmissionService ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
    }

    /// <inheritdoc />
    public async Task<ToolActuationAuthorityRevalidation> RevalidateAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var correlation = request.AuditCorrelation;
        if (correlation is null || !string.Equals(correlation.LoopId, BuiltInLoopIds.DefaultConversation, StringComparison.Ordinal) || !correlation.RunId.StartsWith("run-", StringComparison.Ordinal))
        {
            return Denied("Tool request has no canonical default-conversation run correlation.");
        }

        var turn = await _turnStore.LoadAsync("turn-" + correlation.RunId[4..], cancellationToken);
        var definition = await _definitionStore.LoadAsync(BuiltInLoopIds.DefaultConversation, cancellationToken);
        if (turn is null || definition is null || !string.Equals(turn.Run.RunId, correlation.RunId, StringComparison.Ordinal)
            || !string.Equals(definition.RoleId, turn.Run.RoleId, StringComparison.Ordinal)
            || !LoopCapabilityIds.AllowsWorkspaceCommand(definition.CapabilityIds, request.Command))
        {
            return Denied("The current default-conversation run, role, or command authority is missing or narrower than the request.");
        }

        var allowed = LoopCapabilityRequirements.GetAssignedCapabilityIds(definition.CapabilityRequirements);
        var current = await _capabilityAdmissionService.RevalidateAsync(turn.CapabilityAdmission, allowed, cancellationToken);
        if (!current.IsValid || !current.EffectivePins.Any(pin => pin.DescriptorIdentity.Id.Equals(LoopCapabilityRequirements.WorkspaceCommandId)))
        {
            return Denied("The admitted workspace-command capability is no longer exact and currently available.");
        }

        return new ToolActuationAuthorityRevalidation(true, "Current loop authority and the immutable workspace-command capability pin allow actuation.", new Dictionary<string, object?>
        {
            ["capability_authority_valid"] = true,
            ["capability_id"] = LoopCapabilityRequirements.WorkspaceCommandId.Value,
            ["capability_descriptor_hash"] = current.EffectivePins.Single(pin => pin.DescriptorIdentity.Id.Equals(LoopCapabilityRequirements.WorkspaceCommandId)).DescriptorIdentity.Hash.Value
        });
    }

    private static ToolActuationAuthorityRevalidation Denied(string detail) => new(false, detail, new Dictionary<string, object?> { ["capability_authority_valid"] = false });
}
