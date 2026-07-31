using EmbodySense.Web;
using EmbodySense.Core.Startup.Workspace.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

/// <summary>
/// Maps reusable workspace state and Web options to the browser status contract.
/// </summary>
public static class WebStatusFactory
{
    /// <summary>
    /// Creates a status snapshot without creating an agent runtime or mutating the workspace.
    /// </summary>
    /// <param name="options">The validated Web binding and workspace options.</param>
    /// <param name="status">The current reusable workspace status.</param>
    /// <returns>A Web-primary status projection with the CLI's verification role described.</returns>
    public static WebStatus Create(WebRunOptions options, WorkspaceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);

        return new WebStatus("web", true, status.RootPath, status.IsInitialized, options.Url, "CLI remains supported for verification and third-party client conformance.");
    }
}
