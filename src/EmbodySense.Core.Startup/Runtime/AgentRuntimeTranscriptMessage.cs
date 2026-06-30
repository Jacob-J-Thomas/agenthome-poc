namespace EmbodySense.Core.Startup.Runtime;

// TODO(startup-runtime-dtos): Revisit AgentRuntimeCommandResult/AgentRuntimeTranscriptMessage after the shared host event API exists.
// Deferred because Web/CLI need a Core.Startup-only contract today; remove or consolidate these facade DTOs if they become pass-through duplicates.
public sealed record AgentRuntimeTranscriptMessage(string Role, string Content);
