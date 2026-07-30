namespace EmbodySense.Web.Models;

/// <summary>
/// Projects one default-conversation transcript entry to browser clients.
/// </summary>
/// <param name="Role">The normalized message role.</param>
/// <param name="Content">The complete message content.</param>
public sealed record WebTranscriptMessage(string Role, string Content);
