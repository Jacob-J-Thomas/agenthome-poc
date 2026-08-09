using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

public sealed class WebClientNotifierTests
{
    [Fact]
    public async Task None_accepts_a_valid_status_through_the_interface()
    {
        IWebClientNotifier notifier = WebClientNotifier.None;
        var status = new WebStatus("web", true, "C:\\workspace", false, "uninitialized", false, null, "http://127.0.0.1:5174", "CLI verification");

        await notifier.StatusChangedAsync(status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task None_rejects_an_ownerless_nonempty_projection_through_the_interface(string? ownerConnectionId)
    {
        IWebClientNotifier notifier = WebClientNotifier.None;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => notifier.ApprovalsChangedAsync(ownerConnectionId, [CreateApproval()]));

        Assert.Equal("ownerConnectionId", exception.ParamName);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("owner-1", false)]
    [InlineData("owner-1", true)]
    public async Task None_accepts_only_valid_owner_projection_combinations(string? ownerConnectionId, bool includeApproval)
    {
        IWebClientNotifier notifier = WebClientNotifier.None;
        IReadOnlyList<WebPendingApproval> approvals = includeApproval ? [CreateApproval()] : [];

        await notifier.ApprovalsChangedAsync(ownerConnectionId, approvals);
    }

    private static WebPendingApproval CreateApproval()
    {
        return new WebPendingApproval("request-1", 1, DateTimeOffset.UnixEpoch, "read", "private/note.txt", "C:\\workspace\\private\\note.txt", "read", "private", "approval required");
    }
}
