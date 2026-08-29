using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Retains one ephemeral, exact Human Input response projection for a single canonical checkpoint continuation.</summary>
/// <remarks>This value is reconstructed from the authenticated response store on each ordered-runtime entry. It must never be copied to a run, frontier, event, audit record, or persisted continuation receipt.</remarks>
/// <param name="SchemaVersion">The binding schema version, which must be 1.</param>
/// <param name="CheckpointId">The exact terminal Human Input checkpoint identity.</param>
/// <param name="Selection">The exact immutable response selection whose reference matches the retained checkpoint evidence.</param>
/// <param name="Response">The exact selected response reference that supplied the projected value.</param>
/// <param name="Value">The untrusted graph-typed response value, retained only in process for the immediate ordered execution.</param>
public sealed record GovernedLoopSequentialHumanInputBinding(
    int SchemaVersion,
    string CheckpointId,
    HumanInputResponseSelection Selection,
    HumanInputResponseReference Response,
    GovernedLoopTypedValue Value)
{
    /// <summary>Gets the only supported binding schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <inheritdoc />
    public override string ToString()
        => $"GovernedLoopSequentialHumanInputBinding {{ SchemaVersion = {SchemaVersion}, CheckpointId = {CheckpointId}, Selection = {HumanInputResponseSelectionReference.Create(Selection)}, Response = {Response}, Value = [REDACTED] }}";
}
