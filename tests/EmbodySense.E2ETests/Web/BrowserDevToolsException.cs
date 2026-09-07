namespace EmbodySense.E2ETests.Web;

internal sealed class BrowserDevToolsException : InvalidOperationException
{
    public BrowserDevToolsException(string category, string method, int? code, string message)
        : base($"Browser DevTools {category}: method={method}; code={code?.ToString() ?? "none"}; message={message}")
    {
        Category = category;
        Method = method;
        Code = code;
    }

    public string Category { get; }

    public string Method { get; }

    public int? Code { get; }
}
