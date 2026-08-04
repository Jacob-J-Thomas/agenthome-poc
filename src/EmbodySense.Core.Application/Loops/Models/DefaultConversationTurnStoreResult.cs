namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Returns one durable turn-record mutation disposition and current record.
/// </summary>
/// <param name="Status">The mutation disposition.</param>
/// <param name="Record">The current durable record when available.</param>
public sealed record DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus Status, DefaultConversationTurnRecord? Record);
