namespace EmbodySense.Tests.Support;

public sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        var identifier = Guid.NewGuid().ToString("N");
        RootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embodysense-tests", identifier);
        ServerStatePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embodysense-test-server-state", identifier);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string ServerStatePath { get; }

    public string File(params string[] segments)
    {
        return System.IO.Path.Combine([RootPath, .. segments]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            if (Directory.Exists(ServerStatePath))
            {
                Directory.Delete(ServerStatePath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
