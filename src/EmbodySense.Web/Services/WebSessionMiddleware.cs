namespace EmbodySense.Web.Services;

public sealed class WebSessionMiddleware
{
    private readonly RequestDelegate _next;

    public WebSessionMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, WebSessionSecurity sessionSecurity)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sessionSecurity);

        if (!sessionSecurity.IsAuthorized(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden EmbodySense web request.", context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
