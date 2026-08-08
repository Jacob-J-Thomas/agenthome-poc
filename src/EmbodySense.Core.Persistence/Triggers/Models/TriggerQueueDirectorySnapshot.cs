namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Binds one governed queue-root ancestor pathname to the exact directory identity observed when the mutation lease was acquired.</summary>
internal sealed record TriggerQueueDirectorySnapshot(string Path, TriggerQueueFileIdentity Identity);
