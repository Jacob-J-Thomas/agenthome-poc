namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>Identifies one regular filesystem object for the lifetime of an open handle.</summary>
internal readonly record struct DefaultConversationTurnFileIdentity(ulong DeviceId, ulong FileId);
