using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

/// <summary>Publishes value-free Human Review refresh hints after durable state changes.</summary>
public interface IWebHumanReviewNotifier
{
    /// <summary>Broadcasts that one run's canonical Human Review state should be reread.</summary>
    /// <param name="notification">The value-free durable run identity.</param>
    /// <param name="cancellationToken">Reserved for notifier implementations; delivery is not authority.</param>
    /// <returns>A task that completes after the server-side notification dispatch.</returns>
    Task HumanReviewChangedAsync(WebHumanReviewChanged notification, CancellationToken cancellationToken = default);
}
