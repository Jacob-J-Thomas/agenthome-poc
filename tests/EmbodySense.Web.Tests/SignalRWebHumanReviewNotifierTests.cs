using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

public sealed class SignalRWebHumanReviewNotifierTests
{
    [Fact]
    public async Task HumanReviewChangedAsync_broadcasts_exact_value_free_run_identity_to_all_clients()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRWebHumanReviewNotifier(context);
        var notification = new WebHumanReviewChanged("run-1");

        await notifier.HumanReviewChangedAsync(notification);

        Assert.Same(notification, Assert.Single(context.ClientsRecorder.AllClient.HumanReviewChanges));
        Assert.Equal("run-1", notification.RunId);
        Assert.Empty(context.ClientsRecorder.TargetedConnectionIds);
        Assert.Empty(context.ClientsRecorder.TargetedClient.HumanReviewChanges);
    }

    [Fact]
    public async Task HumanReviewChangedAsync_dispatches_even_when_cancellation_is_already_requested()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRWebHumanReviewNotifier(context);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await notifier.HumanReviewChangedAsync(new WebHumanReviewChanged("run-2"), cancellation.Token);

        Assert.Single(context.ClientsRecorder.AllClient.HumanReviewChanges);
        Assert.Empty(context.ClientsRecorder.TargetedConnectionIds);
    }

    [Fact]
    public async Task HumanReviewChangedAsync_rejects_null_notification_before_dispatch()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRWebHumanReviewNotifier(context);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => notifier.HumanReviewChangedAsync(null!));

        Assert.Equal("notification", exception.ParamName);
        Assert.Empty(context.ClientsRecorder.AllClient.HumanReviewChanges);
        Assert.Empty(context.ClientsRecorder.TargetedConnectionIds);
    }

    [Fact]
    public void HumanReviewNotification_contract_contains_only_the_bounded_run_identity()
    {
        var properties = typeof(WebHumanReviewChanged).GetProperties();

        var property = Assert.Single(properties);
        Assert.Equal(nameof(WebHumanReviewChanged.RunId), property.Name);
        Assert.Equal(typeof(string), property.PropertyType);
    }
}
