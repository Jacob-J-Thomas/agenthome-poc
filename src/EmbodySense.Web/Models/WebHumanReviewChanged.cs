namespace EmbodySense.Web.Models;

/// <summary>Notifies authenticated clients that one durable Human Review run should be reread.</summary>
/// <param name="RunId">The exact durable run identity whose canonical state changed.</param>
/// <remarks>This value-free notification is never authority or state truth; clients must reread the HTTP facade.</remarks>
public sealed record WebHumanReviewChanged(string RunId);
