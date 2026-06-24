using System.Text.Json;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Tests;

public sealed class WebStreamWriterTests
{
    [Fact]
    public async Task WriteAsync_writes_ndjson_event()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var writer = new WebStreamWriter();

        await writer.WriteAsync(context.Response, WebStreamEvent.AssistantDelta("hi"));

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var line = await reader.ReadLineAsync();
        using var json = JsonDocument.Parse(line!);

        Assert.Equal("assistant_delta", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("hi", json.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void WebStreamEvent_factories_create_final_and_error_events()
    {
        var final = WebStreamEvent.AssistantFinal("done", "OpenAiCodex", "gpt-test");
        var error = WebStreamEvent.Failure("failed");

        Assert.Equal("assistant_final", final.Type);
        Assert.Equal("done", final.Text);
        Assert.Equal("OpenAiCodex", final.Surface);
        Assert.Equal("gpt-test", final.Model);
        Assert.Equal("error", error.Type);
        Assert.Equal("failed", error.Error);
    }
}
