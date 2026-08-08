namespace EmbodySense.Web.Models;

/// <summary>
/// Reports the conclusive server-side disposition of one completed chat hub invocation.
/// </summary>
/// <param name="Status">The bounded durable or direct invocation disposition.</param>
/// <param name="ReleaseRequestIdentity">Whether the browser may retire its durable request identity.</param>
public sealed record WebChatRequestResult(string Status, bool ReleaseRequestIdentity);
