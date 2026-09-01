using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Tests;

internal sealed class ThrowingHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext
    {
        get => throw new InvalidOperationException("private accessor detail");
        set => throw new InvalidOperationException("private accessor detail");
    }
}
