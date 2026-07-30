namespace EmbodySense.Web.Services;

/// <summary>
/// Defines authentication-scheme names owned by the local Web session handler.
/// </summary>
public static class WebSessionAuthenticationDefaults
{
    /// <summary>
    /// Names the opaque-token authentication scheme used by HTTP and SignalR endpoints.
    /// </summary>
    public const string Scheme = "EmbodySenseWebSession";
}
