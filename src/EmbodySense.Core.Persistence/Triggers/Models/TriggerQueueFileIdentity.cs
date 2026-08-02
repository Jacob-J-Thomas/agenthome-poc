namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Identifies one regular file strongly enough to detect path substitution during a guarded operation.</summary>
internal sealed record TriggerQueueFileIdentity(ulong Device, ulong File, ulong Links);
