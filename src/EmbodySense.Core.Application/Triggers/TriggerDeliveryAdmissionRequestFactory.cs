using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>
/// Creates admission requests only from bounded current and delivery evidence.
/// </summary>
public static class TriggerDeliveryAdmissionRequestFactory
{
    /// <summary>
    /// Creates a request without treating any envelope field or direct boundary receipt as an execution grant.
    /// </summary>
    /// <param name="envelope">The bounded delivery evidence to evaluate.</param>
    /// <param name="currentLoop">The exact current loop definition pin.</param>
    /// <param name="currentAdapter">The exact current adapter capability and implementation pin.</param>
    /// <param name="isAdapterAvailable">Whether that exact current pin is available.</param>
    /// <param name="currentActorContext">The exact current actor, surface, workspace, and role.</param>
    /// <param name="currentAuthority">The exact current non-executing authority evidence.</param>
    /// <param name="evaluatedAtUtc">The exact UTC classification instant.</param>
    /// <param name="request">The immutable request when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when all current and delivery evidence is valid; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreate(
        TriggerDeliveryEnvelope? envelope,
        TriggerLoopReference? currentLoop,
        TriggerAdapterReference? currentAdapter,
        bool isAdapterAvailable,
        TriggerActorContext? currentActorContext,
        TriggerAuthorityEvidence? currentAuthority,
        DateTimeOffset evaluatedAtUtc,
        out TriggerDeliveryAdmissionRequest? request,
        out TriggerContractValidationResult validation)
    {
        var errors = new List<TriggerContractError>();
        errors.AddRange(TriggerDeliveryValidator.Validate(envelope).Errors);
        if (currentLoop is null || currentLoop.DefinitionVersion is < 1 or > TriggerDeliveryLimits.MaxLoopDefinitionVersion || !TriggerDeliveryFactory.TryCreateLoopReference(currentLoop.LoopId, currentLoop.DefinitionVersion, currentLoop.ContentHash, out _, out _))
        {
            errors.Add(Error("invalid_current_loop", "currentLoop"));
        }

        if (!TriggerDeliveryValidator.ValidateAdapterReference(currentAdapter).IsValid)
        {
            errors.Add(Error("invalid_current_adapter", "currentAdapter"));
        }

        if (currentActorContext is null || !TriggerDeliveryFactory.TryCreateActorContext(currentActorContext.ActorId, currentActorContext.SurfaceId, currentActorContext.WorkspaceId, currentActorContext.RoleId, out _, out _))
        {
            errors.Add(Error("invalid_current_actor_context", "currentActorContext"));
        }

        if (!TriggerDeliveryValidator.ValidateAuthorityEvidence(currentAuthority).IsValid)
        {
            errors.Add(Error("invalid_current_authority", "currentAuthority"));
        }

        if (evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            errors.Add(Error("utc_required", "evaluatedAtUtc"));
        }

        validation = new TriggerContractValidationResult(errors);
        if (!validation.IsValid)
        {
            request = null;
            return false;
        }

        request = new TriggerDeliveryAdmissionRequest(envelope!, currentLoop!, currentAdapter!, isAdapterAvailable, currentActorContext!, currentAuthority!, evaluatedAtUtc);
        return true;
    }

    private static TriggerContractError Error(string code, string field) => new(code, field);
}
