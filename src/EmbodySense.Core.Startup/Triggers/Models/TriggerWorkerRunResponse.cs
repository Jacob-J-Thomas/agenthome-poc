namespace EmbodySense.Core.Startup.Triggers.Models;

/// <summary>Projects one one-shot worker selection and final durable outcome.</summary>
/// <param name="SelectionStatus">The selection status.</param>
/// <param name="MutationStatus">The final mutation status when selection succeeded.</param>
/// <param name="Entry">The latest entry posture.</param>
public sealed record TriggerWorkerRunResponse(string SelectionStatus, string? MutationStatus, TriggerWorkerEntrySnapshot? Entry);
