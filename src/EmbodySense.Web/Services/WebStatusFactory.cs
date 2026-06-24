using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public static class WebStatusFactory
{
    public static WebStatus Create(WebRunOptions options, WorkspaceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);

        return new WebStatus("web", true, status.RootPath, status.IsInitialized, options.Url, "CLI remains supported for verification and third-party client conformance.");
    }
}
