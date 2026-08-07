using EmbodySense.Core.Common.Memory.Models;

namespace EmbodySense.Core.Application.Memory.Models;

/// <summary>
/// Returns one atomic publication disposition with the canonical transcript observed under the same lease.
/// </summary>
public sealed record ConversationPublicationAppendResult(
    ConversationPublicationAppendStatus Status,
    ConversationMemorySnapshot Snapshot);
