namespace EmbodySense.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        RootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embodysense-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

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
    }
}
