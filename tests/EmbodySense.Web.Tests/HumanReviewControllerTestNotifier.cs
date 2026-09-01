using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

internal sealed class HumanReviewControllerTestNotifier : IWebHumanReviewNotifier
{
    public List<WebHumanReviewChanged> Notifications { get; } = [];

    public Exception? Exception { get; set; }

    public Task HumanReviewChangedAsync(WebHumanReviewChanged notification, CancellationToken cancellationToken = default)
    {
        Notifications.Add(notification);
        if (Exception is not null)
        {
            throw Exception;
        }

        return Task.CompletedTask;
    }
}
