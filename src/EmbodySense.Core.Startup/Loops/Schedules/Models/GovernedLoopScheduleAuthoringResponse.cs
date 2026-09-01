namespace EmbodySense.Core.Startup.Loops.Schedules.Models;

/// <summary>Returns one closed schedule-authoring outcome and a redacted canonical reread when one is available.</summary>
/// <param name="Status">The closed lowercase outcome token.</param>
/// <param name="OperationId">The exact caller-held operation identity when supplied.</param>
/// <param name="Detail">A bounded non-sensitive explanation of the result.</param>
/// <param name="Schedule">The safe current schedule projection when canonical state was reread successfully.</param>
/// <param name="AuthorityPreviewHash">The server-derived opaque confirmation digest when explicit confirmation is required.</param>
public sealed record GovernedLoopScheduleAuthoringResponse(
    string Status,
    string OperationId,
    string Detail,
    GovernedLoopScheduleAuthoringSnapshot? Schedule,
    string? AuthorityPreviewHash);
