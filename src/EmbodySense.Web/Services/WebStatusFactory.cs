using EmbodySense.Core.Common.Workspace;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public static class WebStatusFactory
{
    public static WebStatus Create(WebRunOptions options, WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);

        return new WebStatus("web", true, paths.RootPath, paths.IsInitialized, options.Url, "CLI remains supported for verification and third-party client conformance.");
    }
}
