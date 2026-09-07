namespace EmbodySense.E2ETests.Web;

internal sealed class BrowserDevToolsException : Exception
{
    private const int MaxCategoryLength = 48;
    private const int MaxMethodLength = 96;
    private const int MaxMessageLength = 160;

    public BrowserDevToolsException(string category, string method, int? code, string message)
        : base($"Browser DevTools {Normalize(category, MaxCategoryLength)}: method={Normalize(method, MaxMethodLength)}; code={code?.ToString() ?? "none"}; message={Normalize(message, MaxMessageLength)}")
    {
        Category = Normalize(category, MaxCategoryLength);
        Method = Normalize(method, MaxMethodLength);
        Code = code;
        SafeMessage = Normalize(message, MaxMessageLength);
    }

    public string Category { get; }

    public string Method { get; }

    public int? Code { get; }

    public string SafeMessage { get; }

    private static string Normalize(string value, int maximumLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? "unavailable" : value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
