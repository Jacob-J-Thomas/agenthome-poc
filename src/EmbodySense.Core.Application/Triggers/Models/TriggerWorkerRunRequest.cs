namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Requests one bounded, non-background worker selection and dispatch attempt.</summary>
/// <param name="Selection">The exact selection and fairness inputs.</param>
public sealed record TriggerWorkerRunRequest(TriggerWorkerSelectionRequest Selection);
