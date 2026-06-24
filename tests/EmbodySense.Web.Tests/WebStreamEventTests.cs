using EmbodySense.Web.Models;

namespace EmbodySense.Web.Tests;

public sealed class WebStreamEventTests
{
    [Fact]
    public void WebStreamEvent_factories_create_final_cancelled_and_error_events()
    {
        var final = WebStreamEvent.AssistantFinal("done");
        var cancelled = WebStreamEvent.Cancelled("cancelled");
        var error = WebStreamEvent.Failure("failed");

        Assert.Equal("assistant_final", final.Type);
        Assert.Equal("done", final.Text);
        Assert.Equal("cancelled", cancelled.Type);
        Assert.Equal("cancelled", cancelled.Text);
        Assert.Equal("error", error.Type);
        Assert.Equal("failed", error.Error);
    }
}
