namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>
/// Projects the bounded durable disposition of one exact default-conversation browser request.
/// </summary>
/// <param name="Status">
/// One of <c>not-found</c>, <c>pending</c>, <c>completed</c>, <c>rejected</c>,
/// <c>needs-review</c>, or <c>conflict</c>.
/// </param>
/// <param name="RetrySameRequest">
/// Whether a browser may retry only the same canonical message with the same request identity.
/// This never authorizes a new request identity or automatic provider redispatch.
/// </param>
/// <param name="ReleaseRequestIdentity">
/// Whether durable terminal evidence proves that the browser no longer needs to retain the request identity.
/// </param>
public sealed record DefaultConversationRequestReconciliationSnapshot(
    string Status,
    bool RetrySameRequest,
    bool ReleaseRequestIdentity);
