namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Binds one immutable ledger-generation pathname to its exact observed identity, length, and content digest.</summary>
internal sealed record TriggerQueueArtifactSnapshot(string Path, long Generation, TriggerQueueFileIdentity Identity, long Length, string ContentHash);
