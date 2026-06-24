using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
[Route("api/messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;
    private readonly WebStreamWriter _streamWriter;

    public MessagesController(WebAgentRuntimeHost host, WebStreamWriter streamWriter)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(streamWriter);

        _host = host;
        _streamWriter = streamWriter;
    }

    [HttpPost]
    public async Task SendAsync(WebMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("Message is required.", HttpContext.RequestAborted);
            return;
        }

        Response.ContentType = "application/x-ndjson";
        try
        {
            await _host.SendMessageAsync(request.Message, (item, token) => _streamWriter.WriteAsync(Response, item, token), HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            await _streamWriter.WriteAsync(Response, WebStreamEvent.Failure(exception.Message), HttpContext.RequestAborted);
        }
    }
}
