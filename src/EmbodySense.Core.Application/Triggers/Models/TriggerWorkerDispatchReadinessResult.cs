namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Returns one closed pre-intent dispatch-readiness decision.</summary>
/// <param name="Status">The exact readiness disposition.</param>
public sealed record TriggerWorkerDispatchReadinessResult(TriggerWorkerDispatchReadinessStatus Status);
